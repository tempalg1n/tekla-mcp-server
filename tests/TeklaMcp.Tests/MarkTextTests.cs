using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>Mark and label text from content elements (issue #32, found live on Tekla 2021).</summary>
public class MarkTextTests
{
    private static MarkContentElement E(string kind, string value, string name = "") =>
        new MarkContentElement { Kind = kind, Name = name, Value = value };

    [Fact]
    public void An_empty_mark_has_no_text()
    {
        Assert.Equal("", MarkText.Compose(new MarkContentElement[0]));
        Assert.Equal("", MarkText.Compose(new[] { E("property", "", "ASSEMBLY_POS"), E("text", "  ") }));
    }

    [Fact]
    public void Elements_are_joined_in_order()
    {
        Assert.Equal("К30(?)", MarkText.Compose(new[] { E("property", "К30(?)", "ASSEMBLY_POS") }));
        Assert.Equal("4\n1:10", MarkText.Compose(new[] { E("text", "4"), E("newline", ""), E("text", "1:10") }));
    }

    [Theory]
    [InlineData("{\n}\n", "")]            // an empty mark, as GetUnformattedString() returns it
    [InlineData("{\nК30(?)}\n", "К30(?)")]
    [InlineData("{ {\nX}\n }", "X")]
    [InlineData("plain", "plain")]
    public void Framing_is_stripped(string unformatted, string expected) =>
        Assert.Equal(expected, MarkText.StripFraming(unformatted));
}
