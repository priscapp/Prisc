using System;
using System.Linq;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Playback;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Linked deletes are scoped to the clicked clip's span, never "the whole group": a group's rows
    /// can carry several back-to-back segments — a recording built from keep-slices, or the two
    /// halves of a single-item split — and deleting one clip must not wipe the rest of the recording
    /// (the bug these tests pin down: delete emptied the entire timeline).
    /// </summary>
    public class LinkedDeleteScopeTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>The real recording mapping: screen/webcam/audio rows over one source, one item
        /// per kept slice per row, all rows sharing one group.</summary>
        private static Project BuildRecording(long audioStreamDurationMs = 60_000, params KeepSegment[] segments) =>
            RecordingProject.Build(new RecordingProjectSpec
            {
                InputPath = @"C:\rec\input.mp4",
                Screen = new VideoStreamProbe { StreamIndex = 0, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                Webcam = new VideoStreamProbe { StreamIndex = 1, Width = 640, Height = 480, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                AudioStreams = new[] { new AudioStreamProbe { StreamIndex = 2, SampleRate = 48_000, Channels = 2, DurationTicks = Ms(audioStreamDurationMs) } },
                FpsNum = 30,
                FpsDen = 1,
                Segments = segments,
                Ids = RecordingIds.New(1),
            });

        private static Item ScreenItemAt(EditorSession session, long startTicks) =>
            session.Project.Items.First(i => i.TimelineStartTicks == startTicks
                                             && i.Content is MediaContent { StreamIndex: 0 });

        [Fact]
        public void Deleting_one_slice_of_a_multi_slice_recording_keeps_the_other_slices()
        {
            var project = BuildRecording(segments: new[]
            {
                new KeepSegment(0, Ms(10_000)),
                new KeepSegment(Ms(20_000), Ms(10_000)),
            });
            var session = new EditorSession(project, null, null);
            var before = session.Project.ToJson();

            var firstScreen = ScreenItemAt(session, 0);
            session.RippleDeleteItem(firstScreen.Id);

            // the second slice survives on every row and slides back to the origin
            Assert.Equal(3, session.Project.Items.Count);
            Assert.All(session.Project.Items, i => Assert.Equal(0, i.TimelineStartTicks));
            Assert.All(session.Project.Items, i =>
                Assert.Equal(Ms(20_000), ((MediaContent)i.Content).SourceInTicks));
            Assert.Empty(session.Project.Validate());

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void Deleting_one_half_after_a_single_item_split_cuts_only_that_span()
        {
            var project = BuildRecording(segments: new[] { new KeepSegment(0, Ms(10_000)) });
            var session = new EditorSession(project, null, null);

            // the timeline's right-click split: one row cut, both halves keep the group
            Assert.True(session.SplitItemAt(ScreenItemAt(session, 0).Id, Ms(4_000)));
            session.RippleDeleteItem(ScreenItemAt(session, 0).Id);

            // [0, 4s) is cut out of the whole recording: every row keeps its last 6 seconds
            Assert.Equal(3, session.Project.Items.Count);
            Assert.All(session.Project.Items, i => Assert.Equal(0, i.TimelineStartTicks));
            Assert.All(session.Project.Items, i => Assert.Equal(Ms(6_000), i.DurationTicks));
            Assert.All(session.Project.Items, i =>
                Assert.Equal(Ms(4_000), ((MediaContent)i.Content).SourceInTicks));
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Deleting_the_shortest_member_of_a_column_culls_the_sub_minimum_slivers()
        {
            // real recordings' audio ends a hair before the video; deleting the audio clip must
            // not leave 30ms video slivers behind
            var project = BuildRecording(audioStreamDurationMs: 9_970,
                segments: new[] { new KeepSegment(0, Ms(10_000)) });
            var session = new EditorSession(project, null, null);

            var audio = session.Project.Items.First(i => i.Content is MediaContent { StreamIndex: 2 });
            session.RippleDeleteItem(audio.Id);

            Assert.Empty(session.Project.Items);
            Assert.Empty(session.Project.Validate());
        }
    }
}
