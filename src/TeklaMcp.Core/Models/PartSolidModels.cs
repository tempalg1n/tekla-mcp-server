using System.Collections.Generic;

namespace TeklaMcp.Core.Models;

/// <summary>Which part solids to read and how much of them.</summary>
public sealed class PartSolidRequest
{
    /// <summary>Tekla's SolidCreationTypeEnum names (identical in 2021–2026).</summary>
    public static readonly IReadOnlyList<string> SolidTypes = new[]
    {
        "NORMAL", "RAW", "FITTED", "HIGH_ACCURACY", "PLANECUTTED",
        "NORMAL_WITHOUT_EDGECHAMFERS", "NORMAL_WITHOUT_WELDPREPS",
    };

    /// <summary>Part GUIDs (a few — every solid is a GetSolid() call, the most expensive read).</summary>
    public List<string> Guids { get; set; } = new List<string>();

    /// <summary>
    /// Solid creation type: NORMAL (default: cuts, fittings, chamfers applied), RAW (the profile
    /// swept along the part, no cuts), FITTED, HIGH_ACCURACY, PLANECUTTED,
    /// NORMAL_WITHOUT_EDGECHAMFERS, NORMAL_WITHOUT_WELDPREPS.
    /// </summary>
    public string? SolidType { get; set; }

    /// <summary>Faces returned per part; further faces are only counted.</summary>
    public int MaxFaces { get; set; } = 200;

    /// <summary>Vertices returned per part (all loops of all faces together).</summary>
    public int MaxPoints { get; set; } = 1000;
}

/// <summary>The boundary representation of one part's solid, in GLOBAL mm, capped.</summary>
public sealed class PartSolidGeometry
{
    public string Guid { get; set; } = "";
    public bool Found { get; set; }
    public string? PartType { get; set; }
    public string? SolidType { get; set; }

    /// <summary>Axis-aligned box around the solid. A box — never the shape.</summary>
    public Point3D? AabbMin { get; set; }
    public Point3D? AabbMax { get; set; }

    /// <summary>All faces of the solid, including the ones not returned.</summary>
    public int FaceCount { get; set; }
    public int FacesReturned { get; set; }
    public int PointsReturned { get; set; }

    /// <summary>
    /// True when faces were left out (see <see cref="TruncatedReason"/>). A truncated face list is
    /// NOT the shape: missing faces are missing, and faces are never cut in half.
    /// </summary>
    public bool Truncated { get; set; }
    public string? TruncatedReason { get; set; }

    public List<SolidFace> Faces { get; set; } = new List<SolidFace>();
    public string? Message { get; set; }
}

/// <summary>One planar face: its outward normal and its boundary loops.</summary>
public sealed class SolidFace
{
    public Point3D Normal { get; set; } = new Point3D();

    /// <summary>Id of the part that produced the face — a cutting part for faces made by a cut.</summary>
    public int? OriginPartId { get; set; }

    /// <summary>
    /// Index into <see cref="Loops"/> of the outer boundary, derived geometrically (the loop with
    /// the largest area); the others are holes. Tekla does not document its loop order.
    /// </summary>
    public int OuterLoopIndex { get; set; }

    public List<SolidLoop> Loops { get; set; } = new List<SolidLoop>();
}

public sealed class SolidLoop
{
    public List<Point3D> Vertices { get; set; } = new List<Point3D>();

    /// <summary>Enclosed area in mm², measured in the face plane.</summary>
    public double Area { get; set; }
}
