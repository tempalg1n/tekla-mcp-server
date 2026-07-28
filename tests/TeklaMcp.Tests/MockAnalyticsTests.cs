using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Covers the streaming analytics surface added for the UDA-approval workflow (issue: weight
/// per approval status): AggregateBy with uda-grouping and cursor paging, DiscoverUdas, and
/// the truncation-honest FindAttributesByValue. The mock seeds USER_FIELD_1 with
/// Approved/Rejected/Partially approved on columns/braces/main beams.
/// </summary>
public class MockAnalyticsTests
{
    // Mock fixture: 4 columns (Approved), 6 main beams (Partially approved),
    // 4 braces (Rejected), 8 secondary beams + 4 base plates unset; 16 bolts are not parts.
    private const int Parts = 26;
    private const int AllObjects = 42;

    [Fact]
    public void Aggregate_by_uda_groups_approval_statuses_with_weights()
    {
        var model = new MockTeklaModelService();
        var result = model.AggregateBy(new ObjectQuery(), "uda:USER_FIELD_1");

        Assert.Equal("uda:USER_FIELD_1", result.GroupBy);
        Assert.Equal(Parts, result.ScannedObjects);
        Assert.Equal(Parts, result.MatchedObjects);
        Assert.False(result.Truncated);
        Assert.Null(result.NextCursor);

        var byKey = result.Rows.ToDictionary(r => r.Key);
        Assert.Equal(4, byKey["Approved"].Count);
        Assert.Equal(6, byKey["Partially approved"].Count);
        Assert.Equal(4, byKey["Rejected"].Count);
        Assert.Equal(12, byKey["(none)"].Count); // unset field is a first-class group

        // Column weight: 4 × 4.0 m × 88.3 kg/m.
        Assert.Equal(4 * 4.0 * 88.3, byKey["Approved"].TotalWeightKg, 1);
        // Every matched object carries a weight in the mock.
        Assert.Equal(result.MatchedObjects, result.ObjectsWithWeight);
        // Groups add up to the reported totals.
        Assert.Equal(result.MatchedObjects, result.Rows.Sum(r => r.Count));
        Assert.Equal(result.TotalWeightKg, result.Rows.Sum(r => r.TotalWeightKg), 1);
    }

    [Fact]
    public void Aggregate_cursor_pages_are_disjoint_and_sum_to_the_full_scan()
    {
        var model = new MockTeklaModelService();
        var full = model.AggregateBy(new ObjectQuery(), "uda:USER_FIELD_1");

        var merged = new Dictionary<string, int>();
        var mergedWeight = 0.0;
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = model.AggregateBy(
                new ObjectQuery(), "uda:USER_FIELD_1", cursor: cursor, maxObjects: 10);
            pages++;
            Assert.True(page.ScannedObjects <= 10);
            foreach (var row in page.Rows)
            {
                merged[row.Key] = merged.TryGetValue(row.Key, out var c) ? c + row.Count : row.Count;
                mergedWeight += row.TotalWeightKg;
            }
            cursor = page.NextCursor;
            if (page.Truncated) Assert.NotNull(cursor);
        } while (cursor != null && pages < 20);

        Assert.Equal(3, pages); // 26 parts / 10 per page
        Assert.Equal(full.Rows.Sum(r => r.Count), merged.Values.Sum());
        Assert.Equal(full.TotalWeightKg, mergedWeight, 1);
        foreach (var row in full.Rows)
            Assert.Equal(row.Count, merged[row.Key]);
    }

    [Fact]
    public void Aggregate_without_group_key_sums_into_a_single_bucket()
    {
        var model = new MockTeklaModelService();
        var result = model.AggregateBy(new ObjectQuery(), null);

        var row = Assert.Single(result.Rows);
        Assert.Equal("(all)", row.Key);
        Assert.Equal(Parts, row.Count);
        Assert.Equal(result.TotalWeightKg, row.TotalWeightKg, 1);
    }

    [Fact]
    public void Aggregate_parts_only_false_widens_the_scan_to_bolts()
    {
        var model = new MockTeklaModelService();
        var parts = model.AggregateBy(new ObjectQuery(), "type");
        var everything = model.AggregateBy(new ObjectQuery(), "type", partsOnly: false);

        Assert.Equal(Parts, parts.MatchedObjects);
        Assert.Equal(AllObjects, everything.MatchedObjects);
        Assert.Contains(everything.Rows, r => r.Key == "Bolt");
        Assert.DoesNotContain(parts.Rows, r => r.Key == "Bolt");
    }

    [Fact]
    public void Aggregate_honors_filters_and_uda_filter()
    {
        var model = new MockTeklaModelService();
        var approvedOnly = model.AggregateBy(
            new ObjectQuery { UdaName = "USER_FIELD_1", UdaEquals = "Approved" }, "profile");

        Assert.Equal(4, approvedOnly.MatchedObjects);
        var row = Assert.Single(approvedOnly.Rows);
        Assert.Equal("HEA300", row.Key);
    }

    [Fact]
    public void Aggregate_reports_unknown_group_key_instead_of_throwing()
    {
        var model = new MockTeklaModelService();
        var result = model.AggregateBy(new ObjectQuery(), "florb");

        Assert.Empty(result.Rows);
        Assert.Contains("Unsupported groupBy", result.Message);
    }

    [Fact]
    public void Aggregate_rolls_excess_groups_into_other_row()
    {
        var model = new MockTeklaModelService();
        var result = model.AggregateBy(new ObjectQuery(), "profile", limit: 2);

        Assert.Equal(3, result.Rows.Count);
        var other = result.Rows[2];
        Assert.StartsWith("(other:", other.Key);
        Assert.Equal(result.MatchedObjects, result.Rows.Sum(r => r.Count));
        Assert.Contains("rolled into", result.Message);
    }

    [Fact]
    public void Aggregate_invalid_cursor_is_reported_not_scanned()
    {
        var model = new MockTeklaModelService();
        var result = model.AggregateBy(new ObjectQuery(), "type", cursor: "not-a-cursor");

        Assert.Equal(0, result.ScannedObjects);
        Assert.Contains("Invalid cursor", result.Message);
    }

    [Fact]
    public void Discover_udas_reports_fields_fill_and_top_values()
    {
        var model = new MockTeklaModelService();
        var result = model.DiscoverUdas(new ObjectQuery());

        Assert.Equal(Parts, result.SampledObjects);
        Assert.False(result.Truncated);

        var userField = result.Fields.Single(f => f.Name == "USER_FIELD_1");
        Assert.Equal(14, userField.ObjectCount); // 4 + 6 + 4 filled; unset objects don't count
        Assert.Equal(3, userField.DistinctValueCount);
        Assert.Equal("Partially approved", userField.TopValues[0].Value);
        Assert.Equal(6, userField.TopValues[0].Count);

        // Fields seeded on every object are fully filled.
        Assert.Equal(Parts, result.Fields.Single(f => f.Name == "MCP_TAG").ObjectCount);
    }

    [Fact]
    public void Discover_udas_sample_cap_marks_result_truncated()
    {
        var model = new MockTeklaModelService();
        var result = model.DiscoverUdas(new ObjectQuery(), sampleSize: 5);

        Assert.Equal(5, result.SampledObjects);
        Assert.True(result.Truncated);
        Assert.Contains("SAMPLE ONLY", result.Message);
    }

    [Fact]
    public void Find_attributes_by_value_locates_user_field_and_reports_scope()
    {
        var model = new MockTeklaModelService();
        var result = model.FindAttributesByValue("Approved", exactMatch: true);

        var match = result.Matches.Single(m => m.AttributeName == "USER_FIELD_1");
        Assert.Equal(4, match.MatchCount);
        Assert.Equal(Parts, result.ScannedObjects);
        Assert.True(result.CandidatesTried > 0);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Find_attributes_by_value_empty_result_is_explicit_about_coverage()
    {
        var model = new MockTeklaModelService();

        var complete = model.FindAttributesByValue("no-such-value");
        Assert.Empty(complete.Matches);
        Assert.False(complete.Truncated);
        Assert.Contains("absent", complete.Message);

        var partial = model.FindAttributesByValue("no-such-value", objectLimit: 3);
        Assert.Empty(partial.Matches);
        Assert.True(partial.Truncated);
        Assert.Equal(3, partial.ScannedObjects);
        Assert.Contains("NOT FOUND IN THIS SAMPLE", partial.Message);
    }

    [Fact]
    public void Model_summary_reports_null_weight_when_weights_are_skipped()
    {
        var model = new MockTeklaModelService();

        Assert.Null(model.GetModelSummary(includeWeights: false).TotalWeightKg);
        var weight = model.GetModelSummary(includeWeights: true).TotalWeightKg;
        Assert.NotNull(weight);
        Assert.True(weight > 0);
    }

    [Fact]
    public void Group_key_parser_accepts_uda_and_attr_prefixes()
    {
        Assert.True(Aggregation.TryParseGroupKey("uda:USER_FIELD_1", out var mode, out var name, out var normalized, out _));
        Assert.Equal(GroupKeyMode.Uda, mode);
        Assert.Equal("USER_FIELD_1", name);
        Assert.Equal("uda:USER_FIELD_1", normalized);

        Assert.True(Aggregation.TryParseGroupKey("attribute:ASSEMBLY_POS", out mode, out name, out _, out _));
        Assert.Equal(GroupKeyMode.Attribute, mode);
        Assert.Equal("ASSEMBLY_POS", name);

        Assert.False(Aggregation.TryParseGroupKey("uda:", out _, out _, out _, out var error));
        Assert.Contains("names no field", error);

        Assert.True(Aggregation.TryParseGroupKey(null, out mode, out _, out normalized, out _));
        Assert.Equal(GroupKeyMode.All, mode);
        Assert.Equal("all", normalized);
    }
}
