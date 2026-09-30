using System;
using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Mock;

/// <summary>
/// Mock half of <c>tekla_get_part_solid</c>: every mock part is a box over its solid AABB, fed
/// through the same <see cref="SolidBuilder"/> caps as a live solid.
/// </summary>
public sealed partial class MockTeklaModelService
{
    public IReadOnlyList<PartSolidGeometry> GetPartSolids(PartSolidRequest request)
    {
        request ??= new PartSolidRequest();
        var result = new List<PartSolidGeometry>();
        foreach (var guid in request.Guids ?? new List<string>())
        {
            var solid = new PartSolidGeometry { Guid = guid, SolidType = (request.SolidType ?? "NORMAL").ToUpperInvariant() };
            result.Add(solid);

            var part = GetObjectByGuid(guid);
            if (part is null || !(part.MinX.HasValue && part.MaxX.HasValue && part.MinY.HasValue &&
                                  part.MaxY.HasValue && part.MinZ.HasValue && part.MaxZ.HasValue))
            {
                solid.Message = part is null ? "Object not found." : "The object has no solid (not a part).";
                continue;
            }

            solid.Found = true;
            solid.PartType = part.Type;
            solid.AabbMin = new Point3D(part.MinX.Value, part.MinY.Value, part.MinZ.Value);
            solid.AabbMax = new Point3D(part.MaxX.Value, part.MaxY.Value, part.MaxZ.Value);

            var builder = new SolidBuilder(request.MaxFaces, request.MaxPoints);
            foreach (var (normal, loop) in SolidMath.BoxFaces(solid.AabbMin, solid.AabbMax))
                builder.Add(normal, part.Id, () => new[] { loop });
            builder.Fill(solid);
            solid.Message = "Mock solid: a box over the part's bounding box.";
        }
        return result;
    }
}
