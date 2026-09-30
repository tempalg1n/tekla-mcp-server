using System;
using System.Collections.Generic;
using TeklaMcp.Core;
using TeklaMcp.Core.Geometry;
using TeklaMcp.Core.Models;
using TSM = Tekla.Structures.Model;
using TSS = Tekla.Structures.Solid;

namespace TeklaMcp.Tekla;

/// <summary>
/// Live half of <c>tekla_get_part_solid</c> (backlog §5): <c>Part.GetSolid(type)</c> →
/// <c>GetFaceEnumerator</c> → loops → vertices, all inside the GLOBAL work plane because the solid
/// API answers "in the current plane". Vertices are read lazily through <see cref="SolidBuilder"/>:
/// once the caps are reached the remaining faces are only counted. GetSolid() is the most
/// expensive read in the API (~1.5 ms per part measured on 3155), so the tool takes a few GUIDs,
/// never a filter. Compiles against 2021–2026. TODO(windows): run live, check loop order/winding.
/// </summary>
public sealed partial class TeklaModelService
{
    public IReadOnlyList<PartSolidGeometry> GetPartSolids(PartSolidRequest request)
    {
        request ??= new PartSolidRequest();
        var type = TSM.Solid.SolidCreationTypeEnum.NORMAL;
        if (!string.IsNullOrWhiteSpace(request.SolidType) &&
            !Enum.TryParse(request.SolidType!.Trim(), true, out type))
            throw new ArgumentException(
                "Unknown solidType '" + request.SolidType + "'. Use one of: " +
                string.Join(", ", Enum.GetNames(typeof(TSM.Solid.SolidCreationTypeEnum))) + ".");

        var model = GetConnectedModel();
        return InGlobalWorkPlane(model, () =>
        {
            var result = new List<PartSolidGeometry>();
            foreach (var guid in request.Guids ?? new List<string>())
                result.Add(ReadPartSolid(model, guid, type, request));
            return (IReadOnlyList<PartSolidGeometry>)result;
        });
    }

    private static PartSolidGeometry ReadPartSolid(
        TSM.Model model, string guid, TSM.Solid.SolidCreationTypeEnum type, PartSolidRequest request)
    {
        var solid = new PartSolidGeometry { Guid = guid, SolidType = type.ToString() };
        try
        {
            var mo = TrySelectObjectByGuid(model, guid);
            if (!(mo is TSM.Part part))
            {
                solid.Message = mo is null ? "Object not found." : "Not a part (" + mo.GetType().Name + "): it has no solid.";
                return solid;
            }

            solid.PartType = part.GetType().Name;
            var body = part.GetSolid(type);
            if (body is null)
            {
                solid.Message = "Tekla returned no solid for this part.";
                return solid;
            }

            solid.Found = true;
            solid.AabbMin = ToPoint3D(body.MinimumPoint);
            solid.AabbMax = ToPoint3D(body.MaximumPoint);

            var builder = new SolidBuilder(request.MaxFaces, request.MaxPoints);
            var faces = body.GetFaceEnumerator();
            while (faces.MoveNext())
            {
                var face = faces.Current;
                if (face is null) continue;
                int? origin = null;
                try { origin = face.OriginPartId?.ID; } catch { /* informational only */ }
                builder.Add(ToPoint3D(face.Normal), origin, () => FaceLoops(face));
            }
            builder.Fill(solid);
        }
        catch (Exception ex)
        {
            solid.Message = ErrorText.Flatten(ex);
        }
        return solid;
    }

    private static IEnumerable<IEnumerable<Point3D>> FaceLoops(TSS.Face face)
    {
        var loops = face.GetLoopEnumerator();
        while (loops.MoveNext())
        {
            var loop = loops.Current;
            if (loop != null) yield return LoopVertices(loop);
        }
    }

    private static IEnumerable<Point3D> LoopVertices(TSS.Loop loop)
    {
        var vertices = loop.GetVertexEnumerator();
        while (vertices.MoveNext())
        {
            var vertex = vertices.Current;
            if (vertex != null) yield return ToPoint3D(vertex);
        }
    }

}
