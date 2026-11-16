using System;
using System.Collections.Generic;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Playback;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    // The timeline↔source mapping CompositionPlayer plays a project through (internal class,
    // reached via InternalsVisibleTo). Pure math — no FFmpeg, no timing.
    public class ProjectTimelineMapTests
    {
        private const long Second = 10_000_000;

        private static readonly Guid SourceId = Guid.NewGuid();

        private static Project NewProject()
        {
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 64, HeightPx = 64, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            };
            project.Sources.Add(new Source
            {
                Id = SourceId,
                Path = "fixture.mp4",
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video },
                    new SourceStream { Index = 1, Kind = StreamKind.Audio },
                },
            });
            return project;
        }

        private static Track AddTrack(Project project, TrackKind kind, int order = 0,
            bool hidden = false, bool muted = false)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = kind, Order = order, Hidden = hidden, Muted = muted };
            project.Tracks.Add(track);
            return track;
        }

        private static Item AddMedia(Project project, Track track, long tlStart, long duration,
            long srcIn, int streamIndex = 0)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = tlStart,
                DurationTicks = duration,
                Content = new MediaContent { SourceId = SourceId, StreamIndex = streamIndex, SourceInTicks = srcIn },
            };
            project.Items.Add(item);
            return item;
        }

        [Fact]
        public void Back_to_back_items_with_cut_map_both_directions()
        {
            // A: timeline [0, 0.5s) ← source [0, 0.5s); B: timeline [0.5s, 1.5s) ← source [1s, 2s)
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, Second / 2, 0);
            AddMedia(project, track, Second / 2, Second, Second);

            var map = ProjectTimelineMap.Build(project);
            var stream = map.VideoStreams[(SourceId, 0, 0)];

            // timeline → source, mirroring FrameComposer's per-item mapping
            Assert.Equal(Second / 4, stream.TimelineToSource(Second / 4));
            Assert.Equal(Second + Second / 4, stream.TimelineToSource(Second / 2 + Second / 4));
            Assert.Equal(Second, stream.TimelineToSource(Second / 2)); // seam → B's in-point
            // past the end clamps to the last KEPT source instant — SrcEnd itself is the
            // exclusive out-point (the first instant the edit removed) and seeking there would
            // present a trimmed-away frame.
            Assert.Equal(2 * Second - 1, stream.TimelineToSource(5 * Second));

            // source → timeline (the worker pacing map)
            Assert.Equal(Second / 4, stream.SourceToTimeline(Second / 4, nearTlTicks: 0));
            Assert.Equal(Second / 2 + Second / 4, stream.SourceToTimeline(Second + Second / 4, nearTlTicks: 0));
            // a source instant inside the cut clamps to the seam
            Assert.Equal(Second / 2, stream.SourceToTimeline(Second / 2 + Second / 4, nearTlTicks: 0));

            // offsets: 0 inside A, +0.5s inside B (that change is the seam detector)
            Assert.Equal(0, stream.OffsetAtTimeline(Second / 4));
            Assert.Equal(Second / 2, stream.OffsetAtTimeline(Second));
            Assert.Equal(long.MinValue, stream.OffsetAtTimeline(2 * Second)); // outside

            // the cut-out source span [0.5s, 1s) is a skip range
            var range = Assert.Single(stream.SourceCuts.Ranges);
            Assert.Equal(new TimeSpan(Second / 2), range.Start);
            Assert.Equal(new TimeSpan(Second), range.End);

            Assert.Equal((SourceId, 0, 0), map.PrimaryVideo);
            Assert.Equal(Second / 2 + Second, map.DurationTicks);
        }

        [Fact]
        public void Split_without_cut_produces_no_skip_range_and_constant_offset()
        {
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, Second, 0);
            AddMedia(project, track, Second, Second, Second); // source-continuous

            var map = ProjectTimelineMap.Build(project);
            var stream = map.VideoStreams[(SourceId, 0, 0)];

            Assert.Empty(stream.SourceCuts.Ranges);
            Assert.Equal(stream.OffsetAtTimeline(Second / 2), stream.OffsetAtTimeline(Second + Second / 2));
        }

        [Fact]
        public void Timeline_gap_maps_to_next_in_point_and_reports_no_offset()
        {
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, Second, 0);
            AddMedia(project, track, 2 * Second, Second, 3 * Second);

            var map = ProjectTimelineMap.Build(project);
            var stream = map.VideoStreams[(SourceId, 0, 0)];

            Assert.Equal(3 * Second, stream.TimelineToSource(Second + Second / 2)); // inside the gap
            Assert.Equal(long.MinValue, stream.OffsetAtTimeline(Second + Second / 2));
        }

        [Fact]
        public void Hidden_video_tracks_are_excluded()
        {
            var project = NewProject();
            var hidden = AddTrack(project, TrackKind.Video, order: 0, hidden: true);
            AddMedia(project, hidden, 0, Second, 0);

            var map = ProjectTimelineMap.Build(project);
            Assert.Empty(map.VideoStreams);
            Assert.Null(map.PrimaryVideo);
        }

        [Fact]
        public void Audio_tracks_contribute_duration_only()
        {
            // Audio is mixed in timeline time by AudioMixWorker — the map never sees it, but an
            // audio item running past the last video frame still stretches the timeline.
            var project = NewProject();
            var video = AddTrack(project, TrackKind.Video, order: 0);
            AddMedia(project, video, 0, Second, 0);
            var audio = AddTrack(project, TrackKind.Audio, order: 1);
            AddMedia(project, audio, 0, 3 * Second, 0, streamIndex: 1);

            var map = ProjectTimelineMap.Build(project);
            Assert.Single(map.VideoStreams);
            Assert.Equal((SourceId, 0, 0), map.PrimaryVideo);
            Assert.Equal(3 * Second, map.DurationTicks);
        }

        [Fact]
        public void Retimed_item_scales_both_mapping_directions()
        {
            // 1s of timeline at 2x covers source [1s, 3s)
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            var item = AddMedia(project, track, Second, Second, Second);
            ((MediaContent)item.Content).Speed = 2.0;

            var map = ProjectTimelineMap.Build(project);
            var stream = map.VideoStreams[(SourceId, 0, 0)];

            // half a second in consumes a full second of source…
            Assert.Equal(2 * Second, stream.TimelineToSource(Second + Second / 2));
            // …and the inverse pacing map divides back
            Assert.Equal(Second + Second / 2, stream.SourceToTimeline(2 * Second, nearTlTicks: 0));
            // past the end clamps to the last kept source instant of the SCALED span
            Assert.Equal(3 * Second - 1, stream.TimelineToSource(10 * Second));
        }

        [Fact]
        public void Past_the_end_clamps_to_the_clip_that_ends_last_not_the_one_that_starts_last()
        {
            // a shorter copy at the same source time shares the lane and starts later
            var project = NewProject();
            AddMedia(project, AddTrack(project, TrackKind.Video, order: 0), 0, 10 * Second, 0);
            AddMedia(project, AddTrack(project, TrackKind.Video, order: 1), 5 * Second, 3 * Second, 5 * Second);

            var stream = ProjectTimelineMap.Build(project).VideoStreams[(SourceId, 0, 0)];

            Assert.Equal(2, stream.Segments.Count);
            Assert.Equal(10 * Second - 1, stream.TimelineToSource(10 * Second));
        }

        // ------------------------------------------------------------------------------- lanes

        [Fact]
        public void Clips_overlapping_at_different_source_times_get_a_stream_map_each()
        {
            var project = NewProject();
            AddMedia(project, AddTrack(project, TrackKind.Video, order: 0), 0, 30 * Second, 0);
            AddMedia(project, AddTrack(project, TrackKind.Video, order: 1), 5 * Second, 5 * Second, 20 * Second);

            var map = ProjectTimelineMap.Build(project);

            Assert.Equal(2, map.VideoStreams.Count);
            Assert.Equal(7 * Second, map.VideoStreams[(SourceId, 0, 0)].TimelineToSource(7 * Second));
            Assert.Equal(22 * Second, map.VideoStreams[(SourceId, 0, 1)].TimelineToSource(7 * Second));
            Assert.Equal((SourceId, 0, 0), map.PrimaryVideo);

            // the lanes do not see each other, so neither thinks the other's source is cut out
            Assert.Empty(map.VideoStreams[(SourceId, 0, 0)].SourceCuts.Ranges);
            Assert.Empty(map.VideoStreams[(SourceId, 0, 1)].SourceCuts.Ranges);
        }

        [Fact]
        public void A_duplicate_row_stays_one_stream_map()
        {
            var project = NewProject();
            AddMedia(project, AddTrack(project, TrackKind.Video, order: 0), 0, 30 * Second, 0);
            AddMedia(project, AddTrack(project, TrackKind.Video, order: 1), 0, 30 * Second, 0);

            var stream = Assert.Single(ProjectTimelineMap.Build(project).VideoStreams).Value;

            Assert.Equal(2, stream.Segments.Count);
            Assert.Empty(stream.SourceCuts.Ranges);
            Assert.Equal(4 * Second, stream.SourceToTimeline(4 * Second, nearTlTicks: 9 * Second));
        }

        [Fact]
        public void The_primary_is_the_lane_of_the_first_clip_on_the_lowest_row()
        {
            // the row above starts first and takes lane 0, so the lowest row's clip is in lane 1
            var project = NewProject();
            AddMedia(project, AddTrack(project, TrackKind.Video, order: 0), 5 * Second, 5 * Second, 20 * Second);
            AddMedia(project, AddTrack(project, TrackKind.Video, order: 1), 0, 30 * Second, 0);

            Assert.Equal((SourceId, 0, 1), ProjectTimelineMap.Build(project).PrimaryVideo);
        }

        [Fact]
        public void A_repeated_span_maps_each_frame_to_the_segment_nearest_the_playhead()
        {
            // copy source [3 s, 5 s) and paste it at t=20
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, 20 * Second, 0);
            AddMedia(project, track, 20 * Second, 2 * Second, 3 * Second);

            var stream = ProjectTimelineMap.Build(project).VideoStreams[(SourceId, 0, 0)];

            Assert.Equal(21 * Second, stream.SourceToTimeline(4 * Second, nearTlTicks: 21 * Second));
            Assert.Equal(4 * Second, stream.SourceToTimeline(4 * Second, nearTlTicks: 4 * Second));
            Assert.Equal(4 * Second, stream.SourceToTimeline(4 * Second, nearTlTicks: 11 * Second)); // closer to 4 than to 21
            Assert.Equal(21 * Second, stream.SourceToTimeline(4 * Second, nearTlTicks: 13 * Second)); // closer to 21 than to 4

            // a source instant only one segment plays has one answer, wherever the playhead is
            Assert.Equal(10 * Second, stream.SourceToTimeline(10 * Second, nearTlTicks: 21 * Second));
        }

        /// <summary>Source [3 s, 5 s) on a row: at t=3 in the original, at t=20 as a paste whose in-point
        /// sits just after a frame, then source [25 s, 30 s).</summary>
        private static ProjectTimelineMap.StreamMap RepeatStream()
        {
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, 20 * Second, 0);
            AddMedia(project, track, 20 * Second, 2 * Second, 3 * Second + 100);
            AddMedia(project, track, 22 * Second, 5 * Second, 25 * Second);
            return ProjectTimelineMap.Build(project).VideoStreams[(SourceId, 0, 0)];
        }

        [Fact]
        public void A_landed_frame_maps_only_inside_the_clip_at_the_playhead()
        {
            var stream = RepeatStream();

            Assert.True(stream.TryMapInSegmentAt(21 * Second, 4 * Second, out long tl));
            Assert.Equal(21 * Second - 100, tl);
            Assert.True(stream.TryMapInSegmentAt(4 * Second, 4 * Second, out tl));
            Assert.Equal(4 * Second, tl);

            // the frame before the paste's in-point is played only by the original, 17 s back
            Assert.False(stream.TryMapInSegmentAt(20 * Second, 3 * Second, out _));
            // past the footage, nothing covers the instant
            Assert.False(stream.TryMapInSegmentAt(40 * Second, 29 * Second, out _));
        }

        [Fact]
        public void A_step_goes_forward_and_stops_at_the_end_of_a_repeat()
        {
            var stream = RepeatStream();

            // inside the paste, the frame's other home is behind the playhead
            Assert.Equal(21 * Second - 100, stream.StepForward(4 * Second, 20 * Second + Second / 2));
            // the frame after the paste's end is played only by the original: stop at the seam
            Assert.Equal(22 * Second, stream.StepForward(5 * Second + 200, 22 * Second - 1));
            // from the original, the first place forward is still the original
            Assert.Equal(4 * Second, stream.StepForward(4 * Second, 4 * Second - 1));
            // off a segment into a contiguous or later one that plays the frame
            Assert.Equal(22 * Second + 1, stream.StepForward(25 * Second + 1, 22 * Second - 1));
            // past the footage the playhead has nowhere forward to go, and it does not go back
            Assert.Equal(30 * Second, stream.StepForward(40 * Second, 30 * Second));
        }

        [Fact]
        public void A_step_with_the_picture_a_little_behind_the_playhead_keeps_its_clip()
        {
            // after playback the next frame can sit a few ms behind the stopped clock
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, 6 * Second, 0);
            AddMedia(project, track, 6 * Second, 6 * Second, 6 * Second);
            AddMedia(project, track, 12 * Second, 2 * Second, 5 * Second); // plays the frames either side of the split again
            var stream = ProjectTimelineMap.Build(project).VideoStreams[(SourceId, 0, 0)];

            // the frame's own instant, not the end of the clip and not the repeat
            Assert.Equal(Second + Second / 5, stream.StepForward(Second + Second / 5, Second + Second / 5 + 5000));
            Assert.Equal(6 * Second, stream.StepForward(6 * Second, 6 * Second + 5000));

            // just over the seam the picture is still in the clip before: it has yet to reach this
            // clip, so the playhead holds instead of running to the end of the clip or the repeat
            Assert.Equal(6 * Second + 5000, stream.StepForward(6 * Second - 5000, 6 * Second + 5000));

            // inside a repeat, the instant in the repeat and not in the original, 17 s back
            var repeat = RepeatStream();
            Assert.Equal(21 * Second - 100, repeat.StepForward(4 * Second, 21 * Second));
            // the frame before the paste's in-point, which only the original plays
            Assert.Equal(20 * Second + 5000, repeat.StepForward(3 * Second, 20 * Second + 5000));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)] // a gap, and then a clip that plays something else
        public void A_step_off_a_repeat_with_nothing_after_it_never_goes_back(bool clipAfterAGap)
        {
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, 8 * Second, 0);
            AddMedia(project, track, 8 * Second, 2 * Second, Second);
            if (clipAfterAGap)
                AddMedia(project, track, 12 * Second, 2 * Second, 20 * Second);
            var stream = ProjectTimelineMap.Build(project).VideoStreams[(SourceId, 0, 0)];

            // the frame after the repeat's end is played only by the original, 7 s back
            Assert.Equal(10 * Second, stream.StepForward(3 * Second, 10 * Second - 1));
            Assert.Equal(10 * Second, stream.StepForward(3 * Second, 10 * Second));
            Assert.Equal(11 * Second, stream.StepForward(3 * Second + 1, 11 * Second));
            // a frame nothing plays clamps to the seam behind the playhead, and holds there too
            Assert.Equal(11 * Second, stream.StepForward(9 * Second, 11 * Second));
        }

        [Fact]
        public void A_step_in_a_gap_still_goes_forward_to_the_clip_that_plays_the_frame()
        {
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 2 * Second, 2 * Second, 10 * Second);
            AddMedia(project, track, 6 * Second, 2 * Second, 20 * Second);
            var stream = ProjectTimelineMap.Build(project).VideoStreams[(SourceId, 0, 0)];

            Assert.Equal(6 * Second + 1, stream.StepForward(20 * Second + 1, 5 * Second));
            // before the first clip, where the pacing map clamps to its start
            Assert.Equal(2 * Second, stream.StepForward(Second, 0));
        }

        [Fact]
        public void A_source_instant_no_segment_plays_still_clamps_to_its_seam()
        {
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, 2 * Second, 10 * Second); // source [10, 12)
            AddMedia(project, track, 2 * Second, 2 * Second, 20 * Second); // source [20, 22)

            var stream = ProjectTimelineMap.Build(project).VideoStreams[(SourceId, 0, 0)];

            Assert.Equal(2 * Second, stream.SourceToTimeline(15 * Second, nearTlTicks: 3 * Second));
            Assert.Equal(4 * Second, stream.SourceToTimeline(25 * Second, nearTlTicks: 0));
            Assert.Equal(0, stream.SourceToTimeline(5 * Second, nearTlTicks: 3 * Second));
        }

        // segments are "timeline start, length, source in" in seconds, cuts "from-to" in source
        // seconds. The first four are in source order: what the cuts have always been, the gap
        // between neighbours. The rest are not: the complement of what is played.
        [Theory]
        [InlineData("0,10,0 10,10,10", "")] // a split
        [InlineData("0,5,0 5,5,8 10,5,20", "5-8 13-20")]
        [InlineData("0,5,0 7,3,7", "5-7")] // a timeline gap between source-continuous clips
        [InlineData("0,5,3", "")] // only the leading source trimmed
        [InlineData("0,20,0 20,2,3 22,10,25", "20-25")] // a repeat inside what the row already plays
        [InlineData("0,5,10 5,5,0", "5-10")] // halves swapped
        [InlineData("0,4,6 4,8,0 12,1,20", "10-20")] // a segment running into the one before it
        public void Cuts_are_what_no_segment_plays(string segments, string expected)
        {
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            foreach (var segment in segments.Split(' '))
            {
                var s = Array.ConvertAll(segment.Split(','), long.Parse);
                AddMedia(project, track, s[0] * Second, s[1] * Second, s[2] * Second);
            }

            var stream = ProjectTimelineMap.Build(project).VideoStreams[(SourceId, 0, 0)];

            var actual = new List<string>();
            foreach (var range in stream.SourceCuts.Ranges)
            {
                if (range.End != TimeSpan.MaxValue) // the open tail has its own test
                    actual.Add($"{range.Start.Ticks / Second}-{range.End.Ticks / Second}");
            }

            Assert.Equal(expected, string.Join(' ', actual));
        }

        [Fact]
        public void A_cut_is_measured_across_a_hold()
        {
            // a freeze between the halves of a split plays no source: nothing is cut
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, 5 * Second, 0);
            ((MediaContent)AddMedia(project, track, 5 * Second, 2 * Second, 5 * Second).Content).Freeze = true;
            AddMedia(project, track, 7 * Second, 5 * Second, 5 * Second);

            Assert.Empty(ProjectTimelineMap.Build(project).VideoStreams[(SourceId, 0, 0)].SourceCuts.Ranges);

            // nor does it stretch what is played to reach its frame, wherever that frame is
            project = NewProject();
            track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, 5 * Second, 0);
            ((MediaContent)AddMedia(project, track, 5 * Second, 2 * Second, 20 * Second).Content).Freeze = true;
            AddMedia(project, track, 7 * Second, 5 * Second, 5 * Second);

            Assert.Empty(ProjectTimelineMap.Build(project).VideoStreams[(SourceId, 0, 0)].SourceCuts.Ranges);
        }

        [Fact]
        public void Past_the_furthest_source_is_cut_only_when_a_segment_hands_over_from_there()
        {
            // the row runs to source 30 s, then jumps back: the decoder runs on past 30 s until
            // the hop lands, and that material was never part of the edit
            var project = NewProject();
            var track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, 30 * Second, 0);
            AddMedia(project, track, 30 * Second, 2 * Second, 3 * Second);
            var cuts = ProjectTimelineMap.Build(project).VideoStreams[(SourceId, 0, 0)].SourceCuts;
            Assert.False(cuts.TryGetSkipEnd(new TimeSpan(29 * Second), out _));
            Assert.True(cuts.TryGetSkipEnd(new TimeSpan(30 * Second), out _));
            Assert.True(cuts.TryGetSkipEnd(new TimeSpan(500 * Second), out _));

            // source order: the decoder runs off the end with nothing after it, as ever
            project = NewProject();
            track = AddTrack(project, TrackKind.Video);
            AddMedia(project, track, 0, 2 * Second, 3 * Second);
            AddMedia(project, track, 2 * Second, 30 * Second, 5 * Second);
            Assert.Empty(ProjectTimelineMap.Build(project).VideoStreams[(SourceId, 0, 0)].SourceCuts.Ranges);
        }
    }
}
