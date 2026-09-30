using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Server.Tools;

/// <summary>Shared parsing + query helpers for the write/generator tools.</summary>
internal static class ToolHelpers
{
    /// <summary>Parse "x,y,z" (mm) into a point, or null if not three numbers.</summary>
    public static Point3D? ParsePoint(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var nums = ParseNums(raw);
        return nums.Count >= 3 ? new Point3D(nums[0], nums[1], nums[2]) : null;
    }

    /// <summary>Parse "x,y,z; x,y,z; ..." into a list of points.</summary>
    public static List<Point3D> ParsePoints(string? raw)
    {
        var result = new List<Point3D>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var chunk in raw!.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = ParsePoint(chunk);
            if (p != null) result.Add(p);
        }
        return result;
    }

    /// <summary>Parse a delimited list of numbers, e.g. "0,6000,12000".</summary>
    public static List<double> ParseNums(string? raw)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var token in raw!.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token.Trim().Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                result.Add(v);
        }
        return result;
    }

    /// <summary>Parse a comma/semicolon/newline separated string list.</summary>
    public static List<string> ParseList(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var part in raw!.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) result.Add(trimmed);
        }
        return result;
    }

    public static ObjectQuery BuildQuery(
        string? type = null,
        string? @class = null,
        string? profile = null,
        string? material = null,
        string? nameContains = null,
        string? udaName = null,
        string? udaEquals = null,
        string? guidIn = null,
        bool useSelection = false) =>
        new ObjectQuery
        {
            Type = type,
            Class = @class,
            Profile = profile,
            Material = material,
            NameContains = nameContains,
            UdaName = udaName,
            UdaEquals = udaEquals,
            GuidIn = ParseList(guidIn),
            UseSelection = useSelection,
        };

    public static PartPosition? BuildPosition(
        string? plane,
        double? planeOffset,
        string? rotation,
        double? rotationOffset,
        string? depth,
        double? depthOffset)
    {
        if (string.IsNullOrWhiteSpace(plane) && !planeOffset.HasValue &&
            string.IsNullOrWhiteSpace(rotation) && !rotationOffset.HasValue &&
            string.IsNullOrWhiteSpace(depth) && !depthOffset.HasValue)
            return null;

        return new PartPosition
        {
            Plane = string.IsNullOrWhiteSpace(plane) ? null : plane,
            PlaneOffset = planeOffset,
            Rotation = string.IsNullOrWhiteSpace(rotation) ? null : rotation,
            RotationOffset = rotationOffset,
            Depth = string.IsNullOrWhiteSpace(depth) ? null : depth,
            DepthOffset = depthOffset,
        };
    }

    /// <summary>True if the object has both endpoints (i.e. a linear member with geometry).</summary>
    public static bool HasEnds(ModelObjectInfo o) =>
        o.StartX.HasValue && o.StartY.HasValue && o.StartZ.HasValue &&
        o.EndX.HasValue && o.EndY.HasValue && o.EndZ.HasValue;

    public static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    // -- Write target + outcome (backlog §4) -----------------------------------------------
    //
    // Every write tool goes through one of the Write(...) wrappers: resolve the model the write
    // would go to (refusing a mismatching expectedModelPath BEFORE anything is written), run the
    // write, stamp result.Target and result.Outcome, and escalate outcomes the agent must not
    // read as progress to isError=true.

    public const string ExpectedModelPathDescription =
        "Optional safety check: the model folder (or its .db1 file) this write is meant for, e.g. the " +
        "modelPath from tekla_get_connection_info. If the connected Tekla has another model open, the " +
        "call is refused before anything is written. Recommended whenever several Tekla instances or " +
        "models are in play.";

    /// <summary>The model a write would go to now; throws (nothing written) on a mismatch.</summary>
    public static WriteTarget ResolveTarget(ITeklaModelService model, string? expectedModelPath)
    {
        var target = model.GetWriteTarget();
        if (!string.IsNullOrWhiteSpace(expectedModelPath) &&
            !ModelPaths.Same(expectedModelPath, target.ModelPath))
            throw new ModelContextProtocol.McpException(
                "Refused before writing: expectedModelPath is '" + expectedModelPath!.Trim() +
                "', but the connected Tekla has '" + target.ModelPath + "' open (model '" + target.ModelName +
                "'" + (target.TeklaPid is int pid ? ", Tekla PID " + pid : "") + "). Nothing was written. " +
                "Open the intended model, or correct expectedModelPath if this IS the right model.");
        return target;
    }

    public static WriteResult Write(
        ITeklaModelService model, string? expectedModelPath, Func<WriteResult> write)
    {
        var target = ResolveTarget(model, expectedModelPath);
        var result = write();
        result.Target = target;
        return FailIfNothingApplied(result);
    }

    public static DrawingWriteResult Write(
        ITeklaModelService model, string? expectedModelPath, Func<DrawingWriteResult> write)
    {
        var target = ResolveTarget(model, expectedModelPath);
        var result = write();
        result.Target = target;
        return FailIfNothingApplied(result);
    }

    public static UdaOperationResult Write(
        ITeklaModelService model, string? expectedModelPath, Func<UdaOperationResult> write)
    {
        var target = ResolveTarget(model, expectedModelPath);
        var result = write();
        result.Target = target;
        if (string.IsNullOrEmpty(result.Outcome))
            result.Outcome = WriteOutcome.FromCounts(
                result.Applied, result.UpdatedObjects, Math.Max(0, result.MatchedObjects - result.UpdatedObjects));
        ThrowIfUnknown(result.Outcome, "uda_write", result);
        return result;
    }

    public static UdaFileWriteResult Write(
        ITeklaModelService model, string? expectedModelPath, Func<UdaFileWriteResult> write)
    {
        var target = ResolveTarget(model, expectedModelPath);
        var result = write();
        result.Target = target;
        if (string.IsNullOrEmpty(result.Outcome))
            result.Outcome = WriteOutcome.FromCounts(result.Applied, result.Updated, 0);
        ThrowIfUnknown(result.Outcome, "set_udas_from_file", result);
        return result;
    }

    /// <summary>
    /// Escalates a TOTAL apply-failure into a protocol-level tool error (isError=true).
    /// Field feedback: apply=true answering with createdCount=0 + a populated errors[] as a
    /// NORMAL result let agents mistake 16 consecutive failed writes for progress. Previews
    /// (apply=false) and partial successes pass through untouched — their per-item errors
    /// stay visible in the structured result. An "unknown" outcome is always an error: it
    /// must be read back, never taken for progress — and never for "nothing was written".
    /// </summary>
    public static WriteResult FailIfNothingApplied(WriteResult result)
    {
        var touched = result.CreatedCount + result.ModifiedCount + result.DeletedCount;
        if (string.IsNullOrEmpty(result.Outcome))
            result.Outcome = WriteOutcome.FromCounts(result.Applied, touched, result.Errors.Count);
        ThrowIfUnknown(result.Outcome, result.Operation, result);

        if (!result.Applied) return result;
        if (touched > 0) return result;
        if (result.Errors.Count == 0 && string.IsNullOrWhiteSpace(result.Message)) return result;

        throw new ModelContextProtocol.McpException(
            "apply=true failed: no objects were written (operation '" + result.Operation +
            "', planned " + result.PlannedCount + ", outcome '" + result.Outcome + "'). " +
            Summarize(result.Errors, result.Message));
    }

    /// <summary>
    /// Drawing-side counterpart of <see cref="FailIfNothingApplied(WriteResult)"/>. Keyed on
    /// errors only: several drawing operations (open/save/close) legitimately succeed with
    /// zero created/modified/deleted counts and an informational message.
    /// </summary>
    public static DrawingWriteResult FailIfNothingApplied(DrawingWriteResult result)
    {
        var touched = result.CreatedCount + result.ModifiedCount + result.DeletedCount + result.OutputFiles.Count;
        if (string.IsNullOrEmpty(result.Outcome))
            result.Outcome = WriteOutcome.FromCounts(result.Applied, touched, result.Errors.Count);
        ThrowIfUnknown(result.Outcome, result.Operation, result);

        if (!result.Applied || result.Errors.Count == 0) return result;
        if (touched > 0) return result;

        throw new ModelContextProtocol.McpException(
            "apply=true failed: nothing was written (drawing operation '" + result.Operation +
            "', planned " + result.PlannedCount + "). " +
            Summarize(result.Errors, result.Message));
    }

    private static readonly System.Text.Json.JsonSerializerOptions ResultJson =
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// "unknown" is an error the agent must act on, but its counters and GUIDs are what the
    /// read-back needs — so the full result travels inside the error text.
    /// </summary>
    private static void ThrowIfUnknown(string outcome, string operation, object result)
    {
        if (outcome != WriteOutcome.Unknown) return;
        string json;
        try { json = System.Text.Json.JsonSerializer.Serialize(result, result.GetType(), ResultJson); }
        catch (Exception ex) { json = "(result not serializable: " + ex.Message + ")"; }
        throw new ModelContextProtocol.McpException(
            "apply=true outcome UNKNOWN (operation '" + operation + "'): the call failed after writing " +
            "began, and Tekla does not roll back — the model may hold some, all or none of these changes. " +
            "Do NOT retry blindly: read the targets back (tekla_get_object_by_guid / tekla_find_objects, the " +
            "GUIDs below), compare, then write only what is missing. Result: " + json);
    }

    private static string Summarize(List<string> errors, string? message)
    {
        var distinct = errors.Distinct().Take(5).ToList();
        var text = distinct.Count > 0
            ? "Errors: " + string.Join(" | ", distinct) +
              (errors.Distinct().Count() > 5 ? " | …" : "")
            : "";
        if (!string.IsNullOrWhiteSpace(message))
            text += (text.Length > 0 ? " " : "") + "Message: " + message;
        return text;
    }
}
