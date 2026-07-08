using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TeklaMcp.Scripting;

/// <summary>
/// Static (syntax-tree) safety policy for agent-authored scripts, applied BEFORE compilation.
///
/// This is a pragmatic barrier for well-behaved agents, not a cryptographic sandbox: it bans
/// the API families a Tekla model script never needs (file system, network, processes,
/// reflection, threads, Console) and — unless mutations are explicitly allowed — the Tekla
/// members that write to the model. Checks are purely syntactic (identifier names), so they
/// can produce false positives; the violation messages tell the agent how to rephrase.
/// </summary>
public static class ScriptPolicy
{
    public const int MaxCodeLength = 32_000;

    // Using-directives are allowed only for these exact namespaces / this prefix.
    private static readonly HashSet<string> AllowedUsings = new HashSet<string>(StringComparer.Ordinal)
    {
        "System",
        "System.Collections",
        "System.Collections.Generic",
        "System.Globalization",
        "System.Linq",
        "System.Text",
    };

    private const string AllowedUsingPrefix = "Tekla.Structures";

    // Identifiers that give scripts capabilities they must not have. Syntactic ban:
    // even a local variable with one of these names is rejected (rename it).
    private static readonly HashSet<string> BannedIdentifiers = new HashSet<string>(StringComparer.Ordinal)
    {
        // process / host
        "Process", "ProcessStartInfo", "Environment", "AppDomain", "Console",
        // file system
        "File", "Directory", "Path", "FileInfo", "DirectoryInfo", "FileStream",
        "StreamReader", "StreamWriter", "TextReader", "TextWriter",
        // network
        "WebClient", "HttpClient", "WebRequest", "HttpWebRequest", "Socket",
        "TcpClient", "UdpClient", "Dns",
        // reflection / dynamic code
        "Assembly", "Activator", "Marshal", "DynamicMethod", "ILGenerator",
        // threading (scripts are synchronous; the host owns the timeout)
        "Thread", "ThreadPool", "Task", "TaskFactory", "Timer", "CancellationTokenSource",
        // other host state
        "Registry", "GC",
    };

    // Namespace segments that reveal a qualified use of a banned area (System.IO.File, ...).
    private static readonly HashSet<string> BannedNamespaceSegments = new HashSet<string>(StringComparer.Ordinal)
    {
        "IO", "Net", "Sockets", "Http", "Reflection", "Emit", "Diagnostics",
        "InteropServices", "Win32", "Threading", "Timers",
    };

    // Member names that mutate the Tekla model (or run arbitrary macros). Rejected unless the
    // caller passed allowMutations=true — which the agent may only do after the user explicitly
    // confirmed the change (the confirmation contract lives in the tool description).
    private static readonly HashSet<string> MutatingMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "Insert", "Delete", "Modify", "CommitChanges",
        "SetUserProperty", "SetAttribute", "SetCurrentTransformationPlane",
        "Operation", "RunMacro", "RunMacroAndWait", "PlaceComponents",
    };

    /// <summary>Validate <paramref name="code"/>; empty violation list = allowed to compile.</summary>
    public static IReadOnlyList<string> Validate(string code, bool allowMutations)
    {
        var violations = new List<string>();

        if (string.IsNullOrWhiteSpace(code))
        {
            violations.Add("Script is empty.");
            return violations;
        }

        if (code.Length > MaxCodeLength)
        {
            violations.Add($"Script is too long ({code.Length} chars, max {MaxCodeLength}). Split the work into smaller scripts.");
            return violations;
        }

        var tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(kind: SourceCodeKind.Script));
        var root = tree.GetRoot();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var mutations = new SortedSet<string>(StringComparer.Ordinal);

        void Add(string message)
        {
            if (seen.Add(message) && violations.Count < 20)
                violations.Add(message);
        }

        // #r / #load / any preprocessor directive: could pull in arbitrary assemblies.
        if (root.DescendantTrivia().Any(t => t.IsDirective))
            Add("Preprocessor directives (#r, #load, #if, ...) are not allowed.");

        foreach (var token in root.DescendantTokens())
        {
            switch (token.Kind())
            {
                case SyntaxKind.UnsafeKeyword:
                case SyntaxKind.FixedKeyword:
                case SyntaxKind.StackAllocKeyword:
                case SyntaxKind.ExternKeyword:
                    Add($"'{token.ValueText}' is not allowed in scripts.");
                    break;
            }
        }

        if (root.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
            Add("'await' is not allowed — scripts are synchronous (the host enforces the timeout).");

        foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var ns = u.Name?.ToString() ?? "";
            var allowed = AllowedUsings.Contains(ns)
                          || ns == AllowedUsingPrefix
                          || ns.StartsWith(AllowedUsingPrefix + ".", StringComparison.Ordinal);
            if (!allowed)
                Add($"'using {ns}' is not allowed. Allowed: {AllowedUsingPrefix}.*, " +
                    string.Join(", ", AllowedUsings.OrderBy(n => n)) + ".");
        }

        foreach (var name in root.DescendantTokens()
                     .Where(t => t.IsKind(SyntaxKind.IdentifierToken))
                     .Select(t => t.ValueText))
        {
            if (BannedIdentifiers.Contains(name))
                Add($"'{name}' is not allowed — scripts have no file/network/process/reflection/thread/Console access. " +
                    "Use Print(...) for output and return a value as the last expression. " +
                    "(If this is just your variable name, rename it.)");
            else if (BannedNamespaceSegments.Contains(name))
                Add($"'{name}' looks like a banned namespace (System.{name}.*) — not allowed in scripts. " +
                    "(If this is just your variable name, rename it.)");
            else if (!allowMutations && MutatingMembers.Contains(name))
                mutations.Add(name);
        }

        if (mutations.Count > 0)
            Add($"Script uses mutating members ({string.Join(", ", mutations)}) but allowMutations=false. " +
                "Scripts are READ-ONLY by default. First check whether a dedicated tekla_create_* / tekla_modify_* / " +
                "tekla_delete_* tool covers this (preview-by-default, reversible). If a scripted mutation is really " +
                "needed: show the script to the user, get their explicit go-ahead, then retry with allowMutations=true.");

        return violations;
    }
}
