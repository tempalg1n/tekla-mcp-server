using System.Collections.Generic;
using System.IO;
using System.Linq;
using TeklaMcp.Core.FileExchange;
using TeklaMcp.Core.Models;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Field parsing plus the hand-rolled JSONL/CSV writers and reader. These formats are the
/// contract between the model and the offline matching step, so their edge cases (quoting,
/// escapes, ';' delimiters, nested values) are pinned here rather than discovered on a
/// 65k-row file.
/// </summary>
public class DataFileFormatTests
{
    // ------------------------------------------------------------------- fields ----

    [Fact]
    public void EmptyFieldListFallsBackToTheDefaultSet()
    {
        Assert.True(ExportFields.TryParse(null, out var set, out var error), error);
        Assert.Equal(ExportFields.DefaultFieldNames, set.Names);
        Assert.False(set.NeedsSolid);
        Assert.True(set.NeedsReportProperties);
    }

    [Fact]
    public void GeometryFieldsRaiseTheMatchingCostFlags()
    {
        Assert.True(ExportFields.TryParse(
            new[] { "guid", "solidAabb", "coordSystem", "cog", "contourPoints", "startPoint", "uda:USER_FIELD_1" },
            out var set, out var error), error);

        Assert.True(set.NeedsSolid);
        Assert.True(set.NeedsCoordSystem);
        Assert.True(set.NeedsCog);
        Assert.True(set.NeedsContour);
        Assert.True(set.NeedsEndPoints);
        Assert.Equal(new[] { "USER_FIELD_1" }, set.UdaNames);
    }

    [Fact]
    public void FieldNamesAreCanonicalizedAndDeduplicated()
    {
        Assert.True(ExportFields.TryParse(new[] { "GUID", "guid", "SolidAABB" }, out var set, out _));
        Assert.Equal(new[] { "guid", "solidAabb" }, set.Names);
    }

    [Fact]
    public void UdaWildcardIsRejectedWithAWayForward()
    {
        Assert.False(ExportFields.TryParse(new[] { "uda:*" }, out _, out var error));
        Assert.Contains("tekla_discover_udas", error);
    }

    [Fact]
    public void UnknownFieldIsRejectedAndListsTheAlternatives()
    {
        Assert.False(ExportFields.TryParse(new[] { "boundingBox" }, out _, out var error));
        Assert.Contains("solidAabb", error);
    }

    // ------------------------------------------------------------------ writers ----

    [Fact]
    public void JsonlWriterEmitsNestedGeometryAndNullsForMissingValues()
    {
        Assert.True(ExportFields.TryParse(
            new[] { "guid", "id", "weightKg", "solidAabb", "startPoint", "uda:USER_FIELD_1" },
            out var set, out _));

        var record = new PartExportRecord
        {
            Guid = "abc",
            Id = 42,
            MinX = 0,
            MinY = 1,
            MinZ = 2,
            MaxX = 10,
            MaxY = 11,
            MaxZ = 12,
            StartPoint = new Point3D(1.2345, 2, 3),
        };

        var line = WriteSingleRow(set, record, DataFileWriters.Jsonl, writeHeader: true).Trim();

        Assert.Contains("\"guid\":\"abc\"", line);
        Assert.Contains("\"id\":42", line);
        Assert.Contains("\"weightKg\":null", line);
        Assert.Contains("\"solidAabb\":{\"minX\":0,\"minY\":1,\"minZ\":2,\"maxX\":10,\"maxY\":11,\"maxZ\":12}", line);
        Assert.Contains("\"startPoint\":{\"x\":1.23,\"y\":2,\"z\":3}", line);
        Assert.Contains("\"USER_FIELD_1\":null", line);
    }

    [Fact]
    public void JsonlWriterEmitsNullForAnAbsentAabbRatherThanZeroes()
    {
        Assert.True(ExportFields.TryParse(new[] { "guid", "solidAabb" }, out var set, out _));
        var line = WriteSingleRow(set, new PartExportRecord { Guid = "abc" }, DataFileWriters.Jsonl, true).Trim();
        Assert.Contains("\"solidAabb\":null", line);
    }

    [Fact]
    public void CsvWriterExpandsCompositeCellsIntoColumns()
    {
        Assert.True(ExportFields.TryParse(
            new[] { "guid", "solidAabb", "startPoint", "coordSystem", "contourPoints" }, out var set, out _));

        var record = new PartExportRecord
        {
            Guid = "abc",
            MinX = 0, MinY = 0, MinZ = 0, MaxX = 1, MaxY = 2, MaxZ = 3,
            StartPoint = new Point3D(1, 2, 3),
            CsOrigin = new Point3D(4, 5, 6),
            CsAxisX = new Point3D(1, 0, 0),
            CsAxisY = new Point3D(0, 1, 0),
            CsAxisZ = new Point3D(0, 0, 1),
            ContourPoints = new List<Point3D> { new Point3D(0, 0, 0), new Point3D(1, 0, 0) },
        };

        var text = WriteSingleRow(set, record, DataFileWriters.Csv, writeHeader: true);
        var lines = text.Split('\n').Where(l => l.Length > 0).ToArray();

        Assert.Equal(
            "guid,minX,minY,minZ,maxX,maxY,maxZ,startX,startY,startZ," +
            "csOriginX,csOriginY,csOriginZ,csAxisXX,csAxisXY,csAxisXZ," +
            "csAxisYX,csAxisYY,csAxisYZ,csAxisZX,csAxisZY,csAxisZZ,contourPoints",
            lines[0].TrimEnd('\r'));
        // A polygon has no fixed column count, so it rides in one packed column. ';' is not the
        // delimiter, so no quoting is needed.
        Assert.EndsWith(",0 0 0;1 0 0", lines[1].TrimEnd('\r'));
    }

    [Fact]
    public void CsvHeaderIsWrittenEvenWhenNoRowsMatch()
    {
        Assert.True(ExportFields.TryParse(new[] { "guid", "solidAabb" }, out var set, out _));
        using var writer = new StringWriter();
        using (DataFileWriters.Create(writer, DataFileWriters.Csv, ExportRowBuilder.PartPrototype(set), true))
        {
            // Deliberately no rows — a headed but empty file is still a usable answer.
        }
        Assert.Equal("guid,minX,minY,minZ,maxX,maxY,maxZ", writer.ToString().Trim());
    }

    // ------------------------------------------------------------------- reader ----

    [Fact]
    public void JsonlReaderSplitsGuidFromValuesAndKeepsNumberLiterals()
    {
        var rows = ReadAll(
            "{\"guid\":\"g1\",\"USER_FIELD_1\":\"Согласованно\",\"COUNT\":12,\"RATIO\":1.5}",
            DataFileWriters.Jsonl);

        var row = Assert.Single(rows);
        Assert.Null(row.Error);
        Assert.Equal("g1", row.Guid);
        Assert.Equal("Согласованно", Value(row, "USER_FIELD_1"));
        Assert.Equal("12", Value(row, "COUNT"));
        Assert.Equal("1.5", Value(row, "RATIO"));
    }

    [Fact]
    public void JsonlReaderSkipsNullsAndNestedValuesButKeepsTheRow()
    {
        // An export row carries geometry the import does not care about — it must not choke.
        var rows = ReadAll(
            "{\"guid\":\"g1\",\"solidAabb\":{\"minX\":1,\"maxX\":2},\"contourPoints\":[{\"x\":0}]," +
            "\"EMPTY\":null,\"USER_FIELD_1\":\"ok\"}",
            DataFileWriters.Jsonl);

        var row = Assert.Single(rows);
        Assert.Null(row.Error);
        Assert.Equal("ok", Value(row, "USER_FIELD_1"));
        Assert.Null(Value(row, "EMPTY"));
        Assert.Null(Value(row, "solidAabb"));
    }

    [Fact]
    public void JsonlReaderDecodesEscapes()
    {
        var rows = ReadAll("{\"guid\":\"g1\",\"NOTE\":\"a\\\"b\\\\c\\u0041\\n\"}", DataFileWriters.Jsonl);
        Assert.Equal("a\"b\\cA\n", Value(Assert.Single(rows), "NOTE"));
    }

    [Fact]
    public void JsonlReaderReportsTheBadLineInsteadOfDroppingIt()
    {
        var rows = ReadAll("{\"guid\":\"g1\"}\nnot json\n{\"guid\":\"g3\"}", DataFileWriters.Jsonl);
        Assert.Equal(3, rows.Count);
        Assert.NotNull(rows[1].Error);
        Assert.Equal(2, rows[1].LineNumber);
        Assert.Null(rows[2].Error);
    }

    [Fact]
    public void JsonlRowWithoutAGuidIsFlagged()
    {
        var row = Assert.Single(ReadAll("{\"USER_FIELD_1\":\"x\"}", DataFileWriters.Jsonl));
        Assert.Contains("guid", row.Error);
    }

    [Fact]
    public void CsvReaderHandlesQuotingCommasAndEmbeddedNewlines()
    {
        var rows = ReadAll(
            "guid,NOTE\ng1,\"has, comma\"\ng2,\"line1\nline2\"\ng3,\"say \"\"hi\"\"\"\n",
            DataFileWriters.Csv);

        Assert.Equal(3, rows.Count);
        Assert.Equal("has, comma", Value(rows[0], "NOTE"));
        Assert.Equal("line1\nline2", Value(rows[1], "NOTE"));
        Assert.Equal("say \"hi\"", Value(rows[2], "NOTE"));
    }

    [Fact]
    public void CsvReaderDetectsASemicolonDelimiter()
    {
        // What Excel writes in a Russian locale.
        var rows = ReadAll("guid;USER_FIELD_1\ng1;Согласованно\n", DataFileWriters.Csv);
        Assert.Equal("Согласованно", Value(Assert.Single(rows), "USER_FIELD_1"));
    }

    [Fact]
    public void CsvRowWithAWrongFieldCountIsFlagged()
    {
        var rows = ReadAll("guid,A,B\ng1,only-two\n", DataFileWriters.Csv);
        Assert.Contains("header has 3", Assert.Single(rows).Error);
    }

    [Fact]
    public void FormatIsInferredFromTheExtensionWhenNotGiven()
    {
        Assert.True(DataFileReader.TryResolveFormat(null, @"C:\x\parts.csv", out var csv, out _));
        Assert.Equal(DataFileWriters.Csv, csv);
        Assert.True(DataFileReader.TryResolveFormat(null, @"C:\x\parts.jsonl", out var jsonl, out _));
        Assert.Equal(DataFileWriters.Jsonl, jsonl);
        Assert.False(DataFileWriters.TryNormalizeFormat("xml", out _, out var error));
        Assert.Contains("jsonl", error);
    }

    // ------------------------------------------------------------------ helpers ----

    private static string WriteSingleRow(
        ExportFieldSet set, PartExportRecord record, string format, bool writeHeader)
    {
        using var writer = new StringWriter { NewLine = "\n" };
        var row = new DataRow();
        using (var dataWriter = DataFileWriters.Create(
                   writer, format, ExportRowBuilder.PartPrototype(set), writeHeader))
        {
            ExportRowBuilder.BuildPartRow(set, record, row);
            dataWriter.Write(row);
        }
        return writer.ToString();
    }

    private static List<DataFileRow> ReadAll(string text, string format)
    {
        var reader = new DataFileReader(format, "guid");
        using var stringReader = new StringReader(text);
        return reader.Read(stringReader).ToList();
    }

    private static string? Value(DataFileRow row, string name) =>
        row.Values.Where(v => v.Key == name).Select(v => v.Value).FirstOrDefault();
}
