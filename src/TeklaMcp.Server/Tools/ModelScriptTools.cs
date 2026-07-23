using System.ComponentModel;
using ModelContextProtocol.Server;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TeklaMcp.Scripting;

namespace TeklaMcp.Server.Tools;

/// <summary>
/// The scripting escape hatch: lets an agent run a short, policy-checked C# script against the
/// full Tekla Open API when no dedicated tool covers the need, plus offline API-reference
/// search so scripts are written against verified signatures instead of guessed ones.
/// Safety: scripts are read-only by default; allowMutations=true unlocks writes, and the tool
/// description obliges the agent to get the user's explicit go-ahead before setting it.
/// </summary>
[McpServerToolType]
public static class ModelScriptTools
{
    [McpServerTool(Name = "tekla_run_csharp")]
    [Description(
        "ESCAPE HATCH — run a short C# script against the live Tekla model with the full Tekla Open API. " +
        "Use ONLY when no dedicated tekla_* tool covers the need; prefer dedicated tools (they are faster and safer). " +
        "If you use this for something recurring, ALSO call tekla_report_gap so it becomes a first-class tool.\n" +
        "\n" +
        "RECOMMENDED WORKFLOW: (1) verify type/member signatures with tekla_search_api / tekla_get_api_doc — do not " +
        "guess them; (2) validate the exact source with tekla_check_csharp; (3) run it. For a mutation, include the " +
        "check result's codeSha256 when showing the exact script to the user for approval.\n" +
        "\n" +
        "SCRIPT ENVIRONMENT:\n" +
        "- C# top-level statements (no class/Main needed). Pre-imported namespaces: System, System.Collections.Generic, " +
        "System.Linq, System.Text, Tekla.Structures, Tekla.Structures.Model, Tekla.Structures.Model.UI, " +
        "Tekla.Structures.Geometry3d, Tekla.Structures.Filtering.\n" +
        "- Drawing is available but NOT globally imported because Drawing.Part/View conflict with Model.Part/UI.View. " +
        "Use an explicit alias such as `using TSD = Tekla.Structures.Drawing;`.\n" +
        "- Connect yourself: `var model = new Model();` (attaches to the running Tekla).\n" +
        "- RETURN a value as the LAST EXPRESSION of the script — it is serialized to JSON (returnValueJson). Return " +
        "small aggregated values (numbers, strings, anonymous objects, lists, dictionaries), never raw model objects.\n" +
        "- `Print(...)` for intermediate output (capped at 500 lines / 64000 characters). Console does NOT exist here.\n" +
        "- Units are mm; coordinates are whatever work plane is current (dedicated tools use the GLOBAL plane).\n" +
        "- Enumerators: `var e = model.GetModelObjectSelector().GetAllObjectsWithType(...); while (e.MoveNext()) ...` " +
        "(AutoFetch is already enabled process-wide). Filter by type early — models can hold 400k+ objects.\n" +
        "\n" +
        "EXAMPLE (count beams longer than 12 m):\n" +
        "var model = new Model();\n" +
        "var e = model.GetModelObjectSelector().GetAllObjectsWithType(ModelObject.ModelObjectEnum.BEAM);\n" +
        "var n = 0;\n" +
        "while (e.MoveNext()) { if (e.Current is Beam b && " +
        "Distance.PointToPoint(b.StartPoint, b.EndPoint) > 12000) n++; }\n" +
        "new { LongBeams = n }\n" +
        "\n" +
        "MUTATIONS: read-only by default — mutating members (Insert/Modify/Delete/CommitChanges/SetUserProperty/" +
        "Operation.*) are rejected unless allowMutations=true. Before setting it you MUST (1) prefer a dedicated " +
        "tekla_create_*/tekla_modify_*/tekla_delete_* tool if one fits (preview-by-default, safer); (2) show the user " +
        "the script and what it will change, and get their explicit go-ahead IN THIS CONVERSATION; (3) make the change " +
        "traceable/reversible: where practical set the MCP_ORIGIN UDA on created/modified objects " +
        "(obj.SetUserProperty(\"MCP_ORIGIN\", \"mcp-script\")) and tell the user committed changes are undoable with " +
        "Tekla's Ctrl+Z.\n" +
        "\n" +
        "LIMITS (enforced): no file/network/process/reflection/thread/Console access, no #r/#load, no await. " +
        "Execution deadline (default 60 s) with best-effort worker abort. On the Mock backend the script is " +
        "policy-checked, compiled only when TEKLA_MCP_SCRIPT_REF_DIR is configured, and NEVER executed — " +
        "do not fabricate results from it.")]
    public static ScriptResult RunCsharp(
        ITeklaModelService model,
        [Description("The C# script (top-level statements; last expression = return value).")] string code,
        [Description("Allow mutating Tekla API calls. Set true ONLY after the user explicitly approved this " +
                     "specific change in this conversation (show them the script first). Prefer the dedicated " +
                     "write tools (preview-by-default).")]
        bool allowMutations = false,
        [Description("Execution deadline in seconds (default 60, max 600). Abort is best-effort around Tekla remoting.")]
        int timeoutSeconds = 60)
        => model.ExecuteScript(code ?? "", allowMutations, timeoutSeconds);

    [McpServerTool(Name = "tekla_check_csharp")]
    [Description(
        "COMPILE-ONLY safety check for the exact C# source intended for tekla_run_csharp. Applies the same banned-API " +
        "policy, resolves all installed Tekla.Structures*.dll references (including Drawing/Dialog when present), and " +
        "returns compiler diagnostics, a SHA-256 code hash, and detected mutating member names. It NEVER executes the " +
        "script, never connects to the model, and never changes a model or drawing. Mutating members are therefore " +
        "allowed during this check so a proposed write can be verified BEFORE asking the user to approve it. A " +
        "successful check is not approval to execute: show the user this exact script plus codeSha256 and only then " +
        "call tekla_run_csharp with allowMutations=true after explicit approval. Inspect compiled=true; on a Mock " +
        "backend without TEKLA_MCP_SCRIPT_REF_DIR, compilation is unavailable and success=false.")]
    public static ScriptResult CheckCsharp(
        ITeklaModelService model,
        [Description("The exact C# script to policy-check and compile without executing.")] string code)
        => model.ExecuteScript(code ?? "", allowMutations: true, timeoutSeconds: 60, compileOnly: true);

    [McpServerTool(Name = "tekla_search_api")]
    [Description(
        "Search the local Tekla Open API reference by keywords (type names + member signature lines). " +
        "ALWAYS verify signatures here before writing a tekla_run_csharp script — do not guess API members. " +
        "Good queries are 1-3 short tokens: 'Beam StartPoint', 'GetReportProperty', 'ModelObjectSelector'. " +
        "Follow up with tekla_get_api_doc to read a matched type's full page. " +
        "Works offline from the generated reference folder; if it is missing, the result says how to generate it.")]
    public static ApiSearchResult SearchApi(
        [Description("Search keywords (type and/or member name fragments), e.g. 'Beam profile'.")] string query,
        [Description("Max matching types to return (default 10, max 50).")] int limit = 10)
        => ApiReference.Search(query ?? "", limit);

    [McpServerTool(Name = "tekla_get_api_doc")]
    [Description(
        "Read the full local reference page for ONE Tekla Open API type: every constructor/property/method " +
        "signature with summaries (Markdown). Accepts a short name ('Beam') or a full name " +
        "('Tekla.Structures.Model.Beam'). Use after tekla_search_api to confirm exact signatures before scripting.")]
    public static ApiTypeDoc GetApiDoc(
        [Description("Type name, short or fully qualified.")] string typeName,
        [Description("Truncate the page to this many characters (default 24000).")] int maxChars = 24_000)
        => ApiReference.GetTypeDoc(typeName ?? "", maxChars);

    [McpServerTool(Name = "tekla_get_api_reference_status")]
    [Description("Check whether the offline Tekla Open API reference is available and return its " +
                 "resolved directory or exact setup guidance. Call this before a scripting session.")]
    public static ApiReferenceStatus GetApiReferenceStatus()
        => ApiReference.GetStatus();
}
