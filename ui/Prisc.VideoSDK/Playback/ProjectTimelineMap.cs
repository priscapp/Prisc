using System;
using System.Collections.Generic;
using Prisc.VideoSDK.Model;
using LaneKey = (System.Guid SourceId, int StreamIndex, int Lane);

namespace Prisc.VideoSDK.Playback
{
    /// <summary>
    /// The precomputed timeline-time ↔ source-time mapping <see cref="CompositionPlayer"/> plays a
    /// <see cref="Project"/>'s <b>video</b> streams through. Immutable — rebuilt on every
    /// <c>UpdateProject</c> and swapped atomically, so decode/present threads read it lock-free.
    /// Audio has no mapping here: it is mixed in timeline time by <see cref="AudioMixWorker"/>
    /// (every unmuted stream audible, gaps silenced, per-sample gain — the render mixer's own
    /// semantics), so audio tracks contribute only to <see cref="DurationTicks"/>.
    ///
    /// Per lane of each referenced video stream (see <see cref="StreamLanes"/>: one lane is what one
    /// decoder can serve) it holds the item spans as <see cref="Segment"/>s (timeline-ordered),
    /// giving the same timeline→source mapping <c>FrameComposer</c> uses
    /// (<c>src = SourceIn + (t - TimelineStart)</c>), plus the inverse used to pace source-stamped
    /// frames against the timeline clock, and the source the lane must never surface as a
    /// <see cref="SkipRangeSchedule"/> (the spans no item plays — a cut made in the editor).
    ///
    /// The segments of a lane can run in any source order and repeat a span: a pasted clip does
    /// both. A source instant played twice therefore has two timeline instants, and the pacing map
    /// picks by where the playhead is. They do not overlap at different source times, up to the
    /// lane cap. The mapping degrades gracefully where a timeline has gaps — a source instant no
    /// segment plays maps to the seam, a timeline instant in a gap maps to the next segment's
    /// in-point.
    /// </summary>
    internal sealed class ProjectTimelineMap
    {
        /// <summary>One item's span: timeline [TlStart, TlEnd) filled from source [SrcIn, SrcEnd),
        /// consumed at <see cref="Speed"/> source ticks per timeline tick (1 = realtime).</summary>
        internal readonly struct Segment
        {
            public Segment(long tlStart, long tlEnd, long srcIn, double speed = 1.0, bool freeze = false)
            {
                TlStart = tlStart;
                TlEnd = tlEnd;
                SrcIn = srcIn;
                Speed = speed > 0 ? speed : 1.0;
                Freeze = freeze;
            }

            public long TlStart { get; }
            public long TlEnd { get; }
            public long SrcIn { get; }
            public double Speed { get; }

            /// <summary>A freeze frame (<see cref="MediaContent.Freeze"/>): the whole span shows
            /// the frame at <see cref="SrcIn"/> and consumes no source, so the segment is empty in
            /// the source domain — it neither paces a frame nor bounds a cut.</summary>
            public bool Freeze { get; }

            public long SrcEnd => SrcIn + ToSource(TlEnd - TlStart);

            /// <summary>Source minus timeline at the segment's start. No longer the per-instant
            /// offset once <see cref="Speed"/> ≠ 1 — it serves as the segment's identity for seam
            /// detection (the player only ever compares it across instants), never arithmetic.</summary>
            public long Offset => Freeze ? FreezeIdentity(TlStart) : SrcIn - TlStart;

            /// <summary>A freeze segment's seam identity: unique per start instant and far below
            /// any real source−timeline offset, so entering or leaving a hold always reads as a
            /// discontinuity — the hop is what puts the held frame on screen. Never
            /// <see cref="long.MinValue"/>, which means "nothing covers the instant".</summary>
            private static long FreezeIdentity(long tlStart) => long.MinValue + 1 + tlStart;

            /// <summary>Whether the segment plays at this timeline instant.</summary>
            public bool Covers(long tlTicks) => tlTicks >= TlStart && tlTicks < TlEnd;

            /// <summary>Whether the segment plays this source instant. A hold plays none.</summary>
            public bool Plays(long srcTicks) => srcTicks >= SrcIn && srcTicks < SrcEnd;

            /// <summary>Where this segment's mapping puts a source instant on the timeline.</summary>
            public long TimelineOf(long srcTicks) => TlStart + ToTimeline(srcTicks - SrcIn);

            /// <summary>A timeline span inside this segment, rendered into source ticks.</summary>
            public long ToSource(long timelineTicks) =>
                Freeze ? 0 : Speed == 1.0 ? timelineTicks : (long)Math.Round(timelineTicks * Speed);

            /// <summary>A source span inside this segment, rendered into timeline ticks.</summary>
            public long ToTimeline(long sourceTicks) =>
                Speed == 1.0 ? sourceTicks : (long)Math.Round(sourceTicks / Speed);
        }

        /// <summary>The mapping for one (sourceId, streamIndex, lane).</summary>
        internal sealed class StreamMap
        {
            private readonly Segment[] _segments; // timeline order

            public StreamMap(List<Segment> segments)
            {
                segments.Sort((a, b) => a.TlStart.CompareTo(b.TlStart));
                _segments = segments.ToArray();
                SourceCuts = BuildCuts(_segments);
            }

            public IReadOnlyList<Segment> Segments => _segments;

            /// <summary>Source spans no segment plays (the cut-out material), in source time. Frames
            /// whose pts land here must never surface in the preview.</summary>
            public SkipRangeSchedule SourceCuts { get; }

            /// <summary>Timeline instant → source instant, mirroring the composer's per-item
            /// mapping. Clamps: before the first segment → its in-point, inside a timeline gap →
            /// the next segment's in-point, past the last segment → its last kept source
            /// instant (source end − 1).</summary>
            public long TimelineToSource(long tlTicks)
            {
                if (_segments.Length == 0)
                    return tlTicks;

                foreach (var seg in _segments)
                {
                    if (tlTicks < seg.TlStart)
                        return seg.SrcIn; // before this segment (leading edge or a gap)
                    if (tlTicks < seg.TlEnd)
                        return seg.SrcIn + seg.ToSource(tlTicks - seg.TlStart);
                }

                // Past the last segment: clamp to the last KEPT source instant, not to SrcEnd.
                // SrcEnd is the exclusive out-point — the first instant the edit removed — and
                // seeking a decode pipeline there presents the first trimmed-away frame (the
                // preview would show material a render never contains). SrcEnd − 1 combined
                // with the workers' floor-to-covering-frame exact-seek discard resolves to the
                // last kept frame instead. The last to end, which a shorter copy inside it is not.
                var last = _segments[0];
                foreach (var seg in _segments)
                {
                    if (seg.TlEnd >= last.TlEnd)
                        last = seg;
                }

                return Math.Max(last.SrcIn, last.SrcEnd - 1);
            }

            /// <summary>Source instant → timeline instant (the pacing map handed to the decode
            /// workers). A span played twice has two answers, and the one closest to
            /// <paramref name="nearTlTicks"/> (the playhead) is the frame's: nothing else tells a
            /// frame of the original from the same frame of its copy. A source instant no segment
            /// plays clamps to the seam (the end of the segment preceding it in source order);
            /// before all segments clamps to the first segment's start.</summary>
            public long SourceToTimeline(long srcTicks, long nearTlTicks)
            {
                long nearest = long.MinValue;
                long nearestDistance = long.MaxValue;
                long seam = long.MinValue;
                long seamSrcEnd = long.MinValue;
                foreach (var seg in _segments)
                {
                    if (seg.Plays(srcTicks))
                    {
                        long tl = seg.TimelineOf(srcTicks);
                        long distance = Math.Abs(tl - nearTlTicks);
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearest = tl;
                        }
                    }
                    else if (seg.SrcEnd <= srcTicks && seg.SrcEnd > seamSrcEnd)
                    {
                        seamSrcEnd = seg.SrcEnd;
                        seam = seg.TlEnd;
                    }
                }

                if (nearestDistance != long.MaxValue)
                    return nearest;
                if (seam != long.MinValue)
                    return seam;
                return _segments.Length > 0 ? _segments[0].TlStart : srcTicks;
            }

            /// <summary>The timeline instant of a frame the segment covering
            /// <paramref name="tlTicks"/> plays itself; false when nothing covers the instant or that
            /// segment does not play the frame. That is what a landed seek's frame must pass before
            /// it can say where the playhead is. Past the footage it is only the last kept frame. A
            /// hold plays no source, so its frame says nothing about the instant inside it. And a clip
            /// that starts between two frames opens on the frame before its in-point, which belongs
            /// to whatever else plays that source.</summary>
            public bool TryMapInSegmentAt(long tlTicks, long srcTicks, out long frameTlTicks)
            {
                foreach (var seg in _segments)
                {
                    if (seg.Covers(tlTicks))
                    {
                        frameTlTicks = seg.TimelineOf(srcTicks);
                        return seg.Plays(srcTicks);
                    }
                }

                frameTlTicks = 0;
                return false;
            }

            /// <summary>The timeline instant of the frame a forward step just presented, from the
            /// playhead <paramref name="fromTlTicks"/>. After playback the frame can sit a few ms
            /// behind the stopped clock, so the clip under the playhead answers first: a frame it
            /// plays is at its instant there, even behind the playhead, and a frame from before its
            /// in-point has yet to reach it, so the playhead holds. Any other frame goes to the first
            /// place at or after the playhead that plays it. A frame played only behind the playhead
            /// means the step ran off the end of a repeat, so the playhead stops at that clip's end,
            /// where its seam then re-syncs the decoder. Off every clip it goes where the pacing map
            /// says, unless that is behind the playhead.</summary>
            public long StepForward(long srcTicks, long fromTlTicks)
            {
                long next = long.MaxValue;
                long coveringEnd = long.MinValue;
                bool beforeCovering = false;
                foreach (var seg in _segments)
                {
                    bool plays = seg.Plays(srcTicks);
                    long tl = seg.TimelineOf(srcTicks);
                    if (seg.Covers(fromTlTicks))
                    {
                        if (plays)
                            return tl;
                        if (coveringEnd == long.MinValue)
                        {
                            coveringEnd = seg.TlEnd;
                            beforeCovering = srcTicks < seg.SrcIn;
                        }
                    }

                    if (plays && tl >= fromTlTicks && tl < next)
                        next = tl;
                }

                if (beforeCovering)
                    return fromTlTicks;
                if (next != long.MaxValue)
                    return next;
                return coveringEnd != long.MinValue
                    ? coveringEnd
                    : Math.Max(fromTlTicks, SourceToTimeline(srcTicks, fromTlTicks));
            }

            /// <summary>The source-timeline offset of the segment covering the timeline instant,
            /// or <see cref="long.MinValue"/> when nothing covers it (a gap, or outside the
            /// stream's span). A change in this value across a played seam is exactly a source
            /// discontinuity — the player's cue to hop the decode pipeline.</summary>
            public long OffsetAtTimeline(long tlTicks)
            {
                foreach (var seg in _segments)
                {
                    if (seg.Covers(tlTicks))
                        return seg.Offset;
                }

                return long.MinValue;
            }

            private static SkipRangeSchedule BuildCuts(Segment[] segments)
            {
                // the complement of what is played, whatever order it is played in. A hold plays no
                // source, so it bounds nothing: a freeze between the halves of a split cuts nothing.
                var played = Array.FindAll(segments, s => !s.Freeze);
                if (played.Length == 0)
                    return SkipRangeSchedule.Empty;

                long playedUntil = 0;
                foreach (var seg in played)
                    playedUntil = Math.Max(playedUntil, seg.TlEnd);

                Array.Sort(played, (a, b) => a.SrcIn.CompareTo(b.SrcIn));
                List<TimeRange> ranges = null;
                long coveredTo = played[0].SrcEnd;
                for (int i = 1; i < played.Length; i++)
                {
                    if (played[i].SrcIn > coveredTo)
                        (ranges ??= new List<TimeRange>()).Add(
                            new TimeRange(new TimeSpan(coveredTo), new TimeSpan(played[i].SrcIn)));
                    coveredTo = Math.Max(coveredTo, played[i].SrcEnd);
                }

                // Past the furthest source played is cut too when a segment that reaches it hands
                // over to another one: the decoder runs on into that material until the hop lands.
                // After the timeline's last segment nothing follows, which is every source-ordered
                // lane, and frames past its end stay as they always were.
                if (Array.Exists(played, s => s.SrcEnd == coveredTo && s.TlEnd < playedUntil))
                    (ranges ??= new List<TimeRange>()).Add(
                        new TimeRange(new TimeSpan(coveredTo), TimeSpan.MaxValue));

                return ranges == null ? SkipRangeSchedule.Empty : new SkipRangeSchedule(ranges);
            }
        }

        private readonly Dictionary<LaneKey, StreamMap> _video;

        private ProjectTimelineMap(
            Dictionary<LaneKey, StreamMap> video,
            LaneKey? primaryVideo,
            long durationTicks)
        {
            _video = video;
            PrimaryVideo = primaryVideo;
            DurationTicks = durationTicks;
        }

        public IReadOnlyDictionary<LaneKey, StreamMap> VideoStreams => _video;

        /// <summary>The lane whose immediate-present frames define the paused position: that of the
        /// first media item on the lowest-order visible video track (the screen recording, in a
        /// recording project).</summary>
        public LaneKey? PrimaryVideo { get; }

        /// <summary>Timeline length — the whole project's, so audio (or text/image) items running
        /// past the last video frame still count.</summary>
        public long DurationTicks { get; }

        public bool TryGetVideo(LaneKey key, out StreamMap map) => _video.TryGetValue(key, out map);

        public static ProjectTimelineMap Build(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);

            var tracks = new List<Track>(project.Tracks ?? new List<Track>());
            tracks.Sort((a, b) =>
            {
                int byOrder = a.Order.CompareTo(b.Order);
                return byOrder != 0 ? byOrder : a.Id.CompareTo(b.Id);
            });

            var items = project.Items ?? new List<Item>();

            var lanes = StreamLanes.Assign(project);
            var videoSegments = new Dictionary<LaneKey, List<Segment>>();
            LaneKey? primary = null;

            foreach (var track in tracks)
            {
                if (track.Kind != TrackKind.Video || track.Hidden)
                    continue;

                // items of this track in timeline order (Project.Normalize sorts, but do not rely on it)
                var trackItems = new List<Item>();
                foreach (var item in items)
                {
                    if (item.TrackId == track.Id && item.Content is MediaContent && item.DurationTicks > 0)
                        trackItems.Add(item);
                }

                trackItems.Sort((a, b) => a.TimelineStartTicks.CompareTo(b.TimelineStartTicks));

                foreach (var item in trackItems)
                {
                    var media = (MediaContent)item.Content;
                    LaneKey key = (media.SourceId, media.StreamIndex, StreamLanes.LaneOf(lanes, item.Id));
                    var segment = new Segment(item.TimelineStartTicks, item.TimelineEndTicks,
                        media.SourceInTicks, TimelineOps.SpeedOf(media), media.Freeze);

                    if (!videoSegments.TryGetValue(key, out var list))
                        videoSegments[key] = list = new List<Segment>();
                    list.Add(segment);
                    primary ??= key;
                }
            }

            var video = new Dictionary<LaneKey, StreamMap>();
            foreach (var (key, segments) in videoSegments)
                video[key] = new StreamMap(segments);

            return new ProjectTimelineMap(video, primary, project.GetDurationTicks());
        }
    }
}
