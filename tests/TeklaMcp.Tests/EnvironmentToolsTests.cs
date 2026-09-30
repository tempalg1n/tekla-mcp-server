using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Advanced options and catalogs (backlog §5). The paging is shared with the live backend, so it
/// is locked down here: disjoint pages by catalog offset, coverage on filtered pages, no mapping
/// of items that never make it into a page.
/// </summary>
public class EnvironmentToolsTests
{
    private static readonly string[] Names = Enumerable.Range(1, 25).Select(i => "IPE" + (i * 20)).ToArray();

    private static CatalogListResult PageOf(CatalogQuery query, List<string>? mapped = null) =>
        CatalogListing.Page("profiles", Names, n => new[] { n },
            n => { mapped?.Add(n); return new CatalogItemInfo { Name = n }; }, query, "Test");

    [Fact]
    public void Pages_are_disjoint_and_cover_the_catalog()
    {
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = PageOf(new CatalogQuery { Limit = 10, Cursor = cursor });
            seen.AddRange(page.Items.Select(i => i.Name));
            cursor = page.NextCursor;
            pages++;
        } while (cursor != null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(Names, seen);
    }

    [Fact]
    public void Full_page_says_truncated_and_where_to_continue()
    {
        var page = PageOf(new CatalogQuery { Limit = 10 });
        Assert.True(page.Truncated);
        Assert.Equal("10", page.NextCursor);
        Assert.Equal(10, page.Scanned);
    }

    [Fact]
    public void Exact_fit_is_not_truncated()
    {
        var page = PageOf(new CatalogQuery { Limit = 25 });
        Assert.False(page.Truncated);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void Filtered_page_reports_what_it_scanned()
    {
        var page = PageOf(new CatalogQuery { NameContains = "ipe10" });
        Assert.Equal(new[] { "IPE100" }, page.Items.Select(i => i.Name));
        Assert.Equal(25, page.Scanned);
    }

    [Fact]
    public void Empty_filtered_page_says_it_searched_everything()
    {
        var page = PageOf(new CatalogQuery { NameContains = "HEA" });
        Assert.Empty(page.Items);
        Assert.Contains("searched 25 entries", page.Message);
    }

    [Fact]
    public void Only_returned_items_are_mapped()
    {
        // Live, mapping reads properties over remoting; skipped and filtered items must not pay.
        var mapped = new List<string>();
        PageOf(new CatalogQuery { NameContains = "IPE2", Cursor = "5" }, mapped);
        Assert.Equal(new[] { "IPE200", "IPE220", "IPE240", "IPE260", "IPE280" }, mapped);
    }

    [Fact]
    public void Bad_cursor_is_reported()
    {
        var page = PageOf(new CatalogQuery { Cursor = "abc" });
        Assert.Empty(page.Items);
        Assert.False(string.IsNullOrEmpty(page.Message));
    }

    [Theory]
    [InlineData("profiles", "profiles")]
    [InlineData("Library-Profiles", "profiles")]
    [InlineData("udas", "uda_definitions")]
    [InlineData("Components", "components")]
    public void Kind_aliases(string raw, string expected)
    {
        Assert.Equal(expected, CatalogListing.NormalizeKind(raw, out _));
    }

    [Fact]
    public void Unknown_kind_lists_the_valid_ones()
    {
        Assert.Null(CatalogListing.NormalizeKind("bolts", out var error));
        Assert.Contains("uda_definitions", error);
    }

    [Fact]
    public void Mock_uda_definitions_filter_by_object_type()
    {
        var result = new MockTeklaModelService().ListCatalog(
            new CatalogQuery { Kind = "uda_definitions", ObjectType = "BOLT", Details = true });
        var item = Assert.Single(result.Items);
        Assert.Equal("BOLT_COMMENT", item.Name);
        Assert.Equal("BOLT", item.Properties["objectTypes"]);
    }

    [Fact]
    public void Mock_components_match_on_the_ui_name_too()
    {
        var result = new MockTeklaModelService().ListCatalog(
            new CatalogQuery { Kind = "components", NameContains = "(144)" });
        Assert.Equal(144, Assert.Single(result.Items).Number);
    }

    [Fact]
    public void Mock_details_are_opt_in()
    {
        var mock = new MockTeklaModelService();
        Assert.Empty(mock.ListCatalog(new CatalogQuery { Kind = "materials" }).Items[0].Properties);
        Assert.NotEmpty(mock.ListCatalog(new CatalogQuery { Kind = "materials", Details = true }).Items[0].Properties);
    }

    [Fact]
    public void Mock_advanced_options_split_paths_and_report_unknowns()
    {
        var options = new MockTeklaModelService().GetAdvancedOptions(
            new[] { "XS_MACRO_DIRECTORY", "XS_DOES_NOT_EXIST" }, asPaths: true);

        Assert.True(options[0].Found);
        Assert.Equal(2, options[0].Paths!.Count);
        Assert.False(options[1].Found);
        Assert.Null(options[1].Paths);
    }
}
