using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Scripting;

/// <summary>
/// Compiles and (on the Tekla backend) executes agent-authored C# scripts via Roslyn scripting.
///
/// Tekla-agnostic by design: the caller supplies the Tekla assemblies either as loaded
/// <see cref="Assembly"/> instances (net48 backend — resolved from the running Tekla) or as
/// DLL file paths (mock backend — compile-only validation on any OS). Execution runs on a
/// dedicated thread with a hard timeout; on .NET Framework a timed-out script is aborted.
/// </summary>
public static class ScriptEngine
{
    /// <summary>Namespaces pre-imported into every script (no using directives needed).</summary>
    public static readonly string[] DefaultImports =
    {
        "System",
        "System.Collections.Generic",
        "System.Linq",
        "System.Text",
        "Tekla.Structures",
        "Tekla.Structures.Model",
        "Tekla.Structures.Model.UI",
        "Tekla.Structures.Geometry3d",
        "Tekla.Structures.Filtering",
    };

    public const int DefaultTimeoutSeconds = 60;
    public const int MaxTimeoutSeconds = 600;

    /// <summary>
    /// Build the metadata references for a script compilation: the core runtime assemblies of
    /// whatever runtime we're on, the globals assembly (for <c>Print</c>), plus the Tekla
    /// assemblies passed by the backend.
    /// </summary>
    public static IReadOnlyList<MetadataReference> BuildReferences(
        IEnumerable<Assembly>? teklaAssemblies = null,
        IEnumerable<string>? teklaDllPaths = null)
    {
        var refs = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        void AddFile(string? path)
        {
            if (string.IsNullOrEmpty(path) || refs.ContainsKey(path!) || !File.Exists(path))
                return;
            try { refs[path!] = MetadataReference.CreateFromFile(path!); }
            catch { /* unreadable/native — skip */ }
        }

        void AddAssembly(Assembly? assembly)
        {
            try { AddFile(assembly?.Location); }
            catch { /* dynamic assembly without location — skip */ }
        }

        // Core runtime references. On .NET (Core) the runtime assemblies are listed in
        // TRUSTED_PLATFORM_ASSEMBLIES; on .NET Framework that key is null — use well-known
        // assemblies plus the netstandard facade (needed to consume the netstandard2.0
        // globals assembly).
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mscorlib", "netstandard", "System.Private.CoreLib", "System.Runtime",
                "System.Collections", "System.Linq", "System.Runtime.Extensions",
                "System.Globalization", "System.ObjectModel", "System.Text.Encoding.Extensions",
            };
            foreach (var path in tpa.Split(Path.PathSeparator))
                if (wanted.Contains(Path.GetFileNameWithoutExtension(path)))
                    AddFile(path);
        }
        else
        {
            AddAssembly(typeof(object).Assembly);     // mscorlib
            AddAssembly(typeof(Enumerable).Assembly); // System.Core
            AddAssembly(typeof(Uri).Assembly);        // System
            AddAssembly(AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "netstandard", StringComparison.OrdinalIgnoreCase)));
        }

        AddAssembly(typeof(ScriptGlobals).Assembly);

        foreach (var assembly in teklaAssemblies ?? Enumerable.Empty<Assembly>())
            AddAssembly(assembly);
        foreach (var path in teklaDllPaths ?? Enumerable.Empty<string>())
            AddFile(path);

        return refs.Values.ToList();
    }

    public static Script<object> Create(string code, IReadOnlyList<MetadataReference> references)
        => CSharpScript.Create(
            code,
            ScriptOptions.Default
                .WithReferences(references)
                .WithImports(DefaultImports)
                .WithEmitDebugInformation(false)
                .WithAllowUnsafe(false),
            typeof(ScriptGlobals));

    /// <summary>Compile without running. Returns error diagnostics as agent-readable strings.</summary>
    public static List<string> Compile(Script<object> script)
    {
        var errors = new List<string>();
        try
        {
            foreach (var diagnostic in script.Compile())
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                    errors.Add(diagnostic.ToString());
        }
        catch (CompilationErrorException ex)
        {
            errors.AddRange(ex.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
        }
        catch (Exception ex)
        {
            errors.Add("Compilation failed: " + ex.Message);
        }

        if (errors.Count > 20)
        {
            var extra = errors.Count - 20;
            errors = errors.Take(20).ToList();
            errors.Add($"…and {extra} more errors.");
        }
        return errors;
    }

    /// <summary>
    /// Run a compiled script with a hard timeout, filling <paramref name="result"/> in place.
    /// Only call this on a backend that can actually reach Tekla (net48) — the mock validates
    /// and compiles but never executes.
    /// </summary>
    public static void Run(Script<object> script, ScriptGlobals globals, int timeoutSeconds, ScriptResult result)
    {
        var timeout = TimeSpan.FromSeconds(Math.Min(Math.Max(timeoutSeconds, 1), MaxTimeoutSeconds));

        object? returnValue = null;
        Exception? failure = null;
        var completed = false;

        var thread = new Thread(() =>
        {
            try
            {
                var state = script.RunAsync(globals).GetAwaiter().GetResult();
                returnValue = state.ReturnValue;
                completed = true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "tekla-mcp-script",
        };

        thread.Start();
        result.Stage = "execute";

        if (!thread.Join(timeout))
        {
            // Hard abort works on .NET Framework (the only runtime that executes scripts);
            // on .NET Core it throws PlatformNotSupportedException and the background thread
            // is simply left to finish on its own.
            try { thread.Abort(); } catch { }
            result.Error = $"Script exceeded the {timeout.TotalSeconds:0}s timeout and was aborted. " +
                           "If the script had already mutated the model, those changes may have been committed. " +
                           "Narrow the scan (filter by object type, cap counts) or raise timeoutSeconds.";
            return;
        }

        if (!completed)
        {
            result.Error = Describe(failure);
            return;
        }

        result.Executed = true;
        result.Success = true;
        result.ReturnValueJson = SafeJson.ToJson(returnValue);
    }

    private static string Describe(Exception? ex)
    {
        if (ex == null)
            return "Script failed for an unknown reason.";
        var root = ex;
        while (root.InnerException != null)
            root = root.InnerException;
        var text = root.GetType().Name + ": " + root.Message;
        return ReferenceEquals(root, ex) ? text : text + " (outer: " + ex.GetType().Name + ")";
    }
}
