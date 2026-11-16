using System;
using System.Collections.Generic;
using System.Linq;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Playback;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Which clips of a stream one decoder can serve. Pure: the numbers here are what the composer,
    /// the mixer and the player's timeline map all act on, so the decoder cost of an edit and the
    /// frame each clip gets are both decided by this table.
    /// </summary>
    public class StreamLanesTests
    {
        private const long Sec = TimeSpan.TicksPerSecond;

        private static readonly Guid SourceA = Guid.NewGuid();
        private static readonly Guid SourceB = Guid.NewGuid();

        private static Project NewProject() => new Project();

        private static Track AddRow(Project p, TrackKind kind = TrackKind.Video, int? order = null,
            bool hidden = false, bool muted = false)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                Order = order ?? p.Tracks.Count,
                Hidden = hidden,
                Muted = muted,
            };
            p.Tracks.Add(track);
            return track;
        }

        private static Item AddClip(Project p, Track row, long start, long duration, long sourceIn,
            Guid? source = null, int stream = 0, double speed = 1.0, bool freeze = false)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = row.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = new MediaContent
                {
                    SourceId = source ?? SourceA,
                    StreamIndex = stream,
                    SourceInTicks = sourceIn,
                    Speed = speed,
                    Freeze = freeze,
                },
            };
            p.Items.Add(item);
            return item;
        }

        private static int Lane(Project p, Item item) => StreamLanes.LaneOf(StreamLanes.Assign(p), item.Id);

        [Fact]
        public void Without_an_overlap_every_clip_is_in_lane_0_and_nothing_is_listed()
        {
            // back to back, out of source order, with a gap: all one decoder hopping at seams
            var p = NewProject();
            var row = AddRow(p);
            AddClip(p, row, 0, 5 * Sec, 0);
            AddClip(p, row, 5 * Sec, 5 * Sec, 20 * Sec);
            AddClip(p, row, 12 * Sec, 2 * Sec, 3 * Sec);

            Assert.Empty(StreamLanes.Assign(p));
        }

        [Fact]
        public void A_repeat_past_the_end_of_a_row_stays_in_lane_0()
        {
            var p = NewProject();
            var row = AddRow(p);
            AddClip(p, row, 0, 30 * Sec, 0);
            AddClip(p, row, 30 * Sec, 2 * Sec, 3 * Sec);

            Assert.Empty(StreamLanes.Assign(p));
        }

        [Fact]
        public void Clips_that_only_touch_do_not_overlap()
        {
            var p = NewProject();
            AddClip(p, AddRow(p), 0, 5 * Sec, 0);
            var next = AddClip(p, AddRow(p), 5 * Sec, 5 * Sec, 20 * Sec);

            Assert.Equal(0, Lane(p, next));
        }

        [Fact]
        public void An_overlap_at_a_different_source_time_takes_lane_1()
        {
            var p = NewProject();
            var screen = AddClip(p, AddRow(p), 0, 30 * Sec, 0);
            var paste = AddClip(p, AddRow(p), 5 * Sec, 5 * Sec, 20 * Sec);

            var lanes = StreamLanes.Assign(p);
            Assert.Equal(0, StreamLanes.LaneOf(lanes, screen.Id));
            Assert.Equal(1, StreamLanes.LaneOf(lanes, paste.Id));
            Assert.False(lanes.ContainsKey(screen.Id), "an absent item means lane 0: the table lists only the rest");
        }

        [Fact]
        public void A_lane_comes_free_when_its_clip_ends()
        {
            var p = NewProject();
            var row = AddRow(p);
            AddClip(p, AddRow(p), 0, 30 * Sec, 0);
            var first = AddClip(p, row, 5 * Sec, 5 * Sec, 20 * Sec);
            var second = AddClip(p, row, 15 * Sec, 5 * Sec, 2 * Sec);

            Assert.Equal(1, Lane(p, first));
            Assert.Equal(1, Lane(p, second));
        }

        [Fact]
        public void Clips_showing_one_source_time_share_a_lane_on_video()
        {
            var p = NewProject();
            AddClip(p, AddRow(p), 0, 30 * Sec, 0);
            var duplicate = AddClip(p, AddRow(p), 0, 30 * Sec, 0);
            var partial = AddClip(p, AddRow(p), 5 * Sec, 3 * Sec, 5 * Sec); // same instants, later start

            Assert.Empty(StreamLanes.Assign(p));
            Assert.Equal(0, Lane(p, duplicate));
            Assert.Equal(0, Lane(p, partial));
        }

        [Fact]
        public void Audio_clips_never_share_a_lane_when_they_overlap()
        {
            // each read consumes the stream position, so even one source time needs two decoders
            var p = NewProject();
            var a = AddClip(p, AddRow(p, TrackKind.Audio), 0, 30 * Sec, 0, stream: 1);
            var b = AddClip(p, AddRow(p, TrackKind.Audio), 0, 30 * Sec, 0, stream: 1);

            Assert.Equal(0, Lane(p, a));
            Assert.Equal(1, Lane(p, b));
        }

        [Fact]
        public void A_clip_needs_every_overlap_in_a_lane_to_show_its_source_time()
        {
            var p = NewProject();
            AddClip(p, AddRow(p), 0, 30 * Sec, 0);
            AddClip(p, AddRow(p), 0, 30 * Sec, 0); // lane 0, with the first
            var other = AddClip(p, AddRow(p), 5 * Sec, 5 * Sec, 20 * Sec); // lane 1
            var sharing = AddClip(p, AddRow(p), 6 * Sec, 2 * Sec, 21 * Sec); // same instants as `other`

            Assert.Equal(1, Lane(p, other));
            Assert.Equal(1, Lane(p, sharing));
        }

        public static TheoryData<string, double, long, long, double, long, long, bool> ShareCases => new()
        {
            // name, speed A, start A, in A, speed B, start B, in B, shares
            { "one tick apart would seek every frame", 1, 0, 0, 1, 0, 1, false },
            { "another speed", 1, 0, 0, 2, 0, 0, false },
            { "one speed, one origin", 2, 0, 0, 2, 0, 0, true },
            { "one speed, later start on the same line: rounding can differ", 2, 0, 0, 2, Sec, 2 * Sec, false },
        };

        [Theory]
        [MemberData(nameof(ShareCases))]
        public void Sharing_is_exact(string name, double speedA, long startA, long inA,
            double speedB, long startB, long inB, bool shares)
        {
            _ = name;
            var p = NewProject();
            AddClip(p, AddRow(p), startA, 10 * Sec, inA, speed: speedA);
            var b = AddClip(p, AddRow(p), startB, 5 * Sec, inB, speed: speedB);

            Assert.Equal(shares ? 0 : 1, Lane(p, b));
        }

        [Fact]
        public void Freeze_frames_share_only_with_a_freeze_of_the_same_frame()
        {
            var p = NewProject();
            AddClip(p, AddRow(p), 0, 10 * Sec, 3 * Sec, freeze: true);
            var sameFrame = AddClip(p, AddRow(p), 2 * Sec, 2 * Sec, 3 * Sec, freeze: true);
            var otherFrame = AddClip(p, AddRow(p), 2 * Sec, 2 * Sec, 4 * Sec, freeze: true);
            var playing = AddClip(p, AddRow(p), 3 * Sec, 2 * Sec, 3 * Sec); // shows the frame for one instant

            var lanes = StreamLanes.Assign(p);
            Assert.Equal(0, StreamLanes.LaneOf(lanes, sameFrame.Id));
            Assert.Equal(1, StreamLanes.LaneOf(lanes, otherFrame.Id));
            Assert.Equal(2, StreamLanes.LaneOf(lanes, playing.Id));
        }

        [Fact]
        public void Past_the_cap_a_clip_takes_the_last_lane()
        {
            var p = NewProject();
            var clips = Enumerable.Range(0, StreamLanes.MaxLanes + 2)
                .Select(i => AddClip(p, AddRow(p), 0, 10 * Sec, i * Sec))
                .ToList();

            Assert.Equal(new[] { 0, 1, 2, 3, 3, 3 }, clips.Select(c => Lane(p, c)));
        }

        [Fact]
        public void Hidden_video_rows_and_muted_audio_rows_take_no_lane()
        {
            var p = NewProject();
            var hidden = AddClip(p, AddRow(p, hidden: true), 0, 10 * Sec, 0);
            var visible = AddClip(p, AddRow(p), 0, 10 * Sec, 5 * Sec);
            var muted = AddClip(p, AddRow(p, TrackKind.Audio, muted: true), 0, 10 * Sec, 0, stream: 1);
            var audible = AddClip(p, AddRow(p, TrackKind.Audio), 0, 10 * Sec, 5 * Sec, stream: 1);

            Assert.Empty(StreamLanes.Assign(p));
            Assert.Equal(0, Lane(p, hidden));
            Assert.Equal(0, Lane(p, visible));
            Assert.Equal(0, Lane(p, muted));
            Assert.Equal(0, Lane(p, audible));
        }

        [Fact]
        public void Unhiding_a_row_is_what_gives_its_clip_a_lane()
        {
            var p = NewProject();
            var row = AddRow(p, hidden: true);
            AddClip(p, AddRow(p, order: 5), 0, 10 * Sec, 5 * Sec);
            var clip = AddClip(p, row, 2 * Sec, 2 * Sec, 0);
            Assert.Equal(0, Lane(p, clip));

            row.Hidden = false;
            Assert.Equal(1, Lane(p, clip));
        }

        [Fact]
        public void Each_stream_of_each_source_has_its_own_lanes()
        {
            var p = NewProject();
            AddClip(p, AddRow(p), 0, 10 * Sec, 0);
            var webcam = AddClip(p, AddRow(p), 0, 10 * Sec, 5 * Sec, stream: 1);
            var other = AddClip(p, AddRow(p), 0, 10 * Sec, 5 * Sec, source: SourceB);

            Assert.Equal(0, Lane(p, webcam));
            Assert.Equal(0, Lane(p, other));
        }

        [Fact]
        public void Ties_go_in_track_order_whatever_the_order_of_the_items()
        {
            var p = NewProject();
            var front = AddRow(p, order: 7);
            var back = AddRow(p, order: 2);
            var frontClip = AddClip(p, front, 0, 10 * Sec, 5 * Sec); // listed first, stacked last
            var backClip = AddClip(p, back, 0, 10 * Sec, 0);

            Assert.Equal(1, Lane(p, frontClip));
            Assert.Equal(0, Lane(p, backClip));

            p.Items.Reverse();
            Assert.Equal(1, Lane(p, frontClip));
            Assert.Equal(0, Lane(p, backClip));
        }

        [Fact]
        public void Items_with_no_media_or_no_length_take_no_lane()
        {
            var p = NewProject();
            var row = AddRow(p);
            AddClip(p, row, 0, 10 * Sec, 0);
            var empty = AddClip(p, AddRow(p), 0, 0, 5 * Sec);
            var text = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = row.Id,
                DurationTicks = 10 * Sec,
                Content = new TextContent { Text = "a title" },
            };
            p.Items.Add(text);
            var after = AddClip(p, AddRow(p), Sec, Sec, 7 * Sec);

            Assert.Equal(0, Lane(p, empty));
            Assert.Equal(0, Lane(p, text));
            Assert.Equal(1, Lane(p, after));
        }

        public static TheoryData<string> FixturesWithNoOverlap => new TheoryData<string>(
            LegacyFixtures.Names.Where(name => name != "duplicate-row"));

        [Theory]
        [MemberData(nameof(FixturesWithNoOverlap))]
        public void Projects_saved_before_paste_existed_are_all_lane_0(string name)
        {
            // duplicate-in-front among them: same source time, one frame
            Assert.Empty(StreamLanes.Assign(LegacyFixtures.Load(name)));
        }

        [Fact]
        public void A_duplicate_row_nudged_along_the_timeline_really_is_an_overlap()
        {
            // 3 s later than the row it copies, so the two show different moments, which the
            // preview could only draw as one until it had lanes
            var lane = Assert.Single(StreamLanes.Assign(LegacyFixtures.Load("duplicate-row")));

            Assert.Equal(1, lane.Value);
        }
    }
}
