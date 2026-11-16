using System;

namespace Prisc.VideoSDK.Model;

/// <summary>
/// An empty stretch of one row between two items, between the origin and the row's first item,
/// or between the row's last item and the end of the timeline: <c>[StartTicks, EndTicks)</c> with
/// nothing on <see cref="TrackId"/> in it. Found by
/// <see cref="TimelineOps.FindGap"/> and handed back to the gap operations, which re-find it
/// first and refuse a gap the project no longer has (the row changed under an open menu).
/// </summary>
/// <param name="LeftItemId">The item ending at <paramref name="StartTicks"/>, or null for the
/// leading gap before a row's first item.</param>
/// <param name="RightItemId">The item starting at <paramref name="EndTicks"/>, or null for the
/// trailing gap after a row's last item, which ends where the timeline does.</param>
public readonly record struct TimelineGap(Guid TrackId, long StartTicks, long EndTicks,
    Guid? LeftItemId, Guid? RightItemId)
{
    public long DurationTicks => EndTicks - StartTicks;

    /// <summary>Whether this is the gap between a row's last item and the end of the
    /// timeline.</summary>
    public bool IsTrailing => RightItemId == null;
}
