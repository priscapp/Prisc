using System;
using System.IO;
using System.Linq;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="EditorSession"/>'s input-overlay surface: the cursor/keyboard track factories
    /// (placement above the screen row, span mirroring, joining the group), the CanAdd flags, overlay
    /// rows behaving as ordinary rows (ungroup, reorder, duplicate), pruning and undo/redo — pure model
    /// math over a recording-shaped fixture; the capture sidecars are the only files involved.
    /// </summary>
    public class CursorKeyboardSessionTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>A recording with two keep-segments: screen row (order 0) and webcam row
        /// (order 1) over one source, in one group — the shape the editor produces after one
        /// cut — plus an audio row with nothing on it.</summary>
        private static Project BaseProject(string capturePath, out Source source,
            out Track screenTrack, out Track webcamTrack, out Track audioTrack)
        {
            source = new Source
            {
                Id = Guid.NewGuid(),
                Path = @"C:\rec\in.mp4",
                InputCapturePath = capturePath,
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080 },
                    new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480 },
                },
            };

            var group = Guid.NewGuid();
            screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0, GroupId = group };
            webcamTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Webcam", Order = 1, GroupId = group };
            audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 2 };

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources = { source },
                Tracks = { screenTrack, webcamTrack, audioTrack },
            };

            AddSegment(project, screenTrack, webcamTrack, source.Id, 0, Ms(8_000));
            AddSegment(project, screenTrack, webcamTrack, source.Id, Ms(8_000), Ms(12_000));
            return project;
        }

        private static void AddSegment(Project project, Track screen, Track webcam, Guid sourceId,
            long startTicks, long durationTicks)
        {
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = screen.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = startTicks },
            });
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = webcam.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1, SourceInTicks = startTicks },
            });
        }

        private static EditorSession NewSession(out Track screenTrack, out Track webcamTrack, out Track audioTrack)
        {
            var project = BaseProject(TestPath.ExistingCapture, out _, out screenTrack, out webcamTrack, out audioTrack);
            return new EditorSession(project, null, save => save());
        }

        private static EditorSession NewSession(string capturePath, out Track screenTrack, out Track webcamTrack,
            out Track audioTrack)
        {
            var project = BaseProject(capturePath, out _, out screenTrack, out webcamTrack, out audioTrack);
            return new EditorSession(project, null, save => save());
        }

        private static int OrderOf(EditorSession session, Guid trackId) =>
            session.Project.Tracks.Single(t => t.Id == trackId).Order;

        private static Track TrackOf(EditorSession session, Item item) =>
            session.Project.Tracks.Single(t => t.Id == item.TrackId);

        // ------------------------------------------------------------------------------ factories

        [Fact]
        public void AddCursorTrack_slots_directly_above_the_screen_row()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out var audioTrack);
            ProjectChangeKind? kind = null;
            session.ProjectChanged += (_, e) => kind = e.Kind;

            var item = session.AddCursorTrack();

            Assert.NotNull(item);
            Assert.Equal(ProjectChangeKind.Structural, kind);
            Assert.False(session.CanAddCursorTrack);

            var track = TrackOf(session, item);
            Assert.Equal(TrackKind.Video, track.Kind);
            Assert.Equal("Cursor", track.Name);
            Assert.Equal(0, OrderOf(session, screenTrack.Id));
            Assert.Equal(1, track.Order);
            Assert.Equal(2, OrderOf(session, webcamTrack.Id));
            Assert.Equal(3, OrderOf(session, audioTrack.Id));
            Assert.Empty(session.Project.Validate());

            // the returned item is the live model instance, not a copy.
            Assert.Contains(session.Project.Items, i => ReferenceEquals(i, item));
        }

        [Fact]
        public void Cursor_items_mirror_the_screen_segments_and_their_row_joins_the_group()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            var first = session.AddCursorTrack();

            var screenItems = session.Project.Items
                .Where(i => i.TrackId == screenTrack.Id).OrderBy(i => i.TimelineStartTicks).ToList();
            var cursorItems = session.Project.Items
                .Where(i => i.Content is CursorContent).OrderBy(i => i.TimelineStartTicks).ToList();

            Assert.Equal(2, cursorItems.Count);
            Assert.Same(first, cursorItems[0]);
            Assert.NotNull(TrackOf(session, first).GroupId);
            Assert.Equal(session.Project.Tracks.Single(t => t.Id == screenTrack.Id).GroupId, TrackOf(session, first).GroupId);
            for (var i = 0; i < 2; i++)
            {
                Assert.Equal(screenItems[i].TimelineStartTicks, cursorItems[i].TimelineStartTicks);
                Assert.Equal(screenItems[i].DurationTicks, cursorItems[i].DurationTicks);

                var content = (CursorContent)cursorItems[i].Content;
                Assert.Equal(session.Project.Sources.Single().Id, content.SourceId);
            }
        }

        [Fact]
        public void AddKeyboardTrack_slots_between_screen_and_an_existing_cursor_row()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out _);
            var cursor = session.AddCursorTrack();

            var keys = session.AddKeyboardTrack();

            Assert.NotNull(keys);
            Assert.False(session.CanAddKeyboardTrack);
            var keysTrack = TrackOf(session, keys);
            Assert.Equal("Keys", keysTrack.Name);
            Assert.Equal(TrackKind.Video, keysTrack.Kind);

            Assert.Equal(0, OrderOf(session, screenTrack.Id));
            Assert.Equal(1, keysTrack.Order);
            Assert.Equal(2, TrackOf(session, cursor).Order);
            Assert.Equal(3, OrderOf(session, webcamTrack.Id));
            Assert.Empty(session.Project.Validate());

            // keyboard placement is the item's transform: bottom-center anchor, half width.
            Assert.Equal(0.5, keys.Transform.X);
            Assert.Equal(0.85, keys.Transform.Y);
            Assert.Equal(0.5, keys.Transform.Scale);
        }

        [Fact]
        public void AddCursorTrack_lands_above_an_existing_keyboard_row()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            var keys = session.AddKeyboardTrack();

            var cursor = session.AddCursorTrack();

            Assert.Equal(0, OrderOf(session, screenTrack.Id));
            Assert.Equal(1, TrackOf(session, keys).Order);
            Assert.Equal(2, TrackOf(session, cursor).Order);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_second_add_for_the_one_recording_is_refused_and_changes_nothing()
        {
            var session = NewSession(out _, out _, out _);
            Assert.True(session.CanAddCursorTrack);
            Assert.True(session.CanAddKeyboardTrack);
            session.AddCursorTrack();
            session.AddKeyboardTrack();
            Assert.False(session.CanAddCursorTrack);
            Assert.False(session.CanAddKeyboardTrack);
            var json = session.Project.ToJson();

            Assert.Null(session.AddCursorTrack());
            Assert.Null(session.AddKeyboardTrack());
            Assert.Equal(json, session.Project.ToJson());
        }

        [Fact]
        public void Adds_are_refused_without_an_input_capture_source()
        {
            var session = NewSession(capturePath: null, out _, out _, out _);
            var json = session.Project.ToJson();

            Assert.False(session.CanAddCursorTrack);
            Assert.False(session.CanAddKeyboardTrack);
            Assert.Null(session.AddCursorTrack());
            Assert.Null(session.AddKeyboardTrack());
            Assert.Equal(json, session.Project.ToJson());
        }

        [Fact]
        public void An_ungrouped_screen_row_gets_a_fresh_group_shared_with_the_overlay()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out _);
            session.UngroupTracks(new[] { webcamTrack.Id });
            var liveScreen = session.Project.Tracks.Single(t => t.Id == screenTrack.Id);
            Assert.Null(liveScreen.GroupId);

            var cursor = session.AddCursorTrack();

            liveScreen = session.Project.Tracks.Single(t => t.Id == screenTrack.Id);
            Assert.NotNull(liveScreen.GroupId);
            Assert.Equal(liveScreen.GroupId, TrackOf(session, cursor).GroupId);
            Assert.Null(session.Project.Tracks.Single(t => t.Id == webcamTrack.Id).GroupId);
            Assert.Empty(session.Project.Validate());
        }

        // ------------------------------------------------------------------------------ the group

        [Fact]
        public void Moving_a_cursor_item_moves_its_whole_recording_segment()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            session.AddCursorTrack();
            var cursorItem = session.Project.Items
                .Where(i => i.Content is CursorContent).OrderBy(i => i.TimelineStartTicks).Last();
            var screenItem = session.Project.Items
                .Where(i => i.TrackId == screenTrack.Id).OrderBy(i => i.TimelineStartTicks).Last();

            Assert.Equal(Ms(1_000), session.MoveLinked(cursorItem.Id, Ms(1_000)));

            Assert.Equal(Ms(9_000), session.Project.Items.Single(i => i.Id == screenItem.Id).TimelineStartTicks);
            Assert.Equal(Ms(9_000), session.Project.Items.Single(i => i.Id == cursorItem.Id).TimelineStartTicks);
        }

        [Fact]
        public void SplitAtPlayhead_cuts_the_overlay_with_its_segment()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            session.AddCursorTrack();

            Assert.True(session.SplitAtPlayhead(Ms(4_000)));

            Assert.Equal(3, session.Project.Items.Count(i => i.Content is CursorContent));
            Assert.Contains(session.Project.Items,
                i => i.Content is CursorContent && i.TimelineStartTicks == Ms(4_000));
            Assert.Equal(3, session.Project.Items.Count(i => i.TrackId == screenTrack.Id));
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void RippleDeleting_a_screen_segment_takes_its_overlay_items_along()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            session.AddCursorTrack();
            var firstScreen = session.Project.Items
                .Where(i => i.TrackId == screenTrack.Id).OrderBy(i => i.TimelineStartTicks).First();

            session.RippleDeleteItem(firstScreen.Id);

            var cursorItems = session.Project.Items.Where(i => i.Content is CursorContent).ToList();
            Assert.Single(cursorItems);
            Assert.Equal(0, cursorItems[0].TimelineStartTicks); // the gap closed
            Assert.Empty(session.Project.Validate());
        }

        // ---------------------------------------------------------------------------------- trim

        /// <summary>The overlay items of the first recording segment ([0, 8s)) and of the second
        /// ([8s, 20s)), in timeline order.</summary>
        private static (Item First, Item Second) CursorItems(EditorSession session)
        {
            var items = session.Project.Items
                .Where(i => i.Content is CursorContent).OrderBy(i => i.TimelineStartTicks).ToList();
            return (items[0], items[1]);
        }

        private static Item Live(EditorSession session, Item item) =>
            session.Project.Items.Single(i => i.Id == item.Id);

        /// <summary>What each clip spans, by id.</summary>
        private static System.Collections.Generic.Dictionary<Guid, (long Start, long Duration)> Spans(EditorSession session) =>
            session.Project.Items.ToDictionary(i => i.Id, i => (i.TimelineStartTicks, i.DurationTicks));

        /// <summary>A trim is the grabbed clip alone on every kind of row: screen, webcam, cursor,
        /// keys, text and zoom alike, grouped or not.</summary>
        [Fact]
        public void A_trim_changes_only_the_grabbed_clip_on_every_kind_of_row()
        {
            var session = NewSession(out _, out _, out _);
            session.AddCursorTrack();
            session.AddKeyboardTrack();
            session.AddText(Ms(1_000), Ms(5_000));
            session.AddZoomEffect(Ms(1_000), Ms(5_000));

            foreach (var id in session.Project.Items.Select(i => i.Id).ToList())
            {
                var before = Spans(session);

                Assert.Equal(Ms(500), session.TrimItemStart(id, Ms(500)));
                Assert.Equal(-Ms(500), session.TrimItemEnd(id, -Ms(500)));

                var after = Spans(session);
                Assert.Equal(before.Count, after.Count);
                Assert.All(after.Where(a => a.Key != id), a => Assert.Equal(before[a.Key], a.Value));
                Assert.Equal((before[id].Start + Ms(500), before[id].Duration - Ms(1_000)), after[id]);
                Assert.Empty(session.Project.Validate());
            }
        }

        [Fact]
        public void Trimming_the_screen_leaves_the_overlays_where_they_are()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            session.AddCursorTrack();
            var keys = session.AddKeyboardTrack();
            var firstScreen = session.Project.Items
                .Where(i => i.TrackId == screenTrack.Id).OrderBy(i => i.TimelineStartTicks).First();
            var (cursor, _) = CursorItems(session);

            Assert.Equal(-Ms(3_000), session.TrimItemEnd(firstScreen.Id, -Ms(3_000)));

            // the cursor hangs past the screen now: it draws nothing there, it is not touched
            Assert.Equal(Ms(5_000), Live(session, firstScreen).DurationTicks);
            Assert.Equal(Ms(8_000), Live(session, cursor).DurationTicks);
            Assert.Equal(Ms(8_000), Live(session, keys).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        /// <summary>The old rule refused to let an overlay reach past its screen segment.</summary>
        [Fact]
        public void Trimming_an_overlay_leaves_the_screen_and_may_run_past_it()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            session.AddCursorTrack();
            var (_, cursor) = CursorItems(session); // the last clip on the row: room to grow into
            var lastScreen = session.Project.Items
                .Where(i => i.TrackId == screenTrack.Id).OrderBy(i => i.TimelineStartTicks).Last();

            Assert.Equal(Ms(3_000), session.TrimItemEnd(cursor.Id, Ms(3_000)));

            Assert.Equal(Ms(15_000), Live(session, cursor).DurationTicks);
            Assert.Equal(Ms(12_000), Live(session, lastScreen).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        /// <summary>Nothing is stranded by a trim any more, so no row can go with it, and the
        /// timeline never has to rebuild for one.</summary>
        [Fact]
        public void A_trim_is_a_mapping_change_and_one_undo_restores_it()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            session.AddCursorTrack();
            var firstScreen = session.Project.Items
                .Where(i => i.TrackId == screenTrack.Id).OrderBy(i => i.TimelineStartTicks).First();
            var before = session.Project.ToJson();
            ProjectChangeKind? kind = null;
            session.ProjectChanged += (_, e) => kind = e.Kind;

            Assert.Equal(Ms(7_000), session.TrimItemStart(firstScreen.Id, Ms(7_000)));

            Assert.Equal(ProjectChangeKind.Mapping, kind);

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        // -------------------------------------------------------------------- ordinary rows

        [Fact]
        public void Overlay_rows_ungroup_like_any_row_and_then_stay_put_on_a_drag()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            var cursor = session.AddCursorTrack();
            var keys = session.AddKeyboardTrack();
            var screenClip = session.Project.Items.First(i => i.TrackId == screenTrack.Id);
            Assert.Contains(cursor.Id, TimelineOps.TimeLinked(session.Project, new[] { screenClip.Id }));

            session.UngroupTracks(new[] { cursor.TrackId });

            Assert.Null(TrackOf(session, cursor).GroupId);
            Assert.NotNull(TrackOf(session, keys).GroupId);
            Assert.DoesNotContain(cursor.Id, TimelineOps.TimeLinked(session.Project, new[] { screenClip.Id }));
            Assert.Contains(keys.Id, TimelineOps.TimeLinked(session.Project, new[] { screenClip.Id }));
            Assert.Empty(session.Project.Validate());

            session.Undo();
            Assert.NotNull(TrackOf(session, cursor).GroupId);
        }

        /// <summary>Cursor and keys rows step through the stack like any row, past their own screen
        /// row too: what lands behind it composes behind it, as any stacking does.</summary>
        [Fact]
        public void Overlay_rows_reorder_like_any_row()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out _);
            var cursor = session.AddCursorTrack();
            var cursorRow = cursor.TrackId;

            // screen=0, cursor=1, webcam=2
            Assert.True(session.CanMoveTrackLayer(cursorRow, towardsFront: true));
            Assert.True(session.CanMoveTrackLayer(cursorRow, towardsFront: false));

            Assert.True(session.MoveTrackLayer(cursorRow, towardsFront: true));
            Assert.Equal(1, OrderOf(session, webcamTrack.Id));
            Assert.Equal(2, OrderOf(session, cursorRow));
            Assert.False(session.CanMoveTrackLayer(cursorRow, towardsFront: true));

            Assert.True(session.MoveTrackToIndex(cursorRow, 0));
            Assert.Equal(0, OrderOf(session, cursorRow));
            Assert.Equal(1, OrderOf(session, screenTrack.Id));
            Assert.Equal(2, OrderOf(session, webcamTrack.Id));
            Assert.False(session.CanMoveTrackLayer(cursorRow, towardsFront: false));
            Assert.Empty(session.Project.Validate());
        }

        /// <summary>The copy sits directly above the original, plays the same moments and is its own
        /// material: no group, so a drag of the original does not bring it.</summary>
        [Fact]
        public void Overlay_rows_duplicate_like_any_row()
        {
            var session = NewSession(out var screenTrack, out _, out _);
            session.SplitAtPlayhead(Ms(4_000));
            var cursor = session.AddCursorTrack();
            session.SetItemSpeed(cursor.Id, 2.0);
            var row = TrackOf(session, cursor);
            var before = session.Project.ToJson();
            var originals = session.Project.Items.Where(i => i.TrackId == row.Id).ToList();

            Assert.True(session.DuplicateTrack(row.Id));

            var copyRow = session.Project.Tracks.Single(t => t.Name == "Cursor copy");
            Assert.Equal(TrackKind.Video, copyRow.Kind);
            Assert.Equal(OrderOf(session, row.Id) + 1, copyRow.Order);
            Assert.Null(copyRow.GroupId);

            var copies = session.Project.Items.Where(i => i.TrackId == copyRow.Id).OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(originals.Count, copies.Count);
            for (var i = 0; i < copies.Count; i++)
            {
                var (original, copy) = (originals.OrderBy(o => o.TimelineStartTicks).ElementAt(i), copies[i]);
                Assert.NotEqual(original.Id, copy.Id);
                Assert.Equal((original.TimelineStartTicks, original.DurationTicks), (copy.TimelineStartTicks, copy.DurationTicks));
                var (a, b) = ((CursorContent)original.Content, (CursorContent)copy.Content);
                Assert.Equal((a.SourceId, a.SourceInTicks, a.Speed, a.Freeze), (b.SourceId, b.SourceInTicks, b.Speed, b.Freeze));
            }

            var screenClip = session.Project.Items.First(i => i.TrackId == screenTrack.Id);
            Assert.DoesNotContain(TimelineOps.TimeLinked(session.Project, new[] { screenClip.Id }), id => copies.Any(c => c.Id == id));
            Assert.Empty(session.Project.Validate());

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        /// <summary>A screen row is a row: nothing comes along when it moves, and a move can leave
        /// its overlays beneath it.</summary>
        [Fact]
        public void The_screen_row_moves_alone()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out _);
            var cursor = session.AddCursorTrack();

            // screen=0, cursor=1, webcam=2
            Assert.True(session.MoveTrackLayer(screenTrack.Id, towardsFront: true));
            Assert.Equal(0, TrackOf(session, cursor).Order);
            Assert.Equal(1, OrderOf(session, screenTrack.Id));
            Assert.Equal(2, OrderOf(session, webcamTrack.Id));

            // index 2 of three: a real place now, with no unit taken out of the count
            Assert.True(session.MoveTrackToIndex(screenTrack.Id, 2));
            Assert.Equal(0, TrackOf(session, cursor).Order);
            Assert.Equal(1, OrderOf(session, webcamTrack.Id));
            Assert.Equal(2, OrderOf(session, screenTrack.Id));

            // and out of range or staying put is still no move
            var json = session.Project.ToJson();
            Assert.False(session.MoveTrackToIndex(screenTrack.Id, 2));
            Assert.False(session.MoveTrackToIndex(screenTrack.Id, 3));
            Assert.False(session.MoveTrackToIndex(screenTrack.Id, -1));
            Assert.Equal(json, session.Project.ToJson());
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void AddText_never_lands_on_an_overlay_row()
        {
            var session = NewSession(out _, out var webcamTrack, out _);
            session.SetTrackHidden(webcamTrack.Id, true); // the fresh-edit shape: webcam hidden
            session.AddCursorTrack();
            var firstCursor = session.Project.Items
                .Where(i => i.Content is CursorContent).OrderBy(i => i.TimelineStartTicks).First();
            session.DeleteItems(new[] { firstCursor.Id }); // frees [0, 8s) on the cursor row

            var text = session.AddText(Ms(1_000), Ms(2_000));

            Assert.NotNull(text);
            var row = TrackOf(session, text);
            Assert.DoesNotContain(session.Project.Items,
                i => i.TrackId == row.Id && i.Content is CursorContent);
            Assert.Equal("Overlay", row.Name);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Other_video_rows_still_reorder_around_an_overlay_row()
        {
            var session = NewSession(out var screenTrack, out var webcamTrack, out _);
            session.AddCursorTrack();

            // webcam (frontmost of the block) drops one layer, stepping past the cursor row.
            Assert.True(session.MoveTrackLayer(webcamTrack.Id, towardsFront: false));

            Assert.Equal(0, OrderOf(session, screenTrack.Id));
            Assert.Equal(1, OrderOf(session, webcamTrack.Id));
            Assert.Empty(session.Project.Validate());
        }

        // -------------------------------------------------------------------------- prune / flags

        /// <summary>A recording that kept any of its cursor clips is not offered a second row: a
        /// clip deleted from it means "no cursor here". The last one going is what offers it
        /// again.</summary>
        [Fact]
        public void Deleting_the_last_overlay_item_prunes_the_row_and_offers_the_add_again()
        {
            var session = NewSession(out _, out _, out _);
            session.AddCursorTrack();
            var cursorItems = session.Project.Items.Where(i => i.Content is CursorContent).ToList();

            session.DeleteItems(new[] { cursorItems[0].Id });
            Assert.False(session.CanAddCursorTrack); // one item left, row survives
            Assert.Contains(session.Project.Tracks, t => t.Name == "Cursor");

            session.DeleteItems(new[] { cursorItems[1].Id });
            Assert.True(session.CanAddCursorTrack); // row pruned — the sidebar re-enables
            Assert.DoesNotContain(session.Project.Tracks, t => t.Name == "Cursor");
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_pruned_row_can_be_added_again()
        {
            var session = NewSession(out _, out _, out _);
            var first = session.AddCursorTrack();
            session.DeleteTrack(first.TrackId);
            Assert.True(session.CanAddCursorTrack);

            Assert.NotNull(session.AddCursorTrack());
            Assert.False(session.CanAddCursorTrack);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void CanAdd_flags_classify_by_content_not_name()
        {
            var session = NewSession(out _, out _, out _);

            session.AddCursorTrack();
            Assert.False(session.CanAddCursorTrack);
            Assert.True(session.CanAddKeyboardTrack);

            // renaming the row changes nothing — classification is by item content.
            var track = session.Project.Tracks.Single(t => t.Name == "Cursor");
            session.RenameTrack(track.Id, "My pointer");
            Assert.False(session.CanAddCursorTrack);
        }

        // -------------------------------------------------------------------------- undo / redo

        [Fact]
        public void Undo_and_redo_replay_an_overlay_add()
        {
            var session = NewSession(out _, out _, out _);
            var before = session.Project.ToJson();
            session.AddCursorTrack();
            var after = session.Project.ToJson();

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.True(session.CanAddCursorTrack);

            session.Redo();
            Assert.Equal(after, session.Project.ToJson());
            Assert.False(session.CanAddCursorTrack);
        }

        // ---------------------------------------------------------------------- HasInputCapture

        [Fact]
        public void HasInputCapture_requires_the_sidecar_file_to_exist()
        {
            var missing = Path.Combine(Path.GetTempPath(), $"input-capture-{Guid.NewGuid():N}.jsonl");
            var session = NewSession(missing, out _, out _, out _);

            Assert.False(session.HasInputCapture);
        }

        [Fact]
        public void HasInputCapture_is_true_for_an_existing_sidecar_and_caches_the_probe()
        {
            var path = Path.Combine(Path.GetTempPath(), $"input-capture-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(path, "");
            try
            {
                var session = NewSession(path, out _, out _, out _);
                Assert.True(session.HasInputCapture);

                // the probe is cached: the file vanishing mid-session does not flip the flag
                // (and CanExecute polling never hits the disk again).
                File.Delete(path);
                Assert.True(session.HasInputCapture);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Fact]
        public void HasInputCapture_is_false_without_a_capture_path()
        {
            var session = NewSession(capturePath: null, out _, out _, out _);
            Assert.False(session.HasInputCapture);
        }

        // --------------------------------------------------------------------------- source ops

        [Fact]
        public void Removing_the_source_takes_the_overlay_rows_with_it()
        {
            var session = NewSession(out _, out _, out _);
            session.AddCursorTrack();
            session.AddKeyboardTrack();
            var sourceId = session.Project.Sources.Single().Id;

            Assert.True(session.RemoveSource(sourceId));

            Assert.Empty(session.Project.Items);
            Assert.DoesNotContain(session.Project.Tracks, t => t.Name is "Cursor" or "Keys");
            Assert.False(session.CanAddCursorTrack);
            Assert.Empty(session.Project.Validate());
        }
    }
}
