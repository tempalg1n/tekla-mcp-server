using System;
using System.Collections.Generic;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.FileExchange;

/// <summary>
/// Everything the part export can write about one object. Backends fill only the members their
/// <see cref="ExportFieldSet"/> asked for; unset members simply come out null.
///
/// This exists instead of widening <see cref="ModelObjectInfo"/>: coordinate system, centre of
/// gravity and contour polygons are export-only concerns and would otherwise land in the output
/// of every query tool.
/// </summary>
public sealed class PartExportRecord
{
    public string Guid { get; set; } = "";
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Class { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Material { get; set; } = "";
    public string? AssemblyPos { get; set; }
    public string? Finish { get; set; }
    public double? LengthMm { get; set; }
    public double? WeightKg { get; set; }

    public double? MinX { get; set; }
    public double? MinY { get; set; }
    public double? MinZ { get; set; }
    public double? MaxX { get; set; }
    public double? MaxY { get; set; }
    public double? MaxZ { get; set; }

    public Point3D? StartPoint { get; set; }
    public Point3D? EndPoint { get; set; }
    public Point3D? Cog { get; set; }

    public Point3D? CsOrigin { get; set; }
    public Point3D? CsAxisX { get; set; }
    public Point3D? CsAxisY { get; set; }
    public Point3D? CsAxisZ { get; set; }

    public List<Point3D>? ContourPoints { get; set; }

    /// <summary>UDA name → value, for the fields named in the request.</summary>
    public Dictionary<string, string> Udas { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reuse one instance across a 65k-object scan instead of allocating per row.</summary>
    public void Reset()
    {
        Guid = "";
        Id = 0;
        Type = "";
        Name = "";
        Class = "";
        Profile = "";
        Material = "";
        AssemblyPos = null;
        Finish = null;
        LengthMm = null;
        WeightKg = null;
        MinX = MinY = MinZ = MaxX = MaxY = MaxZ = null;
        StartPoint = EndPoint = Cog = null;
        CsOrigin = CsAxisX = CsAxisY = CsAxisZ = null;
        ContourPoints = null;
        Udas.Clear();
    }

    /// <summary>Seed the cheap identity/basic members from an existing DTO (used by the mock).</summary>
    public void CopyFrom(ModelObjectInfo info)
    {
        Guid = info.Guid;
        Id = info.Id;
        Type = info.Type;
        Name = info.Name;
        Class = info.Class;
        Profile = info.Profile;
        Material = info.Material;
        AssemblyPos = info.AssemblyPos;
        Finish = info.Finish;
        LengthMm = info.LengthMm;
        WeightKg = info.WeightKg;
        MinX = info.MinX;
        MinY = info.MinY;
        MinZ = info.MinZ;
        MaxX = info.MaxX;
        MaxY = info.MaxY;
        MaxZ = info.MaxZ;
        if (info.StartX.HasValue)
            StartPoint = new Point3D(info.StartX.Value, info.StartY ?? 0, info.StartZ ?? 0);
        if (info.EndX.HasValue)
            EndPoint = new Point3D(info.EndX.Value, info.EndY ?? 0, info.EndZ ?? 0);
    }
}

/// <summary>
/// Turns records into <see cref="DataRow"/>s. Shared by both backends so the Mock and live
/// Tekla produce byte-identical file layouts — the offline matching step is written against one
/// schema, not two.
/// </summary>
public static class ExportRowBuilder
{
    /// <summary>Fixed column set of the reference-object export.</summary>
    public static readonly IReadOnlyList<string> ReferenceFieldNames = new[]
    {
        "id", "externalGuid", "entity", "name", "description", "objectType",
        "referenceModelTitle", "solidAabb", "aabbSource",
        "placementOrigin", "placementXAxis", "placementYAxis", "placementZAxis", "placementSource",
    };

    /// <summary>Build (or rebuild, into <paramref name="row"/>) one part row.</summary>
    public static void BuildPartRow(ExportFieldSet fieldSet, PartExportRecord record, DataRow row)
    {
        row.Clear();
        foreach (var field in fieldSet.Fields)
        {
            switch (field.Kind)
            {
                case ExportFieldKind.Guid: row.Add(field.Name, RowValue.Text(record.Guid)); break;
                case ExportFieldKind.Id: row.Add(field.Name, RowValue.Integer(record.Id)); break;
                case ExportFieldKind.Type: row.Add(field.Name, RowValue.Text(record.Type)); break;
                case ExportFieldKind.Name: row.Add(field.Name, RowValue.Text(record.Name)); break;
                case ExportFieldKind.Class: row.Add(field.Name, RowValue.Text(record.Class)); break;
                case ExportFieldKind.Profile: row.Add(field.Name, RowValue.Text(record.Profile)); break;
                case ExportFieldKind.Material: row.Add(field.Name, RowValue.Text(record.Material)); break;
                case ExportFieldKind.AssemblyPos: row.Add(field.Name, RowValue.Text(record.AssemblyPos)); break;
                case ExportFieldKind.Finish: row.Add(field.Name, RowValue.Text(record.Finish)); break;
                case ExportFieldKind.LengthMm: row.Add(field.Name, RowValue.Number(record.LengthMm, 1)); break;
                case ExportFieldKind.WeightKg: row.Add(field.Name, RowValue.Number(record.WeightKg, 2)); break;
                case ExportFieldKind.SolidAabb:
                    row.Add(field.Name, RowValue.Aabb(
                        record.MinX, record.MinY, record.MinZ, record.MaxX, record.MaxY, record.MaxZ));
                    break;
                case ExportFieldKind.StartPoint:
                    row.Add(field.Name, RowValue.Point(record.StartPoint, "start"));
                    break;
                case ExportFieldKind.EndPoint:
                    row.Add(field.Name, RowValue.Point(record.EndPoint, "end"));
                    break;
                case ExportFieldKind.Cog:
                    row.Add(field.Name, RowValue.Point(record.Cog, "cog"));
                    break;
                case ExportFieldKind.CoordSystem:
                    row.Add(field.Name, RowValue.CoordSystem(
                        record.CsOrigin, record.CsAxisX, record.CsAxisY, record.CsAxisZ));
                    break;
                case ExportFieldKind.ContourPoints:
                    row.Add(field.Name, RowValue.Points(record.ContourPoints));
                    break;
                case ExportFieldKind.Uda:
                    row.Add(field.Name, RowValue.Text(
                        record.Udas.TryGetValue(field.UdaName, out var value) ? value : null));
                    break;
            }
        }
    }

    /// <summary>Build (or rebuild, into <paramref name="row"/>) one reference-object row.</summary>
    public static void BuildReferenceRow(ReferenceGeometryInfo info, DataRow row)
    {
        row.Clear();
        row.Add("id", RowValue.Integer(info.Id));
        row.Add("externalGuid", RowValue.Text(info.ExternalGuid));
        row.Add("entity", RowValue.Text(info.Entity));
        row.Add("name", RowValue.Text(info.Name));
        row.Add("description", RowValue.Text(info.Description));
        row.Add("objectType", RowValue.Text(info.ObjectType));
        row.Add("referenceModelTitle", RowValue.Text(info.ReferenceModelTitle));
        row.Add("solidAabb", RowValue.Aabb(info.MinX, info.MinY, info.MinZ, info.MaxX, info.MaxY, info.MaxZ));
        row.Add("aabbSource", RowValue.Text(info.AabbSource));
        row.Add("placementOrigin", RowValue.Point(info.PlacementOrigin, "placementOrigin"));
        row.Add("placementXAxis", RowValue.Point(info.PlacementXAxis, "placementXAxis", 6));
        row.Add("placementYAxis", RowValue.Point(info.PlacementYAxis, "placementYAxis", 6));
        row.Add("placementZAxis", RowValue.Point(info.PlacementZAxis, "placementZAxis", 6));
        row.Add("placementSource", RowValue.Text(info.PlacementSource));
    }

    /// <summary>
    /// A row of the right SHAPE but with no values — the CSV writer takes its header from this,
    /// so an export that matched nothing still produces a headed file.
    /// </summary>
    public static DataRow PartPrototype(ExportFieldSet fieldSet)
    {
        var row = new DataRow();
        BuildPartRow(fieldSet, new PartExportRecord(), row);
        return row;
    }

    /// <summary>Header prototype for the reference-object export.</summary>
    public static DataRow ReferencePrototype()
    {
        var row = new DataRow();
        BuildReferenceRow(new ReferenceGeometryInfo(), row);
        return row;
    }
}
