using System;
using System.IO;
using TeklaMcp.Core.Ifc;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Covers the IFC fallback path of tekla_get_reference_geometry: resolving a nested
/// IFCLOCALPLACEMENT chain to a world origin + axes, unit scaling, IfcWindow overall
/// dimensions and STEP string decoding — the exact workflow from the field report
/// (window 1500×2000 in an architectural overlay, addressed by IFC GlobalId).
/// </summary>
public class IfcPlacementReaderTests : IDisposable
{
    private const string WindowGuid = "0VZkpIecn7$9mG$7iL8u45";
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tekla-mcp-ifc-tests-" + Guid.NewGuid().ToString("N"));

    public IfcPlacementReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteIfc(string name, string data)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path,
            "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION((''),'2;1');\nENDSEC;\nDATA;\n" +
            data +
            "\nENDSEC;\nEND-ISO-10303-21;\n");
        return path;
    }

    /// <summary>Site → building → storey → rotated wall → window, millimetre units.</summary>
    private static string MillimetreModel =>
        "#10=IFCCARTESIANPOINT((0.,0.,0.));\n" +
        "#11=IFCDIRECTION((0.,0.,1.));\n" +
        "#13=IFCAXIS2PLACEMENT3D(#10,$,$);\n" +
        "#14=IFCLOCALPLACEMENT($,#13);\n" +
        "#20=IFCCARTESIANPOINT((1000.,2000.,0.));\n" +
        "#21=IFCAXIS2PLACEMENT3D(#20,$,$);\n" +
        "#22=IFCLOCALPLACEMENT(#14,#21);\n" +
        "#25=IFCCARTESIANPOINT((0.,0.,3000.));\n" +
        "#26=IFCAXIS2PLACEMENT3D(#25,$,$);\n" +
        "#27=IFCLOCALPLACEMENT(#22,#26);\n" +
        "#30=IFCCARTESIANPOINT((5000.,300.,0.));\n" +
        "#31=IFCDIRECTION((0.,1.,0.));\n" +
        "#32=IFCAXIS2PLACEMENT3D(#30,#11,#31);\n" +
        "#33=IFCLOCALPLACEMENT(#27,#32);\n" +
        "#39=IFCCARTESIANPOINT((500.,0.,900.));\n" +
        "#41=IFCAXIS2PLACEMENT3D(#39,$,$);\n" +
        "#40=IFCLOCALPLACEMENT(#33,#41);\n" +
        "#50=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);\n" +
        // Window name uses STEP \X2\ escapes ("Окно"), spans TWO lines mid-record.
        "#100=IFCWINDOW('" + WindowGuid + "',#2,'\\X2\\041E043A043D043E\\X0\\ 1500x2000',$,\n" +
        "'Window',#40,$,'W-01',2000.,1500.);";

    [Fact]
    public void Window_NestedRotatedChain_WorldPlacementAndDims()
    {
        var path = WriteIfc("model-mm.ifc", MillimetreModel);
        var placement = IfcPlacementReader.TryRead(path, WindowGuid, out var error);

        Assert.Null(error);
        Assert.NotNull(placement);
        Assert.Equal("IFCWINDOW", placement!.EntityType);
        Assert.Equal("Окно 1500x2000", placement.Name);
        Assert.Equal("Window", placement.ObjectType);
        Assert.Equal(1500, placement.OverallWidth);
        Assert.Equal(2000, placement.OverallHeight);
        Assert.Equal(1, placement.UnitScaleToMm);
        Assert.Null(placement.Warning);

        // Composed: building(1000,2000,0) + storey(0,0,3000) + wall(5000,300,0)
        //           + R_wall(500,0,900) with the wall frame rotated 90° about Z.
        Assert.Equal(6000, placement.Origin.X, 2);
        Assert.Equal(2800, placement.Origin.Y, 2);
        Assert.Equal(3900, placement.Origin.Z, 2);

        Assert.Equal(0, placement.AxisX.X, 6);
        Assert.Equal(1, placement.AxisX.Y, 6);
        Assert.Equal(-1, placement.AxisY.X, 6);
        Assert.Equal(0, placement.AxisY.Y, 6);
        Assert.Equal(1, placement.AxisZ.Z, 6);
    }

    [Fact]
    public void MetreUnits_ScaledToMillimetres()
    {
        var path = WriteIfc("model-m.ifc",
            "#10=IFCCARTESIANPOINT((0.5,0.,0.9));\n" +
            "#11=IFCAXIS2PLACEMENT3D(#10,$,$);\n" +
            "#12=IFCLOCALPLACEMENT($,#11);\n" +
            "#50=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);\n" +
            "#100=IFCWINDOW('" + WindowGuid + "',#2,'W',$,$,#12,$,$,2.0,1.5);");
        var placement = IfcPlacementReader.TryRead(path, WindowGuid, out var error);

        Assert.Null(error);
        Assert.NotNull(placement);
        Assert.Equal(1000, placement!.UnitScaleToMm);
        Assert.Equal(500, placement.Origin.X, 2);
        Assert.Equal(900, placement.Origin.Z, 2);
        Assert.Equal(1500, placement.OverallWidth);
        Assert.Equal(2000, placement.OverallHeight);
    }

    [Fact]
    public void UnknownGuid_ReturnsNullWithError()
    {
        var path = WriteIfc("model-missing.ifc", MillimetreModel);
        var placement = IfcPlacementReader.TryRead(path, "0000000000000000000000", out var error);
        Assert.Null(placement);
        Assert.Contains("not found", error);
    }

    [Fact]
    public void MissingFile_ReturnsNullWithError()
    {
        var placement = IfcPlacementReader.TryRead(
            Path.Combine(_dir, "nope.ifc"), WindowGuid, out var error);
        Assert.Null(placement);
        Assert.Contains("not found", error);
    }

    [Fact]
    public void NoLengthUnit_AssumesMillimetresWithWarning()
    {
        var path = WriteIfc("model-nounit.ifc",
            "#10=IFCCARTESIANPOINT((100.,0.,0.));\n" +
            "#11=IFCAXIS2PLACEMENT3D(#10,$,$);\n" +
            "#12=IFCLOCALPLACEMENT($,#11);\n" +
            "#100=IFCWINDOW('" + WindowGuid + "',#2,$,$,$,#12,$,$,$,$);");
        var placement = IfcPlacementReader.TryRead(path, WindowGuid, out var error);

        Assert.Null(error);
        Assert.NotNull(placement);
        Assert.Equal(100, placement!.Origin.X, 2);
        Assert.Null(placement.OverallWidth);
        Assert.Contains("assumed millimetres", placement.Warning);
    }

    [Fact]
    public void NonWindowEntity_PlacementWithoutDims()
    {
        var path = WriteIfc("model-column.ifc",
            "#10=IFCCARTESIANPOINT((10.,20.,30.));\n" +
            "#11=IFCAXIS2PLACEMENT3D(#10,$,$);\n" +
            "#12=IFCLOCALPLACEMENT($,#11);\n" +
            "#50=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);\n" +
            "#100=IFCCOLUMN('" + WindowGuid + "',#2,'K-1',$,'Column',#12,$,'T-1');");
        var placement = IfcPlacementReader.TryRead(path, WindowGuid, out var error);

        Assert.Null(error);
        Assert.NotNull(placement);
        Assert.Equal("IFCCOLUMN", placement!.EntityType);
        Assert.Equal("K-1", placement.Name);
        Assert.Null(placement.OverallWidth);
        Assert.Equal(10, placement.Origin.X, 2);
    }

    [Fact]
    public void DecodeString_HandlesEscapes()
    {
        Assert.Equal("Окно", IfcPlacementReader.DecodeString("'\\X2\\041E043A043D043E\\X0\\'"));
        Assert.Equal("it's", IfcPlacementReader.DecodeString("'it''s'"));
        Assert.Equal("", IfcPlacementReader.DecodeString("$"));
    }
}
