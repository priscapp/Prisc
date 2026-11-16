using System;
using System.Collections.Generic;
using System.Linq;
using Prisc.VideoSDK.Composition;

namespace Prisc.VideoSDK.Model;

/// <summary>
/// The editing operations, and the <b>only</b> place <see cref="Track.GroupId"/> semantics
/// live — timeline control, keyboard shortcuts and tests all come through here, so link behavior
/// cannot drift between entry points. A group belongs to rows, and an ungrouped row is a group of
/// one throughout. <see cref="Move"/> moves the ids it is given, which a drag freezes from
/// <see cref="TimeLinked"/> at press; <see cref="SplitAcross"/> cuts every clip running through the
/// instant, all-or-nothing per group; <see cref="RippleDelete"/> and <see cref="CloseGap"/> act on
/// the group's rows. The operations that change how much of an item is shown
/// (<see cref="TrimStart"/>, <see cref="TrimEnd"/>), cut one clip (<see cref="SplitItem"/>) or
/// remove one (<see cref="Delete"/>) are single-item, on every kind of row.
///
/// Operations clamp rather than corrupt: a move that would push a clip before the
/// timeline origin, or a trim that would take an item under <see cref="MinSegmentTicks"/> or
/// before the start of its source, is reduced to the largest amount that fits, and the applied
/// amount is returned so callers can reflect it. Operations that cannot be partially applied
/// (<see cref="SplitItem"/>, <see cref="CloseGap"/>) reject instead.
/// </summary>
public static class TimelineOps
{
    /// <summary>Shortest item an edit may produce, in 100ns ticks: 100ms, mirroring the v1
    /// <c>VideoEditDocument.MinSegmentMs = 100</c> — anything below this is an accidental click,
    /// not an edit.</summary>
    public const long MinSegmentTicks = 1_000_000;

    /// <summary>Shortest item an <i>insert</i> may produce, in 100ns ticks: 1s. A new clip is
    /// something the user is about to grab — drag it, trim it, open its properties — and
    /// <see cref="MinSegmentTicks"/> of it lands under 6px at the default zoom, narrower than the
    /// trim handles on its own edges. So an insert with no room left in front of it (the playhead
    /// parked at the end of the recording) is backed off the end rather than squeezed; only a free
    /// stretch shorter than this is taken whole.</summary>
    public const long MinInsertTicks = TimeSpan.TicksPerSecond;

    /// <summary>The row plus every row sharing its <see cref="Track.GroupId"/>. An ungrouped row
    /// is alone in the result, and an unknown id gives an empty one.</summary>
    public static IReadOnlyList<Track> GroupTracks(Project project, Guid trackId)
    {
        var track = project.Tracks.FirstOrDefault(t => t.Id == trackId);
        if (track == null)
            return Array.Empty<Track>();

        return track.GroupId is Guid group
            ? project.Tracks.Where(t => t.GroupId == group).ToList()
            : new[] { track };
    }

    /// <summary>
    /// The clips a drag of <paramref name="seedIds"/> brings: the seeds, plus each clip on a seed's
    /// group rows that overlaps a clip already in the set in time, transitively. Spans are
    /// half-open, so the two sides of a cut never link. An ungrouped seed brings only itself, and
    /// non-overlapping clips stay out however they are grouped. Ids not in the project add
    /// nothing.
    /// </summary>
    public static HashSet<Guid> TimeLinked(Project project, IEnumerable<Guid> seedIds)
    {
        var groupOfRow = new Dictionary<Guid, Guid?>();
        foreach (var track in project.Tracks)
            groupOfRow[track.Id] = track.GroupId;

        var seeds = seedIds.ToHashSet();
        var linked = new HashSet<Guid>();
        var frontier = new Queue<Item>();
        foreach (var item in project.Items)
        {
            if (seeds.Contains(item.Id) && linked.Add(item.Id))
                frontier.Enqueue(item);
        }

        while (frontier.TryDequeue(out var item))
        {
            if (groupOfRow.GetValueOrDefault(item.TrackId) is not Guid group)
                continue;

            foreach (var other in project.Items)
            {
                if (groupOfRow.GetValueOrDefault(other.TrackId) == group && Overlaps(item, other)
                    && linked.Add(other.Id))
                    frontier.Enqueue(other);
            }
        }

        return linked;
    }

    /// <summary>Shifts exactly the given clips along the timeline by
    /// <paramref name="deltaTicks"/>, clamped so none starts before 0. A
    /// <see cref="MediaContent.SpeedWarpExempt"/> clip re-fits as <see cref="ShiftItems"/>
    /// describes, from <paramref name="outputSpanOf"/> when given. Returns the delta actually
    /// applied. Throws when an id is not in the project.</summary>
    public static long Move(Project project, IEnumerable<Guid> itemIds, long deltaTicks, Func<Item, long?> outputSpanOf = null)
    {
        var members = itemIds.Select(id => Require(project, id)).ToList();
        if (members.Count == 0)
            return 0;

        var minStart = members.Min(m => m.TimelineStartTicks);
        if (deltaTicks < -minStart)
            deltaTicks = -minStart;

        if (deltaTicks == 0)
            return 0;

        ShiftItems(project, members, _ => deltaTicks, outputSpanOf);
        return deltaTicks;
    }

    /// <summary>
    /// Moves each of <paramref name="items"/> along the timeline by its
    /// <paramref name="deltaOf"/>, read once per item before anything moves: the one place clips
    /// slide in time, so every slide treats exempt clips alike. A moved
    /// <see cref="MediaContent.SpeedWarpExempt"/> clip keeps the real-time (output) span it had,
    /// which is the truth for such a clip: it plays on the output clock, so its project duration
    /// is re-derived at the new position (see <see cref="FitExemptItem"/>) and changes when the
    /// slide crosses into or out of a speed item. An item whose delta is 0 is left entirely alone.
    /// That span is measured from the clip's extent as the slide begins, unless
    /// <paramref name="outputSpanOf"/> knows better: one step of a drag can have end-trimmed the
    /// clip at a neighbour it has since moved away from, and the drag still knows its full span.
    /// </summary>
    public static void ShiftItems(Project project, IEnumerable<Item> items, Func<Item, long> deltaOf,
        Func<Item, long?> outputSpanOf = null)
    {
        var moves = items.Select(i => (Item: i, Delta: deltaOf(i))).Where(m => m.Delta != 0).ToList();

        // the exempt clips' output spans, under the warp before anything moves
        List<(Item Item, long OutputSpan)> exempt = null;
        Playback.TimeWarp warp = null;
        var warpMoves = false;
        foreach (var (item, _) in moves)
        {
            warpMoves |= item.Content is SpeedContent;
            if (!IsExempt(item))
                continue;

            warp ??= Playback.TimeWarp.Build(project);
            (exempt ??= new List<(Item, long)>()).Add((item, outputSpanOf?.Invoke(item)
                ?? warp.OutputSpan(item.TimelineStartTicks, item.TimelineEndTicks)));
        }

        foreach (var (item, delta) in moves)
            item.TimelineStartTicks += delta;

        if (exempt == null)
            return;

        // a speed item in the set moved the warp along with the clips, so the spans land on
        // the mapping it left behind
        if (warpMoves)
            warp = Playback.TimeWarp.Build(project);

        foreach (var (item, outputSpan) in exempt)
            FitExemptItem(project, warp, item, outputSpan);
    }

    /// <summary>The one re-fit of a <see cref="MediaContent.SpeedWarpExempt"/> clip: sets its
    /// project duration to cover <paramref name="outputSpan"/> output ticks from its start under
    /// <paramref name="warp"/>. Growth that reaches the next item on the row is end-trimmed
    /// there, like <see cref="SetSpeed"/>, but the clip is never trimmed below the length it came
    /// in with: one already lying on another item got there by a slide, which is a plain
    /// collision, for validation to refuse as it does for any other clip.</summary>
    public static void FitExemptItem(Project project, Playback.TimeWarp warp, Item item, long outputSpan)
    {
        var duration = warp.ProjectEndOf(item.TimelineStartTicks, outputSpan) - item.TimelineStartTicks;
        item.DurationTicks = Math.Max(ClampDurationToRow(project, item, duration),
            Math.Min(duration, item.DurationTicks));
    }

    /// <summary>
    /// Slides everything at or past <paramref name="start"/> left by <paramref name="span"/> on
    /// every row, clamped so nothing lands before <paramref name="start"/>: how a span already cut
    /// out of the timeline closes up.
    ///
    /// <para>Clips that are <see cref="TimeLinked"/> move as one, by what the earliest of them can
    /// move, so that nothing stops being in step with what it overlaps. A row the cut did not reach
    /// can hold clips starting inside the span, each with its own clamp, and a linked set reaching
    /// back before the span stays where it is. On rows the cut did reach nothing is left inside the
    /// span to link across it, so a set there is wholly before it or wholly after it.</para>
    /// </summary>
    private static void CloseSpan(Project project, long start, long span)
    {
        var deltas = new Dictionary<Guid, long>();
        foreach (var item in project.Items)
        {
            if (deltas.ContainsKey(item.Id))
                continue;

            var linked = TimeLinked(project, new[] { item.Id });
            var first = project.Items.Where(i => linked.Contains(i.Id)).Min(i => i.TimelineStartTicks);
            var delta = first >= start ? Math.Max(start, first - span) - first : 0;
            foreach (var id in linked)
                deltas[id] = delta;
        }

        ShiftItems(project, project.Items, i => deltas[i.Id]);
    }

    /// <summary>
    /// Moves the in-point of a <b>single</b> item, grouped row or not: positive
    /// <paramref name="deltaTicks"/> shrinks it from the start, negative extends it earlier.
    /// Clamped so the item keeps at least <see cref="MinSegmentTicks"/>, starts at or after 0,
    /// and — for a clip with a clock — never rewinds before the start of its source
    /// (<see cref="SourceClockContent.SourceInTicks"/> stays ≥ 0). The in-point moves with the
    /// trim, so every instant the item still covers maps to the source frame it mapped to before:
    /// trimming a clip cannot put it out of step with the clips beside it on other rows, which is
    /// why trim needs no group scope. Returns the delta actually applied.
    /// </summary>
    public static long TrimStart(Project project, Guid itemId, long deltaTicks)
    {
        var item = Require(project, itemId);
        var clock = item.Content as SourceClockContent;
        var speed = SpeedOf(clock);

        var maxShrink = item.DurationTicks - MinSegmentTicks;
        if (deltaTicks > maxShrink)
            deltaTicks = Math.Max(0, maxShrink);

        // a timeline tick consumes `speed` source ticks, so the room before the source's start
        // is SourceInTicks / speed timeline ticks (floored — never let rounding rewind past 0).
        // An exempt clip consumes source on the output clock, so its room is measured there and
        // mapped back into project ticks (see ExemptStartHeadroom).
        // a freeze frame consumes no source, so only the origin bounds it.
        var maxExtend = clock == null || clock.Freeze
            ? item.TimelineStartTicks
            : IsExempt(clock)
                ? Math.Min(item.TimelineStartTicks, ExemptStartHeadroom(project, item, clock, speed))
                : Math.Min(item.TimelineStartTicks, (long)Math.Floor(clock.SourceInTicks / speed));
        if (deltaTicks < -maxExtend)
            deltaTicks = -maxExtend;

        if (deltaTicks == 0)
            return 0;

        var oldStart = item.TimelineStartTicks;
        item.TimelineStartTicks += deltaTicks;
        item.DurationTicks -= deltaTicks;
        if (clock != null)
            clock.SourceInTicks = Math.Max(0, clock.SourceInTicks
                + SourceTicksBetween(project, clock, oldStart, oldStart + deltaTicks));

        return deltaTicks;
    }

    /// <summary>
    /// Moves the out-point of a <b>single</b> item, grouped row or not: positive
    /// <paramref name="deltaTicks"/> lengthens it, negative shortens it, clamped so it keeps at
    /// least <see cref="MinSegmentTicks"/> and — for a clip with a clock whose stream duration is
    /// known — never extends past the end of its source (there is no material there: video would
    /// freeze on the last frame and audio would mix to silence). An item already hanging past the
    /// source end (older project, or a stale probe) may still shrink, it just cannot grow. The
    /// item's start and in-point are untouched, so the source↔timeline mapping of every instant it
    /// still covers is unchanged. Returns the delta actually applied.
    /// </summary>
    public static long TrimEnd(Project project, Guid itemId, long deltaTicks)
    {
        var item = Require(project, itemId);

        var maxShrink = item.DurationTicks - MinSegmentTicks;
        if (deltaTicks < -maxShrink)
            deltaTicks = Math.Min(0, -maxShrink);

        if (item.Content is SourceClockContent { Freeze: false } clock)
        {
            var streamDuration = StreamDurationOf(project, clock);
            if (streamDuration > 0)
            {
                // remaining source, expressed in timeline ticks at the item's speed
                var speed = SpeedOf(clock);
                var remainingTimeline = (long)Math.Floor((streamDuration - clock.SourceInTicks) / speed);
                long maxExtend;
                if (IsExempt(clock))
                {
                    // the remaining source plays out in output ticks; where that lands in
                    // project time depends on the warp between here and there.
                    var warp = Playback.TimeWarp.Build(project);
                    var allowedEnd = warp.ProjectEndOf(item.TimelineStartTicks, Math.Max(0, remainingTimeline));
                    maxExtend = Math.Max(0, allowedEnd - item.TimelineEndTicks);
                }
                else
                {
                    maxExtend = Math.Max(0, remainingTimeline - item.DurationTicks);
                }
                if (deltaTicks > maxExtend)
                    deltaTicks = maxExtend;
            }
        }

        if (deltaTicks == 0)
            return 0;

        item.DurationTicks += deltaTicks;
        return deltaTicks;
    }

    /// <summary>The probed duration of the stream an item plays, which for an overlay is its
    /// source's screen stream, or 0 when the source/stream is missing or the probe recorded no
    /// duration — in which case trims are not source-bounded.</summary>
    private static long StreamDurationOf(Project project, SourceClockContent clock)
    {
        foreach (var source in project.Sources ?? Enumerable.Empty<Source>())
        {
            if (source.Id != clock.SourceId)
                continue;
            foreach (var stream in source.Streams ?? Enumerable.Empty<SourceStream>())
            {
                var playing = clock is MediaContent media
                    ? stream.Index == media.StreamIndex
                    : FrameComposer.IsScreenStream(source, stream.Index);
                if (playing)
                    return stream.DurationTicks;
            }
        }

        return 0;
    }

    /// <summary>
    /// Cuts every clip running through <paramref name="timelineTicks"/>: each becomes two
    /// back-to-back items, the right half starting exactly there with its in-point advanced by the
    /// left half's length. Entry transitions stay on the left, exit transitions move to the right.
    /// Half-open spans keep the two sides of the cut apart, so each drags separately afterwards
    /// with no group state changing. A clip starting exactly there is already cut there, whether
    /// by a right-click split or an earlier cut, so it takes no part.
    ///
    /// <para>All-or-nothing per group (an ungrouped row is a group of one): a sliver left on one
    /// row would overlap the other side of the cut and chain both sides back together, so a group
    /// where any half would be shorter than <see cref="MinSegmentTicks"/> is left whole, while
    /// other groups are still cut. Returns whether anything was cut, and whether any group was
    /// refused.</para>
    /// </summary>
    public static (bool Cut, bool Refused) SplitAcross(Project project, long timelineTicks)
    {
        var cut = false;
        var refused = false;

        // materialized first, since a cut adds to the items
        var groups = project.Items.Where(i => i.TimelineStartTicks < timelineTicks && timelineTicks < i.TimelineEndTicks)
            .GroupBy(i => project.TrackOf(i)?.GroupId ?? i.TrackId)
            .Select(g => g.ToList())
            .ToList();

        foreach (var covered in groups)
        {
            if (SplitCore(project, covered, timelineTicks))
                cut = true;
            else
                refused = true;
        }

        return (cut, refused);
    }

    /// <summary>
    /// Cuts <b>one</b> item, leaving every other clip alone — the timeline's right-click split,
    /// where the pointer picked out a single clip and cutting its neighbors with it would be an
    /// edit the user did not ask for. On a grouped row the halves may stay chained through the
    /// other rows; <see cref="SplitAcross"/> is the cut that separates.
    /// </summary>
    public static bool SplitItem(Project project, Guid itemId, long timelineTicks)
    {
        var target = Require(project, itemId);
        if (!target.Covers(timelineTicks))
            return false;

        return SplitCore(project, new[] { target }, timelineTicks);
    }

    /// <summary>Cuts every item in <paramref name="covered"/> at the instant, all-or-nothing: a cut
    /// that would leave any half shorter than <see cref="MinSegmentTicks"/> is refused outright
    /// rather than applied to some rows and not others.</summary>
    private static bool SplitCore(Project project, IReadOnlyList<Item> covered, long timelineTicks)
    {
        foreach (var m in covered)
        {
            if (timelineTicks - m.TimelineStartTicks < MinSegmentTicks ||
                m.TimelineEndTicks - timelineTicks < MinSegmentTicks)
                return false;
        }

        foreach (var m in covered)
        {
            var right = RightPart(project, m, timelineTicks);

            m.DurationTicks = timelineTicks - m.TimelineStartTicks;
            m.Exit = null;
            project.Items.Add(right);
        }

        project.Normalize();
        return true;
    }

    /// <summary>What <paramref name="item"/> plays from <paramref name="ticks"/> on, as a new item
    /// not yet in the project: in-point advanced to that instant, the entry transition left
    /// behind, the exit transition carried over.</summary>
    private static Item RightPart(Project project, Item item, long ticks)
    {
        var right = item.Clone();
        right.TimelineStartTicks = ticks;
        right.DurationTicks = item.TimelineEndTicks - ticks;
        right.Entry = null;
        if (right.Content is SourceClockContent clock)
            clock.SourceInTicks += SourceTicksBetween(project, clock, item.TimelineStartTicks, ticks);

        return right;
    }

    /// <summary>
    /// Cuts the item's <b>own span</b> out of its group's rows and closes the gap: every clip on
    /// those rows is trimmed/split to remove what it played inside <c>[start, end)</c>, and every
    /// remaining item that started at or after the cut shifts left by its length (clamped so
    /// nothing shifts to before where the cut began, and time-linked clips by one amount: see
    /// <see cref="CloseSpan"/>). This is the multi-track generalization of the v1 "cut": items on
    /// <b>all</b> tracks shift, so cross-track sync is preserved. A row outside the group loses
    /// nothing, so a clip of its that straddles the cut can be run into by the clip after it, for
    /// validation to refuse.
    ///
    /// <para>Scoped to the item's span — never "the whole group" — because a row can carry several
    /// back-to-back segments (a recording built from keep-slices, or the two halves a
    /// <see cref="SplitItem"/> leaves): deleting one clip must not take the rest of the recording
    /// with it.</para>
    /// </summary>
    public static void RippleDelete(Project project, Guid itemId)
    {
        var target = Require(project, itemId);
        var start = target.TimelineStartTicks;
        var end = target.TimelineEndTicks;

        var rows = GroupTracks(project, target.TrackId).Select(t => t.Id).ToHashSet();
        CutRange(project, project.Items.Where(i => rows.Contains(i.TrackId)), start, end);
        CloseSpan(project, start, end - start);
    }

    /// <summary>
    /// Removes what each of <paramref name="candidates"/> plays inside <c>[start, end)</c>: one
    /// inside the range is removed, one straddling an edge is trimmed (splitting in two when it
    /// hangs over both, exactly as <see cref="SplitCore"/> would cut it — in-point advanced,
    /// entry left / exit right). A remnant shorter than <see cref="MinSegmentTicks"/> is culled
    /// rather than kept: rows need not agree on their edges (a recording's audio ends a hair
    /// before its video), and a delete that left a sub-minimum sliver behind would strand an item
    /// no edit is allowed to produce.
    /// </summary>
    private static void CutRange(Project project, IEnumerable<Item> candidates, long start, long end)
    {
        foreach (var m in candidates
                     .Where(m => m.TimelineStartTicks < end && m.TimelineEndTicks > start).ToList())
        {
            var leftLength = start - m.TimelineStartTicks;
            var rightLength = m.TimelineEndTicks - end;

            if (rightLength >= MinSegmentTicks)
                project.Items.Add(RightPart(project, m, end));

            if (leftLength >= MinSegmentTicks)
            {
                m.DurationTicks = leftLength;
                m.Exit = null;
            }
            else
            {
                project.Items.Remove(m);
            }
        }
    }

    /// <summary>Removes a single item, leaving a gap where it was: no ripple, and clips on other
    /// rows are left alone (<see cref="RippleDelete"/> is the delete that closes up). Returns
    /// false when the id is not in the project.</summary>
    public static bool Delete(Project project, Guid itemId)
    {
        var item = project.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
            return false;

        project.Items.Remove(item);
        return true;
    }

    // ----------------------------------------------------------------------------------- gaps

    /// <summary>The empty stretch of <paramref name="trackId"/> around <paramref name="ticks"/>:
    /// from the end of the item before it (or the origin) to the start of the item after it, or
    /// to the end of the timeline (<see cref="Project.GetDurationTicks"/>) when none follows. Null
    /// when an item covers the instant, when the row is empty, and past the end of the
    /// timeline.</summary>
    public static TimelineGap? FindGap(Project project, Guid trackId, long ticks)
    {
        if (ticks < 0)
            return null;

        Item left = null, right = null;
        foreach (var item in project.Items)
        {
            if (item.TrackId != trackId)
                continue;
            if (item.Covers(ticks))
                return null;

            if (item.TimelineEndTicks <= ticks)
            {
                if (left == null || item.TimelineEndTicks > left.TimelineEndTicks)
                    left = item;
            }
            else if (right == null || item.TimelineStartTicks < right.TimelineStartTicks)
            {
                right = item;
            }
        }

        if (right == null)
        {
            var end = project.GetDurationTicks();
            return left != null && ticks < end
                ? new TimelineGap(trackId, left.TimelineEndTicks, end, left.Id, null)
                : null;
        }

        var start = left?.TimelineEndTicks ?? 0;
        return right.TimelineStartTicks > start
            ? new TimelineGap(trackId, start, right.TimelineStartTicks, left?.Id, right.Id)
            : null;
    }

    /// <summary>Whether the project still has exactly this gap — the guard every gap operation
    /// runs first, so a menu built before an edit cannot act on a row that has since
    /// changed.</summary>
    public static bool IsCurrentGap(Project project, TimelineGap gap) =>
        FindGap(project, gap.TrackId, gap.StartTicks) == gap;

    /// <summary>
    /// Closes a gap: everything at or past its end on the row and the rest of the row's group
    /// slides left onto the gap's start, so a recording's other rows (its audio, its cursor) stay
    /// in sync. Clips before the gap stay put, and other rows do not move. A trailing gap closes
    /// the other way: everything before it on those rows slides right onto the end of the
    /// timeline, so the row's last clip ends with the video. Returns false when the
    /// gap is not current, or when a clip on another row of the group overlaps the gap: closing
    /// past it would put that row out of step, cutting it would delete material on a row the user
    /// did not point at, and <see cref="RippleCloseGap"/> is the explicit way to remove the time
    /// from every row. With nothing inside the gap and everything later shifting by the same
    /// amount, no collision is possible.
    /// </summary>
    public static bool CloseGap(Project project, TimelineGap gap)
    {
        if (!IsCurrentGap(project, gap))
            return false;

        var rows = GroupTracks(project, gap.TrackId).Select(t => t.Id).ToHashSet();
        var onRows = project.Items.Where(i => rows.Contains(i.TrackId)).ToList();
        if (onRows.Any(i => i.TimelineStartTicks < gap.EndTicks && i.TimelineEndTicks > gap.StartTicks))
            return false;

        var span = gap.DurationTicks;
        if (gap.IsTrailing)
            ShiftItems(project, onRows.Where(i => i.TimelineEndTicks <= gap.StartTicks), _ => span);
        else
            ShiftItems(project, onRows.Where(i => i.TimelineStartTicks >= gap.EndTicks), _ => -span);

        return true;
    }

    /// <summary>
    /// The gap's span cut out of the whole timeline: whatever any other row plays inside it is
    /// removed (trimmed, split or dropped exactly as <see cref="RippleDelete"/> cuts its rows), then
    /// everything at or past the gap shifts left by its length. Cross-row sync survives because
    /// every row loses the same span. Returns false when the gap is not current, and for a trailing
    /// gap, where nothing follows to close up and the cut would only take the end off every other
    /// row.
    /// </summary>
    public static bool RippleCloseGap(Project project, TimelineGap gap)
    {
        if (gap.IsTrailing || !IsCurrentGap(project, gap))
            return false;

        CutRange(project, project.Items, gap.StartTicks, gap.EndTicks);
        CloseSpan(project, gap.StartTicks, gap.DurationTicks);
        return true;
    }

    /// <summary>Whether <see cref="FillGapWithFreeze"/> can hold a frame of the gap's left
    /// (<paramref name="fromLeft"/>) or right neighbor: the gap is current, sits on a video row,
    /// and that neighbor exists and is media.</summary>
    public static bool CanFillGapWithFreeze(Project project, TimelineGap gap, bool fromLeft)
    {
        if (!IsCurrentGap(project, gap))
            return false;

        var neighborId = fromLeft ? gap.LeftItemId : gap.RightItemId;
        var neighbor = neighborId == null ? null : project.Items.FirstOrDefault(i => i.Id == neighborId);
        var track = project.Tracks.FirstOrDefault(t => t.Id == gap.TrackId);
        return neighbor?.Content is MediaContent && track?.Kind == TrackKind.Video;
    }

    /// <summary>
    /// Fills the gap with a freeze frame (<see cref="MediaContent.Freeze"/>) of a neighbor: the
    /// last frame the left item shows, or the first frame the right item shows, held for the
    /// gap's whole length. The new item wears the neighbor's placement, surround and effect so
    /// the picture does not jump at the seam; it carries no transitions. Overlays on its group's
    /// rows hold with it (<see cref="RebaseOverlays"/>). Returns the new item's id, or null when
    /// <see cref="CanFillGapWithFreeze"/> says no.
    /// </summary>
    public static Guid? FillGapWithFreeze(Project project, TimelineGap gap, bool fromLeft)
    {
        if (!CanFillGapWithFreeze(project, gap, fromLeft))
            return null;

        var neighbor = Require(project, fromLeft ? gap.LeftItemId.Value : gap.RightItemId.Value);

        var item = neighbor.Clone();
        item.TimelineStartTicks = gap.StartTicks;
        item.DurationTicks = gap.DurationTicks;
        item.Entry = null;
        item.Exit = null;

        var content = (MediaContent)item.Content;
        if (fromLeft)
            content.SourceInTicks = LastFrameTicks(project, neighbor, (MediaContent)neighbor.Content);
        content.Freeze = true;
        content.Speed = 1.0;
        content.SpeedWarpExempt = false;

        project.Items.Add(item);
        project.Normalize();
        RebaseOverlays(project, item, gap.StartTicks, gap.EndTicks);
        return item.Id;
    }

    /// <summary>The source instant of the last frame a media item shows: one tick short of its
    /// out-point (the out-point itself is the first instant the edit removed — the convention
    /// the preview's past-the-end clamp follows too), held inside the stream when the item hangs
    /// past the end of its source.</summary>
    private static long LastFrameTicks(Project project, Item item, MediaContent media)
    {
        if (media.Freeze)
            return media.SourceInTicks;

        var end = media.SourceInTicks
                  + SourceTicksBetween(project, media, item.TimelineStartTicks, item.TimelineEndTicks);
        var streamDuration = StreamDurationOf(project, media);
        if (streamDuration > 0)
            end = Math.Min(end, streamDuration);

        return Math.Max(media.SourceInTicks, end - 1);
    }

    /// <summary>Whether <see cref="ExtendIntoGap"/> would fill the whole gap: the neighbor has
    /// that much material on the gap's side (trimmed-off source for media; anything else stretches
    /// freely). Answered by trial on a copy, so it can never disagree with the trims
    /// themselves.</summary>
    public static bool CanExtendIntoGap(Project project, TimelineGap gap, bool fromLeft)
    {
        if (!IsCurrentGap(project, gap) || (fromLeft ? gap.LeftItemId : gap.RightItemId) == null)
            return false;

        return ExtendIntoGapCore(Project.FromJson(project.ToJson()), gap, fromLeft);
    }

    /// <summary>
    /// Fills the gap by un-trimming a neighbor into it: the left item's out-point moves to the
    /// gap's end, or the right item's in-point moves back to the gap's start, revealing the
    /// source that was trimmed away. Single-item, like the trims it is made of — clips on the
    /// neighbor's other group rows keep their own edges, and overlays there take their clock from
    /// what the gap now shows (<see cref="RebaseOverlays"/>). All-or-nothing: returns false without
    /// touching the project when the neighbor cannot cover the whole gap (see
    /// <see cref="CanExtendIntoGap"/>).
    /// </summary>
    public static bool ExtendIntoGap(Project project, TimelineGap gap, bool fromLeft)
    {
        if (!CanExtendIntoGap(project, gap, fromLeft) || !ExtendIntoGapCore(project, gap, fromLeft))
            return false;

        RebaseOverlays(project, Require(project, fromLeft ? gap.LeftItemId.Value : gap.RightItemId.Value),
            gap.StartTicks, gap.EndTicks);
        return true;
    }

    private static bool ExtendIntoGapCore(Project project, TimelineGap gap, bool fromLeft)
    {
        var span = gap.DurationTicks;
        return fromLeft
            ? TrimEnd(project, gap.LeftItemId.Value, span) == span
            : TrimStart(project, gap.RightItemId.Value, -span) == -span;
    }

    /// <summary>
    /// Whether the gap's two neighbors are the halves of one cut that <see cref="RejoinGap"/> can
    /// heal: the same stream at the same speed, neither a freeze frame nor on the output clock,
    /// and the right one picking up at or after where the left one stops in the source (so the
    /// rejoined clip plays the source forward, through whatever the cut and trims removed).
    /// </summary>
    public static bool CanRejoinGap(Project project, TimelineGap gap)
    {
        if (!IsCurrentGap(project, gap) || gap.LeftItemId is not Guid leftId || gap.RightItemId is not Guid rightId)
            return false;

        var left = project.Items.FirstOrDefault(i => i.Id == leftId);
        var right = project.Items.FirstOrDefault(i => i.Id == rightId);
        if (left?.Content is not MediaContent lm || right?.Content is not MediaContent rm)
            return false;

        return lm.SourceId == rm.SourceId && lm.StreamIndex == rm.StreamIndex
               && !lm.Freeze && !rm.Freeze && !lm.SpeedWarpExempt && !rm.SpeedWarpExempt
               && SpeedOf(lm) == SpeedOf(rm)
               && rm.SourceInTicks >= lm.SourceInTicks + SourceTicksBetween(project, lm, left.TimelineStartTicks, left.TimelineEndTicks);
    }

    /// <summary>
    /// Undoes the cut between the gap's neighbors on this row only: the left clip grows to play
    /// its source straight through to where the right clip ended — the material the cut and any
    /// trims removed included — and the right clip is absorbed into it (its exit transition
    /// carried over). Everything after it on the row shifts by however far the right clip's end
    /// moved — left when the gap was wider than the removed material, right when it was
    /// narrower — so the rest of the row keeps its spacing. Other rows, grouped or not, keep their
    /// clips where they are; overlays on them take their clocks from what the row now shows
    /// (<see cref="RebaseOverlays"/>): over the rejoined clip if nothing else moved, otherwise from
    /// its start through the end of the timeline, for its recording and for every recording whose
    /// screen moved with the rest of the row. Returns false when <see cref="CanRejoinGap"/> says no.
    /// </summary>
    public static bool RejoinGap(Project project, TimelineGap gap)
    {
        if (!CanRejoinGap(project, gap))
            return false;

        var left = Require(project, gap.LeftItemId.Value);
        var right = Require(project, gap.RightItemId.Value);
        var lm = (MediaContent)left.Content;
        var rm = (MediaContent)right.Content;
        var speed = SpeedOf(lm);

        var sourceEnd = rm.SourceInTicks + SourceTicksBetween(project, rm, right.TimelineStartTicks, right.TimelineEndTicks);
        var sourceSpan = sourceEnd - lm.SourceInTicks;
        var newEnd = left.TimelineStartTicks + (speed == 1.0 ? sourceSpan : (long)Math.Round(sourceSpan / speed));
        var rightEnd = right.TimelineEndTicks;
        var shift = newEnd - rightEnd;
        var moved = shift == 0
            ? new List<Item>()
            : project.Items.Where(i => i.TrackId == gap.TrackId && i.TimelineStartTicks >= rightEnd).ToList();

        left.DurationTicks = newEnd - left.TimelineStartTicks;
        left.Exit = right.Exit;
        project.Items.Remove(right);

        // last, so an exempt clip that grows as it slides is limited by the row it lands on,
        // not by the clip just absorbed
        ShiftItems(project, moved, _ => shift);

        var to = moved.Count == 0 ? newEnd : long.MaxValue;
        var recordings = moved.Prepend(left).Select(i => ScreenSourceOf(project, i)).Where(s => s != null).Distinct();
        foreach (var source in recordings)
            RebaseSource(project, source, project.TrackOf(left), left.TimelineStartTicks, to);

        return true;
    }

    /// <summary>
    /// Whether the row can take <paramref name="content"/>, wherever on it the clip lands. Effect
    /// items go on effect rows, one kind to a row and one speed row to a project. Audio rows take
    /// sound and video rows everything else, by the stream a media clip plays; a clip whose stream
    /// this project cannot name yet (its source is still to be added) passes on that count.
    ///
    /// <para>Cursor and keys rows stay homogeneous. The timeline classifies a row by its earliest
    /// clip (<c>TimelineRowLayout.KindOf</c>), so a text card on a cursor row, or a cursor clip on
    /// a text row, could change what the row is and how tall it draws. A row holding overlays takes
    /// only more of that kind, whichever recording they came from, and overlays go only on an empty
    /// row or one of their own kind. A wallpaper likewise goes only on an empty row or one of
    /// wallpapers: in a picture row's front gap it would make that row a background row
    /// (<see cref="IsBackgroundRow"/>), and the next wallpaper added would land in front of it.</para>
    /// </summary>
    public static bool CanHost(Track track, ItemContent content, Project project)
    {
        if (track == null || content == null)
            return false;

        var held = project.Items.Where(i => i.TrackId == track.Id).Select(i => i.Content);
        var stream = content is MediaContent media ? StreamKindOf(project, media) : null;
        return track.Kind switch
        {
            TrackKind.Effect => content is SpeedContent or ZoomContent
                                && held.All(c => c.GetType() == content.GetType())
                                && (content is not SpeedContent
                                    || !project.Items.Any(i => i.Content is SpeedContent && i.TrackId != track.Id)),
            TrackKind.Audio => content is MediaContent && stream != StreamKind.Video,
            _ => content is not (SpeedContent or ZoomContent) && stream != StreamKind.Audio
                 && (content is InputOverlayContent or BackgroundContent
                     ? held.All(c => c.GetType() == content.GetType())
                     : !held.Any(c => c is InputOverlayContent)),
        };
    }

    private static StreamKind? StreamKindOf(Project project, MediaContent media) =>
        FrameComposer.FindSource(project, media.SourceId)?.Streams?
            .FirstOrDefault(s => s.Index == media.StreamIndex)?.Kind;

    // ----------------------------------------------------------------------------- copy and paste

    /// <summary>
    /// What copying these clips puts on the clipboard: a project holding them, the rows they sit on
    /// and every source they play, sharing nothing with <paramref name="project"/>. Ids not in the
    /// project are ignored. A <see cref="MediaContent.SpeedWarpExempt"/> clip stores its output span
    /// in <see cref="Item.DurationTicks"/>, because the project span it needs depends on the warp it
    /// lands under (<see cref="Paste"/>). On a row with no other kind of clip, each one after the
    /// first also stores its start as an output offset from the first's start, because the row is
    /// one take, perhaps split, and only that clock keeps its pieces and pauses as they play.
    /// </summary>
    public static Project Lift(Project project, IEnumerable<Guid> itemIds)
    {
        var ids = itemIds.ToHashSet();
        var items = project.Items.Where(i => ids.Contains(i.Id)).ToList();
        var rows = items.Select(i => i.TrackId).ToHashSet();
        var sources = items.Select(i => i.Content).OfType<SourceClockContent>().Select(c => c.SourceId).ToHashSet();

        var clip = Project.FromJson(new Project
        {
            Output = project.Output,
            Sources = project.Sources.Where(s => sources.Contains(s.Id)).ToList(),
            Tracks = project.Tracks.Where(t => rows.Contains(t.Id)).ToList(),
            Items = items,
        }.ToJson());

        var warp = Playback.TimeWarp.Build(project);
        foreach (var item in clip.Items.Where(IsExempt))
            item.DurationTicks = warp.OutputSpan(item.TimelineStartTicks, item.TimelineEndTicks);

        foreach (var row in OutputClockRows(clip))
        {
            var first = row.Min(i => i.TimelineStartTicks);
            foreach (var item in row)
                item.TimelineStartTicks = first + warp.OutputSpan(first, item.TimelineStartTicks);
        }

        return clip;
    }

    /// <summary>Where a one-row <paramref name="clip"/> pasted into <paramref name="gap"/> aimed at
    /// <paramref name="at"/> starts: at <paramref name="at"/> when it fits from there, moved earlier
    /// just enough to end with the gap when it does not, and at the gap's start when it is longer
    /// than the gap (where <see cref="Paste"/> trims it).</summary>
    public static long GapPasteAnchor(Project clip, TimelineGap gap, long at)
    {
        if (clip.Items.Count == 0)
            return gap.StartTicks;

        var span = clip.Items.Max(i => i.TimelineEndTicks) - clip.Items.Min(i => i.TimelineStartTicks);
        return Math.Max(gap.StartTicks, Math.Min(at, gap.EndTicks - span));
    }

    /// <summary>Whether a <see cref="Paste"/> of <paramref name="clip"/> can fit it into the gap: the
    /// clip has one row, of the gap's row's kind, the row can host all of it
    /// (<see cref="CanHost"/>), and the gap is current and at least
    /// <see cref="MinSegmentTicks"/> long.</summary>
    public static bool CanFitInGap(Project project, Project clip, TimelineGap gap)
    {
        if (clip.Tracks.Count != 1 || !IsCurrentGap(project, gap) || gap.DurationTicks < MinSegmentTicks)
            return false;

        var row = project.Tracks.FirstOrDefault(t => t.Id == gap.TrackId);
        return row?.Kind == clip.Tracks[0].Kind && clip.Items.All(i => CanHost(row, i.Content, project));
    }

    /// <summary>
    /// Pastes <paramref name="clip"/> (from <see cref="Lift"/>, and private to this call: its
    /// sources are taken as they are) so that its earliest clip starts at
    /// <paramref name="anchor"/>, keeping relative time and row layout. Nothing already on the
    /// timeline is overwritten.
    ///
    /// <para>Speed items go to the project's speed row, since a second one cannot validate, and if
    /// they do not fit there the whole paste is refused with <c>Refused</c> set and nothing changed.
    /// With no speed row here they get one (<see cref="InsertNewSpeedRow"/>). Everything else goes
    /// by block: rows sharing a group are one block, and any other row is a block of its own. A
    /// block returns to its own rows when they are all here, can host it and
    /// are free over its spans, so its clips take those rows' groups. Otherwise the whole block
    /// gets new rows (<see cref="InsertNewRows"/>), so a pasted cursor never ends up on a row under
    /// its pasted screen, where it would compose invisibly. A <paramref name="fitGap"/> applies only
    /// to a clip with one row: that row's clips go on the gap's row, the last end-trimmed to the gap
    /// and those with no room dropped. Nothing is pasted when <see cref="CanFitInGap"/> says no.</para>
    ///
    /// <para>A <see cref="MediaContent.SpeedWarpExempt"/> clip takes the project span its output
    /// span needs under the warp it lands on, and the free-span test uses that span. Those on a row
    /// of nothing else land by output time from the first of them (<see cref="Lift"/>), so a split
    /// take stays whole and continuous under any warp. Beside a plain clip of the same row one can
    /// land longer than the room before it, and is end-trimmed.</para>
    ///
    /// <para>A screen clip landing on a grouped row hands the overlays already there its time
    /// (<see cref="RebaseOverlays"/>). Pasted overlays keep the clocks they came with, and new rows
    /// have no others to reach.</para>
    ///
    /// <para>With <paramref name="restoreRows"/>, which only a paste into the project the clip was
    /// lifted from may set, a block's row that is no longer here (a cut or delete removed it with its
    /// last clip) counts as one of its own rows: when the block can return, that row is put back as
    /// it was, with its id, name, group and place in the row order, rather than the block going to
    /// new rows. Cut then paste of a whole row therefore lands where it was.</para>
    /// </summary>
    /// <returns>The pasted clips, how many rows were made for them (not counting a speed row), and
    /// whether the speed row refused the paste.</returns>
    public static (IReadOnlyList<Guid> Pasted, int NewRows, bool Refused) Paste(Project project, Project clip, long anchor,
        TimelineGap? fitGap = null, bool restoreRows = false)
    {
        if (clip.Items.Count == 0)
            return (Array.Empty<Guid>(), 0, false);

        var gap = clip.Tracks.Count == 1 ? fitGap : null;
        if (gap != null && !CanFitInGap(project, clip, gap.Value))
            return (Array.Empty<Guid>(), 0, false);

        var offset = anchor - clip.Items.Min(i => i.TimelineStartTicks);
        var speedItems = clip.Items.Where(i => i.Content is SpeedContent).ToList();
        var speedRow = FindSpeedTrack(project);
        if (speedRow != null && speedItems.Any(s => project.Items.Any(i => i.TrackId == speedRow.Id
                && i.TimelineStartTicks < s.TimelineEndTicks + offset && s.TimelineStartTicks + offset < i.TimelineEndTicks)))
            return (Array.Empty<Guid>(), 0, true);

        // nothing refuses past here
        foreach (var source in clip.Sources)
        {
            if (project.Sources.All(s => s.Id != source.Id))
                project.Sources.Add(source);
        }

        var warpBefore = Playback.TimeWarp.Build(project);
        var pasted = new List<Item>();
        void Place(Item from, Track row, long start, long duration)
        {
            var copy = from.Clone();
            copy.TrackId = row.Id;
            copy.TimelineStartTicks = start;
            copy.DurationTicks = duration;
            project.Items.Add(copy);
            pasted.Add(copy);
        }

        foreach (var speed in speedItems)
        {
            speedRow ??= InsertNewSpeedRow(project, clip.TrackOf(speed));
            Place(speed, speedRow, speed.TimelineStartTicks + offset, speed.DurationTicks);
        }

        var warp = speedItems.Count > 0 ? Playback.TimeWarp.Build(project) : warpBefore;

        var firstOfRow = OutputClockRows(clip).ToDictionary(r => r.Key, r => r.Min(i => i.TimelineStartTicks));

        (long Start, long End) SpanOf(Item from)
        {
            var start = from.TimelineStartTicks + offset;
            if (!IsExempt(from))
                return (start, start + from.DurationTicks);

            var first = firstOfRow.GetValueOrDefault(from.TrackId, from.TimelineStartTicks) + offset;
            var output = warp.ToOutput(first) + start - first;
            return (start == first ? start : warp.ToProject(output), warp.ToProject(output + from.DurationTicks));
        }

        // where a clip already here will end once the session has re-fit it to the pasted speed items
        long EndAfterPaste(Item item)
        {
            if (!IsExempt(item) || speedItems.Count == 0)
                return item.TimelineEndTicks;
            return warp.ProjectEndOf(item.TimelineStartTicks, warpBefore.OutputSpan(item.TimelineStartTicks, item.TimelineEndTicks));
        }

        bool Fits(Track row, IEnumerable<Item> clips) => clips.All(c =>
        {
            var (start, end) = SpanOf(c);
            return CanHost(row, c.Content, project)
                   && !project.Items.Any(i => i.TrackId == row.Id && i.TimelineStartTicks < end && start < EndAfterPaste(i));
        });

        var clipsOn = clip.Items.Where(i => i.Content is not SpeedContent).ToLookup(i => i.TrackId);
        var blocks = clip.Tracks.Where(t => clipsOn.Contains(t.Id))
            .OrderBy(t => t.Order).ThenBy(t => t.Id)
            .GroupBy(t => t.GroupId ?? t.Id).Select(b => b.ToList()).ToList();

        var newRows = 0;
        foreach (var block in blocks)
        {
            if (gap is { } fit)
            {
                var row = project.Tracks.First(t => t.Id == fit.TrackId);
                foreach (var from in clipsOn[block[0].Id])
                {
                    var (start, end) = SpanOf(from);
                    var room = fit.EndTicks - start;
                    var span = end - start;
                    if (room > 0 && (span <= room || room >= MinSegmentTicks))
                        Place(from, row, start, Math.Min(span, room));
                }
                continue;
            }

            // a row that is gone stands for itself, empty, when it may be restored
            var gone = block.Where(r => project.Tracks.All(t => t.Id != r.Id)).ToList();
            var targets = block.Select(r => project.Tracks.FirstOrDefault(t => t.Id == r.Id) ?? r).ToList();
            var reuse = (gone.Count == 0 || restoreRows) && targets.All(o => Fits(o, clipsOn[o.Id]));
            if (reuse)
            {
                // back into its own slot when nothing has taken it, so no other row moves; else
                // ascending, so each one lands where it was among the rows that stayed
                foreach (var row in gone.OrderBy(r => r.Order).ThenBy(r => r.Id))
                {
                    if (project.Tracks.Any(t => t.Order == row.Order))
                        InsertTrackAt(project, row, row.Order);
                    else
                        project.Tracks.Add(row);
                }
            }

            var rows = reuse ? targets.ToDictionary(o => o.Id) : InsertNewRows(project, clip, block);
            if (!reuse)
                newRows += block.Count;

            foreach (var row in block)
            {
                foreach (var from in clipsOn[row.Id])
                {
                    var (start, end) = SpanOf(from);
                    Place(from, rows[row.Id], start, end - start);
                }
            }
        }

        // an output span can land longer than the room before a plain clip pasted on its row
        foreach (var copy in pasted.Where(IsExempt))
            copy.DurationTicks = ClampDurationToRow(project, copy, copy.DurationTicks);

        var ids = pasted.Select(i => i.Id).ToList();
        var mine = ids.ToHashSet();
        foreach (var copy in pasted)
            RebaseOverlays(project, copy, copy.TimelineStartTicks, copy.TimelineEndTicks, mine);

        project.Normalize();
        return (ids, newRows, false);
    }

    /// <summary>Whether this content plays on the output clock (<see cref="MediaContent.SpeedWarpExempt"/>).</summary>
    public static bool IsExempt(ItemContent content) => content is MediaContent { SpeedWarpExempt: true };

    private static bool IsExempt(Item item) => IsExempt(item.Content);

    /// <summary>The clip's rows holding only clips on the output clock (<see cref="Lift"/>).</summary>
    private static IEnumerable<IGrouping<Guid, Item>> OutputClockRows(Project clip) =>
        clip.Items.GroupBy(i => i.TrackId).Where(r => r.All(IsExempt));

    /// <summary>
    /// New rows for a block whose own rows cannot take it, in the block's order and as one
    /// contiguous stack per kind of row. Picture rows go directly above the block's topmost picture
    /// row, and audio rows directly after its last audio row, because the timeline lists those
    /// separately. A kind with no origin here (another project's rows, say) goes on top of the
    /// picture rows or after the audio rows, except that a wallpaper with no row here goes behind
    /// the picture, since on top it would cover it. Stacking them rather than placing each above its
    /// own origin means an opaque pasted screen covers the original overlays, which would otherwise
    /// compose over it as a second pointer. Two or more rows share a fresh group. They never join an
    /// existing one, or the paste would be time-linked to the clips it overlaps.
    /// </summary>
    private static Dictionary<Guid, Track> InsertNewRows(Project project, Project clip, List<Track> block)
    {
        Func<Project, int> Slot(Track row) =>
            row.Kind == TrackKind.Audio ? OrderAfterAudioRows
            : IsBackgroundRow(clip, row) && project.Tracks.All(t => t.Id != row.Id) ? OrderBehindPicture
            : OrderAbovePictureRows;

        var group = block.Count > 1 ? Guid.NewGuid() : (Guid?)null;
        var rows = new Dictionary<Guid, Track>();
        foreach (var ofKind in block.GroupBy(Slot))
        {
            var ids = ofKind.Select(t => t.Id).ToHashSet();
            var origins = project.Tracks.Where(t => ids.Contains(t.Id)).ToList();
            var order = origins.Count > 0 ? origins.Max(t => t.Order) + 1 : ofKind.Key(project);

            foreach (var row in ofKind)
            {
                var copy = row.Clone();
                copy.GroupId = group;
                rows[row.Id] = InsertTrackAt(project, copy, order++);
            }
        }

        return rows;
    }

    /// <summary>The speed row for a project that has none, from the row its items were copied off.
    /// There is only ever one, so unlike other new rows it is not named as a copy. And since only a
    /// row that left the project can be replaced like this, it keeps its group: a Cut removes the
    /// speed row while the group's other rows keep the id, and with none of them here the group
    /// dissolves in <see cref="CollapseLoneGroups"/>.</summary>
    private static Track InsertNewSpeedRow(Project project, Track from)
    {
        var row = from.Clone();
        row.Name = from.Name;
        row.GroupId = from.GroupId;
        return InsertTrackAt(project, row, OrderAboveEverything(project));
    }

    /// <summary>Puts <paramref name="track"/> into the row order at <paramref name="order"/>. Every
    /// row at or past it steps up one, because the order values need not be contiguous and a row
    /// sharing the slot would be placed by its id.</summary>
    public static Track InsertTrackAt(Project project, Track track, int order)
    {
        foreach (var existing in project.Tracks)
        {
            if (existing.Order >= order)
                existing.Order++;
        }

        track.Order = order;
        project.Tracks.Add(track);
        return track;
    }

    /// <summary>Whether this is the speed row: an effect row is a speed row or a zoom row by what
    /// its items are, and <see cref="Project.Validate"/> keeps it unique.</summary>
    public static bool IsSpeedTrack(Project project, Track track) =>
        track.Kind == TrackKind.Effect && project.Items.Any(i => i.TrackId == track.Id && i.Content is SpeedContent);

    /// <summary>The row carrying the project's speed items, or null.</summary>
    public static Track FindSpeedTrack(Project project) =>
        project.Tracks.FirstOrDefault(t => IsSpeedTrack(project, t));

    /// <summary>Whether a row is a background row: a video row whose earliest clip is a wallpaper,
    /// the same test the timeline classifies rows by (<c>TimelineRowLayout.KindOf</c>).</summary>
    public static bool IsBackgroundRow(Project project, Track track) =>
        track.Kind == TrackKind.Video && project.Items
            .Where(i => i.TrackId == track.Id)
            .OrderBy(i => i.TimelineStartTicks).ThenBy(i => i.Id)
            .FirstOrDefault()?.Content is BackgroundContent;

    /// <summary>The slot a new wallpaper takes: behind every other row and in front of every
    /// background row, so it is not hidden by the wallpapers there and does not hide the picture.
    /// With no background rows that is slot 0, which shifts every row up one.</summary>
    public static int OrderBehindPicture(Project project) =>
        NextOrder(project.Tracks.Where(t => IsBackgroundRow(project, t)));

    /// <summary>The slot above every picture and zoom row, under the audio rows and the speed row.</summary>
    public static int OrderAbovePictureRows(Project project) =>
        NextOrder(project.Tracks.Where(t => t.Kind != TrackKind.Audio && !IsSpeedTrack(project, t)));

    /// <summary>The slot after every row except the speed row, which stays on top.</summary>
    public static int OrderAfterAudioRows(Project project) =>
        NextOrder(project.Tracks.Where(t => !IsSpeedTrack(project, t)));

    /// <summary>The slot after every row.</summary>
    public static int OrderAboveEverything(Project project) => NextOrder(project.Tracks);

    private static int NextOrder(IEnumerable<Track> rows) => rows.Select(t => t.Order + 1).DefaultIfEmpty(0).Max();

    // --------------------------------------------------------------------------------- groups

    /// <summary>How many rows of this row's group hold clips, this row included: the count a group
    /// is talked about in, since an emptied row is not a member
    /// (<see cref="CollapseLoneGroups"/>).</summary>
    public static int OccupiedGroupRows(Project project, Guid trackId)
    {
        var occupied = project.Items.Select(i => i.TrackId).ToHashSet();
        return GroupTracks(project, trackId).Count(t => occupied.Contains(t.Id));
    }

    /// <summary>Whether <see cref="MergeIntoGroup"/> would do anything: at least two rows, not all
    /// in one group already.</summary>
    public static bool CanMergeIntoGroup(Project project, IEnumerable<Guid> trackIds)
    {
        var ids = trackIds.ToHashSet();
        var rows = project.Tracks.Where(t => ids.Contains(t.Id)).ToList();
        return rows.Count >= 2 && (rows[0].GroupId == null || rows.Any(t => t.GroupId != rows[0].GroupId));
    }

    /// <summary>
    /// Puts these rows, and every row of any group they touch, into one group, so grouping never
    /// takes a row out of a group it was in. The group of the lowest-order row that has one is kept,
    /// and a new one is made when none has. Any kind of row can join. Returns how many rows of the
    /// result hold clips, or 0 when <see cref="CanMergeIntoGroup"/> is false.
    /// </summary>
    public static int MergeIntoGroup(Project project, IEnumerable<Guid> trackIds)
    {
        var ids = trackIds.ToHashSet();
        if (!CanMergeIntoGroup(project, ids))
            return 0;

        var rows = project.Tracks.Where(t => ids.Contains(t.Id)).OrderBy(t => t.Order).ToList();
        var merged = rows.FirstOrDefault(t => t.GroupId != null)?.GroupId ?? Guid.NewGuid();
        var touched = rows.Where(t => t.GroupId != null).Select(t => t.GroupId).ToHashSet();
        foreach (var track in project.Tracks)
        {
            if (ids.Contains(track.Id) || touched.Contains(track.GroupId))
                track.GroupId = merged;
        }

        return OccupiedGroupRows(project, rows[0].Id);
    }

    /// <summary>Takes these rows out of their groups. Their partners stay grouped to each other, and
    /// <see cref="CollapseLoneGroups"/> dissolves what is left when only one of them holds clips.
    /// Returns how many rows came out.</summary>
    public static int UngroupTracks(Project project, IEnumerable<Guid> trackIds)
    {
        var ids = trackIds.ToHashSet();
        var removed = 0;
        foreach (var track in project.Tracks)
        {
            if (track.GroupId != null && ids.Contains(track.Id))
            {
                track.GroupId = null;
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Dissolves every group these rows belong to, on all of its rows. Returns how many of
    /// those rows hold clips.</summary>
    public static int UngroupAll(Project project, IEnumerable<Guid> trackIds)
    {
        var ids = trackIds.ToHashSet();
        var groups = project.Tracks.Where(t => t.GroupId != null && ids.Contains(t.Id)).Select(t => t.GroupId).ToHashSet();
        var occupied = project.Items.Select(i => i.TrackId).ToHashSet();
        var removed = 0;
        foreach (var track in project.Tracks)
        {
            if (track.GroupId != null && groups.Contains(track.GroupId))
            {
                track.GroupId = null;
                if (occupied.Contains(track.Id))
                    removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Dissolves every group with exactly one row holding clips, on all its rows: a group exists
    /// to keep rows in step, so with one row left (the audio rows deleted, the webcam ungrouped, a
    /// recording that never had anything but a screen) there is nothing left to keep in step. An
    /// emptied row does not count as a member, and a group whose rows are all empty is left alone,
    /// so a later paste can rejoin it. Returns true when anything changed.
    /// </summary>
    public static bool CollapseLoneGroups(Project project)
    {
        var changed = false;
        var occupied = project.Items.Select(i => i.TrackId).ToHashSet();
        foreach (var rows in project.Tracks.Where(t => t.GroupId != null).GroupBy(t => t.GroupId.Value))
        {
            if (rows.Count(t => occupied.Contains(t.Id)) != 1)
                continue;

            foreach (var track in rows)
                track.GroupId = null;
            changed = true;
        }

        return changed;
    }

    private static bool Overlaps(Item a, Item b) =>
        a.TimelineStartTicks < b.TimelineEndTicks && b.TimelineStartTicks < a.TimelineEndTicks;

    /// <summary>
    /// Sets a clip's <see cref="SourceClockContent.Speed"/>, re-timing it in place: the item
    /// keeps showing the same stretch of source, so its timeline duration scales by
    /// <c>oldSpeed / newSpeed</c>, anchored at its start. The new duration is clamped to at least
    /// <see cref="MinSegmentTicks"/> and to the gap before the next item on the track (slowing a
    /// clip down must not run it into its neighbor — the content is end-trimmed instead). Single
    /// item, on clips with a clock only (a freeze frame has no speed), though overlays on a screen
    /// clip's group rows follow it (<see cref="RebaseOverlays"/>). Returns the speed actually stored
    /// (1.0 for other contents and freeze frames).
    /// </summary>
    public static double SetSpeed(Project project, Guid itemId, double speed)
    {
        var item = Require(project, itemId);
        if (item.Content is not SourceClockContent clock || clock.Freeze)
            return 1.0;

        speed = Math.Clamp(speed, 0.01, 100);
        var oldSpeed = SpeedOf(clock);
        if (speed == oldSpeed)
            return speed;

        var oldEnd = item.TimelineEndTicks;
        var sourceSpan = SourceTicksBetween(project, clock, item.TimelineStartTicks, item.TimelineEndTicks);
        long newDuration;
        if (IsExempt(clock))
        {
            // the same source plays out over sourceSpan / speed OUTPUT ticks; the project span
            // that covers is whatever the warp makes of it from the item's start.
            var warp = Playback.TimeWarp.Build(project);
            newDuration = warp.ProjectEndOf(item.TimelineStartTicks, (long)Math.Round(sourceSpan / speed)) - item.TimelineStartTicks;
        }
        else
        {
            newDuration = (long)Math.Round(sourceSpan / speed);
        }

        clock.Speed = speed;
        item.DurationTicks = ClampDurationToRow(project, item, newDuration);
        RebaseOverlays(project, item, item.TimelineStartTicks, Math.Max(oldEnd, item.TimelineEndTicks));
        return speed;
    }

    /// <summary>A duration <paramref name="item"/> may take without breaking the model: at
    /// least <see cref="MinSegmentTicks"/>, and short of the next item on its row. The clamp
    /// every re-timing applies (<see cref="SetSpeed"/>, <see cref="FitExemptItem"/>): a duration
    /// that ran the clip into its neighbor would be rolled back by validation, and end-trimming
    /// the clip is the useful answer. Only for a clip whose new length is what reaches the
    /// neighbor, which <see cref="FitExemptItem"/> makes sure of.</summary>
    public static long ClampDurationToRow(Project project, Item item, long duration)
    {
        var limit = long.MaxValue;
        foreach (var other in project.Items)
        {
            if (other.Id != item.Id && other.TrackId == item.TrackId &&
                other.TimelineStartTicks > item.TimelineStartTicks)
                limit = Math.Min(limit, other.TimelineStartTicks - item.TimelineStartTicks);
        }

        return Math.Clamp(duration, MinSegmentTicks, Math.Max(MinSegmentTicks, limit));
    }

    /// <summary>The item's playback speed with the model's "unset means realtime" collapsed:
    /// always a positive factor, 1.0 for a null clock.</summary>
    public static double SpeedOf(SourceClockContent clock) =>
        clock != null && clock.Speed > 0 ? clock.Speed : 1.0;

    /// <summary>A timeline span rendered into source ticks at <paramref name="speed"/> — exact
    /// for realtime so speed-1 projects keep their integer-perfect math.</summary>
    public static long ToSourceTicks(long timelineTicks, double speed) =>
        speed == 1.0 ? timelineTicks : (long)Math.Round(timelineTicks * speed);

    /// <summary>
    /// The source ticks a clip consumes between two project instants. For an ordinary clip that is
    /// the project span at the clip's speed; for a <see cref="MediaContent.SpeedWarpExempt"/>
    /// clip it is the OUTPUT span between the two instants (the clip plays in real time under
    /// the project's speed items) at the clip's speed. Every operation that moves an in-point
    /// (trim, split, cut) goes through here so the two clocks cannot drift apart.
    /// </summary>
    public static long SourceTicksBetween(Project project, SourceClockContent clock, long fromProjectTicks,
        long toProjectTicks)
    {
        if (clock is { Freeze: true })
            return 0;

        var speed = SpeedOf(clock);
        if (!IsExempt(clock))
            return ToSourceTicks(toProjectTicks - fromProjectTicks, speed);

        var warp = Playback.TimeWarp.Build(project);
        return ToSourceTicks(warp.OutputSpan(fromProjectTicks, toProjectTicks), speed);
    }

    /// <summary>How far an exempt clip's start may move earlier before its in-point would rewind
    /// past the start of its source, in project ticks: the source it has behind the in-point
    /// plays out in output ticks, mapped back through the warp from the clip's output start.</summary>
    private static long ExemptStartHeadroom(Project project, Item item, SourceClockContent clock, double speed)
    {
        var warp = Playback.TimeWarp.Build(project);
        var outputStart = warp.ToOutput(item.TimelineStartTicks);
        var roomOutput = (long)Math.Floor(clock.SourceInTicks / speed);
        var earliest = warp.ToProject(Math.Max(0, outputStart - roomOutput));
        return Math.Max(0, item.TimelineStartTicks - earliest);
    }

    // --------------------------------------------------------------------------- overlay clocks

    /// <summary>
    /// Re-derives the clocks of the overlays that <paramref name="screen"/>'s row keeps in step
    /// over <c>[from, to)</c>, from the screen clips now at those times: what an edit that changes
    /// which source a stretch of a grouped row shows ends with. A move or a cut leaves each mapping
    /// alone and needs none of this. A no-op unless <paramref name="screen"/> is a screen-stream
    /// clip on a grouped video row. Reaches the cursor and keys clips of that source on the row's
    /// group, and a clip's stretch with no screen clip keeps the clock it had. Where several rows of
    /// the group play the screen at once the backmost leads. Overlays in <paramref name="except"/>
    /// keep their clocks. See <see cref="OverlayClock.Rederive"/>.
    /// </summary>
    public static void RebaseOverlays(Project project, Item screen, long from, long to, IReadOnlySet<Guid> except = null)
    {
        if (ScreenSourceOf(project, screen) is { } source)
            RebaseSource(project, source, project.TrackOf(screen), from, to, except);
    }

    /// <summary>The recording whose screen stream <paramref name="item"/> plays, or null when it
    /// plays anything else.</summary>
    private static Source ScreenSourceOf(Project project, Item item) =>
        item.Content is MediaContent media && FrameComposer.FindSource(project, media.SourceId) is { } source
            && FrameComposer.IsScreenStream(source, media.StreamIndex)
            ? source
            : null;

    /// <summary><see cref="RebaseOverlays"/> for one recording on <paramref name="row"/>'s group,
    /// whatever clip the edit started from.</summary>
    private static void RebaseSource(Project project, Source source, Track row, long from, long to,
        IReadOnlySet<Guid> except = null)
    {
        if (row is not { Kind: TrackKind.Video, GroupId: not null })
            return;

        // in row order, so that the search below finds the backmost first
        project.Normalize();

        var rows = GroupTracks(project, row.Id).Where(t => t.Kind == TrackKind.Video).Select(t => t.Id).ToHashSet();
        var hosts = project.Items.Where(i => rows.Contains(i.TrackId) && FrameComposer.PlaysScreen(source, i)).ToList();
        var overlays = project.Items.Where(i => i.Content is InputOverlayContent overlay
            && overlay.SourceId == source.Id && rows.Contains(i.TrackId)
            && i.TimelineStartTicks < to && i.TimelineEndTicks > from && except?.Contains(i.Id) != true).ToList();

        foreach (var overlay in overlays)
            OverlayClock.Rederive(project, overlay, from, to, t => hosts.FirstOrDefault(h => h.Covers(t)));

        project.Normalize();
    }

    /// <summary>
    /// The stretches of a cursor clip where <c>FrameComposer.ResolveCursorHost</c> finds a clip to
    /// draw through: in timeline order, half-open, merged, and limited to the clip's own span.
    /// Anywhere else the cursor draws nothing, and the timeline dims that part of the bar. Empty for
    /// a clip that is not a cursor, or whose source is gone.
    /// </summary>
    public static IReadOnlyList<(long Start, long End)> CursorHostSpans(Project project, Item cursorItem)
    {
        var spans = new List<(long Start, long End)>();
        if (cursorItem?.Content is not CursorContent cursor
            || FrameComposer.FindSource(project, cursor.SourceId) is not { } source)
            return spans;

        var hosts = project.Items
            .Where(i => i.TimelineStartTicks < cursorItem.TimelineEndTicks
                        && cursorItem.TimelineStartTicks < i.TimelineEndTicks
                        && FrameComposer.IsCursorHost(project, source, i))
            .OrderBy(i => i.TimelineStartTicks);
        foreach (var host in hosts)
        {
            var start = Math.Max(host.TimelineStartTicks, cursorItem.TimelineStartTicks);
            var end = Math.Min(host.TimelineEndTicks, cursorItem.TimelineEndTicks);
            if (spans.Count > 0 && start <= spans[^1].End)
                spans[^1] = (spans[^1].Start, Math.Max(spans[^1].End, end));
            else
                spans.Add((start, end));
        }

        return spans;
    }

    /// <summary><see cref="RebaseOverlays"/> over what a trim added to <paramref name="item"/>, which
    /// spanned <c>[oldStart, oldEnd)</c> before it. Revealing more of a clip does not change its
    /// mapping, but an overlay can already be there on a clock of its own: left behind by a speed
    /// change, or hanging off a neighbour that was trimmed shorter. What it had there was not this
    /// clip's time, so it takes this clip's. Nothing that shrank needs this, and neither does an
    /// overlay already in step, so a trim back out over its own overlay changes nothing.</summary>
    public static void RebaseGrownSpan(Project project, Item item, long oldStart, long oldEnd)
    {
        if (item.TimelineStartTicks < oldStart)
            RebaseOverlays(project, item, item.TimelineStartTicks, oldStart);
        if (item.TimelineEndTicks > oldEnd)
            RebaseOverlays(project, item, oldEnd, item.TimelineEndTicks);
    }

    private static Item Require(Project project, Guid itemId)
    {
        var item = project.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
            throw new ArgumentException($"Item {itemId} is not in the project.", nameof(itemId));

        return item;
    }
}
