using System.Collections.Generic;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Core.Models;
using TeklaMcp.Mock;
using Xunit;

namespace TeklaMcp.Tests;

/// <summary>
/// Write verification of tekla_modify_parts (issue #22): every written part is compared with the
/// request after the commit, and a capped preview says that it is capped.
/// </summary>
public class PartWriteCheckTests
{
    private static ModelObjectInfo Stored(string guid, string cls = "14", string profile = "HEA300") =>
        new ModelObjectInfo
        {
            Guid = guid, Type = "Beam", Class = cls, Profile = profile, Material = "S355", Name = "BEAM",
            StartX = 0, StartY = 0, StartZ = 0, EndX = 6000, EndY = 0, EndZ = 0,
        };

    [Fact]
    public void Accepted_but_unchanged_field_is_a_mismatch()
    {
        // The field case: Class 50 requested, Modify() true, the database kept 14.
        var batch = new PartWriteCheck.Batch();
        batch.Add(new PartModification { Guid = "g1", Class = "50" }, Stored("g1"), null, null);

        var verification = batch.Build();

        Assert.Equal("mismatch", verification.Status);
        Assert.Equal(1, verification.MismatchedObjects);
        var mismatch = Assert.Single(verification.Mismatches);
        Assert.Equal(("class", "50", "14"), (mismatch.Field, mismatch.Requested, mismatch.Stored));
        Assert.Contains("class requested '50', stored '14'", Assert.Single(batch.Errors));
    }

    [Fact]
    public void A_mismatch_outside_the_first_twenty_is_still_found()
    {
        var batch = new PartWriteCheck.Batch();
        for (var i = 0; i < 200; i++)
        {
            var guid = "g" + i;
            batch.Add(new PartModification { Guid = guid, Class = "50" }, Stored(guid, i == 150 ? "14" : "50"), null, null);
        }

        var verification = batch.Build();

        Assert.Equal(200, verification.CheckedObjects);
        Assert.Equal(199, verification.VerifiedObjects);
        Assert.Equal("g150", Assert.Single(verification.Mismatches).Guid);
    }

    [Fact]
    public void Matching_fields_verify_and_blank_requests_are_ignored()
    {
        var batch = new PartWriteCheck.Batch();
        batch.Add(new PartModification { Guid = "g1", Class = "14", Profile = "HEA 300", Material = " ", Name = "beam" },
            Stored("g1"), null, null);

        var verification = batch.Build();

        Assert.Equal("verified", verification.Status);
        Assert.Empty(batch.Errors);
    }

    [Fact]
    public void Endpoints_compare_within_tolerance_and_follow_the_handle_swap()
    {
        PartWriteCheck.ExpectedEnds(
            new PartModification { SwapHandles = true }, new Point3D(0, 0, 0), new Point3D(6000, 0, 0),
            out var start, out var end);
        Assert.Equal(6000, start!.X);
        Assert.Equal(0, end!.X);

        var swapped = Stored("g1");
        swapped.StartX = 6000.04;
        swapped.EndX = 0;
        Assert.Empty(PartWriteCheck.Compare(new PartModification { SwapHandles = true }, swapped, start, end));

        var unswapped = Stored("g1"); // the swap did not happen
        var mismatches = PartWriteCheck.Compare(new PartModification { SwapHandles = true }, unswapped, start, end);
        Assert.Equal(new[] { "start", "end" }, mismatches.Select(m => m.Field));
    }

    [Fact]
    public void Endpoints_of_a_part_without_them_are_not_readable()
    {
        var polyBeam = new ModelObjectInfo { Guid = "p1", Type = "PolyBeam" };
        var mismatch = Assert.Single(PartWriteCheck.Compare(
            new PartModification { NewStart = new Point3D(1, 2, 3) }, polyBeam, new Point3D(1, 2, 3), null));
        Assert.Equal("(not readable)", mismatch.Stored);
    }

    [Fact]
    public void Unread_objects_make_the_check_incomplete_and_position_is_not_compared()
    {
        var batch = new PartWriteCheck.Batch();
        batch.Add(new PartModification { Guid = "g1", Position = new PartPosition { Rotation = "TOP" } }, null, null, null);

        var verification = batch.Build();

        Assert.Equal("incomplete", verification.Status);
        Assert.Equal(1, verification.UnreadObjects);
        Assert.Contains(verification.NotCompared, n => n.StartsWith("position"));
    }

    [Fact]
    public void Nothing_written_is_not_checked() =>
        Assert.Equal("not_checked", new PartWriteCheck.Batch().Build().Status);

    [Fact]
    public void Mock_batch_reports_verification_and_preview_coverage()
    {
        var model = new MockTeklaModelService();
        var parts = model.FindObjects(new ObjectQuery { Type = "Beam" }, 100).Take(25).ToList();
        Assert.True(parts.Count > 20, "the mock fixture needs more than 20 parts for this test");
        var mods = parts.Select(p => new PartModification { Guid = p.Guid, Class = "7" }).ToList();

        var preview = model.ModifyParts(mods, apply: false);
        Assert.Equal(parts.Count, preview.PreviewTotal);
        Assert.True(preview.PreviewTruncated);
        Assert.Null(preview.Verification);

        var applied = model.ModifyParts(mods, apply: true);
        Assert.Equal("verified", applied.Verification!.Status);
        Assert.Equal(parts.Count, applied.Verification.CheckedObjects);
        Assert.True(applied.PreviewTruncated);
    }
}
