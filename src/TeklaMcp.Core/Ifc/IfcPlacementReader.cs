using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Ifc;

/// <summary>World placement of one IFC entity, in the IFC file's coordinates scaled to mm.</summary>
public sealed class IfcEntityPlacement
{
    public string GlobalId { get; set; } = "";
    /// <summary>STEP entity keyword, e.g. "IFCWINDOW".</summary>
    public string EntityType { get; set; } = "";
    public string Name { get; set; } = "";
    public string ObjectType { get; set; } = "";
    /// <summary>IfcWindow/IfcDoor OverallWidth in mm, when present in the file.</summary>
    public double? OverallWidth { get; set; }
    /// <summary>IfcWindow/IfcDoor OverallHeight in mm, when present in the file.</summary>
    public double? OverallHeight { get; set; }
    /// <summary>World origin of the entity's ObjectPlacement, file coordinates in mm.</summary>
    public Point3D Origin { get; set; } = new Point3D(0, 0, 0);
    public Point3D AxisX { get; set; } = new Point3D(1, 0, 0);
    public Point3D AxisY { get; set; } = new Point3D(0, 1, 0);
    public Point3D AxisZ { get; set; } = new Point3D(0, 0, 1);
    /// <summary>Multiplier that converted file length units to mm (1 = file was in mm).</summary>
    public double UnitScaleToMm { get; set; } = 1;
    /// <summary>Non-fatal notes (e.g. "no length unit found, assumed mm").</summary>
    public string? Warning { get; set; }
}

/// <summary>
/// Minimal ISO-10303-21 (STEP) reader that resolves ONE entity's world placement by its IFC
/// GlobalId: IFCLOCALPLACEMENT chain → composed IFCAXIS2PLACEMENT3D matrices → origin + axes,
/// with the project length unit applied. This is the fallback used when the Tekla Open API
/// cannot return reference-object geometry (field report: internal face queries fail for IFC
/// overlay windows while the data in the file itself is correct). Reads plain .ifc files and
/// .ifczip/.zip archives (the STEP entry inside is located automatically).
///
/// Deliberately NOT a general IFC parser: no representation/geometry, no styles. Memory-safe
/// on large files — the placement entities it indexes are a tiny fraction of a model file,
/// and points/directions are fetched by a targeted second pass.
/// </summary>
public static class IfcPlacementReader
{
    /// <summary>
    /// Reads the world placement of the entity with the given IFC GlobalId (22-char encoded
    /// form, e.g. "0VZkpIecn7$9mG$7iL8u45"). Returns null with <paramref name="error"/> set
    /// when the file/entity cannot be resolved.
    /// </summary>
    public static IfcEntityPlacement? TryRead(string filePath, string globalId, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            error = "IFC file not found: " + filePath;
            return null;
        }
        if (string.IsNullOrWhiteSpace(globalId))
        {
            error = "No IFC GlobalId to look up.";
            return null;
        }

        try
        {
            // -- Pass 1: the target entity, every IFCLOCALPLACEMENT/IFCAXIS2PLACEMENT3D, units.
            var index = ScanFile(filePath, globalId);
            if (index.TargetId == 0)
            {
                error = "Entity with GlobalId '" + globalId + "' not found in " +
                        Path.GetFileName(filePath) + ".";
                return null;
            }

            // -- Resolve the placement chain and collect the point/direction ids it needs.
            var wanted = new HashSet<int>();
            var chain = new List<int>(); // IFCAXIS2PLACEMENT3D ids, innermost first
            var guard = 0;
            for (var placementId = index.TargetPlacementId;
                 placementId != 0 && guard++ < 64;
                 placementId = index.LocalPlacements.TryGetValue(placementId, out var lp) ? lp.ParentId : 0)
            {
                if (!index.LocalPlacements.TryGetValue(placementId, out var local)) break;
                if (index.AxisPlacements.TryGetValue(local.AxisPlacementId, out var axis))
                {
                    chain.Add(local.AxisPlacementId);
                    if (axis.LocationId != 0) wanted.Add(axis.LocationId);
                    if (axis.AxisId != 0) wanted.Add(axis.AxisId);
                    if (axis.RefDirectionId != 0) wanted.Add(axis.RefDirectionId);
                }
            }

            // -- Pass 2: fetch just those IFCCARTESIANPOINT / IFCDIRECTION records.
            var vectors = FetchVectors(filePath, wanted);

            // -- Compose world = outermost ∘ … ∘ innermost.
            var scale = index.LengthUnitToMm ?? 1;
            var rotation = Matrix3.Identity;
            var origin = new double[3];
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var axis = index.AxisPlacements[chain[i]];
                var local = BuildFrame(axis, vectors);
                // world' = world ∘ local: R = R_w * R_l; T = T_w + R_w * (scale * T_l)
                var t = rotation.Apply(local.Origin[0] * scale, local.Origin[1] * scale, local.Origin[2] * scale);
                origin[0] += t[0];
                origin[1] += t[1];
                origin[2] += t[2];
                rotation = rotation.Multiply(local.Rotation);
            }

            var result = new IfcEntityPlacement
            {
                GlobalId = globalId,
                EntityType = index.TargetType,
                Name = index.TargetName,
                ObjectType = index.TargetObjectType,
                OverallHeight = index.TargetOverallHeight * scale,
                OverallWidth = index.TargetOverallWidth * scale,
                Origin = new Point3D(Round(origin[0]), Round(origin[1]), Round(origin[2])),
                AxisX = ToPoint(rotation.Column(0)),
                AxisY = ToPoint(rotation.Column(1)),
                AxisZ = ToPoint(rotation.Column(2)),
                UnitScaleToMm = scale,
            };
            if (index.LengthUnitToMm is null)
                result.Warning = "No project length unit found in the IFC; assumed millimetres.";
            if (index.TargetPlacementId == 0)
                result.Warning = Append(result.Warning,
                    "Entity has no ObjectPlacement; origin defaults to (0,0,0).");
            return result;
        }
        catch (Exception ex)
        {
            error = "IFC parse failed: " + ErrorText.Flatten(ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ pass 1 ----

    private sealed class FileIndex
    {
        public int TargetId;
        public string TargetType = "";
        public string TargetName = "";
        public string TargetObjectType = "";
        public int TargetPlacementId;
        public double? TargetOverallHeight;
        public double? TargetOverallWidth;
        public readonly Dictionary<int, (int ParentId, int AxisPlacementId)> LocalPlacements =
            new Dictionary<int, (int, int)>();
        public readonly Dictionary<int, (int LocationId, int AxisId, int RefDirectionId)> AxisPlacements =
            new Dictionary<int, (int, int, int)>();
        public double? LengthUnitToMm;
        public readonly Dictionary<int, double> SiLengthUnits = new Dictionary<int, double>();
        public readonly Dictionary<int, string> ConversionLengthUnits = new Dictionary<int, string>();
        public readonly List<int> AssignedUnitIds = new List<int>();
    }

    private static FileIndex ScanFile(string filePath, string globalId)
    {
        var index = new FileIndex();
        var guidToken = "'" + globalId + "'";

        foreach (var record in ReadRecords(filePath))
        {
            var type = record.Type;
            if (type == "IFCLOCALPLACEMENT")
            {
                var args = SplitTopLevel(record.Args);
                if (args.Count >= 2)
                    index.LocalPlacements[record.Id] = (RefId(args[0]), RefId(args[1]));
            }
            else if (type == "IFCAXIS2PLACEMENT3D")
            {
                var args = SplitTopLevel(record.Args);
                index.AxisPlacements[record.Id] = (
                    args.Count > 0 ? RefId(args[0]) : 0,
                    args.Count > 1 ? RefId(args[1]) : 0,
                    args.Count > 2 ? RefId(args[2]) : 0);
            }
            else if (type == "IFCSIUNIT")
            {
                // IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
                var args = SplitTopLevel(record.Args);
                if (args.Count >= 4 && args[1].Equals(".LENGTHUNIT.", StringComparison.OrdinalIgnoreCase))
                    index.SiLengthUnits[record.Id] = 1000.0 * SiPrefixFactor(args[2]);
            }
            else if (type == "IFCCONVERSIONBASEDUNIT")
            {
                // Rare (imperial). IFCCONVERSIONBASEDUNIT(#dim,.LENGTHUNIT.,'INCH',#measure);
                var args = SplitTopLevel(record.Args);
                if (args.Count >= 3 && args[1].Equals(".LENGTHUNIT.", StringComparison.OrdinalIgnoreCase))
                    index.ConversionLengthUnits[record.Id] = DecodeString(args[2]);
            }
            else if (type == "IFCUNITASSIGNMENT")
            {
                // IFCUNITASSIGNMENT((#8,#9,…)) — the set of PROJECT units.
                var args = SplitTopLevel(record.Args);
                if (args.Count > 0)
                    foreach (var token in SplitTopLevel(Unparenthesize(args[0])))
                    {
                        var id = RefId(token);
                        if (id != 0) index.AssignedUnitIds.Add(id);
                    }
            }
            else if (index.TargetId == 0 && record.Args.Length > 24 &&
                     record.Args.StartsWith(guidToken, StringComparison.Ordinal))
            {
                index.TargetId = record.Id;
                index.TargetType = type;
                var args = SplitTopLevel(record.Args);
                // IfcRoot: GlobalId, OwnerHistory, Name, Description; IfcObject adds ObjectType;
                // IfcProduct adds ObjectPlacement, Representation. IfcWindow/IfcDoor append
                // Tag, OverallHeight, OverallWidth (indices 7/8/9).
                if (args.Count > 2) index.TargetName = DecodeString(args[2]);
                if (args.Count > 4) index.TargetObjectType = DecodeString(args[4]);
                if (args.Count > 5) index.TargetPlacementId = RefId(args[5]);
                if ((type == "IFCWINDOW" || type == "IFCDOOR") && args.Count > 9)
                {
                    index.TargetOverallHeight = ParseDouble(args[8]);
                    index.TargetOverallWidth = ParseDouble(args[9]);
                }
            }
        }

        // The project length unit is the LENGTHUNIT referenced from IFCUNITASSIGNMENT. Files
        // can carry ADDITIONAL length-unit records for derived/auxiliary units — field report:
        // Renga IFC4 declares "#8 MILLI METRE" as the project unit and a bare "#18 METRE"
        // later, so last-record-wins scaled every coordinate ×1000. An assignment-referenced
        // unit wins; without one, the first declared record breaks the tie.
        index.LengthUnitToMm = SelectAssignedOrFirst(index.SiLengthUnits, index.AssignedUnitIds, out var si)
            ? si : (double?)null;

        // Imperial length units would need the measure-with-unit factor; refuse silently and
        // let the caller know via the assumed-mm warning path rather than guess wrong.
        if (index.LengthUnitToMm is null &&
            SelectAssignedOrFirst(index.ConversionLengthUnits, index.AssignedUnitIds, out var conversionName))
        {
            var name = conversionName.ToUpperInvariant();
            if (name.Contains("INCH")) index.LengthUnitToMm = 25.4;
            else if (name.Contains("FOOT") || name.Contains("FEET")) index.LengthUnitToMm = 304.8;
        }
        return index;
    }

    /// <summary>
    /// Picks the unit record referenced from IFCUNITASSIGNMENT; when none is referenced,
    /// falls back to the lowest-id (first-declared) record. False when the map is empty.
    /// </summary>
    private static bool SelectAssignedOrFirst<T>(
        Dictionary<int, T> unitsById, List<int> assignedIds, out T value)
    {
        foreach (var id in assignedIds)
            if (unitsById.TryGetValue(id, out value!))
                return true;
        var bestId = int.MaxValue;
        value = default!;
        foreach (var pair in unitsById)
            if (pair.Key < bestId)
            {
                bestId = pair.Key;
                value = pair.Value;
            }
        return bestId != int.MaxValue;
    }

    private static double SiPrefixFactor(string prefix)
    {
        switch (prefix.Trim().ToUpperInvariant())
        {
            case ".MILLI.": return 0.001;
            case ".CENTI.": return 0.01;
            case ".DECI.": return 0.1;
            case "$": return 1;
            case ".KILO.": return 1000;
            default: return 1;
        }
    }

    // ------------------------------------------------------------------ pass 2 ----

    private static Dictionary<int, double[]> FetchVectors(string filePath, HashSet<int> wanted)
    {
        var result = new Dictionary<int, double[]>();
        if (wanted.Count == 0) return result;
        foreach (var record in ReadRecords(filePath))
        {
            if (!wanted.Contains(record.Id)) continue;
            // IFCCARTESIANPOINT((x,y,z)) / IFCDIRECTION((x,y,z))
            var outer = SplitTopLevel(record.Args);
            if (outer.Count == 0) continue;
            var coords = SplitTopLevel(Unparenthesize(outer[0]));
            var values = new double[3];
            for (var i = 0; i < coords.Count && i < 3; i++)
                values[i] = ParseDouble(coords[i]) ?? 0;
            result[record.Id] = values;
            if (result.Count == wanted.Count) break;
        }
        return result;
    }

    // ------------------------------------------------------------- matrix helpers ----

    private sealed class Frame
    {
        public double[] Origin = new double[3];
        public Matrix3 Rotation = Matrix3.Identity;
    }

    private static Frame BuildFrame(
        (int LocationId, int AxisId, int RefDirectionId) axis,
        Dictionary<int, double[]> vectors)
    {
        var frame = new Frame();
        if (axis.LocationId != 0 && vectors.TryGetValue(axis.LocationId, out var location))
            frame.Origin = location;

        var z = axis.AxisId != 0 && vectors.TryGetValue(axis.AxisId, out var az)
            ? Normalize(az) : new[] { 0.0, 0, 1 };
        var xRaw = axis.RefDirectionId != 0 && vectors.TryGetValue(axis.RefDirectionId, out var ax)
            ? ax : new[] { 1.0, 0, 0 };
        // Gram-Schmidt: X orthogonal to Z (IFC allows a non-orthogonal RefDirection).
        var dot = xRaw[0] * z[0] + xRaw[1] * z[1] + xRaw[2] * z[2];
        var x = Normalize(new[] { xRaw[0] - dot * z[0], xRaw[1] - dot * z[1], xRaw[2] - dot * z[2] });
        var y = new[]
        {
            z[1] * x[2] - z[2] * x[1],
            z[2] * x[0] - z[0] * x[2],
            z[0] * x[1] - z[1] * x[0],
        };
        frame.Rotation = new Matrix3(x, y, z);
        return frame;
    }

    private static double[] Normalize(double[] v)
    {
        var length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return length < 1e-12 ? new[] { 0.0, 0, 1 } : new[] { v[0] / length, v[1] / length, v[2] / length };
    }

    /// <summary>Column-major 3×3 rotation: columns are the frame's X/Y/Z axes.</summary>
    private sealed class Matrix3
    {
        private readonly double[][] _columns;
        public static Matrix3 Identity { get; } = new Matrix3(
            new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 });

        public Matrix3(double[] x, double[] y, double[] z) => _columns = new[] { x, y, z };
        public double[] Column(int i) => _columns[i];

        public double[] Apply(double x, double y, double z) => new[]
        {
            _columns[0][0] * x + _columns[1][0] * y + _columns[2][0] * z,
            _columns[0][1] * x + _columns[1][1] * y + _columns[2][1] * z,
            _columns[0][2] * x + _columns[1][2] * y + _columns[2][2] * z,
        };

        public Matrix3 Multiply(Matrix3 local) => new Matrix3(
            Apply(local._columns[0][0], local._columns[0][1], local._columns[0][2]),
            Apply(local._columns[1][0], local._columns[1][1], local._columns[1][2]),
            Apply(local._columns[2][0], local._columns[2][1], local._columns[2][2]));
    }

    // ------------------------------------------------------------- STEP tokenizer ----

    private struct StepRecord
    {
        public int Id;
        public string Type;
        public string Args;
    }

    /// <summary>
    /// Streams "#id=TYPE(args);" records. Handles multi-line records, quoted strings with
    /// escaped quotes ('') and mixed line endings. Non-entity lines (header etc.) are skipped.
    /// </summary>
    private static IEnumerable<StepRecord> ReadRecords(string filePath)
    {
        using var reader = OpenStepText(filePath);
        var buffer = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (buffer.Length == 0)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '#') continue;
                buffer.Append(trimmed);
            }
            else
            {
                buffer.Append(' ').Append(line.Trim());
            }

            if (!EndsRecord(buffer)) continue;
            var text = buffer.ToString();
            buffer.Clear();

            var eq = text.IndexOf('=');
            var open = text.IndexOf('(', eq < 0 ? 0 : eq);
            if (eq <= 1 || open < 0) continue;
            if (!int.TryParse(text.Substring(1, eq - 1).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var id)) continue;
            var close = text.LastIndexOf(')');
            if (close <= open) continue;
            yield return new StepRecord
            {
                Id = id,
                Type = text.Substring(eq + 1, open - eq - 1).Trim().ToUpperInvariant(),
                Args = text.Substring(open + 1, close - open - 1),
            };
        }
    }

    /// <summary>
    /// Opens the STEP text: a plain .ifc, or the .ifc entry inside an .ifczip/.zip archive
    /// (field report: Tekla's reference cache under DataStorage\ref stores .ifczip files, and
    /// that cache may be the only copy of the reference model present on disk). Zip content is
    /// detected by the "PK" signature, not the extension. FileShare is permissive because the
    /// running Tekla may hold the cache file open.
    /// </summary>
    private static StreamReader OpenStepText(string filePath)
    {
        var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var isZip = stream.ReadByte() == 'P' && stream.ReadByte() == 'K';
            stream.Position = 0;
            if (!isZip)
                return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? best = null;
            var bestIsIfc = false;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length == 0) continue;
                var isIfc = entry.Name.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase);
                if (best == null || (isIfc && !bestIsIfc) ||
                    (isIfc == bestIsIfc && entry.Length > best.Length))
                {
                    best = entry;
                    bestIsIfc = isIfc;
                }
            }
            if (best == null)
            {
                archive.Dispose();
                throw new InvalidDataException(
                    "No IFC entry inside archive " + Path.GetFileName(filePath) + ".");
            }
            return new ArchiveEntryReader(archive, best.Open());
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>StreamReader over a zip entry that also disposes the containing archive.</summary>
    private sealed class ArchiveEntryReader : StreamReader
    {
        private readonly ZipArchive _archive;

        public ArchiveEntryReader(ZipArchive archive, Stream entryStream)
            : base(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true)
            => _archive = archive;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _archive.Dispose();
        }
    }

    /// <summary>True when the buffered text ends with ';' outside any quoted string.</summary>
    private static bool EndsRecord(StringBuilder buffer)
    {
        var inString = false;
        for (var i = 0; i < buffer.Length; i++)
        {
            var c = buffer[i];
            if (c == '\'') inString = !inString; // '' (escaped quote) toggles twice — net zero
            else if (c == ';' && !inString && i == buffer.Length - 1) return true;
        }
        return false;
    }

    /// <summary>Splits "a,b,(c,d),'e,f'" into top-level tokens: a | b | (c,d) | 'e,f'.</summary>
    private static List<string> SplitTopLevel(string args)
    {
        var result = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c == '\'') inString = !inString;
            else if (!inString && c == '(') depth++;
            else if (!inString && c == ')') depth--;
            else if (!inString && c == ',' && depth == 0)
            {
                result.Add(args.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        if (start < args.Length) result.Add(args.Substring(start).Trim());
        else if (args.Length > 0 && args[args.Length - 1] == ',') result.Add("");
        return result;
    }

    private static string Unparenthesize(string token)
    {
        token = token.Trim();
        return token.StartsWith("(", StringComparison.Ordinal) && token.EndsWith(")", StringComparison.Ordinal)
            ? token.Substring(1, token.Length - 2)
            : token;
    }

    private static int RefId(string token)
    {
        token = token.Trim();
        return token.StartsWith("#", StringComparison.Ordinal) &&
               int.TryParse(token.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : 0;
    }

    private static double? ParseDouble(string token)
    {
        token = token.Trim();
        if (token.Length == 0 || token == "$" || token == "*") return null;
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : (double?)null;
    }

    /// <summary>Decodes a STEP string literal: quotes, '' → ', \X2\…\X0\ (UTF-16BE) and \X\HH\ escapes.</summary>
    public static string DecodeString(string token)
    {
        token = token.Trim();
        if (token == "$" || token.Length < 2 || token[0] != '\'') return "";
        var raw = token.Substring(1, token.Length - 2).Replace("''", "'");

        if (raw.IndexOf("\\X", StringComparison.OrdinalIgnoreCase) < 0) return raw;
        var result = new StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            if (i + 3 < raw.Length && raw[i] == '\\' &&
                (raw[i + 1] == 'X' || raw[i + 1] == 'x') && raw[i + 2] == '2' && raw[i + 3] == '\\')
            {
                var end = raw.IndexOf("\\X0\\", i + 4, StringComparison.OrdinalIgnoreCase);
                if (end < 0) { result.Append(raw.Substring(i)); break; }
                var hex = raw.Substring(i + 4, end - i - 4);
                for (var h = 0; h + 4 <= hex.Length; h += 4)
                    if (int.TryParse(hex.Substring(h, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out var code))
                        result.Append((char)code);
                i = end + 4;
            }
            else if (i + 3 < raw.Length && raw[i] == '\\' && (raw[i + 1] == 'X' || raw[i + 1] == 'x') &&
                     raw[i + 2] == '\\')
            {
                // \X\HH\ — single ISO 8859-1 byte
                if (i + 5 <= raw.Length && int.TryParse(raw.Substring(i + 3, 2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var code))
                {
                    result.Append((char)code);
                    i += 5;
                }
                else { result.Append(raw[i]); i++; }
            }
            else
            {
                result.Append(raw[i]);
                i++;
            }
        }
        return result.ToString();
    }

    private static Point3D ToPoint(double[] v) =>
        new Point3D(Math.Round(v[0], 6), Math.Round(v[1], 6), Math.Round(v[2], 6));

    private static double Round(double v) => Math.Round(v, 2);

    private static string Append(string? current, string next) =>
        string.IsNullOrWhiteSpace(current) ? next : current + " " + next;
}
