using System;
using System.Collections.Generic;
using Prisc.VideoSDK.Model;

namespace Prisc.VideoSDK.Playback
{
    /// <summary>
    /// Sorts the clips of each (source, stream) into <b>lanes</b>: sets of clips one decoder can
    /// serve. A decoder holds one position at a time, so two clips that play a stream at once
    /// from different source times need a decoder each; every other arrangement, a repeat past
    /// the end of a row included, is one decoder hopping at its seams, which is how cuts have
    /// always worked. Almost every project is therefore all lane 0, and lane 0 is the path every
    /// source had before lanes existed.
    ///
    /// <para>The clips of a stream are walked in timeline order, ties in track order, and each
    /// takes the lowest lane where it overlaps nothing, or, for video, where every overlap shows
    /// the same source time, since those clips can share a frame. Audio never shares: each read
    /// consumes the stream position. Video reads visible video rows, audio unmuted audio rows.
    /// Past <see cref="MaxLanes"/> a clip takes the last lane, where render still gets the right
    /// frames through its seek path and the preview shows that lane's one frame.</para>
    ///
    /// <para>A pure function of the project, so the composer, the mixer and the player's timeline
    /// map agree on a project by each calling it. A different project gets different lanes: what
    /// composes an edited copy against a player's decoders (the crop draw's isolated copy) takes
    /// the lanes of the player's project.</para>
    /// </summary>
    public static class StreamLanes
    {
        /// <summary>Lanes per (source, stream): the most decoders one stream can cost.</summary>
        public const int MaxLanes = 4;

        private static readonly IReadOnlyDictionary<Guid, int> NoLanes = new Dictionary<Guid, int>();

        /// <summary>Item id to lane, holding only the clips above lane 0: an absent item is in
        /// lane 0, whether it is a clip of lane 0 or anything else (see
        /// <see cref="LaneOf"/>).</summary>
        public static IReadOnlyDictionary<Guid, int> Assign(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);
            if (project.Tracks == null || project.Items == null)
                return NoLanes;

            var clips = CollectClips(project);
            clips.Sort(CompareClips);

            Dictionary<Guid, int> assigned = null;
            var streams = new Dictionary<(Guid SourceId, int StreamIndex, TrackKind Kind), List<Clip>[]>();
            foreach (var clip in clips)
            {
                var key = (clip.Media.SourceId, clip.Media.StreamIndex, clip.Row.Kind);
                if (!streams.TryGetValue(key, out var lanes))
                    streams[key] = lanes = new List<Clip>[MaxLanes];

                int lane = LaneFor(clip, lanes);
                (lanes[lane] ??= new List<Clip>()).Add(clip);
                if (lane > 0)
                    (assigned ??= new Dictionary<Guid, int>())[clip.Item.Id] = lane;
            }

            return assigned ?? NoLanes;
        }

        /// <summary>An item's lane in an <see cref="Assign"/> result.</summary>
        public static int LaneOf(IReadOnlyDictionary<Guid, int> lanes, Guid itemId) =>
            lanes.TryGetValue(itemId, out int lane) ? lane : 0;

        private readonly struct Clip
        {
            public Clip(Item item, MediaContent media, Track row)
            {
                Item = item;
                Media = media;
                Row = row;
            }

            public Item Item { get; }
            public MediaContent Media { get; }
            public Track Row { get; }
        }

        private static List<Clip> CollectClips(Project project)
        {
            var rows = new Dictionary<Guid, Track>();
            foreach (var track in project.Tracks)
            {
                if ((track.Kind == TrackKind.Video && !track.Hidden)
                    || (track.Kind == TrackKind.Audio && !track.Muted))
                    rows.TryAdd(track.Id, track);
            }

            var clips = new List<Clip>(project.Items.Count);
            foreach (var item in project.Items)
            {
                if (item.Content is MediaContent media && item.DurationTicks > 0
                    && rows.TryGetValue(item.TrackId, out var row))
                    clips.Add(new Clip(item, media, row));
            }

            return clips;
        }

        /// <summary>Timeline order, ties by the stacking <c>FrameComposer</c> uses. The item id ends
        /// the order, so the result does not depend on the order of <see cref="Project.Items"/>.</summary>
        private static int CompareClips(Clip a, Clip b)
        {
            int result = a.Item.TimelineStartTicks.CompareTo(b.Item.TimelineStartTicks);
            if (result == 0)
                result = a.Row.Order.CompareTo(b.Row.Order);
            if (result == 0)
                result = a.Row.Id.CompareTo(b.Row.Id);
            return result != 0 ? result : a.Item.Id.CompareTo(b.Item.Id);
        }

        /// <summary>The lowest lane that can take the clip. Clips arrive in timeline order, so one
        /// that has ended cannot overlap this clip or any later one: it leaves its lane here, and
        /// what stays in a lane is exactly what overlaps the clip.</summary>
        private static int LaneFor(in Clip clip, List<Clip>[] lanes)
        {
            for (int lane = 0; lane < lanes.Length; lane++)
            {
                var occupants = lanes[lane];
                if (occupants == null)
                    return lane;

                for (int i = occupants.Count - 1; i >= 0; i--)
                {
                    if (occupants[i].Item.TimelineEndTicks <= clip.Item.TimelineStartTicks)
                        occupants.RemoveAt(i);
                }

                if (CanShare(clip, occupants))
                    return lane;
            }

            return lanes.Length - 1;
        }

        private static bool CanShare(in Clip clip, List<Clip> overlaps)
        {
            if (overlaps.Count == 0)
                return true;
            if (clip.Row.Kind != TrackKind.Video)
                return false;

            foreach (var other in overlaps)
            {
                if (!ShowSameSourceTime(clip, other))
                    return false;
            }

            return true;
        }

        /// <summary>Whether two overlapping clips ask for one source time at every instant,
        /// exactly: a tick apart would make the render's decoder seek back once per frame. Under a
        /// speed the source time is a rounded product, so only clips with one origin are
        /// exact.</summary>
        private static bool ShowSameSourceTime(in Clip a, in Clip b)
        {
            if (a.Media.Freeze || b.Media.Freeze)
                return a.Media.Freeze && b.Media.Freeze && a.Media.SourceInTicks == b.Media.SourceInTicks;

            double speed = TimelineOps.SpeedOf(a.Media);
            if (speed != TimelineOps.SpeedOf(b.Media))
                return false;

            long startDelta = b.Item.TimelineStartTicks - a.Item.TimelineStartTicks;
            return speed == 1.0
                ? b.Media.SourceInTicks - a.Media.SourceInTicks == startDelta
                : startDelta == 0 && b.Media.SourceInTicks == a.Media.SourceInTicks;
        }
    }
}
