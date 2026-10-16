using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core;

/// <summary>
/// Compares what <c>tekla_modify_parts</c> asked for with what the database holds after the
/// commit (issue #22). Both backends feed it the re-selected objects; the comparison, the status
/// and the wording are shared and unit-tested here.
/// </summary>
public static class PartWriteCheck
{
    /// <summary>End points closer than this (mm) count as equal.</summary>
    public const double PointTolerance = 0.1;

    /// <summary>
    /// Where the ends of a linear member should be after <paramref name="mod"/>: the handle swap
    /// first, then explicit new points. Null for an end the modification does not touch.
    /// </summary>
    public static void ExpectedEnds(
        PartModification mod, Point3D? originalStart, Point3D? originalEnd,
        out Point3D? start, out Point3D? end)
    {
        start = null;
        end = null;
        if (mod.SwapHandles)
        {
            start = originalEnd;
            end = originalStart;
        }
        if (mod.NewStart != null) start = mod.NewStart;
        if (mod.NewEnd != null) end = mod.NewEnd;
    }

    /// <summary>Requested fields that <paramref name="stored"/> does not hold.</summary>
    public static List<FieldMismatch> Compare(
        PartModification mod, ModelObjectInfo stored, Point3D? expectedStart, Point3D? expectedEnd)
    {
        var mismatches = new List<FieldMismatch>();
        var guid = string.IsNullOrWhiteSpace(stored.Guid) ? mod.Guid : stored.Guid;

        void Text(string field, string? requested, string storedValue, bool ignoreSpaces)
        {
            if (string.IsNullOrWhiteSpace(requested)) return; // the backends leave blanks alone
            if (SameText(requested!, storedValue, ignoreSpaces)) return;
            mismatches.Add(new FieldMismatch
            {
                Guid = guid, Field = field, Requested = requested!.Trim(), Stored = storedValue ?? "",
            });
        }

        Text("profile", mod.Profile, stored.Profile, ignoreSpaces: true);
        Text("material", mod.Material, stored.Material, ignoreSpaces: false);
        Text("class", mod.Class, stored.Class, ignoreSpaces: false);
        Text("name", mod.Name, stored.Name, ignoreSpaces: false);
        Point("start", expectedStart, stored.StartX, stored.StartY, stored.StartZ, guid, mismatches);
        Point("end", expectedEnd, stored.EndX, stored.EndY, stored.EndZ, guid, mismatches);
        return mismatches;
    }

    private static void Point(
        string field, Point3D? expected, double? x, double? y, double? z, string guid, List<FieldMismatch> into)
    {
        if (expected == null) return;
        if (x.HasValue && y.HasValue && z.HasValue &&
            Math.Abs(x.Value - expected.X) <= PointTolerance &&
            Math.Abs(y.Value - expected.Y) <= PointTolerance &&
            Math.Abs(z.Value - expected.Z) <= PointTolerance)
            return;
        into.Add(new FieldMismatch
        {
            Guid = guid,
            Field = field,
            Requested = Format(expected.X, expected.Y, expected.Z),
            Stored = x.HasValue && y.HasValue && z.HasValue ? Format(x.Value, y.Value, z.Value) : "(not readable)",
        });
    }

    private static bool SameText(string requested, string stored, bool ignoreSpaces)
    {
        string Norm(string s) => ignoreSpaces
            ? new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray())
            : (s ?? "").Trim();
        return string.Equals(Norm(requested), Norm(stored), StringComparison.OrdinalIgnoreCase);
    }

    private static string Format(double x, double y, double z) =>
        string.Join(",", new[] { x, y, z }.Select(v => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture)));

    /// <summary>Collects the per-object comparisons of one batch into a <see cref="WriteVerification"/>.</summary>
    public sealed class Batch
    {
        private readonly WriteVerification _result = new WriteVerification();
        private readonly List<string> _errors = new List<string>();
        private bool _positionRequested;

        /// <summary>One error line per mismatching object, for <c>WriteResult.Errors</c>.</summary>
        public IReadOnlyList<string> Errors => _errors;

        /// <summary>
        /// Records one written object. <paramref name="stored"/> is the re-selected object, or
        /// null when it could not be read back.
        /// </summary>
        public void Add(PartModification mod, ModelObjectInfo? stored, Point3D? expectedStart, Point3D? expectedEnd)
        {
            if (mod.Position != null || !string.IsNullOrWhiteSpace(mod.MatchPositionGuid)) _positionRequested = true;
            if (stored == null)
            {
                _result.UnreadObjects++;
                _errors.Add(mod.Guid + ": written, but could not be read back to verify it.");
                return;
            }

            _result.CheckedObjects++;
            var mismatches = Compare(mod, stored, expectedStart, expectedEnd);
            if (mismatches.Count == 0)
            {
                _result.VerifiedObjects++;
                return;
            }

            _result.MismatchedObjects++;
            foreach (var mismatch in mismatches)
            {
                if (_result.Mismatches.Count < WriteVerification.MismatchLimit) _result.Mismatches.Add(mismatch);
                else _result.MismatchesTruncated = true;
            }
            _errors.Add(mod.Guid + ": Tekla accepted the change but the database holds other values — " +
                        string.Join("; ", mismatches.Select(m =>
                            $"{m.Field} requested '{m.Requested}', stored '{m.Stored}'")) +
                        ". Not retried: find out why (a component or lock may own the field) before writing again.");
        }

        public WriteVerification Build()
        {
            _result.Status =
                _result.MismatchedObjects > 0 ? "mismatch" :
                _result.UnreadObjects > 0 ? "incomplete" :
                _result.CheckedObjects > 0 ? "verified" : "not_checked";
            if (_positionRequested)
                _result.NotCompared.Add(
                    "position: Tekla canonicalizes Part.Position on commit (TOP + 180° is stored as BELOW + 0°), " +
                    "so it is not compared — the read-back preview shows what was stored.");
            return _result;
        }
    }
}
