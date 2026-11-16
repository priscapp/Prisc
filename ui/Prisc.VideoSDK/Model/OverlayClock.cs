using System;
using System.Collections.Generic;
using Prisc.VideoSDK.Composition;

namespace Prisc.VideoSDK.Model;

/// <summary>
/// Re-derives a cursor or keys clip's clock from the screen clips under it. Migration uses it to
/// give an old overlay the clock it was implicitly showing, and <see cref="TimelineOps.RebaseOverlays"/>
/// after an edit that changes what a grouped screen row shows.
/// </summary>
public static class OverlayClock
{
    /// <summary>How far apart two source times may be and still count as the same instant: the
    /// resolution of the capture rows they look up. Fractional speeds round each clip's in-point to
    /// a whole tick, so two aligned clocks are rarely exactly equal.</summary>
    public const long SameInstantTicks = TimeSpan.TicksPerMillisecond;

    public static bool SameInstant(long a, long b) => Math.Abs(a - b) <= SameInstantTicks;

    /// <summary>
    /// Walks <paramref name="overlay"/> over the part of <c>[from, to)</c> it covers, asking
    /// <paramref name="hostAt"/> for the clip to follow at each stretch. <paramref name="hostAt"/>
    /// answers from the source's clips, so its answer only changes at their edges. A stretch with a
    /// host takes the host's source time, speed and freeze; one without keeps the clock the overlay
    /// had, as does everything outside the span.
    ///
    /// <para>Stretches whose mapping continues into each other stay one clip, so a screen cut in two
    /// where nothing changes does not cut its overlay, and an overlay already in step is left exactly
    /// as it was. A discontinuity splits the clip, and the first part keeps the clip's id. Its
    /// transitions then follow how each kind spends them: a cursor fades as a block, so entry stays
    /// on the first part and exit on the last, while keys animate each row of keystrokes rather than
    /// the block, so every part keeps both. A cursor fade longer than its part is clamped to the part
    /// (<see cref="Composition.TransitionMath"/>), so one spanning a discontinuity finishes with the
    /// part rather than carrying on into the next. New parts go on the end of <see cref="Project.Items"/>
    /// for the caller to normalize. Parts can be shorter than
    /// <see cref="TimelineOps.MinSegmentTicks"/> when the host edges are that close: keeping the
    /// pixels matters more here than the rule for what an edit may produce.</para>
    /// </summary>
    public static void Rederive(Project project, Item overlay, long from, long to, Func<long, Item> hostAt)
    {
        if (overlay.Content is not SourceClockContent own)
            return;

        var start = Math.Max(from, overlay.TimelineStartTicks);
        var end = Math.Min(to, overlay.TimelineEndTicks);
        if (start >= end)
            return;

        var parts = new List<Part>();
        var edges = Edges(project, overlay, own.SourceId, start, end);
        for (var i = 0; i + 1 < edges.Count; i++)
        {
            var at = edges[i];
            var host = at >= start && at < end ? hostAt(at) : null;
            var kept = Part.Of(own, overlay, at);
            // what is already in step stays on its own clock, so rounding does not nudge it
            var part = host?.Content is SourceClockContent hostClock && !kept.Continues(at, Part.Of(hostClock, host, at))
                ? Part.Of(hostClock, host, at)
                : kept;

            if (parts.Count > 0 && parts[^1].Continues(at, part))
                continue;
            parts.Add(part);
        }

        Apply(project, overlay, own, parts, edges[^1]);
    }

    /// <summary>The sorted edges of the overlay's stretches: its own ends, the span's, and every edge
    /// of a clip of the source inside the span.</summary>
    private static List<long> Edges(Project project, Item overlay, Guid sourceId, long start, long end)
    {
        var edges = new SortedSet<long> { overlay.TimelineStartTicks, start, end, overlay.TimelineEndTicks };
        foreach (var item in project.Items)
        {
            if (item?.Content is not MediaContent media || media.SourceId != sourceId)
                continue;
            if (item.TimelineStartTicks > start && item.TimelineStartTicks < end)
                edges.Add(item.TimelineStartTicks);
            if (item.TimelineEndTicks > start && item.TimelineEndTicks < end)
                edges.Add(item.TimelineEndTicks);
        }

        return new List<long>(edges);
    }

    private static void Apply(Project project, Item overlay, SourceClockContent own, List<Part> parts, long overlayEnd)
    {
        var exit = overlay.Exit;
        var isKeys = overlay.Content is KeyboardContent;

        for (var i = 1; i < parts.Count; i++)
        {
            var piece = overlay.Clone();
            piece.TimelineStartTicks = parts[i].Start;
            piece.DurationTicks = (i + 1 < parts.Count ? parts[i + 1].Start : overlayEnd) - parts[i].Start;
            parts[i].WriteTo((SourceClockContent)piece.Content);
            if (!isKeys)
            {
                piece.Entry = null;
                piece.Exit = i + 1 == parts.Count ? exit?.Clone() : null;
            }
            project.Items.Add(piece);
        }

        parts[0].WriteTo(own);
        if (parts.Count > 1)
        {
            overlay.DurationTicks = parts[1].Start - overlay.TimelineStartTicks;
            if (!isKeys)
                overlay.Exit = null;
        }
    }

    /// <summary>One stretch's clock: the source time at its start, and how time runs from there.</summary>
    private readonly record struct Part(long Start, long SourceIn, double Speed, bool Freeze)
    {
        public static Part Of(SourceClockContent clock, Item item, long at) => new Part(at,
            FrameComposer.SourceTimeTicks(clock, item, at), TimelineOps.SpeedOf(clock), clock.Freeze);

        /// <summary>Whether <paramref name="next"/> is what this clock already shows at
        /// <paramref name="at"/>.</summary>
        public bool Continues(long at, in Part next)
        {
            if (Freeze != next.Freeze || (!Freeze && Speed != next.Speed))
                return false;

            var elapsed = at - Start;
            var here = Freeze ? SourceIn
                : SourceIn + TimelineOps.ToSourceTicks(elapsed, Speed);
            return SameInstant(here, next.SourceIn);
        }

        public void WriteTo(SourceClockContent clock)
        {
            clock.SourceInTicks = SourceIn;
            clock.Speed = Speed;
            clock.Freeze = Freeze;
        }
    }
}
