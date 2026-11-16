using System;
using System.Linq;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    public class TimelineOpsTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>The Phase-1 import shape: one recording, three streams, three grouped tracks,
        /// three items covering [0, 10s) with a 2s source in-point on each.</summary>
        private static Project RecordingProject(out Item screen, out Item webcam, out Item audio)
        {
            var sourceId = Guid.NewGuid();
            var linkGroup = Guid.NewGuid();
            var screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0, GroupId = linkGroup };
            var webcamTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Webcam", Order = 1, GroupId = linkGroup };
            var audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 2, GroupId = linkGroup };

            Item NewItem(Track track, int streamIndex) => new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(10_000),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = streamIndex, SourceInTicks = Ms(2_000) },
            };

            screen = NewItem(screenTrack, 0);
            webcam = NewItem(webcamTrack, 1);
            audio = NewItem(audioTrack, 2);

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = @"C:\rec\input.mp4",
                        Streams =
                        {
                            new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 2, Kind = StreamKind.Audio, DurationTicks = Ms(60_000) },
                        },
                    },
                },
                Tracks = { screenTrack, webcamTrack, audioTrack },
                Items = { screen, webcam, audio },
            };

            return project;
        }

        private static long SourceIn(Item item) => ((MediaContent)item.Content).SourceInTicks;

        // ---- move ----

        [Fact]
        public void Move_applies_to_every_time_linked_clip()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);

            var applied = TimeLinkedMoves.MoveLinked(project, screen.Id, Ms(3_000));

            Assert.Equal(Ms(3_000), applied);
            Assert.All(new[] { screen, webcam, audio }, i => Assert.Equal(Ms(3_000), i.TimelineStartTicks));
            // moving never touches duration or the media in-point.
            Assert.All(new[] { screen, webcam, audio }, i => Assert.Equal(Ms(10_000), i.DurationTicks));
            Assert.All(new[] { screen, webcam, audio }, i => Assert.Equal(Ms(2_000), SourceIn(i)));
        }

        [Fact]
        public void Move_clamps_at_the_timeline_origin()
        {
            var project = RecordingProject(out var screen, out var webcam, out _);
            TimeLinkedMoves.MoveLinked(project, screen.Id, Ms(1_000));

            var applied = TimeLinkedMoves.MoveLinked(project, webcam.Id, -Ms(5_000));

            Assert.Equal(-Ms(1_000), applied);
            Assert.Equal(0, screen.TimelineStartTicks);
            Assert.Equal(0, webcam.TimelineStartTicks);
        }

        [Fact]
        public void Move_on_an_ungrouped_row_moves_only_that_item()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            TimelineOps.UngroupTracks(project, new[] { webcam.TrackId });

            TimeLinkedMoves.MoveLinked(project, webcam.Id, Ms(1_500));

            Assert.Equal(Ms(1_500), webcam.TimelineStartTicks);
            Assert.Equal(0, screen.TimelineStartTicks);
            Assert.Equal(0, audio.TimelineStartTicks);
        }

        [Fact]
        public void Ops_throw_on_an_unknown_item()
        {
            var project = RecordingProject(out _, out _, out _);
            Assert.Throws<ArgumentException>(() => TimelineOps.Move(project, new[] { Guid.NewGuid() }, Ms(1)));
            Assert.Throws<ArgumentException>(() => TimelineOps.TrimStart(project, Guid.NewGuid(), Ms(1)));
            Assert.Throws<ArgumentException>(() => TimelineOps.TrimEnd(project, Guid.NewGuid(), Ms(1)));
        }

        // ---- trim ----

        [Fact]
        public void TrimStart_shrinks_only_the_target_and_advances_its_media_in_point()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);

            var applied = TimelineOps.TrimStart(project, webcam.Id, Ms(1_000));

            Assert.Equal(Ms(1_000), applied);
            Assert.Equal(Ms(1_000), webcam.TimelineStartTicks);
            Assert.Equal(Ms(9_000), webcam.DurationTicks);
            // the same source frame stays under the new in-point — which is exactly why the other
            // rows need no adjustment to stay in sync.
            Assert.Equal(Ms(3_000), SourceIn(webcam));

            foreach (var item in new[] { screen, audio })
            {
                Assert.Equal(0, item.TimelineStartTicks);
                Assert.Equal(Ms(10_000), item.DurationTicks);
                Assert.Equal(Ms(2_000), SourceIn(item));
            }
        }

        [Fact]
        public void TrimStart_clamps_to_the_minimum_segment_of_the_target_alone()
        {
            var project = RecordingProject(out var screen, out var webcam, out _);
            // a shorter clip on another row no longer governs the clamp — trim is per item.
            webcam.DurationTicks = Ms(2_000);

            var applied = TimelineOps.TrimStart(project, screen.Id, Ms(15_000));

            Assert.Equal(Ms(10_000) - TimelineOps.MinSegmentTicks, applied);
            Assert.Equal(TimelineOps.MinSegmentTicks, screen.DurationTicks);
            Assert.Equal(Ms(2_000), webcam.DurationTicks);
        }

        [Fact]
        public void TrimStart_extension_is_clamped_by_the_source_in_point()
        {
            var project = RecordingProject(out var screen, out _, out _);
            TimeLinkedMoves.MoveLinked(project, screen.Id, Ms(5_000));

            // 5s of timeline room, but only 2s of source before the in-point.
            var applied = TimelineOps.TrimStart(project, screen.Id, -Ms(4_000));

            Assert.Equal(-Ms(2_000), applied);
            Assert.Equal(Ms(3_000), screen.TimelineStartTicks);
            Assert.Equal(Ms(12_000), screen.DurationTicks);
            Assert.Equal(0, SourceIn(screen));
        }

        [Fact]
        public void TrimStart_extension_is_clamped_by_the_timeline_origin()
        {
            var project = RecordingProject(out var screen, out _, out _);
            TimeLinkedMoves.MoveLinked(project, screen.Id, Ms(1_000));

            // 2s of source available, but only 1s of timeline before the item.
            var applied = TimelineOps.TrimStart(project, screen.Id, -Ms(4_000));

            Assert.Equal(-Ms(1_000), applied);
            Assert.Equal(0, screen.TimelineStartTicks);
            Assert.Equal(Ms(1_000), SourceIn(screen));
        }

        [Fact]
        public void TrimEnd_shortens_only_the_target_and_clamps_to_the_minimum_segment()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);

            var applied = TimelineOps.TrimEnd(project, screen.Id, -Ms(20_000));

            Assert.Equal(TimelineOps.MinSegmentTicks - Ms(10_000), applied);
            Assert.Equal(TimelineOps.MinSegmentTicks, screen.DurationTicks);
            // trimming the end never moves the start or the in-point.
            Assert.Equal(0, screen.TimelineStartTicks);
            Assert.Equal(Ms(2_000), SourceIn(screen));
            Assert.All(new[] { webcam, audio }, i => Assert.Equal(Ms(10_000), i.DurationTicks));
        }

        [Fact]
        public void TrimEnd_extends_only_the_target()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);

            var applied = TimelineOps.TrimEnd(project, audio.Id, Ms(2_000));

            Assert.Equal(Ms(2_000), applied);
            Assert.Equal(Ms(12_000), audio.DurationTicks);
            Assert.All(new[] { screen, webcam }, i => Assert.Equal(Ms(10_000), i.DurationTicks));
        }

        [Fact]
        public void TrimEnd_extension_is_clamped_by_the_source_duration()
        {
            var project = RecordingProject(out var screen, out _, out _);
            // 60s stream, 2s in-point, 10s shown: 48s of unused tail is all there is to unroll.
            var applied = TimelineOps.TrimEnd(project, screen.Id, Ms(100_000));

            Assert.Equal(Ms(48_000), applied);
            Assert.Equal(Ms(58_000), screen.DurationTicks);
            Assert.Equal(Ms(2_000), SourceIn(screen));
        }

        [Fact]
        public void TrimEnd_past_source_end_only_shrinks_never_grows()
        {
            var project = RecordingProject(out var screen, out _, out _);
            // an older project (saved before the clamp) may already hang past the source end.
            screen.DurationTicks = Ms(70_000);

            Assert.Equal(0, TimelineOps.TrimEnd(project, screen.Id, Ms(1_000)));
            Assert.Equal(Ms(70_000), screen.DurationTicks);

            Assert.Equal(-Ms(5_000), TimelineOps.TrimEnd(project, screen.Id, -Ms(5_000)));
            Assert.Equal(Ms(65_000), screen.DurationTicks);
        }

        [Fact]
        public void TrimEnd_extension_is_unbounded_when_the_stream_duration_is_unknown()
        {
            var project = RecordingProject(out var screen, out _, out _);
            project.Sources[0].Streams[0].DurationTicks = 0;

            var applied = TimelineOps.TrimEnd(project, screen.Id, Ms(100_000));

            Assert.Equal(Ms(100_000), applied);
            Assert.Equal(Ms(110_000), screen.DurationTicks);
        }

        [Fact]
        public void TrimEnd_extension_is_unbounded_for_non_media_content()
        {
            var project = RecordingProject(out var screen, out _, out _);
            screen.Content = new TextContent { Text = "title" };

            var applied = TimelineOps.TrimEnd(project, screen.Id, Ms(100_000));

            Assert.Equal(Ms(100_000), applied);
            Assert.Equal(Ms(110_000), screen.DurationTicks);
        }

        // ---- split ----

        [Fact]
        public void Split_produces_back_to_back_items_with_correct_source_in()
        {
            var project = RecordingProject(out var screen, out _, out _);

            Assert.True(TimelineOps.SplitItem(project, screen.Id, Ms(4_000)));

            var halves = project.Items.Where(i => i.TrackId == screen.TrackId)
                                      .OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(2, halves.Count);

            Assert.Same(screen, halves[0]);
            Assert.Equal(0, halves[0].TimelineStartTicks);
            Assert.Equal(Ms(4_000), halves[0].DurationTicks);
            Assert.Equal(Ms(2_000), SourceIn(halves[0]));

            Assert.Equal(Ms(4_000), halves[1].TimelineStartTicks);
            Assert.Equal(Ms(6_000), halves[1].DurationTicks);
            // right half resumes exactly where the left half's source span ends.
            Assert.Equal(Ms(6_000), SourceIn(halves[1]));
            Assert.Equal(halves[0].TimelineEndTicks, halves[1].TimelineStartTicks);
        }

        [Fact]
        public void SplitAcross_cuts_every_covering_clip_and_the_two_sides_drag_apart()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            var tracks = project.Tracks.Select(t => t.GroupId).ToList();

            Assert.Equal((true, false), TimelineOps.SplitAcross(project, Ms(4_000)));

            Assert.Equal(6, project.Items.Count);

            var lefts = new[] { screen, webcam, audio };
            var rights = project.Items.Except(lefts).ToList();
            Assert.Equal(3, rights.Count);

            // no group state changes: half-open spans are what keep the sides apart
            Assert.Equal(tracks, project.Tracks.Select(t => t.GroupId));

            Assert.All(rights, i => Assert.Equal(Ms(4_000), i.TimelineStartTicks));
            Assert.All(rights, i => Assert.Equal(Ms(6_000), i.DurationTicks));
            Assert.All(rights, i => Assert.Equal(Ms(6_000), SourceIn(i)));

            // ...and the two sides edit independently of each other.
            TimeLinkedMoves.MoveLinked(project, rights[0].Id, Ms(1_000));
            Assert.All(rights, i => Assert.Equal(Ms(5_000), i.TimelineStartTicks));
            Assert.All(lefts, i => Assert.Equal(0, i.TimelineStartTicks));
        }

        /// <summary>The right-click split: cutting the screen row must leave the webcam and audio
        /// rows of the same recording untouched — the exact complaint that motivated it. Its row
        /// stays in the group, because a group is a row's.</summary>
        [Fact]
        public void SplitItem_cuts_only_the_item_it_is_given()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            var originalGroup = project.TrackOf(screen).GroupId;

            Assert.True(TimelineOps.SplitItem(project, screen.Id, Ms(4_000)));

            Assert.Equal(4, project.Items.Count); // one new segment, not three
            Assert.Equal(Ms(10_000), webcam.DurationTicks);
            Assert.Equal(Ms(10_000), audio.DurationTicks);

            var right = project.Items.Single(i => i.Id != screen.Id && i.TrackId == screen.TrackId);
            Assert.Equal(Ms(4_000), screen.DurationTicks);
            Assert.Equal(Ms(4_000), right.TimelineStartTicks);
            Assert.Equal(Ms(6_000), right.DurationTicks);
            Assert.Equal(Ms(6_000), SourceIn(right));

            Assert.Equal(originalGroup, project.TrackOf(right).GroupId);
        }

        /// <summary>A later cut across still behaves, because it only ever acts on the clips that
        /// cover the instant — the half that does not is simply left alone.</summary>
        [Fact]
        public void SplitItem_leaves_the_group_splittable_afterwards()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            Assert.True(TimelineOps.SplitItem(project, screen.Id, Ms(4_000)));

            Assert.True(TimelineOps.SplitAcross(project, Ms(6_000)).Cut);

            Assert.Equal(Ms(4_000), screen.DurationTicks);   // the untouched left half stays put
            Assert.Equal(Ms(6_000), webcam.DurationTicks);
            Assert.Equal(Ms(6_000), audio.DurationTicks);

            // the screen's right half covered 6s too, so it was cut as well
            var screenSegments = project.Items.Where(i => i.TrackId == screen.TrackId)
                                              .OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(3, screenSegments.Count);
            Assert.Equal(new[] { 0, Ms(4_000), Ms(6_000) },
                screenSegments.Select(i => i.TimelineStartTicks).ToArray());
        }

        [Fact]
        public void SplitItem_refuses_a_cut_that_is_off_the_item_or_too_short()
        {
            var project = RecordingProject(out var screen, out _, out _);

            Assert.False(TimelineOps.SplitItem(project, screen.Id, 0));
            Assert.False(TimelineOps.SplitItem(project, screen.Id, Ms(10_000)));
            Assert.False(TimelineOps.SplitItem(project, screen.Id, TimelineOps.MinSegmentTicks - 1));
            Assert.False(TimelineOps.SplitItem(project, screen.Id, Ms(10_000) - 1));
            Assert.Equal(3, project.Items.Count);
        }

        [Fact]
        public void Split_keeps_entry_on_the_left_and_moves_exit_to_the_right()
        {
            var project = RecordingProject(out var screen, out _, out _);
            var entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = Ms(300) };
            var exit = new Transition { Kind = TransitionKind.SlideRight, DurationTicks = Ms(300) };
            screen.Entry = entry;
            screen.Exit = exit;

            Assert.True(TimelineOps.SplitItem(project, screen.Id, Ms(4_000)));

            var right = project.Items.Single(i => i.TrackId == screen.TrackId && i.Id != screen.Id);
            Assert.Same(entry, screen.Entry);
            Assert.Null(screen.Exit);
            Assert.Null(right.Entry);
            Assert.Equal(TransitionKind.SlideRight, right.Exit.Kind);
            Assert.Equal(Ms(300), right.Exit.DurationTicks);
        }

        [Fact]
        public void Split_clones_the_transform_instead_of_sharing_it()
        {
            var project = RecordingProject(out _, out var webcam, out _);
            webcam.Transform = new Transform
            {
                X = 0.82, Y = 0.78, Scale = 0.2,
                Mask = new Mask { Shape = MaskShape.Circle },
            };

            Assert.True(TimelineOps.SplitItem(project, webcam.Id, Ms(4_000)));

            var right = project.Items.Single(i => i.TrackId == webcam.TrackId && i.Id != webcam.Id);
            Assert.NotSame(webcam.Transform, right.Transform);
            Assert.NotSame(webcam.Transform.Mask, right.Transform.Mask);
            Assert.Equal(0.82, right.Transform.X);
            Assert.Equal(MaskShape.Circle, right.Transform.Mask.Shape);

            webcam.Transform.X = 0.1;
            Assert.Equal(0.82, right.Transform.X);
        }

        [Fact]
        public void SplitAcross_refuses_a_whole_group_when_any_half_would_be_too_short()
        {
            var project = RecordingProject(out var screen, out var webcam, out _);
            // the webcam clip starts later, so a cut that is fine for the screen clip lands within
            // MinSegment of the webcam clip's start.
            webcam.TimelineStartTicks = Ms(4_000) - TimelineOps.MinSegmentTicks / 2;
            webcam.DurationTicks = Ms(6_000);

            var before = project.ToJson();
            Assert.Equal((false, true), TimelineOps.SplitAcross(project, Ms(4_000)));
            Assert.Equal(before, project.ToJson()); // all-or-nothing: untouched on rejection

            // near the very edges of a clip it also refuses.
            Assert.Equal((false, true), TimelineOps.SplitAcross(project, TimelineOps.MinSegmentTicks - 1));
            Assert.Equal((false, true), TimelineOps.SplitAcross(project, Ms(10_000) - 1));
            // and past every clip there is nothing to cut and nothing to refuse.
            Assert.Equal((false, false), TimelineOps.SplitAcross(project, Ms(20_000)));
            Assert.Equal(before, project.ToJson());
        }

        [Fact]
        public void SplitAcross_leaves_clips_that_do_not_cover_the_instant_alone()
        {
            var project = RecordingProject(out _, out var webcam, out _);
            // shorten the webcam clip so the cut falls past its end.
            webcam.DurationTicks = Ms(3_000);

            Assert.True(TimelineOps.SplitAcross(project, Ms(5_000)).Cut);

            Assert.Equal(5, project.Items.Count); // screen + audio split, webcam untouched
            Assert.Equal(Ms(3_000), webcam.DurationTicks);
            Assert.Single(project.Items, i => i.TrackId == webcam.TrackId);
        }

        // ---- ripple delete ----

        [Fact]
        public void RippleDelete_removes_the_group_and_closes_the_gap_on_all_tracks()
        {
            var project = RecordingProject(out var screen, out _, out _);

            // split at 4s, then ripple-delete the left pairs: the right pairs slide back to 0.
            Assert.True(TimelineOps.SplitAcross(project, Ms(4_000)).Cut);
            TimelineOps.RippleDelete(project, screen.Id);

            Assert.Equal(3, project.Items.Count);
            Assert.All(project.Items, i => Assert.Equal(0, i.TimelineStartTicks));
            Assert.All(project.Items, i => Assert.Equal(Ms(6_000), i.DurationTicks));
            // the survivors still play the later part of the source — a ripple delete of the
            // first half is exactly the old "cut [0,4s)".
            Assert.All(project.Items, i => Assert.Equal(Ms(6_000), SourceIn(i)));
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void RippleDelete_leaves_items_before_the_gap_alone()
        {
            var project = RecordingProject(out var screen, out _, out _);
            Assert.True(TimelineOps.SplitAcross(project, Ms(4_000)).Cut);
            var right = project.Items.First(i => i.TrackId == screen.TrackId && i.Id != screen.Id);

            TimelineOps.RippleDelete(project, right.Id);

            Assert.Equal(3, project.Items.Count);
            Assert.All(project.Items, i => Assert.Equal(0, i.TimelineStartTicks));
            Assert.All(project.Items, i => Assert.Equal(Ms(4_000), i.DurationTicks));
            Assert.All(project.Items, i => Assert.Equal(Ms(2_000), SourceIn(i)));
        }

        // ---- delete ----

        [Fact]
        public void Delete_removes_only_that_item_and_leaves_a_gap()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            Assert.True(TimelineOps.SplitAcross(project, Ms(4_000)).Cut);
            var webcamRight = project.Items.Single(i => i.TrackId == webcam.TrackId && i.Id != webcam.Id);

            Assert.True(TimelineOps.Delete(project, webcam.Id));

            Assert.Equal(5, project.Items.Count);
            Assert.DoesNotContain(webcam, project.Items);
            // no ripple: the other rows and the row's other segment stay where they were,
            // leaving [0, 4s) empty on the webcam row.
            Assert.Equal(Ms(4_000), webcamRight.TimelineStartTicks);
            Assert.All(new[] { screen, audio }, i => Assert.Equal(0, i.TimelineStartTicks));
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void Delete_returns_false_for_an_unknown_item()
        {
            var project = RecordingProject(out _, out _, out _);

            Assert.False(TimelineOps.Delete(project, Guid.NewGuid()));
            Assert.Equal(3, project.Items.Count);
        }

        // ---- ungroup ----

        [Fact]
        public void UngroupTrack_takes_only_that_row_out_of_the_drag()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            var group = project.TrackOf(screen).GroupId;

            TimelineOps.UngroupTracks(project, new[] { audio.TrackId });
            TimeLinkedMoves.MoveLinked(project, screen.Id, Ms(2_000));

            Assert.Equal(Ms(2_000), screen.TimelineStartTicks);
            Assert.Equal(Ms(2_000), webcam.TimelineStartTicks);
            Assert.Equal(0, audio.TimelineStartTicks);
            Assert.Null(project.TrackOf(audio).GroupId);
            Assert.Equal(group, project.TrackOf(webcam).GroupId);
        }

        // ---- playback speed ----

        [Fact]
        public void SetSpeed_scales_duration_and_keeps_the_source_span()
        {
            var project = RecordingProject(out _, out var webcam, out _);
            TimelineOps.UngroupTracks(project, new[] { webcam.TrackId });

            // 10s of timeline at 2x plays the same 10s of source in 5s.
            Assert.Equal(2.0, TimelineOps.SetSpeed(project, webcam.Id, 2.0));
            Assert.Equal(Ms(5_000), webcam.DurationTicks);
            Assert.Equal(Ms(2_000), SourceIn(webcam));

            // back to normal restores the original timeline length.
            TimelineOps.SetSpeed(project, webcam.Id, 1.0);
            Assert.Equal(Ms(10_000), webcam.DurationTicks);
        }

        [Fact]
        public void SetSpeed_clamps_against_the_next_item_on_the_track()
        {
            var project = RecordingProject(out _, out var webcam, out _);
            TimelineOps.UngroupTracks(project, new[] { webcam.TrackId });
            Assert.True(TimelineOps.SplitItem(project, webcam.Id, Ms(4_000)));

            // halving the left segment's speed wants 8s of timeline, but the right half starts
            // at 4s — the clip is end-trimmed into the gap instead of overlapping.
            TimelineOps.SetSpeed(project, webcam.Id, 0.5);
            Assert.Equal(Ms(4_000), webcam.DurationTicks);
            Assert.Equal(0.5, ((MediaContent)webcam.Content).Speed);
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void Trim_and_split_scale_source_movement_by_the_speed()
        {
            var project = RecordingProject(out _, out var webcam, out _);
            TimelineOps.UngroupTracks(project, new[] { webcam.TrackId });
            TimelineOps.SetSpeed(project, webcam.Id, 2.0); // 5s timeline over source [2s, 12s)

            // trimming 1s off the start advances the source in-point by 2s.
            Assert.Equal(Ms(1_000), TimelineOps.TrimStart(project, webcam.Id, Ms(1_000)));
            Assert.Equal(Ms(4_000), SourceIn(webcam));
            Assert.Equal(Ms(4_000), webcam.DurationTicks);

            // splitting 2s in puts the right half's in-point 4s further into the source.
            Assert.True(TimelineOps.SplitItem(project, webcam.Id, webcam.TimelineStartTicks + Ms(2_000)));
            var right = project.Items.Single(i =>
                i.TrackId == webcam.TrackId && i.TimelineStartTicks > webcam.TimelineStartTicks);
            Assert.Equal(Ms(8_000), SourceIn(right));
            Assert.Equal(2.0, ((MediaContent)right.Content).Speed);
        }

        [Fact]
        public void TrimEnd_extension_is_bounded_by_the_source_at_the_item_speed()
        {
            var project = RecordingProject(out _, out var webcam, out _);
            TimelineOps.UngroupTracks(project, new[] { webcam.TrackId });
            TimelineOps.SetSpeed(project, webcam.Id, 2.0); // 5s timeline over source [2s, 12s), stream 60s

            // remaining source is 48s, which at 2x is 24s of timeline extension.
            Assert.Equal(Ms(24_000), TimelineOps.TrimEnd(project, webcam.Id, Ms(60_000)));
        }

        // ------------------------------------------------------------- lone-group collapse

        /// <summary>A group is only worth having across rows: once one row of it holds clips —
        /// the other rows ungrouped or emptied — it dissolves, and a group still spanning two rows
        /// is left alone.</summary>
        [Fact]
        public void CollapseLoneGroups_dissolves_a_group_left_on_one_track()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);

            // three rows: nothing to do
            Assert.False(TimelineOps.CollapseLoneGroups(project));
            Assert.NotNull(project.TrackOf(screen).GroupId);

            // two rows: still a sync worth keeping
            TimelineOps.UngroupTracks(project, new[] { webcam.TrackId });
            Assert.False(TimelineOps.CollapseLoneGroups(project));
            Assert.NotNull(project.TrackOf(screen).GroupId);
            Assert.Equal(project.TrackOf(screen).GroupId, project.TrackOf(audio).GroupId);

            // one row: nothing left to keep in step
            TimelineOps.UngroupTracks(project, new[] { audio.TrackId });
            Assert.True(TimelineOps.CollapseLoneGroups(project));
            Assert.All(project.Tracks, t => Assert.Null(t.GroupId));

            Assert.False(TimelineOps.CollapseLoneGroups(project)); // idempotent
        }

        /// <summary>Several segments of one row are still one row, and dissolving the group is
        /// what lets those segments be dragged apart.</summary>
        [Fact]
        public void CollapseLoneGroups_counts_rows_not_clips()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            project.Items.Remove(webcam);
            project.Items.Remove(audio);
            Assert.True(TimelineOps.SplitItem(project, screen.Id, Ms(5_000)));
            Assert.Equal(2, project.Items.Count);

            Assert.True(TimelineOps.CollapseLoneGroups(project));

            // on every row of the group, the emptied ones too
            Assert.All(project.Tracks, t => Assert.Null(t.GroupId));
        }

        /// <summary>An emptied row is not a member, but it keeps its id while the group lives, so
        /// a later paste back onto it rejoins.</summary>
        [Fact]
        public void CollapseLoneGroups_keeps_an_emptied_rows_id_while_two_other_rows_hold_clips()
        {
            var project = RecordingProject(out var screen, out _, out var audio);
            var group = project.TrackOf(screen).GroupId;
            project.Items.Remove(audio);

            Assert.False(TimelineOps.CollapseLoneGroups(project));

            Assert.All(project.Tracks, t => Assert.Equal(group, t.GroupId));
        }

        /// <summary>Cut then Paste of a whole group has to find it again.</summary>
        [Fact]
        public void CollapseLoneGroups_leaves_a_group_with_every_row_empty()
        {
            var project = RecordingProject(out var screen, out _, out _);
            var group = project.TrackOf(screen).GroupId;
            project.Items.Clear();

            Assert.False(TimelineOps.CollapseLoneGroups(project));

            Assert.All(project.Tracks, t => Assert.Equal(group, t.GroupId));
        }

        /// <summary>An overlay row gets no exception: a cursor left alone in a group is as lone as
        /// anything else, and validation no longer asks for a group.</summary>
        [Fact]
        public void CollapseLoneGroups_dissolves_a_group_left_on_an_overlay_row()
        {
            var project = RecordingProject(out var screen, out var webcam, out var audio);
            project.Items.Remove(webcam);
            project.Items.Remove(audio);
            var cursorTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Cursor", Order = 3, GroupId = project.TrackOf(screen).GroupId };
            project.Tracks.Add(cursorTrack);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = cursorTrack.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(10_000),
                Content = new CursorContent { SourceId = project.Sources[0].Id },
            });

            // screen + cursor: two rows, untouched
            Assert.False(TimelineOps.CollapseLoneGroups(project));

            project.Items.Remove(screen);
            Assert.True(TimelineOps.CollapseLoneGroups(project));
            Assert.Null(cursorTrack.GroupId);
            Assert.Empty(project.Validate());
        }
    }
}
