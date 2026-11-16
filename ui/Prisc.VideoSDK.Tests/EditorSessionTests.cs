using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Playback;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="EditorSession"/> — the single mutation funnel. Pure model math: no FFmpeg, no
    /// filesystem (persistence is a counting stub), so every test drives the real pipeline —
    /// snapshot, mutate, normalize, validate/rollback, undo, persist, notify — end to end.
    /// </summary>
    public class EditorSessionTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>The recording shape: three linked items covering [0, 10s) on Screen/Webcam/
        /// Audio rows, over one 60s source with a 2s in-point — same fixture as
        /// <see cref="TimelineOpsTests"/>.</summary>
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

            return new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = TestPath.Native(@"C:\rec\input.mp4"),
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
        }

        private sealed class CountingPersistence : IEditorPersistence
        {
            public int Writes;
            public byte[] Last;

            public void Write(byte[] utf8Json)
            {
                Writes++;
                Last = utf8Json;
            }

            public string LastJson => Encoding.UTF8.GetString(Last);
        }

        /// <summary>Session over the recording fixture with a counting sink and a synchronous
        /// save scheduler — every committed mutation is one <see cref="CountingPersistence"/>
        /// write.</summary>
        private static EditorSession NewSession(out Item screen, out Item webcam, out Item audio,
            out CountingPersistence persist)
        {
            persist = new CountingPersistence();
            return new EditorSession(RecordingProject(out screen, out webcam, out audio), persist, save => save());
        }

        /// <summary>A 1280x720 clip with one video and one audio stream, for import tests.</summary>
        private static MediaProbeResult ClipProbe(bool withAudio = true) => new MediaProbeResult
        {
            Path = TestPath.Native(@"C:\media\clip.mp4"),
            DurationTicks = Ms(8_000),
            VideoStreams = new[]
            {
                new VideoStreamProbe { StreamIndex = 0, Width = 1280, Height = 720, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(8_000) },
            },
            AudioStreams = withAudio
                ? new[] { new AudioStreamProbe { StreamIndex = 1, SampleRate = 48_000, Channels = 2, DurationTicks = Ms(7_900) } }
                : Array.Empty<AudioStreamProbe>(),
            HasAudio = withAudio,
        };

        /// <summary>A reprobe matching the recording fixture's stream shape exactly.</summary>
        private static MediaProbeResult RecordingReprobe(string path, bool includeWebcamStream = true)
        {
            var video = new List<VideoStreamProbe>
            {
                new VideoStreamProbe { StreamIndex = 0, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
            };
            if (includeWebcamStream)
                video.Add(new VideoStreamProbe { StreamIndex = 1, Width = 640, Height = 480, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) });

            return new MediaProbeResult
            {
                Path = path,
                DurationTicks = Ms(60_000),
                VideoStreams = video,
                AudioStreams = new[] { new AudioStreamProbe { StreamIndex = 2, SampleRate = 48_000, Channels = 2, DurationTicks = Ms(60_000) } },
                HasAudio = true,
            };
        }

        private static Item Resolve(EditorSession session, Guid itemId) =>
            session.Project.Items.Single(i => i.Id == itemId);

        // -------------------------------------------------------------------- the undo round trip

        [Fact]
        public void Every_mutator_mutates_and_one_undo_restores_byte_identical_json()
        {
            var cases = new (string Name,
                Action<EditorSession, Item, Item, Item> Setup,
                Action<EditorSession, Item, Item, Item> Act)[]
            {
                ("MoveItems", null, (s, sc, wc, au) => s.MoveLinked(sc.Id, Ms(1_000))),
                ("TrimItemStart", null, (s, sc, wc, au) => s.TrimItemStart(sc.Id, Ms(500))),
                ("TrimItemEnd", null, (s, sc, wc, au) => s.TrimItemEnd(sc.Id, -Ms(500))),
                ("SplitItemAt", null, (s, sc, wc, au) => Assert.True(s.SplitItemAt(sc.Id, Ms(4_000)))),
                ("SplitAtPlayhead", null, (s, sc, wc, au) => Assert.True(s.SplitAtPlayhead(Ms(4_000)))),
                ("RippleDeleteItem", null, (s, sc, wc, au) => s.RippleDeleteItem(sc.Id)),
                ("DeleteItem", null, (s, sc, wc, au) => s.DeleteItems(new[] { wc.Id })),
                ("UngroupTrack", null, (s, sc, wc, au) => s.UngroupTracks(new[] { wc.TrackId })),
                ("SetTrackHidden", null, (s, sc, wc, au) => s.SetTrackHidden(wc.TrackId, true)),
                ("SetTrackMuted", null, (s, sc, wc, au) => s.SetTrackMuted(au.TrackId, true)),
                ("RenameTrack", null, (s, sc, wc, au) => s.RenameTrack(au.TrackId, "Microphone")),
                ("EditItem", null, (s, sc, wc, au) => s.EditItem(wc.Id, i => i.Transform.X = 0.8)),
                ("EditPlacement", null, (s, sc, wc, au) => Assert.True(s.EditPlacement(wc.Id, i => i.Transform.X = 0.8, "x", null))),
                ("SetTrackPlacementLocked", null, (s, sc, wc, au) => s.SetTrackPlacementLocked(wc.TrackId, true)),
                ("SetTrackPlacementLocked off", (s, sc, wc, au) => s.SetTrackPlacementLocked(wc.TrackId, true),
                    (s, sc, wc, au) => s.SetTrackPlacementLocked(wc.TrackId, false)),
                ("AddText", null, (s, sc, wc, au) => Assert.NotNull(s.AddText(Ms(12_000), Ms(5_000)))),
                ("AddImage", null, (s, sc, wc, au) => Assert.NotNull(s.AddImage(TestPath.Native(@"C:\media\logo.png"), Ms(12_000), Ms(5_000)))),
                ("ImportMedia", null, (s, sc, wc, au) => Assert.NotEmpty(s.ImportMedia(TestPath.Native(@"C:\media\clip.mp4"), ClipProbe(), 0))),
                ("RelinkSource", null, (s, sc, wc, au) => s.RelinkSource(
                    ((MediaContent)sc.Content).SourceId, TestPath.Native(@"C:\rec\moved.mp4"), RecordingReprobe(TestPath.Native(@"C:\rec\moved.mp4")))),
                ("RemoveSource", null, (s, sc, wc, au) =>
                    Assert.True(s.RemoveSource(((MediaContent)sc.Content).SourceId))),
            };

            foreach (var (name, setup, act) in cases)
            {
                var session = new EditorSession(RecordingProject(out var sc, out var wc, out var au), null, null);
                setup?.Invoke(session, sc, wc, au);

                var before = session.Project.ToJson();
                act(session, sc, wc, au);
                Assert.True(before != session.Project.ToJson(), $"{name} did not change the project.");

                session.Undo();
                Assert.True(before == session.Project.ToJson(), $"{name}: undo did not restore byte-identical JSON.");
            }
        }

        [Fact]
        public void Undo_and_redo_replace_the_project_instance_and_raise_structural()
        {
            var session = NewSession(out var screen, out _, out _, out var persist);
            var kinds = new List<ProjectChangeKind>();
            var historyEvents = 0;
            session.ProjectChanged += (_, e) => kinds.Add(e.Kind);
            session.HistoryChanged += (_, _) => historyEvents++;

            var original = session.Project;
            var before = session.Project.ToJson();
            session.MoveLinked(screen.Id, Ms(1_000));
            var after = session.Project.ToJson();

            Assert.True(session.CanUndo);
            Assert.False(session.CanRedo);

            session.Undo();
            Assert.NotSame(original, session.Project);
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
            Assert.True(session.CanRedo);

            session.Redo();
            Assert.Equal(after, session.Project.ToJson());
            Assert.True(session.CanUndo);
            Assert.False(session.CanRedo);

            Assert.Equal(new[] { ProjectChangeKind.Mapping, ProjectChangeKind.Structural, ProjectChangeKind.Structural }, kinds);
            Assert.Equal(3, historyEvents); // push, undo, redo
            Assert.Equal(3, persist.Writes); // every committed state is persisted, restores included
        }

        [Fact]
        public void A_new_edit_clears_the_redo_stack()
        {
            var session = NewSession(out var screen, out _, out _, out _);

            session.MoveLinked(screen.Id, Ms(1_000));
            session.Undo();
            Assert.True(session.CanRedo);

            session.MoveLinked(screen.Id, Ms(2_000));
            Assert.False(session.CanRedo);
        }

        [Fact]
        public void The_undo_stack_caps_at_100_entries_evicting_the_oldest()
        {
            var session = NewSession(out var screen, out _, out _, out _);

            for (var i = 0; i < 105; i++)
                session.MoveLinked(screen.Id, Ms(10));

            var undos = 0;
            while (session.CanUndo)
            {
                session.Undo();
                undos++;
            }

            Assert.Equal(EditorSession.UndoCapacity, undos);
            // the five evicted entries' moves are unreachable: undo bottoms out at 5 x 10ms.
            Assert.Equal(Ms(50), Resolve(session, screen.Id).TimelineStartTicks);
        }

        // -------------------------------------------------------------------------------- no-ops

        [Fact]
        public void A_no_op_mutation_records_and_notifies_nothing()
        {
            var session = NewSession(out var screen, out _, out _, out var persist);
            var projectEvents = 0;
            var historyEvents = 0;
            session.ProjectChanged += (_, _) => projectEvents++;
            session.HistoryChanged += (_, _) => historyEvents++;

            Assert.Equal(0, session.MoveLinked(screen.Id, 0));
            session.SetTrackHidden(screen.TrackId, false); // already visible

            Assert.False(session.CanUndo);
            Assert.Equal(0, persist.Writes);
            Assert.Equal(0, projectEvents);
            Assert.Equal(0, historyEvents);
        }

        // ------------------------------------------------------------------------------ splits

        /// <summary>The toolbar's Ctrl+K cuts straight down the timeline: every row covering the
        /// playhead, text rows included, whatever happens to be selected. A selected clip used to
        /// narrow it to that one group, which made the button's behavior depend on invisible
        /// state.</summary>
        [Fact]
        public void SplitAtPlayhead_cuts_every_row_regardless_of_the_selection()
        {
            var session = NewSession(out var screen, out var webcam, out var audio, out _);
            var text = session.AddText(0, Ms(8_000));
            session.Select(screen.Id);

            Assert.True(session.SplitAtPlayhead(Ms(4_000)));

            foreach (var id in new[] { screen.Id, webcam.Id, audio.Id, text.Id })
            {
                var left = session.Project.Items.First(i => i.Id == id);
                Assert.Equal(Ms(4_000), left.DurationTicks);
                Assert.Contains(session.Project.Items,
                    i => i.TrackId == left.TrackId && i.TimelineStartTicks == Ms(4_000));
            }
        }

        /// <summary>…while the right-click split cuts the one clip, leaving the rest of its
        /// recording whole.</summary>
        [Fact]
        public void SplitItemAt_cuts_one_clip_out_of_a_linked_recording()
        {
            var session = NewSession(out var screen, out var webcam, out var audio, out _);

            Assert.True(session.SplitItemAt(screen.Id, Ms(4_000)));

            Assert.Equal(Ms(4_000), session.Project.Items.First(i => i.Id == screen.Id).DurationTicks);
            Assert.Equal(Ms(10_000), session.Project.Items.First(i => i.Id == webcam.Id).DurationTicks);
            Assert.Equal(Ms(10_000), session.Project.Items.First(i => i.Id == audio.Id).DurationTicks);
            Assert.Equal(4, session.Project.Items.Count);
        }

        // --------------------------------------------------------------------------- z ordering

        /// <summary>Screen is Order 0 and Webcam Order 1, so the webcam composites over the screen.
        /// Moving the screen towards the front swaps that.</summary>
        [Fact]
        public void MoveTrackLayer_swaps_a_row_with_the_one_it_passes()
        {
            var session = NewSession(out var screen, out var webcam, out var audio, out _);

            Assert.True(session.MoveTrackLayer(screen.TrackId, towardsFront: true));

            Assert.Equal(1, Track(session, screen.TrackId).Order);
            Assert.Equal(0, Track(session, webcam.TrackId).Order);
            // audio keeps its place above every video row, which is what keeps it at the bottom
            Assert.Equal(2, Track(session, audio.TrackId).Order);
        }

        [Fact]
        public void MoveTrackLayer_refuses_to_walk_off_either_end()
        {
            var session = NewSession(out var screen, out var webcam, out var audio, out _);

            Assert.False(session.MoveTrackLayer(screen.TrackId, towardsFront: false)); // already last
            Assert.False(session.MoveTrackLayer(webcam.TrackId, towardsFront: true));  // already first
            Assert.False(session.CanMoveTrackLayer(screen.TrackId, towardsFront: false));
            Assert.False(session.CanMoveTrackLayer(webcam.TrackId, towardsFront: true));
            Assert.True(session.CanMoveTrackLayer(screen.TrackId, towardsFront: true));
            Assert.True(session.CanMoveTrackLayer(webcam.TrackId, towardsFront: false));

            // audio does not stack at all
            Assert.False(session.CanMoveTrackLayer(audio.TrackId, towardsFront: true));
            Assert.False(session.MoveTrackLayer(audio.TrackId, towardsFront: true));

            Assert.False(session.CanMoveTrackLayer(Guid.NewGuid(), towardsFront: true));
            Assert.False(session.CanUndo); // nothing above changed the project
        }

        /// <summary>Order is neither unique nor contiguous, so the move renumbers instead of
        /// swapping two values — a swap between rows that share an Order would do nothing.</summary>
        [Fact]
        public void MoveTrackLayer_separates_rows_that_shared_an_order()
        {
            var session = NewSession(out var screen, out var webcam, out _, out _);
            session.EditItem(screen.Id, _ => { }, structural: true); // no-op, keeps the fixture honest

            Track(session, screen.TrackId).Order = 1; // now tied with the webcam row
            Track(session, webcam.TrackId).Order = 1;

            // the tie-break is (Order, Id), so whichever id sorts first is at the back
            var backId = Track(session, screen.TrackId).Id.CompareTo(Track(session, webcam.TrackId).Id) < 0
                ? screen.TrackId
                : webcam.TrackId;

            Assert.True(session.MoveTrackLayer(backId, towardsFront: true));

            var orders = new[] { Track(session, screen.TrackId).Order, Track(session, webcam.TrackId).Order };
            Assert.Equal(new[] { 0, 1 }, orders.OrderBy(o => o).ToArray()); // separated, not still tied
            Assert.Equal(1, Track(session, backId).Order);                  // …and it really moved forward
        }

        [Fact]
        public void MoveTrackLayer_is_structural_and_undoable()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var seen = new List<ProjectChangeKind>();
            session.ProjectChanged += (_, e) => seen.Add(e.Kind);

            session.MoveTrackLayer(screen.TrackId, towardsFront: true);
            Assert.Equal(new[] { ProjectChangeKind.Structural }, seen.ToArray());

            session.Undo();
            Assert.Equal(0, Track(session, screen.TrackId).Order);
        }

        /// <summary>Text and image rows are video rows, so they share the one stack: a text card
        /// can sit behind a video simply by having the lower Order.</summary>
        [Fact]
        public void MoveTrackLayer_puts_a_text_row_behind_a_video_row()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var text = session.AddText(0, Ms(2_000));
            var textTrack = Track(session, text.TrackId);

            // a new card is frontmost, so it starts ahead of the screen
            Assert.True(textTrack.Order > Track(session, screen.TrackId).Order);

            Assert.True(session.MoveTrackLayer(text.TrackId, towardsFront: false));
            Assert.True(session.MoveTrackLayer(text.TrackId, towardsFront: false));

            Assert.True(Track(session, text.TrackId).Order < Track(session, screen.TrackId).Order);
        }

        /// <summary>The headers' drag-reorder drop: one move over any distance, so the drag is one
        /// undo entry rather than one per row it crossed. Index 0 is the backmost layer.</summary>
        [Fact]
        public void MoveTrackToIndex_lifts_a_row_to_an_absolute_layer()
        {
            var session = NewSession(out var screen, out var webcam, out var audio, out _);
            var text = session.AddText(0, Ms(2_000)); // a third video row, frontmost

            Assert.True(session.MoveTrackToIndex(screen.TrackId, 2)); // screen to the front

            Assert.Equal(0, Track(session, webcam.TrackId).Order);
            Assert.Equal(1, Track(session, text.TrackId).Order);
            Assert.Equal(2, Track(session, screen.TrackId).Order);
            Assert.Equal(3, Track(session, audio.TrackId).Order); // still above every video row

            session.Undo();
            Assert.Equal(0, Track(session, screen.TrackId).Order);
        }

        [Fact]
        public void MoveTrackToIndex_refuses_a_no_op_or_an_index_outside_the_row_kind()
        {
            var session = NewSession(out var screen, out _, out var audio, out _);

            Assert.False(session.MoveTrackToIndex(screen.TrackId, 0));  // where it already is
            Assert.False(session.MoveTrackToIndex(screen.TrackId, 2));  // past the last video row
            Assert.False(session.MoveTrackToIndex(screen.TrackId, -1));
            Assert.False(session.MoveTrackToIndex(audio.TrackId, 1));   // one audio row, one slot
            Assert.False(session.MoveTrackToIndex(Guid.NewGuid(), 0));
            Assert.False(session.CanUndo); // nothing above changed the project
        }

        /// <summary>Audio does not stack, so <see cref="EditorSession.MoveTrackLayer"/> refuses it —
        /// but the audio rows' order is still the order the timeline lists them in, and dragging one
        /// under the video it belongs to is a real edit.</summary>
        [Fact]
        public void MoveTrackToIndex_reorders_audio_rows_that_MoveTrackLayer_will_not()
        {
            var session = NewSession(out _, out _, out var audio, out _);
            var mic = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Mic", Order = 9 };
            session.Project.Tracks.Add(mic);

            Assert.False(session.MoveTrackLayer(mic.Id, towardsFront: false));

            Assert.True(session.MoveTrackToIndex(mic.Id, 0));
            Assert.Equal(2, Track(session, mic.Id).Order);            // first of the audio block…
            Assert.Equal(3, Track(session, audio.TrackId).Order);     // …which still sits above the video rows
        }

        /// <summary>A duplicated row is independent of the recording it was copied from: left in
        /// the group, every group move and split would apply to it as well.</summary>
        [Fact]
        public void DuplicateTrack_leaves_the_copy_out_of_the_original_group()
        {
            var session = NewSession(out var screen, out var webcam, out _, out _);
            var group = Track(session, screen.TrackId).GroupId;
            Assert.NotNull(group);

            Assert.True(session.DuplicateTrack(screen.TrackId));

            var copy = session.Project.Items.Single(i => i.TrackId != screen.TrackId
                && session.Project.Tracks.First(t => t.Id == i.TrackId).Name == "Screen copy");
            Assert.Null(Track(session, copy.TrackId).GroupId);
            Assert.Equal(group, Track(session, screen.TrackId).GroupId);

            session.MoveLinked(webcam.Id, Ms(1_000));
            Assert.Equal(0, session.Project.Items.Single(i => i.Id == copy.Id).TimelineStartTicks);
        }

        /// <summary>A new card goes in front of everything, and a second one at a free time reuses
        /// that same row rather than stacking up a new one.</summary>
        [Fact]
        public void AddedItems_land_on_the_frontmost_row()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            var tracksBefore = session.Project.Tracks.Count;

            var first = session.AddText(0, Ms(2_000));
            Assert.Equal(tracksBefore + 1, session.Project.Tracks.Count);
            Assert.Equal(session.Project.Tracks.Where(t => t.Kind == TrackKind.Video).Max(t => t.Order),
                Track(session, first.TrackId).Order);

            // free span on the frontmost row: reuse it
            var second = session.AddText(Ms(3_000), Ms(2_000));
            Assert.Equal(first.TrackId, second.TrackId);
            Assert.Equal(tracksBefore + 1, session.Project.Tracks.Count);

            // …overlapping it earns a new row in front, never a lower free one
            var third = session.AddText(0, Ms(1_000));
            Assert.NotEqual(first.TrackId, third.TrackId);
            Assert.NotEqual(webcam.TrackId, third.TrackId);
            Assert.Equal(session.Project.Tracks.Where(t => t.Kind == TrackKind.Video).Max(t => t.Order),
                Track(session, third.TrackId).Order);
        }

        private static Track Track(EditorSession session, Guid trackId) =>
            session.Project.Tracks.First(t => t.Id == trackId);

        // -------------------------------------------------------------------------------- kinds

        [Fact]
        public void Change_kinds_and_origin_flow_through_to_the_event()
        {
            var session = NewSession(out var screen, out var webcam, out _, out _);
            var origin = new object();
            var seen = new List<(ProjectChangeKind Kind, object Origin)>();
            session.ProjectChanged += (_, e) => seen.Add((e.Kind, e.Origin));

            session.MoveLinked(screen.Id, Ms(1_000), origin);
            session.RenameTrack(webcam.TrackId, "Camera");
            session.SetTrackHidden(webcam.TrackId, true);
            session.DeleteItems(new[] { webcam.Id }, origin);

            Assert.Equal(ProjectChangeKind.Mapping, seen[0].Kind);
            Assert.Same(origin, seen[0].Origin);
            Assert.Equal(ProjectChangeKind.Mapping, seen[1].Kind);
            Assert.Null(seen[1].Origin);
            Assert.Equal(ProjectChangeKind.Structural, seen[2].Kind);
            Assert.Equal(ProjectChangeKind.Structural, seen[3].Kind);
        }

        // ------------------------------------------------------------------------------ gestures

        [Fact]
        public void A_gesture_of_50_moves_is_one_undo_entry_and_previews_never_persist()
        {
            var session = NewSession(out var screen, out _, out _, out var persist);
            var previews = 0;
            var committed = new List<ProjectChangeKind>();
            session.ProjectChanged += (_, e) =>
            {
                if (e.Kind == ProjectChangeKind.Preview)
                    previews++;
                else
                    committed.Add(e.Kind);
            };

            var before = session.Project.ToJson();
            using (var gesture = session.BeginGesture("Move"))
            {
                for (var i = 0; i < 50; i++)
                    session.MoveLinked(screen.Id, Ms(10));

                Assert.Equal(50, previews);
                Assert.Equal(0, persist.Writes);
                Assert.False(session.CanUndo);

                gesture.Commit();
            }

            Assert.Equal(Ms(500), Resolve(session, screen.Id).TimelineStartTicks);
            Assert.Equal(new[] { ProjectChangeKind.Mapping }, committed);
            Assert.Equal(1, persist.Writes);

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo); // exactly one entry spanned the whole drag
        }

        [Fact]
        public void Gesture_cancel_and_dispose_without_commit_restore_byte_identical()
        {
            var session = NewSession(out var screen, out _, out _, out var persist);
            var before = session.Project.ToJson();

            var gesture = session.BeginGesture("Move");
            session.MoveLinked(screen.Id, Ms(3_000));
            gesture.Cancel();

            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
            Assert.Equal(0, persist.Writes);

            // dispose-without-commit is the exception-safety path and must behave identically.
            using (session.BeginGesture("Move"))
                session.MoveLinked(screen.Id, Ms(3_000));

            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
            Assert.Equal(0, persist.Writes);
        }

        [Fact]
        public void A_no_op_gesture_commit_is_free()
        {
            var session = NewSession(out var screen, out _, out _, out var persist);

            using (var gesture = session.BeginGesture("Move"))
            {
                session.MoveLinked(screen.Id, 0);
                gesture.Commit();
            }

            Assert.False(session.CanUndo);
            Assert.Equal(0, persist.Writes);
        }

        [Fact]
        public void A_gesture_containing_a_structural_edit_commits_as_structural()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            var committed = new List<ProjectChangeKind>();
            session.ProjectChanged += (_, e) =>
            {
                if (e.Kind != ProjectChangeKind.Preview)
                    committed.Add(e.Kind);
            };

            using (var gesture = session.BeginGesture("Delete"))
            {
                session.DeleteItems(new[] { webcam.Id });
                gesture.Commit();
            }

            Assert.Equal(new[] { ProjectChangeKind.Structural }, committed);
        }

        [Fact]
        public void Gestures_do_not_nest_and_do_not_outlive_their_end()
        {
            var session = NewSession(out _, out _, out _, out _);

            var gesture = session.BeginGesture("Move");
            Assert.Throws<InvalidOperationException>(() => session.BeginGesture("Trim"));
            Assert.Throws<InvalidOperationException>(() => session.Undo());
            gesture.Commit();

            Assert.Throws<InvalidOperationException>(() => gesture.Commit());
            gesture.Dispose(); // disposing an ended gesture is a no-op, not a cancel
        }

        // ---------------------------------------------------------------------------- coalescing

        [Fact]
        public void Same_key_edits_inside_the_window_merge_into_one_undo_entry()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            long now = 0;
            session.Clock = () => now;

            var before = session.Project.ToJson();
            session.EditItem(webcam.Id, i => i.Transform.X = 0.6, "inspector:x");
            now += 500;
            session.EditItem(webcam.Id, i => i.Transform.X = 0.7, "inspector:x");

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void Edits_beyond_the_window_or_with_different_or_null_keys_stay_separate()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            long now = 0;
            session.Clock = () => now;

            // beyond the window
            session.EditItem(webcam.Id, i => i.Transform.X = 0.6, "inspector:x");
            now += 1_500;
            session.EditItem(webcam.Id, i => i.Transform.X = 0.7, "inspector:x");
            session.Undo();
            Assert.Equal(0.6, Resolve(session, webcam.Id).Transform.X);
            Assert.True(session.CanUndo);
            session.Undo();
            Assert.Equal(0.5, Resolve(session, webcam.Id).Transform.X);

            // different keys
            session.EditItem(webcam.Id, i => i.Transform.X = 0.6, "inspector:x");
            session.EditItem(webcam.Id, i => i.Transform.Y = 0.6, "inspector:y");
            session.Undo();
            Assert.Equal(0.5, Resolve(session, webcam.Id).Transform.Y);
            Assert.Equal(0.6, Resolve(session, webcam.Id).Transform.X);
            session.Undo();

            // null keys never coalesce
            session.EditItem(webcam.Id, i => i.Transform.X = 0.6);
            session.EditItem(webcam.Id, i => i.Transform.X = 0.7);
            session.Undo();
            Assert.Equal(0.6, Resolve(session, webcam.Id).Transform.X);
        }

        [Fact]
        public void The_window_slides_a_steady_stream_of_edits_into_one_entry()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            long now = 0;
            session.Clock = () => now;

            var before = session.Project.ToJson();
            for (var i = 0; i < 5; i++)
            {
                session.EditItem(webcam.Id, item => item.Transform.X = 0.6 + i * 0.01, "inspector:x");
                now += 900; // each within 1s of the previous, though 4.5s from the first
            }

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void Coalescing_never_reaches_across_an_undo_or_redo()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            session.Clock = () => 0;

            session.EditItem(webcam.Id, i => i.Transform.X = 0.6, "inspector:x");
            session.EditItem(webcam.Id, i => i.Transform.X = 0.7, "inspector:x"); // coalesced
            session.Undo();
            Assert.Equal(0.5, Resolve(session, webcam.Id).Transform.X);
            session.Redo();
            Assert.Equal(0.7, Resolve(session, webcam.Id).Transform.X);

            // an edit right after a redo must not merge into the restored entry.
            session.EditItem(webcam.Id, i => i.Transform.X = 0.8, "inspector:x");
            session.Undo();
            Assert.Equal(0.7, Resolve(session, webcam.Id).Transform.X);
            session.Undo();
            Assert.Equal(0.5, Resolve(session, webcam.Id).Transform.X);
        }

        /// <summary>The belt-and-braces under the inspector's item-scoped keys: even a caller
        /// whose coalesce key names no item cannot merge edits made across a selection change —
        /// the last thing the user did must always have its own undo step.</summary>
        [Fact]
        public void A_selection_change_breaks_coalescing()
        {
            var session = NewSession(out var screen, out var webcam, out _, out _);
            session.Clock = () => 0; // everything lands inside the coalesce window

            session.EditItem(webcam.Id, i => i.Transform.X = 0.6, "inspector:x");
            session.Select(screen.Id);
            session.EditItem(webcam.Id, i => i.Transform.X = 0.7, "inspector:x");

            session.Undo();
            Assert.Equal(0.6, Resolve(session, webcam.Id).Transform.X);
            Assert.True(session.CanUndo);
            session.Undo();
            Assert.Equal(0.5, Resolve(session, webcam.Id).Transform.X);
        }

        // ---------------------------------------------------------------------------- multi-edit

        [Fact]
        public void EditItems_edits_every_item_in_one_mutation_with_one_event_and_one_undo_entry()
        {
            var session = NewSession(out var screen, out var webcam, out _, out var persist);
            var events = new List<ProjectChangeKind>();
            session.ProjectChanged += (_, e) => events.Add(e.Kind);

            var before = session.Project.ToJson();
            session.EditItems(new[] { screen.Id, webcam.Id }, i => i.Transform.X = 0.8);

            Assert.Equal(0.8, Resolve(session, screen.Id).Transform.X);
            Assert.Equal(0.8, Resolve(session, webcam.Id).Transform.X);
            Assert.Equal(new[] { ProjectChangeKind.Mapping }, events);
            Assert.Equal(1, persist.Writes);

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo); // exactly one entry spanned both items

            // an empty id list is a free no-op (the undo itself persisted the restored state)
            var writesAfterUndo = persist.Writes;
            session.EditItems(Array.Empty<Guid>(), i => i.Transform.X = 0.9);
            Assert.False(session.CanUndo);
            Assert.Equal(writesAfterUndo, persist.Writes);
        }

        // ----------------------------------------------------------------------------- selection

        [Fact]
        public void Selection_is_session_owned_and_resolved_live()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var selectionEvents = 0;
            session.SelectionChanged += (_, _) => selectionEvents++;

            session.Select(screen.Id);
            Assert.Equal(new[] { screen.Id }, session.SelectedItemIds);
            Assert.Same(screen, session.SoleSelectedItem);
            Assert.Equal(1, selectionEvents);

            session.Select(screen.Id); // reselecting is a no-op
            Assert.Equal(1, selectionEvents);

            // undo replaces the item instances; the selection resolves against the new one.
            session.MoveLinked(screen.Id, Ms(1_000));
            session.Undo();
            Assert.NotSame(screen, session.SoleSelectedItem);
            Assert.Equal(screen.Id, session.SoleSelectedItem.Id);

            session.ClearSelection();
            Assert.Empty(session.SelectedItemIds);
            Assert.Null(session.SoleSelectedItem);

            session.ClearSelection(); // clearing nothing raises nothing
            Assert.Equal(2, selectionEvents);
        }

        [Fact]
        public void Deleting_the_selected_item_clears_selection_and_undo_restores_it()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            session.Select(screen.Id);

            session.DeleteItems(new[] { screen.Id });
            Assert.Empty(session.SelectedItemIds);
            Assert.Null(session.SoleSelectedItem);

            session.Undo();
            Assert.Equal(new[] { screen.Id }, session.SelectedItemIds);
            Assert.Equal(screen.Id, session.SoleSelectedItem.Id);
        }

        [Fact]
        public void Selection_restoration_filters_ids_that_no_longer_exist()
        {
            var session = NewSession(out var screen, out _, out _, out _);

            // the session does not validate selected ids up front; the first committed mutation
            // filters them, and the undo entry's dead id filters again on restore.
            session.Select(Guid.NewGuid());
            Assert.Null(session.SoleSelectedItem);

            session.MoveLinked(screen.Id, Ms(1_000));
            Assert.Empty(session.SelectedItemIds);

            session.Undo();
            Assert.Empty(session.SelectedItemIds);
            Assert.Null(session.SoleSelectedItem);
        }

        // ------------------------------------------------------------------- validation rollback

        [Fact]
        public void A_mutation_that_fails_validation_rolls_back_and_surfaces_without_throwing()
        {
            var session = NewSession(out var screen, out _, out _, out var persist);
            Assert.True(session.SplitItemAt(screen.Id, Ms(4_000)));
            var right = session.Project.Items.Single(i => i.TrackId == screen.TrackId && i.Id != screen.Id);

            var before = session.Project.ToJson();
            var writesBefore = persist.Writes;
            var projectEvents = 0;
            session.ProjectChanged += (_, _) => projectEvents++;
            var failures = new List<ValidationFailureEventArgs>();
            session.ValidationFailed += (_, e) => failures.Add(e);

            // sliding the right half back onto the left overlaps them — TimelineOps would never
            // do this, but EditItem accepts arbitrary writes, and validation is the net.
            session.EditItem(right.Id, i => i.TimelineStartTicks = 0);

            Assert.Equal(before, session.Project.ToJson());
            Assert.Equal(0, projectEvents);
            Assert.Equal(writesBefore, persist.Writes);
            var failure = Assert.Single(failures);
            Assert.Equal("Edit", failure.Operation);
            Assert.Contains(failure.Errors, e => e.Contains("overlap"));

            // no undo entry was pushed for the failure: one undo unwinds the split, nothing more.
            session.Undo();
            Assert.False(session.CanUndo);
            Assert.Equal(3, session.Project.Items.Count);
        }

        // --------------------------------------------------------------------------- track prune

        [Fact]
        public void Deleting_the_last_item_of_a_session_created_track_removes_the_track_and_undo_restores_both()
        {
            var session = NewSession(out _, out _, out _, out _);

            // both video rows are occupied over [0, 5s), so the image gets a fresh track.
            var image = session.AddImage(TestPath.Native(@"C:\media\logo.png"), 0, Ms(5_000));
            Assert.Equal(4, session.Project.Tracks.Count);
            var afterAdd = session.Project.ToJson();

            session.DeleteItems(new[] { image.Id });
            Assert.Equal(3, session.Project.Tracks.Count);
            Assert.DoesNotContain(session.Project.Tracks, t => t.Id == image.TrackId);

            session.Undo();
            Assert.Equal(afterAdd, session.Project.ToJson()); // track and item come back together
        }

        [Fact]
        public void A_row_the_project_opened_with_goes_with_its_last_clip_too()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            var before = session.Project.ToJson();

            session.DeleteItems(new[] { webcam.Id });

            Assert.DoesNotContain(session.Project.Tracks, t => t.Id == webcam.TrackId);

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        /// <summary>Only rows the edit emptied go: a row that held nothing before it (a voice take's
        /// row waiting for its first take, a row a file was saved with) is not the delete's.</summary>
        [Fact]
        public void A_delete_leaves_a_row_that_was_already_empty()
        {
            var session = NewSession(out var screen, out var webcam, out _, out _);
            session.DeleteItems(new[] { webcam.Id });
            var project = Project.FromJson(session.Project.ToJson());
            var empty = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Empty", Order = 50 };
            project.Tracks.Add(empty);
            var reopened = new EditorSession(project, null, null);

            reopened.DeleteItems(new[] { screen.Id });

            Assert.Contains(reopened.Project.Tracks, t => t.Id == empty.Id);
            Assert.DoesNotContain(reopened.Project.Tracks, t => t.Id == screen.TrackId);
        }

        // -------------------------------------------------------------------------- add / import

        [Fact]
        public void AddText_defaults_land_on_the_topmost_free_video_track()
        {
            var session = NewSession(out _, out var webcam, out var audio, out _);
            session.UngroupTracks(new[] { webcam.TrackId });
            session.UngroupTracks(new[] { audio.TrackId });

            // [12s, 17s) is free on both video rows; the webcam row is the topmost.
            var text = session.AddText(Ms(12_000), Ms(5_000));

            Assert.Equal(webcam.TrackId, text.TrackId);
            Assert.Equal(Ms(12_000), text.TimelineStartTicks);
            Assert.Equal(Ms(5_000), text.DurationTicks);

            var content = Assert.IsType<TextContent>(text.Content);
            Assert.Equal("Title", content.Text);
            Assert.Equal(1080 * 0.08, content.Size);
            Assert.Equal("#FFFFFFFF", content.Color);
            Assert.Equal(TextAlign.Center, content.Align);
            Assert.Equal(0.5, text.Transform.X);
            Assert.Equal(0.5, text.Transform.Y);
            Assert.Equal(1.0, text.Transform.Scale);

            // when every video row is busy over the span, a fresh row appears above them all
            // and the audio row shifts down.
            var second = session.AddText(0, Ms(5_000));
            var overlay = session.Project.Tracks.Single(t => t.Id == second.TrackId);
            Assert.Equal("Overlay", overlay.Name);
            Assert.Equal(TrackKind.Video, overlay.Kind);
            Assert.Equal(2, overlay.Order);
            Assert.Equal(3, session.Project.Tracks.Single(t => t.Kind == TrackKind.Audio).Order);
        }

        /// <summary>A hidden row is composed by nothing, so an item added to it is invisible with
        /// no explanation — and the webcam row is Hidden on every fresh edit of a recording whose
        /// overlay was never enabled, making it the topmost "free" row exactly when the timeline
        /// is empty or the playhead sits past the end.</summary>
        [Fact]
        public void AddText_never_lands_on_a_hidden_row()
        {
            var session = NewSession(out var screen, out var webcam, out var audio, out _);
            session.UngroupTracks(new[] { webcam.TrackId });
            session.UngroupTracks(new[] { audio.TrackId });
            session.SetTrackHidden(webcam.TrackId, true);

            // the hidden webcam row is skipped even though it is topmost and free
            var text = session.AddText(Ms(12_000), Ms(5_000));
            Assert.Equal(screen.TrackId, text.TrackId);

            // with the first card now filling that span, the hidden row is the only free one:
            // a fresh visible Overlay row is minted instead
            var second = session.AddText(Ms(12_000), Ms(5_000));
            var track = session.Project.Tracks.Single(t => t.Id == second.TrackId);
            Assert.Equal("Overlay", track.Name);
            Assert.False(track.Hidden);
        }

        /// <summary>A card on a grouped row would be time-linked to the recording, so a grouped
        /// frontmost row is passed over for a fresh one, even with its span free.</summary>
        [Fact]
        public void AddText_mints_a_new_top_row_when_the_frontmost_row_is_grouped()
        {
            var session = NewSession(out _, out var webcam, out _, out _);

            var text = session.AddText(Ms(12_000), Ms(5_000));

            var track = Track(session, text.TrackId);
            Assert.NotEqual(webcam.TrackId, text.TrackId);
            Assert.Equal("Overlay", track.Name);
            Assert.Null(track.GroupId);
            Assert.True(track.Order > Track(session, webcam.TrackId).Order);
        }

        /// <summary>The frontmost row is chosen first and only then checked: skipping a grouped row
        /// while choosing would land the card on the free row behind it, under the import the user
        /// can see.</summary>
        [Fact]
        public void AddText_does_not_fall_back_to_a_free_row_behind_a_grouped_one()
        {
            var session = NewSession(out _, out _, out _, out _);
            var freeRow = session.AddText(Ms(30_000), Ms(1_000)).TrackId;
            var import = session.ImportMedia(TestPath.Native(@"C:\media\clip.mp4"), ClipProbe(), 0);
            var importRow = import.Single(i => Track(session, i.TrackId).Kind == TrackKind.Video).TrackId;
            Assert.NotNull(Track(session, importRow).GroupId);
            Assert.True(Track(session, importRow).Order > Track(session, freeRow).Order);

            var text = session.AddText(Ms(50_000), Ms(5_000));

            Assert.NotEqual(freeRow, text.TrackId);
            Assert.NotEqual(importRow, text.TrackId);
            Assert.True(Track(session, text.TrackId).Order > Track(session, importRow).Order);
        }

        /// <summary>A card on a placement-locked row could not be put where the user wants it, so that
        /// row is passed over like a grouped one, for every kind of add, even with its span free.</summary>
        [Fact]
        public void An_add_mints_a_new_top_row_when_the_frontmost_row_is_placement_locked()
        {
            var adds = new (string Name, Func<EditorSession, Item> Add)[]
            {
                ("Text", s => s.AddText(Ms(30_000), Ms(1_000))),
                ("Image", s => s.AddImage(TestPath.Native(@"C:\media\logo.png"), Ms(30_000), Ms(1_000))),
                ("Obscure", s => s.AddObscure(Ms(30_000), Ms(1_000))),
                ("Shape", s => s.AddShape(Ms(30_000), Ms(1_000))),
            };

            foreach (var (name, add) in adds)
            {
                var session = NewSession(out _, out _, out _, out _);
                var free = session.AddText(Ms(12_000), Ms(5_000)).TrackId;
                var reused = add(session);
                Assert.True(free == reused.TrackId, $"{name}: an unlocked frontmost row with its span free is reused.");
                session.DeleteItems(new[] { reused.Id });

                session.SetTrackPlacementLocked(free, true);
                var item = add(session);

                Assert.True(free != item.TrackId, $"{name} landed on the locked row.");
                Assert.True(Track(session, item.TrackId).Order > Track(session, free).Order, $"{name} went behind it.");
                Assert.False(Track(session, item.TrackId).PlacementLocked);
            }
        }

        /// <summary>The frontmost row is chosen first and only then checked, the same as a grouped one:
        /// skipping a locked row while choosing would land the card on the free row behind it.</summary>
        [Fact]
        public void An_add_does_not_fall_back_to_a_free_row_behind_a_placement_locked_one()
        {
            var session = NewSession(out _, out _, out _, out _);
            var behind = session.AddText(Ms(30_000), Ms(1_000)).TrackId;
            var front = session.AddImage(TestPath.Native(@"C:\media\logo.png"), Ms(30_000), Ms(1_000)).TrackId;
            Assert.NotEqual(behind, front);
            session.SetTrackPlacementLocked(front, true);

            var text = session.AddText(Ms(50_000), Ms(5_000));

            Assert.NotEqual(behind, text.TrackId);
            Assert.NotEqual(front, text.TrackId);
            Assert.True(Track(session, text.TrackId).Order > Track(session, front).Order);
        }

        /// <summary>A hidden row is never a candidate, so a locked hidden row says nothing about the
        /// free row below it.</summary>
        [Fact]
        public void A_hidden_placement_locked_row_does_not_stop_a_card_reusing_the_row_below()
        {
            var session = NewSession(out _, out _, out _, out _);
            var visible = session.AddText(Ms(12_000), Ms(1_000)).TrackId;
            var hidden = session.AddText(Ms(12_000), Ms(1_000)).TrackId;
            Assert.NotEqual(visible, hidden);
            session.SetTrackPlacementLocked(hidden, true);
            session.SetTrackHidden(hidden, true);

            var text = session.AddText(Ms(50_000), Ms(5_000));

            Assert.Equal(visible, text.TrackId);
        }

        [Fact]
        public void AddImage_defaults_to_half_width_centered()
        {
            var session = NewSession(out _, out var webcam, out _, out _);

            var image = session.AddImage(TestPath.Native(@"C:\media\logo.png"), Ms(12_000), Ms(5_000));

            Assert.NotEqual(webcam.TrackId, image.TrackId);
            Assert.Equal(TestPath.Native(@"C:\media\logo.png"), Assert.IsType<ImageContent>(image.Content).Path);
            Assert.Equal(0.5, image.Transform.X);
            Assert.Equal(0.5, image.Transform.Y);
            Assert.Equal(0.5, image.Transform.Scale);
            Assert.Null(Track(session, image.TrackId).GroupId);
        }

        [Fact]
        public void ImportMedia_creates_source_tracks_and_linked_items_in_one_undo_entry()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var recordingGroup = Track(session, screen.TrackId).GroupId;
            var before = session.Project.ToJson();

            var created = session.ImportMedia(TestPath.Native(@"C:\media\clip.mp4"), ClipProbe(), Ms(2_000));

            Assert.Equal(2, created.Count);
            Assert.Empty(session.Project.Validate());

            // the source is mapped exactly as a recording's streams are.
            var source = session.Project.Sources.Single(s => s.Path == TestPath.Native(@"C:\media\clip.mp4"));
            Assert.Equal(new[] { 0, 1 }, source.Streams.Select(s => s.Index).ToArray());
            Assert.Equal(1280, source.Streams[0].Width);
            Assert.Equal(StreamKind.Audio, source.Streams[1].Kind);

            // video row above the recording's video rows, audio row at the very bottom.
            var videoItem = created.Single(i => i.Transform.Scale == 0.5);
            var audioItem = created.Single(i => i != videoItem);
            var videoTrack = session.Project.Tracks.Single(t => t.Id == videoItem.TrackId);
            var audioTrack = session.Project.Tracks.Single(t => t.Id == audioItem.TrackId);
            Assert.Equal(TrackKind.Video, videoTrack.Kind);
            Assert.Equal("clip", videoTrack.Name);
            Assert.Equal(2, videoTrack.Order);
            Assert.Equal(TrackKind.Audio, audioTrack.Kind);
            Assert.Equal(4, audioTrack.Order); // recording audio was bumped to 3

            // one item per stream at the playhead, linked as one file, in-points at zero.
            Assert.All(created, i => Assert.Equal(Ms(2_000), i.TimelineStartTicks));
            Assert.All(created, i => Assert.Equal(0L, ((MediaContent)i.Content).SourceInTicks));
            Assert.Equal(Ms(8_000), videoItem.DurationTicks);
            Assert.Equal(Ms(7_900), audioItem.DurationTicks);
            Assert.NotNull(videoTrack.GroupId);
            Assert.Equal(videoTrack.GroupId, audioTrack.GroupId);
            Assert.NotEqual(recordingGroup, videoTrack.GroupId);
            Assert.Equal(0.5, videoItem.Transform.X);

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo); // the whole import was one entry
        }

        [Fact]
        public void A_single_stream_import_is_not_linked_so_it_stays_movable()
        {
            var session = NewSession(out _, out _, out _, out _);

            var created = session.ImportMedia(TestPath.Native(@"C:\media\clip.mp4"), ClipProbe(withAudio: false), 0);

            var item = Assert.Single(created);
            Assert.Null(Track(session, item.TrackId).GroupId);
        }

        /// <summary>A silent audio stream (as the caller's scan reported it) gets no row: the
        /// source still describes the whole file, but the import is a lone video item — unlinked,
        /// because the group counts rows made, not streams probed.</summary>
        [Fact]
        public void ImportMedia_skips_the_audio_streams_reported_silent()
        {
            var session = NewSession(out _, out _, out _, out _);
            var tracksBefore = session.Project.Tracks.Count;

            var created = session.ImportMedia(TestPath.Native(@"C:\media\clip.mp4"), ClipProbe(), Ms(2_000),
                silentAudioStreams: new[] { 1 });

            var item = Assert.Single(created);
            Assert.Empty(session.Project.Validate());
            Assert.Equal(0, ((MediaContent)item.Content).StreamIndex);
            Assert.Null(Track(session, item.TrackId).GroupId);
            Assert.Equal(tracksBefore + 1, session.Project.Tracks.Count);

            var source = session.Project.Sources.Single(s => s.Path == TestPath.Native(@"C:\media\clip.mp4"));
            Assert.Equal(2, source.Streams.Count);

            // an index that names no audio stream changes nothing
            Assert.Equal(2, session.ImportMedia(TestPath.Native(@"C:\media\clip.mp4"), ClipProbe(), Ms(2_000),
                silentAudioStreams: new[] { 7 }).Count);
        }

        // ------------------------------------------------------------- lone-group collapse

        /// <summary>A recording with nothing but a screen row opens with its segments unlinked:
        /// a group with one row has nothing to keep in step, so
        /// <see cref="Model.TimelineOps.CollapseLoneGroups"/> dissolves it and each segment moves
        /// alone.</summary>
        [Fact]
        public void A_screen_only_recording_opens_ungrouped_and_movable()
        {
            var project = Model.RecordingProject.Build(new RecordingProjectSpec
            {
                InputPath = TestPath.Native(@"C:\rec\input.mp4"),
                Screen = new VideoStreamProbe { StreamIndex = 0, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                FpsNum = 30,
                FpsDen = 1,
                Segments = new[] { new KeepSegment(0, Ms(10_000)), new KeepSegment(Ms(20_000), Ms(10_000)) },
                Ids = RecordingIds.New(0),
            });
            Assert.NotNull(Assert.Single(project.Tracks).GroupId); // Build itself still groups

            var session = new EditorSession(project, null, null);

            Assert.Equal(2, session.Project.Items.Count);
            Assert.Null(Assert.Single(session.Project.Tracks).GroupId);

            var second = session.Project.Items.Single(i => i.TimelineStartTicks == Ms(10_000));
            Assert.Equal(Ms(5_000), session.MoveLinked(second.Id, Ms(5_000)));
            Assert.Equal(Ms(15_000), session.Project.Items.Single(i => i.Id == second.Id).TimelineStartTicks);
            Assert.Equal(0, session.Project.Items.Single(i => i.Id != second.Id).TimelineStartTicks);
        }

        /// <summary>Deleting the recording's other rows leaves the screen row alone in its group,
        /// and the group goes with them — inside the same mutation, so one undo brings back rows
        /// and link alike.</summary>
        [Fact]
        public void Deleting_the_other_rows_ungroups_the_last_one()
        {
            var session = NewSession(out var screen, out var webcam, out var audio, out _);
            var before = session.Project.ToJson();

            Assert.True(session.DeleteTrack(webcam.TrackId));
            Assert.NotNull(Track(session, screen.TrackId).GroupId); // still two rows

            Assert.True(session.DeleteTrack(audio.TrackId));
            Assert.Null(Track(session, screen.TrackId).GroupId);

            session.Undo();
            Assert.Equal(2, session.Project.Tracks.Count);
            Assert.NotNull(Track(session, screen.TrackId).GroupId);
            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        /// <summary>Unlinking every other row is the same thing said differently: the row that
        /// was never unlinked is unlinked too, because there is nothing left to be linked to.</summary>
        [Fact]
        public void Ungrouping_every_other_row_ungroups_the_last_one()
        {
            var session = NewSession(out _, out var webcam, out var audio, out _);

            session.UngroupTracks(new[] { webcam.TrackId });
            session.UngroupTracks(new[] { audio.TrackId });

            Assert.All(session.Project.Tracks, t => Assert.Null(t.GroupId));
        }

        [Fact]
        public void RelinkSource_updates_path_and_streams_and_notes_mismatches()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var sourceId = ((MediaContent)screen.Content).SourceId;

            // a clean reprobe: path and stream data replaced, nothing to note.
            var notes = session.RelinkSource(sourceId, TestPath.Native(@"C:\rec\moved.mp4"), RecordingReprobe(TestPath.Native(@"C:\rec\moved.mp4")));
            Assert.Empty(notes);
            Assert.Equal(TestPath.Native(@"C:\rec\moved.mp4"), session.Project.Sources[0].Path);
            Assert.Empty(session.Project.Validate());

            // a file missing the webcam stream: the old description is kept so the webcam items
            // stay resolvable, and the mismatch is reported.
            notes = session.RelinkSource(sourceId, TestPath.Native(@"C:\rec\other.mp4"),
                RecordingReprobe(TestPath.Native(@"C:\rec\other.mp4"), includeWebcamStream: false));
            var note = Assert.Single(notes);
            Assert.Contains("index 1", note);
            Assert.Contains(session.Project.Sources[0].Streams, s => s.Index == 1 && s.Kind == StreamKind.Video);
            Assert.Empty(session.Project.Validate());
        }

        /// <summary>The missing-media dialog's Skip/relink pair: skipping a source disables every
        /// row that plays it in ONE undo entry, and the restore is symmetric — so a Locate in a
        /// later session can bring back exactly what the skip hid.</summary>
        [Fact]
        public void SetSourceRowsEnabled_toggles_every_row_of_the_source_in_one_undo_entry()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var sourceId = ((MediaContent)screen.Content).SourceId;

            session.SetSourceRowsEnabled(sourceId, false);

            Assert.All(session.Project.Tracks.Where(t => t.Kind == TrackKind.Video), t => Assert.True(t.Hidden));
            Assert.All(session.Project.Tracks.Where(t => t.Kind == TrackKind.Audio), t => Assert.True(t.Muted));

            // one undo covers all three rows
            session.Undo();
            Assert.All(session.Project.Tracks, t => Assert.False(t.Hidden || t.Muted));
            session.Redo();

            // the relink flow's other half: pointing the source at its new home and re-enabling
            // leaves no row hidden or muted behind the "found" file.
            session.RelinkSource(sourceId, TestPath.Native(@"C:\rec\moved.mp4"), RecordingReprobe(TestPath.Native(@"C:\rec\moved.mp4")));
            session.SetSourceRowsEnabled(sourceId, true);
            Assert.All(session.Project.Tracks, t => Assert.False(t.Hidden || t.Muted));

            // already-enabled rows make the restore a free no-op (no undo entry pushed)
            var json = session.Project.ToJson();
            session.SetSourceRowsEnabled(sourceId, true);
            Assert.Equal(json, session.Project.ToJson());
        }

        [Fact]
        public void GetMissingSources_reports_files_that_do_not_exist()
        {
            var session = NewSession(out var screen, out _, out _, out _);

            var missing = Assert.Single(session.GetMissingSources());
            Assert.Equal(((MediaContent)screen.Content).SourceId, missing.Id);
        }

        /// <summary>A source no item plays is never opened — by the player or by the render — so
        /// its file being gone is nothing to prompt (or refuse) over.</summary>
        [Fact]
        public void GetMissingSources_ignores_a_source_nothing_plays()
        {
            var session = NewSession(out _, out _, out _, out _);
            var created = session.ImportMedia(TestPath.Native(@"C:\media\clip.mp4"), ClipProbe(), 0);

            Assert.Contains(session.GetMissingSources(), s => s.Path == TestPath.Native(@"C:\media\clip.mp4"));
            var clipId = ((MediaContent)created[0].Content).SourceId;
            Assert.True(EditorSession.IsSourceReferenced(session.Project, clipId));

            foreach (var item in created)
                session.DeleteItems(new[] { item.Id });

            // the source outlives its items (nothing prunes it); it just stops mattering.
            Assert.Contains(session.Project.Sources, s => s.Path == TestPath.Native(@"C:\media\clip.mp4"));
            Assert.DoesNotContain(session.GetMissingSources(), s => s.Path == TestPath.Native(@"C:\media\clip.mp4"));
            Assert.False(EditorSession.IsSourceReferenced(session.Project, clipId));
        }

        [Fact]
        public void RemoveSource_drops_its_items_and_the_rows_they_emptied_in_one_undo_entry()
        {
            var session = NewSession(out var screen, out _, out _, out _);
            var created = session.ImportMedia(TestPath.Native(@"C:\media\clip.mp4"), ClipProbe(), Ms(2_000));
            var clipId = ((MediaContent)created[0].Content).SourceId;
            var before = session.Project.ToJson();

            Assert.True(session.RemoveSource(clipId));

            // the import's source, both its items and both the rows it brought with it are gone…
            Assert.DoesNotContain(session.Project.Sources, s => s.Id == clipId);
            Assert.Equal(3, session.Project.Items.Count);
            Assert.Equal(3, session.Project.Tracks.Count);
            Assert.DoesNotContain(session.Project.Tracks, t => created.Any(i => i.TrackId == t.Id));
            // …and the recording it was imported over is untouched.
            Assert.Contains(session.Project.Items, i => i.Id == screen.Id);
            Assert.Empty(session.Project.Validate());

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        /// <summary>The missing-media dialog's Remove on the recording itself: rows the session
        /// opened with are normally never pruned, but there is nothing left for them to hold.</summary>
        [Fact]
        public void RemoveSource_removes_the_recordings_own_rows_too()
        {
            var session = NewSession(out var screen, out _, out _, out _);

            Assert.True(session.RemoveSource(((MediaContent)screen.Content).SourceId));

            Assert.Empty(session.Project.Sources);
            Assert.Empty(session.Project.Items);
            Assert.Empty(session.Project.Tracks);
            Assert.Empty(session.Project.Validate());
            Assert.Equal(0, session.DurationTicks);
        }

        [Fact]
        public void RemoveSource_leaves_rows_that_still_hold_something()
        {
            var session = NewSession(out _, out var webcam, out _, out _);
            session.UngroupTracks(new[] { webcam.TrackId });

            // a text card shares the webcam row; removing the recording must not take it with it.
            var text = session.AddText(Ms(12_000), Ms(5_000));
            Assert.Equal(webcam.TrackId, text.TrackId);

            Assert.True(session.RemoveSource(session.Project.Sources[0].Id));

            var item = Assert.Single(session.Project.Items);
            Assert.Equal(text.Id, item.Id);
            Assert.Single(session.Project.Tracks);
            Assert.Equal(webcam.TrackId, session.Project.Tracks[0].Id);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void RemoveSource_of_an_unknown_source_changes_nothing()
        {
            var session = NewSession(out _, out _, out _, out var persist);
            var before = session.Project.ToJson();

            Assert.False(session.RemoveSource(Guid.NewGuid()));

            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
            Assert.Equal(0, persist.Writes);
        }

        [Fact]
        public void GetTracksForSource_returns_the_rows_the_skip_flow_hides()
        {
            var session = NewSession(out var screen, out var webcam, out var audio, out _);

            var tracks = session.GetTracksForSource(((MediaContent)screen.Content).SourceId);

            Assert.Equal(3, tracks.Count);
            Assert.Contains(tracks, t => t.Id == screen.TrackId);
            Assert.Contains(tracks, t => t.Id == webcam.TrackId);
            Assert.Contains(tracks, t => t.Id == audio.TrackId);
        }

        // ----------------------------------------------------------------------------- snapshots

        [Fact]
        public void SnapshotForPlayer_is_fully_detached_from_the_session()
        {
            var session = NewSession(out var screen, out _, out _, out _);

            var snapshot = session.SnapshotForPlayer();
            Assert.NotSame(session.Project, snapshot);
            Assert.Equal(session.Project.ToJson(), snapshot.ToJson());

            // mutating the snapshot leaves the session alone…
            snapshot.Items.Clear();
            snapshot.Tracks.Clear();
            Assert.Equal(3, session.Project.Items.Count);
            Assert.Equal(3, session.Project.Tracks.Count);

            // …and mutating the session leaves the snapshot alone.
            session.MoveLinked(screen.Id, Ms(1_000));
            Assert.Empty(snapshot.Items);
        }

        // ------------------------------------------------------------------------- persistence

        [Fact]
        public void FlushSave_writes_the_latest_pending_state_exactly_once()
        {
            var persist = new CountingPersistence();
            var deferred = new List<Action>();
            var session = new EditorSession(RecordingProject(out var screen, out _, out _),
                persist, save => deferred.Add(save));

            session.MoveLinked(screen.Id, Ms(1_000));
            session.MoveLinked(screen.Id, Ms(1_000));
            Assert.Equal(0, persist.Writes);

            session.FlushSave();
            Assert.Equal(1, persist.Writes);
            Assert.Equal(session.Project.ToJson(), persist.LastJson);

            // the debounced callbacks find nothing left to write.
            foreach (var save in deferred)
                save();
            Assert.Equal(1, persist.Writes);
        }

        [Fact]
        public void A_session_without_persistence_is_legal()
        {
            var session = new EditorSession(RecordingProject(out var screen, out _, out _), null, null);

            session.MoveLinked(screen.Id, Ms(1_000));
            session.FlushSave();
            session.Undo();

            Assert.Equal(0, Resolve(session, screen.Id).TimelineStartTicks);
        }

        // --------------------------------------------------------------------------- empty project

        [Fact]
        public void An_empty_project_is_legal_and_edits_on_it_are_undoable()
        {
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            };
            var session = new EditorSession(project, null, null);
            Assert.Equal(0, session.DurationTicks);

            var before = session.Project.ToJson();
            var text = session.AddText(0, Ms(5_000));

            Assert.NotNull(text);
            var track = Assert.Single(session.Project.Tracks);
            Assert.Equal(TrackKind.Video, track.Kind);
            Assert.Equal(0, track.Order);
            Assert.Equal(Ms(5_000), session.DurationTicks);
            Assert.Empty(session.Project.Validate());

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.Empty(session.Project.Items);
            Assert.Empty(session.Project.Tracks);
        }

        // ------------------------------------------------------------------------ output frame rate

        [Fact]
        public void SetOutputFrameRate_stores_the_rational_and_is_undoable()
        {
            var session = NewSession(out _, out _, out _, out var persist);

            Assert.True(session.SetOutputFrameRate(60, 1));

            Assert.Equal(60, session.Project.Output.FpsNum);
            Assert.Equal(1, session.Project.Output.FpsDen);
            Assert.Empty(session.Project.Validate());
            Assert.Equal(1, persist.Writes);

            session.Undo();
            Assert.Equal(30, session.Project.Output.FpsNum);
            Assert.Equal(1, session.Project.Output.FpsDen);
        }

        /// <summary>29.97 must survive as 30000/1001 rather than collapsing onto 30.</summary>
        [Fact]
        public void SetOutputFrameRate_keeps_a_broadcast_rate_exact()
        {
            var session = NewSession(out _, out _, out _, out _);

            Assert.True(session.SetOutputFrameRate(30_000, 1001));

            Assert.Equal(30_000, session.Project.Output.FpsNum);
            Assert.Equal(1001, session.Project.Output.FpsDen);
        }

        [Fact]
        public void SetOutputFrameRate_rejects_a_non_positive_rational()
        {
            var session = NewSession(out _, out _, out _, out var persist);
            var before = session.Project.ToJson();

            Assert.False(session.SetOutputFrameRate(0, 1));
            Assert.False(session.SetOutputFrameRate(30, 0));
            Assert.False(session.SetOutputFrameRate(-30, 1));

            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
            Assert.Equal(0, persist.Writes);
        }

        [Fact]
        public void SetOutputFrameRate_to_the_rate_already_set_changes_nothing()
        {
            var session = NewSession(out _, out _, out _, out var persist);

            Assert.False(session.SetOutputFrameRate(30, 1));

            Assert.False(session.CanUndo);
            Assert.Equal(0, persist.Writes);
        }

        /// <summary>First referenced video stream, not the largest or the fastest: the recording's
        /// own rate is the native one even when a faster webcam shares the file.</summary>
        [Fact]
        public void GetNativeFrameRate_takes_the_first_referenced_video_stream()
        {
            var session = NewSession(out _, out _, out _, out _);
            session.Project.Sources[0].Streams[1].AvgFrameRateNum = 60;

            var native = EditorSession.GetNativeFrameRate(session.Project);

            Assert.Equal((30, 1), native);
        }

        [Fact]
        public void GetNativeFrameRate_ignores_a_source_nothing_plays()
        {
            var session = NewSession(out _, out _, out _, out _);
            session.Project.Sources.Insert(0, new Source
            {
                Id = Guid.NewGuid(),
                Path = TestPath.Native(@"C:\media\unused.mp4"),
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1280, Height = 720, AvgFrameRateNum = 24, AvgFrameRateDen = 1 },
                },
            });

            Assert.Equal((30, 1), EditorSession.GetNativeFrameRate(session.Project));
        }

        [Fact]
        public void GetNativeFrameRate_is_null_when_nothing_declares_a_rate()
        {
            var session = NewSession(out _, out _, out _, out _);
            foreach (var stream in session.Project.Sources[0].Streams)
                stream.AvgFrameRateNum = 0;

            Assert.Null(EditorSession.GetNativeFrameRate(session.Project));
        }

        [Fact]
        public void GetMaxFrameRate_takes_the_fastest_referenced_video_stream()
        {
            var session = NewSession(out _, out _, out _, out _);
            session.Project.Sources[0].Streams[1].AvgFrameRateNum = 60;

            Assert.Equal((60, 1), EditorSession.GetMaxFrameRate(session.Project));
        }

        [Fact]
        public void GetMaxFrameRate_ignores_a_source_nothing_plays()
        {
            var session = NewSession(out _, out _, out _, out _);
            session.Project.Sources.Insert(0, new Source
            {
                Id = Guid.NewGuid(),
                Path = TestPath.Native(@"C:\media\unused.mp4"),
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1280, Height = 720, AvgFrameRateNum = 120, AvgFrameRateDen = 1 },
                },
            });

            Assert.Equal((30, 1), EditorSession.GetMaxFrameRate(session.Project));
        }

        [Fact]
        public void GetMaxFrameRate_is_null_when_nothing_declares_a_rate()
        {
            var session = NewSession(out _, out _, out _, out _);
            foreach (var stream in session.Project.Sources[0].Streams)
                stream.AvgFrameRateNum = 0;

            Assert.Null(EditorSession.GetMaxFrameRate(session.Project));
        }
    }
}
