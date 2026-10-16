using System;
using TeklaMcp.Core.Models;

namespace TeklaMcp.Core.Geometry;

/// <summary>
/// Placing a drawing view by its FRAME instead of its origin (issue #33). A view's
/// <c>Origin</c> is where the model origin lands on the sheet, so a plan restricted to
/// X 55 200…62 600 at 1:50 with origin 720,150 puts the frame ~1 100 mm off the sheet. All points
/// here are sheet coordinates in paper mm.
/// </summary>
public static class ViewPlacement
{
    public const string Origin = "origin";
    public const string FrameLowerLeft = "frame_lower_left";
    public const string FrameCenter = "frame_center";

    /// <summary>origin / frame_lower_left / frame_center (also camelCase and dashed spellings); null when unknown.</summary>
    public static string? Normalize(string? placeBy)
    {
        var key = (placeBy ?? "").Trim().Replace("-", "").Replace("_", "").ToLowerInvariant();
        switch (key)
        {
            case "":
            case "origin": return Origin;
            case "framelowerleft": return FrameLowerLeft;
            case "framecenter":
            case "framecentre": return FrameCenter;
            default: return null;
        }
    }

    /// <summary>
    /// The origin that moves a view whose frame spans <paramref name="frameMin"/>…<paramref name="frameMax"/>
    /// at <paramref name="currentOrigin"/> so that the frame's anchor (lower-left corner or centre)
    /// lands on <paramref name="target"/>. A view moves rigidly with its origin.
    /// </summary>
    public static Point3D OriginForFrame(
        string placeBy, Point3D target, Point3D currentOrigin, Point3D frameMin, Point3D frameMax)
    {
        var anchor = placeBy == FrameCenter
            ? new Point3D((frameMin.X + frameMax.X) / 2, (frameMin.Y + frameMax.Y) / 2, 0)
            : new Point3D(frameMin.X, frameMin.Y, 0);
        if (placeBy == Origin) return new Point3D(target.X, target.Y, target.Z);
        return new Point3D(
            currentOrigin.X + target.X - anchor.X,
            currentOrigin.Y + target.Y - anchor.Y,
            currentOrigin.Z);
    }

    /// <summary>
    /// Where the restriction box lands at <paramref name="origin"/> and 1:<paramref name="scale"/>,
    /// assuming display coordinates equal view coordinates (plans; rotated views differ). An
    /// ESTIMATE: Tekla adds frame margins (grid labels, ~13–23 mm) known only after the insert.
    /// </summary>
    public static void EstimateFrame(
        Point3D origin, Point3D restrictionMin, Point3D restrictionMax, double scale,
        out Point3D frameMin, out Point3D frameMax)
    {
        var s = scale > 0 ? scale : 1.0;
        frameMin = new Point3D(origin.X + restrictionMin.X / s, origin.Y + restrictionMin.Y / s, 0);
        frameMax = new Point3D(origin.X + restrictionMax.X / s, origin.Y + restrictionMax.Y / s, 0);
    }

    /// <summary>
    /// Where a leader-line mark's insertion point goes when its view changes scale (issue #39):
    /// insertion points are view (model mm) coordinates and are not rescaled, so at 1:10 the paper
    /// distance from the leader start to the text halves and marks crowd the parts. Scaling the
    /// offset by newScale / oldScale keeps it on paper:
    /// <c>ins' = start + (ins − start) × newScale / oldScale</c> (the live fix, Tekla 2021).
    /// </summary>
    public static Point3D RescaleMarkOffset(Point3D leaderStart, Point3D insertion, double oldScale, double newScale)
    {
        var factor = oldScale > 0 && newScale > 0 ? newScale / oldScale : 1.0;
        return new Point3D(
            leaderStart.X + (insertion.X - leaderStart.X) * factor,
            leaderStart.Y + (insertion.Y - leaderStart.Y) * factor,
            insertion.Z);
    }

    /// <summary>True when the frame anchor is within 0.5 mm of the target.</summary>
    public static bool IsPlaced(string placeBy, Point3D target, Point3D frameMin, Point3D frameMax)
    {
        var anchorX = placeBy == FrameCenter ? (frameMin.X + frameMax.X) / 2 : frameMin.X;
        var anchorY = placeBy == FrameCenter ? (frameMin.Y + frameMax.Y) / 2 : frameMin.Y;
        return Math.Abs(anchorX - target.X) <= 0.5 && Math.Abs(anchorY - target.Y) <= 0.5;
    }
}
