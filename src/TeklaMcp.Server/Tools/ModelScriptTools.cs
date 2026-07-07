using System;
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
/// Safety: scripts are read-only unless BOTH the tool call sets allowMutations=true AND the
/// server was started with TEKLA_MCP_ALLOW_SCRIPT_WRITES=1 (see ScriptPolicy).
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
        "guess them; (2) run the script; (3) if compilation fails, fix using the reported errors and retry.\n" +
        "\n" +
        "SCRIPT ENVIRONMENT:\n" +
        "- C# top-level statements (no class/Main needed). Pre-imported namespaces: System, System.Collections.Generic, " +
        "System.Linq, System.Text, Tekla.Structures, Tekla.Structures.Model, Tekla.Structures.Model.UI, " +
        "Tekla.Structures.Geometry3d, Tekla.Structures.Filtering.\n" +
        "- Connect yourself: `var model = new Model();` (attaches to the running Tekla).\n" +
        "- RETURN a value as the LAST EXPRESSION of the script — it is serialized to JSON (returnValueJson). Return " +
        "small aggregated values (numbers, strings, anonymous objects, lists, dictionaries), never raw model objects.\n" +
        "- `Print(...)` for intermediate output (capped at 500 lines). Console does NOT exist here.\n" +
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
        "LIMITS (enforced): read-only by default — mutating members (Insert/Modify/Delete/CommitChanges/SetUserProperty/" +
        "Operation.*) are rejected unless allowMutations=true and the server runs with TEKLA_MCP_ALLOW_SCRIPT_WRITES=1. " +
        "No file/network/process/reflection/thread/Console access, no #r/#load, no await. Hard timeout (default 60 s). " +
        "On the Mock backend the script is validated/compiled but NEVER executed — do not fabricate results from it.")]
    public static ScriptResult RunCsharp(
        ITeklaModelService model,
        [Description("The C# script (top-level statements; last expression = return value).")] string code,
        [Description("Allow mutating Tekla API calls. Requires the user's explicit intent AND the server env " +
                     "TEKLA_MCP_ALLOW_SCRIPT_WRITES=1. Prefer the dedicated write tools (preview-by-default).")]
        bool allowMutations = false,
        [Description("Hard timeout in seconds (default 60, max 600).")] int timeoutSeconds = 60)
    {
        if (allowMutations &&
            Environment.GetEnvironmentVariable(ScriptPolicy.AllowWritesEnvVar) != "1")
        {
            return new ScriptResult
            {
                Stage = "policy",
                PolicyViolations =
                {
                    "allowMutations=true is disabled: the server was started without " +
                    ScriptPolicy.AllowWritesEnvVar + "=1. Scripts run READ-ONLY here.",
                },
                Guidance = "Either do the change with the dedicated write tools (tekla_create_*/tekla_modify_*/" +
                           "tekla_delete_*, preview-by-default), or ask the user to restart the server with " +
                           ScriptPolicy.AllowWritesEnvVar + "=1 if a scripted mutation is really required.",
            };
        }

        return model.ExecuteScript(code ?? "", allowMutations, timeoutSeconds);
    }

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
}
