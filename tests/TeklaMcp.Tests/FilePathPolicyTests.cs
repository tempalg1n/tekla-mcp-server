using System;
using System.IO;
using TeklaMcp.Core.FileExchange;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// The exchange tools are the only file access the server has, so the gate around them is worth
/// pinning down: an escape here means an agent can write anywhere the user can.
/// </summary>
public class FilePathPolicyTests : IDisposable
{
    private readonly string _root;
    private readonly FilePathPolicy _policy;

    public FilePathPolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "teklamcp-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _policy = new FilePathPolicy(new[] { _root });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void AcceptsFileUnderRoot()
    {
        Assert.True(_policy.TryResolveForWrite(Path.Combine(_root, "parts.jsonl"), out var full, out var error), error);
        Assert.Equal(Path.Combine(_root, "parts.jsonl"), full);
    }

    [Fact]
    public void CreatesMissingSubdirectoryForWrite()
    {
        var nested = Path.Combine(_root, "sub", "deeper", "parts.csv");
        Assert.True(_policy.TryResolveForWrite(nested, out _, out var error), error);
        Assert.True(Directory.Exists(Path.GetDirectoryName(nested)));
    }

    [Fact]
    public void RejectsTraversalOutOfRoot()
    {
        var escape = Path.Combine(_root, "..", "escaped.jsonl");
        Assert.False(_policy.TryResolveForWrite(escape, out _, out var error));
        Assert.Contains("outside the allowed roots", error);
    }

    [Fact]
    public void RejectsSiblingDirectoryWithSharedPrefix()
    {
        // "C:\data-private" must NOT count as being under "C:\data".
        var sibling = _root + "-private";
        Assert.False(_policy.TryResolveForWrite(Path.Combine(sibling, "parts.jsonl"), out _, out var error));
        Assert.Contains("outside the allowed roots", error);
    }

    [Fact]
    public void RejectsDisallowedExtension()
    {
        Assert.False(_policy.TryResolveForWrite(Path.Combine(_root, "payload.ps1"), out _, out var error));
        Assert.Contains("not allowed", error);
    }

    [Fact]
    public void RejectsRelativePath()
    {
        Assert.False(_policy.TryResolveForWrite("parts.jsonl", out _, out var error));
        Assert.Contains("absolute", error);
    }

    [Fact]
    public void RejectsUncPath()
    {
        Assert.False(_policy.TryResolveForWrite(@"\\server\share\parts.jsonl", out _, out var error));
        Assert.Contains("UNC", error);
    }

    [Fact]
    public void RejectsReservedDeviceName()
    {
        Assert.False(_policy.TryResolveForWrite(Path.Combine(_root, "CON.csv"), out _, out var error));
        Assert.Contains("reserved", error);
    }

    [Fact]
    public void RejectsWildcards()
    {
        Assert.False(_policy.TryResolveForWrite(Path.Combine(_root, "*.jsonl"), out _, out var error));
        Assert.Contains("Wildcards", error);
    }

    [Fact]
    public void RejectsBlankPath()
    {
        Assert.False(_policy.TryResolveForWrite("   ", out _, out var error));
        Assert.Contains("required", error);
    }

    [Fact]
    public void ReadRequiresAnExistingFile()
    {
        var path = Path.Combine(_root, "missing.jsonl");
        Assert.False(_policy.TryResolveForRead(path, out _, out var error));
        Assert.Contains("not found", error);

        File.WriteAllText(path, "{}\n");
        Assert.True(_policy.TryResolveForRead(path, out var full, out var readError), readError);
        Assert.Equal(path, full);
    }

    [Fact]
    public void WithoutRootsEverythingIsRejected()
    {
        var empty = new FilePathPolicy(Array.Empty<string>());
        Assert.False(empty.TryResolveForWrite(Path.Combine(_root, "parts.jsonl"), out _, out var error));
        Assert.Contains(FilePathPolicy.RootEnvironmentVariable, error);
    }

    [Fact]
    public void StatusSidecarSitsNextToTheExport()
    {
        Assert.Equal(
            Path.Combine(_root, "parts.jsonl.status.json"),
            FilePathPolicy.StatusSidecarPath(Path.Combine(_root, "parts.jsonl")));
    }
}
