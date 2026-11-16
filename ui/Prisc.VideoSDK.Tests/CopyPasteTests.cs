using System;
using System.Collections.Generic;
using System.Linq;
using Prisc.UI.VideoEditor.Timeline;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Playback;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Copy and paste of any selection: what a copy holds, where each block of rows lands, how a gap
    /// fits a paste, speed routing, clips on the output clock, the overlays a pasted screen clip
    /// hands its time to, what the user is told, and Duplicate Track being the same path.
    /// </summary>
    [Collection("EditorSession clipboard")] // the copy/paste tests share its one static clipboard
    public class CopyPasteTests
    {
        private static long Sec(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

        private static Source Recording() => new Source
        {
            Id = Guid.NewGuid(),
            Path = $@"C:\rec\{Guid.NewGuid():N}.mp4",
            InputCapturePath = TestPath.ExistingCapture,
            Streams =
            {
                new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, DurationTicks = Sec(500) },
                new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480, DurationTicks = Sec(500) },
                new SourceStream { Index = 2, Kind = StreamKind.Audio, DurationTicks = Sec(500) },
            },
        };

        private static Project NewProject(params Source[] sources)
        {
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            };
            project.Sources.AddRange(sources);
            return project;
        }

        private static Track Row(Project project, string name, Guid? group = null, TrackKind kind = TrackKind.Video)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = kind, Name = name, Order = project.Tracks.Count, GroupId = group };
            project.Tracks.Add(track);
            return track;
        }

        private static Item Clip(Project project, Track row, double start, double length, ItemContent content)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = row.Id,
                TimelineStartTicks = Sec(start),
                DurationTicks = Sec(length),
                Content = content,
            };
            project.Items.Add(item);
            return item;
        }

        private static MediaContent Screen(Source source, double inSec = 0) =>
            new MediaContent { SourceId = source.Id, StreamIndex = 0, SourceInTicks = Sec(inSec) };

        private static MediaContent Sound(Source source, bool exempt = false) =>
            new MediaContent { SourceId = source.Id, StreamIndex = 2, SpeedWarpExempt = exempt };

        private static CursorContent Cursor(Source source, double inSec = 0) =>
            new CursorContent { SourceId = source.Id, SourceInTicks = Sec(inSec) };

        private static TextContent Card() => new TextContent { Text = "x" };

        private static EditorSession SessionOf(Project project)
        {
            project.Normalize();
            Assert.Empty(project.Validate());
            return new EditorSession(project, null, null);
        }

        private static Item Find(EditorSession session, Guid id) => session.Project.Items.Single(i => i.Id == id);

        private static Track RowNamed(EditorSession session, string name) => session.Project.Tracks.Single(t => t.Name == name);

        private static IEnumerable<Item> On(EditorSession session, Track row) => session.Project.Items.Where(i => i.TrackId == row.Id);

        private static long SourceTimeOn(EditorSession session, Track row, double at)
        {
            var clip = On(session, row).Single(i => i.Covers(Sec(at)));
            return FrameComposer.SourceTimeTicks((SourceClockContent)clip.Content, clip, Sec(at));
        }

        private static (string Name, int Order)[] RowOrder(EditorSession session) =>
            session.Project.Tracks.OrderBy(t => t.Order).Select(t => (t.Name, t.Order)).ToArray();

        /// <summary>Screen, Keys and Cursor grouped, then a text row and an audio row of their own,
        /// each with a clip over [0, 10).</summary>
        private static EditorSession GroupedRecording(out Source source, out Item screen, out Item keys, out Item cursor,
            out Item card, out Item sound)
        {
            source = Recording();
            var project = NewProject(source);
            var group = Guid.NewGuid();
            screen = Clip(project, Row(project, "Screen", group), 0, 10, Screen(source));
            keys = Clip(project, Row(project, "Keys", group), 0, 10, new KeyboardContent { SourceId = source.Id });
            cursor = Clip(project, Row(project, "Cursor", group), 0, 10, Cursor(source));
            card = Clip(project, Row(project, "Card"), 0, 10, Card());
            sound = Clip(project, Row(project, "Audio", kind: TrackKind.Audio), 0, 10, Sound(source));
            return SessionOf(project);
        }

        // ------------------------------------------------------------------------------------ Lift

        [Fact]
        public void Lift_holds_exactly_the_clips_with_their_rows_and_sources_and_shares_nothing()
        {
            var used = Recording();
            var unused = Recording();
            var project = NewProject(used, unused);
            var group = Guid.NewGuid();
            var taken = Clip(project, Row(project, "Taken", group), 2, 3, Screen(used, 7));
            var left = Clip(project, Row(project, "Left", group), 2, 3, Screen(used));
            Clip(project, Row(project, "Card"), 0, 1, Card());

            var clip = TimelineOps.Lift(project, new[] { taken.Id, Guid.NewGuid() });

            var item = Assert.Single(clip.Items);
            Assert.Equal((taken.Id, Sec(2), Sec(3)), (item.Id, item.TimelineStartTicks, item.DurationTicks));
            var row = Assert.Single(clip.Tracks);
            Assert.Equal(("Taken", 0, group), (row.Name, row.Order, row.GroupId.Value));
            var source = Assert.Single(clip.Sources);
            Assert.Equal((used.Id, TestPath.ExistingCapture), (source.Id, source.InputCapturePath));

            var before = project.ToJson();
            ((MediaContent)item.Content).SourceInTicks = Sec(99);
            row.Name = "Changed";
            source.Path = "changed";
            Assert.Equal(before, project.ToJson());
            Assert.DoesNotContain(clip.Items, i => i.Id == left.Id);
        }

        [Fact]
        public void Lift_of_nothing_is_empty_and_a_copy_of_nothing_leaves_the_clipboard_alone()
        {
            var session = GroupedRecording(out _, out var screen, out _, out _, out _, out _);
            Assert.True(session.CopyItems(new[] { screen.Id }));

            Assert.False(session.CopyItems(new[] { Guid.NewGuid() }));

            Assert.Equal((1, 1), session.ClipboardSize);
            Assert.Empty(TimelineOps.Lift(session.Project, Array.Empty<Guid>()).Items);
        }

        [Fact]
        public void Every_kind_of_clip_can_be_copied()
        {
            var source = Recording();
            var project = NewProject(source);
            var ids = new[]
            {
                Clip(project, Row(project, "Screen"), 0, 1, Screen(source)),
                Clip(project, Row(project, "Sound", kind: TrackKind.Audio), 0, 1, Sound(source)),
                Clip(project, Row(project, "Cursor"), 0, 1, Cursor(source)),
                Clip(project, Row(project, "Keys"), 0, 1, new KeyboardContent { SourceId = source.Id }),
                Clip(project, Row(project, "Card"), 0, 1, Card()),
                Clip(project, Row(project, "Shape"), 0, 1, new ShapeContent()),
                Clip(project, Row(project, "Obscure"), 0, 1, new ObscureContent()),
                Clip(project, Row(project, "Wallpaper"), 0, 1, new BackgroundContent()),
                Clip(project, Row(project, "Zoom", kind: TrackKind.Effect), 0, 1, new ZoomContent { Zoom = 2 }),
                Clip(project, Row(project, "Speed", kind: TrackKind.Effect), 20, 1, new SpeedContent()),
            }.Select(i => i.Id).ToList();
            var session = SessionOf(project);

            Assert.True(session.CopyItems(ids));
            Assert.Equal((10, 10), session.ClipboardSize);

            Assert.Equal(10, session.PasteAt(Sec(50)).Count);
            Assert.Empty(session.Project.Validate());
            Assert.Equal(20, session.Project.Items.Count);
        }

        // -------------------------------------------------------------------------------- wallpapers

        [Fact]
        public void A_wallpaper_does_not_paste_into_a_gap_on_a_picture_row_so_the_row_stays_a_picture_row()
        {
            var source = Recording();
            var project = NewProject(source);
            var wallpaper = Clip(project, Row(project, "Backdrop"), 0, 3, new BackgroundContent());
            var screenRow = Row(project, "Screen");
            Clip(project, screenRow, 5, 10, Screen(source));
            Clip(project, Row(project, "Card"), 0, 10, Card());
            var session = SessionOf(project);
            Assert.True(session.CopyItems(new[] { wallpaper.Id }));
            var gap = session.FindGap(screenRow.Id, Sec(1)).Value;

            Assert.False(session.CanPasteIntoGap(gap));
            Assert.Empty(session.PasteIntoGap(gap));

            // what the row would have become: the next wallpaper would land above the picture
            var added = session.AddBackground(0, Sec(3));
            Assert.True(session.Project.TrackOf(added).Order < RowNamed(session, "Screen").Order);
        }

        [Fact]
        public void A_wallpaper_pastes_into_a_gap_on_a_wallpaper_row()
        {
            var project = NewProject();
            var row = Row(project, "Backdrop");
            var wallpaper = Clip(project, row, 0, 3, new BackgroundContent());
            Clip(project, row, 5, 3, new BackgroundContent());
            var session = SessionOf(project);
            Assert.True(session.CopyItems(new[] { wallpaper.Id }));
            var gap = session.FindGap(row.Id, Sec(4)).Value;

            Assert.True(session.CanPasteIntoGap(gap));
            Assert.Equal(row.Id, Find(session, Assert.Single(session.PasteIntoGap(gap))).TrackId);
        }

        [Fact]
        public void CanHost_takes_a_wallpaper_only_on_an_empty_row_or_a_row_of_wallpapers()
        {
            var source = Recording();
            var project = NewProject(source);
            var empty = Row(project, "Empty");
            var wallpapers = Row(project, "Wallpapers");
            Clip(project, wallpapers, 0, 1, new BackgroundContent());
            var screen = Row(project, "Screen");
            Clip(project, screen, 0, 1, Screen(source));
            var cards = Row(project, "Cards");
            Clip(project, cards, 0, 1, Card());
            var wallpaper = new BackgroundContent();

            Assert.True(TimelineOps.CanHost(empty, wallpaper, project));
            Assert.True(TimelineOps.CanHost(wallpapers, wallpaper, project));
            Assert.False(TimelineOps.CanHost(screen, wallpaper, project));
            Assert.False(TimelineOps.CanHost(cards, wallpaper, project));
        }

        [Fact]
        public void A_wallpaper_with_no_row_to_return_to_goes_behind_the_picture()
        {
            var theirs = NewProject();
            var wallpaper = Clip(theirs, Row(theirs, "Theirs"), 0, 5, new BackgroundContent());
            Assert.True(SessionOf(theirs).CopyItems(new[] { wallpaper.Id }));
            var source = Recording();
            var project = NewProject(source);
            Clip(project, Row(project, "Screen"), 0, 10, Screen(source));
            var session = SessionOf(project);

            Assert.Single(session.PasteAt(0));

            Assert.Equal(new[] { "Theirs copy", "Screen" }, RowOrder(session).Select(r => r.Name));
        }

        [Fact]
        public void A_wallpaper_with_no_row_to_return_to_goes_in_front_of_the_wallpapers_there()
        {
            var theirs = NewProject();
            var wallpaper = Clip(theirs, Row(theirs, "Theirs"), 0, 5, new BackgroundContent());
            Assert.True(SessionOf(theirs).CopyItems(new[] { wallpaper.Id }));
            var source = Recording();
            var project = NewProject(source);
            Clip(project, Row(project, "Backdrop"), 0, 10, new BackgroundContent());
            Clip(project, Row(project, "Screen"), 0, 10, Screen(source));
            var session = SessionOf(project);

            Assert.Single(session.PasteAt(0));

            Assert.Equal(new[] { "Backdrop", "Theirs copy", "Screen" }, RowOrder(session).Select(r => r.Name));
        }

        [Fact]
        public void Wallpapers_from_another_project_stay_in_clip_order_behind_the_picture_and_their_block_stays_grouped()
        {
            var elsewhere = Recording();
            var theirs = NewProject(elsewhere);
            var group = Guid.NewGuid();
            var ids = new[]
            {
                Clip(theirs, Row(theirs, "Far", group), 0, 5, new BackgroundContent()),
                Clip(theirs, Row(theirs, "Near", group), 0, 5, new BackgroundContent()),
                Clip(theirs, Row(theirs, "Their Screen", group), 0, 5, Screen(elsewhere)),
            }.Select(i => i.Id).ToList();
            Assert.True(SessionOf(theirs).CopyItems(ids));
            var source = Recording();
            var project = NewProject(source);
            Clip(project, Row(project, "Screen"), 0, 10, Screen(source));
            var session = SessionOf(project);

            Assert.Equal(3, session.PasteAt(0).Count);

            Assert.Equal(new[] { "Far copy", "Near copy", "Screen", "Their Screen copy" }, RowOrder(session).Select(r => r.Name));
            var fresh = RowNamed(session, "Far copy").GroupId;
            Assert.NotNull(fresh);
            Assert.NotEqual(group, fresh);
            Assert.Equal(fresh, RowNamed(session, "Near copy").GroupId);
            Assert.Equal(fresh, RowNamed(session, "Their Screen copy").GroupId);
            Assert.Null(RowNamed(session, "Screen").GroupId);
        }

        // ---------------------------------------------------------------------------- where it goes

        [Fact]
        public void A_block_returns_to_its_own_rows_when_they_are_free_keeping_relative_time()
        {
            var source = Recording();
            var project = NewProject(source);
            var group = Guid.NewGuid();
            var screenRow = Row(project, "Screen", group);
            var cursorRow = Row(project, "Cursor", group);
            var screen = Clip(project, screenRow, 1, 2, Screen(source, 5));
            var cursor = Clip(project, cursorRow, 2, 3, Cursor(source, 6));
            var session = SessionOf(project);
            var rows = session.Project.Tracks.Count;

            Assert.True(session.CopyItems(new[] { screen.Id, cursor.Id }));
            var pasted = session.PasteAt(Sec(20));

            Assert.Equal(2, pasted.Count);
            Assert.Equal(rows, session.Project.Tracks.Count);
            var screenCopy = On(session, screenRow).Single(i => i.Id != screen.Id);
            var cursorCopy = On(session, cursorRow).Single(i => i.Id != cursor.Id);
            Assert.Equal((Sec(20), Sec(2), Sec(5)), (screenCopy.TimelineStartTicks, screenCopy.DurationTicks, ((MediaContent)screenCopy.Content).SourceInTicks));
            Assert.Equal((Sec(21), Sec(3), Sec(6)), (cursorCopy.TimelineStartTicks, cursorCopy.DurationTicks, ((CursorContent)cursorCopy.Content).SourceInTicks));
            Assert.Equal(group, session.Project.Tracks.Single(t => t.Id == screenRow.Id).GroupId);
        }

        [Fact]
        public void A_block_that_finds_its_rows_occupied_goes_to_one_stack_directly_above_them()
        {
            var session = GroupedRecording(out _, out var screen, out var keys, out var cursor, out _, out _);

            Assert.True(session.CopyItems(new[] { screen.Id, keys.Id, cursor.Id }));
            var pasted = session.PasteAt(Sec(3));

            Assert.Equal(new[]
            {
                ("Screen", 0), ("Keys", 1), ("Cursor", 2),
                ("Screen copy", 3), ("Keys copy", 4), ("Cursor copy", 5),
                ("Card", 6), ("Audio", 7),
            }, RowOrder(session));

            foreach (var name in new[] { "Screen copy", "Keys copy", "Cursor copy" })
            {
                var clip = Assert.Single(On(session, RowNamed(session, name)));
                Assert.Contains(clip.Id, pasted);
                Assert.Equal((Sec(3), Sec(10)), (clip.TimelineStartTicks, clip.DurationTicks));
            }
        }

        [Fact]
        public void One_occupied_row_sends_the_whole_block_to_new_rows()
        {
            var source = Recording();
            var project = NewProject(source);
            var group = Guid.NewGuid();
            var screenRow = Row(project, "Screen", group);
            var cursorRow = Row(project, "Cursor", group);
            var screen = Clip(project, screenRow, 0, 5, Screen(source));
            var cursor = Clip(project, cursorRow, 0, 5, Cursor(source));
            Clip(project, cursorRow, 20, 5, Cursor(source));
            var session = SessionOf(project);

            Assert.True(session.CopyItems(new[] { screen.Id, cursor.Id }));
            session.PasteAt(Sec(20));

            // the screen row is free at 20 s, and a pasted cursor on its own row under a pasted
            // screen on a new one would compose invisibly
            Assert.Single(On(session, screenRow));
            Assert.Equal(2, On(session, cursorRow).Count());
            Assert.Single(On(session, RowNamed(session, "Screen copy")));
            Assert.Single(On(session, RowNamed(session, "Cursor copy")));
        }

        [Fact]
        public void Each_block_decides_for_itself()
        {
            var source = Recording();
            var project = NewProject(source);
            var group = Guid.NewGuid();
            var screenRow = Row(project, "Screen", group);
            var cursorRow = Row(project, "Cursor", group);
            var cardRow = Row(project, "Card");
            var screen = Clip(project, screenRow, 0, 5, Screen(source));
            var cursor = Clip(project, cursorRow, 0, 5, Cursor(source));
            var card = Clip(project, cardRow, 0, 5, Card());
            Clip(project, cardRow, 20, 5, Card());
            var session = SessionOf(project);
            var statuses = new List<string>();
            session.StatusRaised += (_, text) => statuses.Add(text);

            Assert.True(session.CopyItems(new[] { screen.Id, cursor.Id, card.Id }));
            session.PasteAt(Sec(20));

            Assert.Equal(2, On(session, screenRow).Count());
            Assert.Equal(2, On(session, cursorRow).Count());
            Assert.Equal(2, On(session, cardRow).Count());
            Assert.Single(On(session, RowNamed(session, "Card copy")));
            Assert.Equal(4, session.Project.Tracks.Count);
            Assert.Equal("Pasted on a new track: the original track is occupied here.", Assert.Single(statuses));
        }

        [Fact]
        public void New_rows_of_a_block_share_a_fresh_group_and_are_not_time_linked_to_what_they_overlap()
        {
            var session = GroupedRecording(out _, out var screen, out _, out var cursor, out _, out _);
            var original = RowNamed(session, "Screen").GroupId;

            Assert.True(session.CopyItems(new[] { screen.Id, cursor.Id }));
            var pasted = session.PasteAt(Sec(3));

            var screenCopy = RowNamed(session, "Screen copy");
            var cursorCopy = RowNamed(session, "Cursor copy");
            Assert.NotNull(screenCopy.GroupId);
            Assert.NotEqual(original, screenCopy.GroupId);
            Assert.Equal(screenCopy.GroupId, cursorCopy.GroupId);
            Assert.Equal(original, RowNamed(session, "Screen").GroupId);

            Assert.Equal(pasted.ToHashSet(), TimelineOps.TimeLinked(session.Project, new[] { pasted[0] }));
        }

        [Fact]
        public void A_lone_new_row_has_no_group()
        {
            var session = GroupedRecording(out _, out var screen, out _, out _, out _, out _);

            Assert.True(session.CopyItems(new[] { screen.Id }));
            session.PasteAt(Sec(3));

            Assert.Null(RowNamed(session, "Screen copy").GroupId);

            // and not only because the session dissolves a group with one row
            var project = Project.FromJson(session.Project.ToJson());
            var result = TimelineOps.Paste(project, TimelineOps.Lift(project, new[] { screen.Id }), 0);
            Assert.Equal(1, result.NewRows);
            Assert.Null(project.TrackOf(project.Items.Single(i => i.Id == result.Pasted[0])).GroupId);
        }

        [Fact]
        public void Clips_on_a_returned_to_row_simply_take_its_group_as_it_is_now()
        {
            var session = GroupedRecording(out _, out var screen, out _, out _, out var card, out _);
            Assert.True(session.CopyItems(new[] { screen.Id }));
            var screenRow = screen.TrackId;
            session.UngroupTracks(new[] { screenRow });

            session.PasteAt(Sec(20));

            Assert.Equal(2, session.Project.Items.Count(i => i.TrackId == screenRow));
            Assert.Null(session.Project.Tracks.Single(t => t.Id == screenRow).GroupId);

            // the other way about: a row grouped since the copy stays in that group
            Assert.True(session.CopyItems(new[] { card.Id }));
            session.GroupTracks(new[] { card.TrackId, RowNamed(session, "Keys").Id });
            var group = RowNamed(session, "Card").GroupId;
            Assert.NotNull(group);

            session.PasteAt(Sec(20));

            Assert.Equal(2, session.Project.Items.Count(i => i.TrackId == card.TrackId));
            Assert.Equal(group, RowNamed(session, "Card").GroupId);
        }

        [Fact]
        public void A_block_with_picture_and_audio_rows_stacks_each_where_the_timeline_lists_it()
        {
            var source = Recording();
            var project = NewProject(source);
            var group = Guid.NewGuid();
            var screen = Clip(project, Row(project, "Screen", group), 0, 5, Screen(source));
            Clip(project, Row(project, "Overlay"), 0, 5, Card());
            var sound = Clip(project, Row(project, "Sound", group, TrackKind.Audio), 0, 5, Sound(source));
            Clip(project, Row(project, "Other", kind: TrackKind.Audio), 0, 5, Sound(source));
            var session = SessionOf(project);

            Assert.True(session.CopyItems(new[] { screen.Id, sound.Id }));
            session.PasteAt(Sec(1));

            Assert.Equal(new[]
            {
                ("Screen", 0), ("Screen copy", 1), ("Overlay", 2), ("Sound", 3), ("Sound copy", 4), ("Other", 5),
            }, RowOrder(session));
            Assert.Equal(RowNamed(session, "Screen copy").GroupId, RowNamed(session, "Sound copy").GroupId);
        }

        [Fact]
        public void A_paste_into_another_project_brings_its_sources_and_puts_new_rows_above_the_pictures_and_after_the_audio()
        {
            var session = GroupedRecording(out var source, out var screen, out _, out _, out _, out var sound);
            session.GroupTracks(new[] { screen.TrackId, sound.TrackId });
            Assert.True(session.CopyItems(new[] { screen.Id, sound.Id }));

            var elsewhere = Recording();
            var other = NewProject(elsewhere);
            Clip(other, Row(other, "Their Screen"), 0, 5, Screen(elsewhere));
            Clip(other, Row(other, "Their Zoom", kind: TrackKind.Effect), 0, 5, new ZoomContent { Zoom = 2 });
            Clip(other, Row(other, "Their Sound", kind: TrackKind.Audio), 0, 5, Sound(elsewhere));
            Clip(other, Row(other, "Their Speed", kind: TrackKind.Effect), 0, 5, new SpeedContent());
            var otherSession = SessionOf(other);
            var statuses = new List<string>();
            otherSession.StatusRaised += (_, text) => statuses.Add(text);

            Assert.Equal(2, otherSession.PasteAt(Sec(1)).Count);

            Assert.Equal(new[]
            {
                ("Their Screen", 0), ("Their Zoom", 1), ("Screen copy", 2),
                ("Their Sound", 3), ("Audio copy", 4), ("Their Speed", 5),
            }, RowOrder(otherSession));
            Assert.Equal(RowNamed(otherSession, "Screen copy").GroupId, RowNamed(otherSession, "Audio copy").GroupId);
            var brought = otherSession.Project.Sources.Single(s => s.Id == source.Id);
            Assert.Equal(TestPath.ExistingCapture, brought.InputCapturePath);
            Assert.Empty(otherSession.Project.Validate());

            // their originals are not "occupied here": they are not here
            Assert.Equal("Pasted on 2 new tracks.", Assert.Single(statuses));
        }

        [Fact]
        public void A_source_already_here_is_not_added_twice()
        {
            var session = GroupedRecording(out _, out var screen, out _, out _, out _, out _);
            Assert.True(session.CopyItems(new[] { screen.Id }));

            session.PasteAt(Sec(20));

            Assert.Single(session.Project.Sources);
        }

        [Fact]
        public void A_row_that_now_holds_another_kind_of_clip_is_not_returned_to()
        {
            var source = Recording();
            var project = NewProject(source);
            var row = Row(project, "Row");
            var card = Clip(project, row, 0, 5, Card());
            var clip = TimelineOps.Lift(project, new[] { card.Id });
            project.Items.Remove(card);
            Clip(project, row, 50, 5, Cursor(source));

            var result = TimelineOps.Paste(project, clip, 0);

            Assert.Equal(1, result.NewRows);
            var copy = project.Items.Single(i => i.Id == result.Pasted[0]);
            Assert.NotEqual(row.Id, copy.TrackId);
            Assert.Equal(TimelineRowKind.Cursor, TimelineRowLayout.KindOf(row, project.Items.Where(i => i.TrackId == row.Id)));
            Assert.Equal(TimelineRowKind.Text, TimelineRowLayout.KindOf(project.TrackOf(copy), new[] { copy }));
        }

        // ------------------------------------------------------------------------------- selection and history

        [Fact]
        public void Pasted_clips_become_the_selection_in_one_undo_entry_that_restores_everything()
        {
            var session = GroupedRecording(out _, out var screen, out _, out var cursor, out _, out _);
            session.Select(screen.Id);
            Assert.True(session.CopyItems(new[] { screen.Id, cursor.Id }));
            var before = session.Project.ToJson();
            Assert.False(session.CanUndo);

            var pasted = session.PasteAt(Sec(3));

            Assert.Equal(pasted.ToHashSet(), session.SelectedItemIds.ToHashSet());
            var after = session.Project.ToJson();

            session.Undo();
            Assert.False(session.CanUndo);
            Assert.Equal(before, session.Project.ToJson());
            Assert.Equal(new[] { screen.Id }, session.SelectedItemIds);

            session.Redo();
            Assert.Equal(after, session.Project.ToJson());
        }

        [Fact]
        public void Pasting_again_pastes_new_clips_each_time()
        {
            var session = GroupedRecording(out _, out var screen, out _, out _, out _, out _);
            Assert.True(session.CopyItems(new[] { screen.Id }));

            var first = Assert.Single(session.PasteAt(Sec(20)));
            var second = Assert.Single(session.PasteAt(Sec(40)));

            Assert.NotEqual(first, second);
            Assert.NotEqual(screen.Id, first);
            Assert.Equal(screen.TrackId, Find(session, first).TrackId);
            Assert.Equal(screen.TrackId, Find(session, second).TrackId);

            // a third over the second: the row is taken now, so a new row
            session.PasteAt(Sec(40));
            Assert.Single(On(session, RowNamed(session, "Screen copy")));
        }

        [Fact]
        public void A_paste_or_cut_during_a_drag_does_nothing_and_a_copy_still_works()
        {
            var session = GroupedRecording(out _, out var screen, out _, out var cursor, out _, out _);
            Assert.True(session.CopyItems(new[] { screen.Id }));

            using var gesture = session.BeginGesture("Move");
            var before = session.Project.ToJson();

            Assert.False(session.CanPaste);
            Assert.Empty(session.PasteAt(Sec(20)));
            Assert.False(session.CanPasteIntoGap(default));
            Assert.False(session.CanCut);
            Assert.False(session.CutItems(new[] { cursor.Id }));
            Assert.True(session.CopyItems(new[] { cursor.Id }));

            Assert.Equal(before, session.Project.ToJson());
            Assert.Equal((1, 1), session.ClipboardSize);
            gesture.Cancel();

            Assert.True(session.CanPaste);
            Assert.True(session.CanCut);
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void A_paste_or_cut_during_a_voice_take_does_nothing()
        {
            var session = GroupedRecording(out _, out var screen, out _, out _, out _, out _);
            Assert.True(session.CopyItems(new[] { screen.Id }));
            session.BeginVoiceTake(Sec(50));
            var before = session.Project.ToJson();

            Assert.False(session.CanPaste);
            Assert.Empty(session.PasteAt(Sec(20)));
            Assert.False(session.CanCut);
            Assert.False(session.CutItems(new[] { screen.Id }));

            Assert.Equal(before, session.Project.ToJson());
        }

        // ------------------------------------------------------------------------------------- cut

        [Fact]
        public void Cut_removes_the_rows_and_paste_returns_them_and_undo_walks_back_through_both()
        {
            var session = GroupedRecording(out _, out var screen, out _, out var cursor, out _, out _);
            var start = session.Project.ToJson();
            var rows = session.Project.Tracks.Select(t => t.Id).ToList();

            Assert.True(session.CutItems(new[] { screen.Id, cursor.Id }));
            Assert.DoesNotContain(session.Project.Items, i => i.Id == screen.Id || i.Id == cursor.Id);
            Assert.DoesNotContain(session.Project.Tracks, t => t.Id == screen.TrackId || t.Id == cursor.TrackId);
            var afterCut = session.Project.ToJson();

            var pasted = session.PasteAt(0);

            Assert.Equal(rows, session.Project.Tracks.Select(t => t.Id));
            Assert.Equal(screen.TrackId, pasted.Select(id => Find(session, id)).Single(i => i.Content is MediaContent).TrackId);
            Assert.Equal(cursor.TrackId, pasted.Select(id => Find(session, id)).Single(i => i.Content is CursorContent).TrackId);

            session.Undo();
            Assert.Equal(afterCut, session.Project.ToJson());
            session.Undo();
            Assert.Equal(start, session.Project.ToJson());
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void Cut_removes_the_row_it_empties_and_the_paste_puts_it_back_as_itself()
        {
            // a card over grouped rows gets a row of its own
            var source = Recording();
            var project = NewProject(source);
            var group = Guid.NewGuid();
            Clip(project, Row(project, "Screen", group), 0, 10, Screen(source));
            Clip(project, Row(project, "Cursor", group), 0, 10, Cursor(source));
            var session = SessionOf(project);
            var opened = session.Project.Tracks.Select(t => t.Id).ToList();
            var made = session.AddText(Sec(30), Sec(5));
            var row = made.TrackId;
            var withCard = RowOrder(session);

            Assert.True(session.CutItems(new[] { made.Id }));
            Assert.Equal(opened, session.Project.Tracks.Select(t => t.Id));

            var pasted = Find(session, Assert.Single(session.PasteAt(Sec(30))));
            Assert.Equal(row, pasted.TrackId);
            Assert.Equal(withCard, RowOrder(session));

            session.Undo(); // the paste, and the row it put back
            Assert.Equal(opened, session.Project.Tracks.Select(t => t.Id));
            session.Undo(); // the cut: the card is back on its own row
            Assert.Equal(row, Find(session, made.Id).TrackId);
        }

        [Fact]
        public void Cut_of_a_whole_group_then_paste_finds_the_group_still_there()
        {
            var source = Recording();
            var project = NewProject(source);
            var group = Guid.NewGuid();
            var screen = Clip(project, Row(project, "Screen", group), 0, 5, Screen(source));
            var cursor = Clip(project, Row(project, "Cursor", group), 0, 5, Cursor(source));
            var session = SessionOf(project);

            Assert.True(session.CutItems(new[] { screen.Id, cursor.Id }));
            session.PasteAt(Sec(2));

            Assert.All(session.Project.Tracks, t => Assert.Equal(group, t.GroupId));
            Assert.Equal(2, session.Project.Tracks.Count);
        }

        [Fact]
        public void Cut_then_paste_of_a_group_holding_a_zoom_returns_to_the_same_rows()
        {
            var source = Recording();
            var project = NewProject(source);
            var group = Guid.NewGuid();
            var screenRow = Row(project, "Screen", group);
            var zoomRow = Row(project, "Zoom", group, TrackKind.Effect);
            var screen = Clip(project, screenRow, 0, 10, Screen(source));
            var zoom = Clip(project, zoomRow, 0, 10, new ZoomContent { Zoom = 2 });
            Clip(project, Row(project, "Card"), 0, 10, Card());
            var session = SessionOf(project);
            var statuses = new List<string>();
            session.StatusRaised += (_, text) => statuses.Add(text);
            var rows = RowOrder(session);

            Assert.True(session.CutItems(new[] { screen.Id, zoom.Id }));
            var pasted = session.PasteAt(0).Select(id => Find(session, id)).ToList();

            Assert.Equal(rows, RowOrder(session));
            Assert.Equal(screenRow.Id, pasted.Single(i => i.Content is MediaContent).TrackId);
            Assert.Equal(zoomRow.Id, pasted.Single(i => i.Content is ZoomContent).TrackId);
            Assert.Equal(new Guid?[] { group, group, null }, session.Project.Tracks.OrderBy(t => t.Order).Select(t => t.GroupId));
            Assert.Empty(statuses);

            session.Undo(); // the paste, and the rows it put back
            Assert.Equal(new[] { "Card" }, RowOrder(session).Select(r => r.Name));
            session.Undo(); // the cut
            Assert.Equal(rows, RowOrder(session));
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_zoom_cut_from_the_middle_of_the_stack_reaches_the_same_rows_when_pasted_back()
        {
            var source = Recording();
            var project = NewProject(source);
            Clip(project, Row(project, "Screen"), 0, 10, Screen(source));
            var zoomRow = Row(project, "Zoom", kind: TrackKind.Effect);
            var zoom = Clip(project, zoomRow, 0, 10, new ZoomContent { Zoom = 2 });
            Clip(project, Row(project, "Card"), 0, 10, Card());
            var session = SessionOf(project);
            bool Zoomed(string row) => !ZoomMath.EffectiveMatrix(session.Project, Sec(5), RowNamed(session, row).Order, 1920, 1080).IsIdentity;
            Assert.True(Zoomed("Screen"));
            Assert.False(Zoomed("Card"));
            var rows = RowOrder(session);

            Assert.True(session.CutItems(new[] { zoom.Id }));
            var pasted = Find(session, Assert.Single(session.PasteAt(0)));

            Assert.Equal(zoomRow.Id, pasted.TrackId);
            Assert.Equal(rows, RowOrder(session));
            Assert.True(Zoomed("Screen"));
            Assert.False(Zoomed("Card"));
        }

        [Fact]
        public void A_zoom_row_a_cut_empties_goes_at_once_and_a_paste_brings_it_back()
        {
            var session = GroupedRecording(out _, out _, out _, out _, out _, out _);
            var zoom = session.AddZoomEffect(Sec(1), Sec(2));
            var order = session.Project.TrackOf(zoom).Order;

            Assert.True(session.CutItems(new[] { zoom.Id }));
            Assert.DoesNotContain(session.Project.Tracks, t => t.Id == zoom.TrackId);
            Assert.Empty(session.Project.Validate());

            var pasted = Find(session, Assert.Single(session.PasteAt(Sec(4))));
            var back = session.Project.TrackOf(pasted);
            Assert.Equal(zoom.TrackId, back.Id);
            Assert.Equal(order, back.Order);
            Assert.Equal(TrackKind.Effect, back.Kind);
            Assert.IsType<ZoomContent>(pasted.Content);
            Assert.Empty(session.Project.Validate());
        }

        /// <summary>The row a cut removed is this project's: pasted into another one it means
        /// nothing there, so the clip gets a new row as any paste from elsewhere does.</summary>
        [Fact]
        public void A_cut_row_comes_back_only_in_the_session_that_cut_it()
        {
            var source = Recording();
            var theirs = NewProject(source);
            var cut = Clip(theirs, Row(theirs, "Card"), 0, 5, Card());
            var other = SessionOf(theirs);
            Assert.True(other.CutItems(new[] { cut.Id }));

            var mine = NewProject(source);
            Clip(mine, Row(mine, "Screen"), 0, 10, Screen(source));
            var session = SessionOf(mine);

            var pasted = Find(session, Assert.Single(session.PasteAt(0)));
            Assert.NotEqual(cut.TrackId, pasted.TrackId);
            Assert.Equal("Card copy", session.Project.TrackOf(pasted).Name);
        }

        /// <summary>Only its items say a row is the speed row, so an empty one would read as a zoom
        /// row and a paste or Add Speed would make a second one beside it.</summary>
        [Fact]
        public void A_cut_removes_the_speed_row_it_empties_and_a_paste_puts_it_back_on_top()
        {
            var source = Recording();
            var project = NewProject(source);
            Clip(project, Row(project, "Screen"), 0, 10, Screen(source));
            var speed = Clip(project, Row(project, "Speed", kind: TrackKind.Effect), 0, 10, new SpeedContent { Factor = 2 });
            var session = SessionOf(project);

            Assert.True(session.CutItems(new[] { speed.Id }));
            Assert.DoesNotContain(session.Project.Tracks, t => t.Kind == TrackKind.Effect);

            var pasted = Find(session, Assert.Single(session.PasteAt(0)));
            Assert.Equal(new[] { "Screen", "Speed" }, RowOrder(session).Select(r => r.Name));
            Assert.True(TimelineOps.IsSpeedTrack(session.Project, session.Project.TrackOf(pasted)));
        }

        /// <summary>A speed row can be in a group. The cut takes the row away, but the emptied rows
        /// keep the group's id, so the paste puts the group back whole.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Cut_then_paste_of_a_whole_group_with_its_speed_row_restores_the_group(bool withMic)
        {
            var source = Recording();
            var project = NewProject(source);
            var group = Guid.NewGuid();
            var screen = Clip(project, Row(project, "Screen", group), 0, 10, Screen(source));
            var items = new List<Item> { screen };
            if (withMic)
                items.Add(Clip(project, Row(project, "Mic", group, TrackKind.Audio), 0, 10, Sound(source)));
            items.Add(Clip(project, Row(project, "Speed", group, TrackKind.Effect), 0, 10, new SpeedContent { Factor = 2 }));
            var session = SessionOf(project);

            Assert.True(session.CutItems(items.Select(i => i.Id)));
            var pasted = session.PasteAt(0);

            Assert.Equal(items.Count, pasted.Count);
            Assert.Equal(items.Count, session.Project.Tracks.Count);
            Assert.All(session.Project.Tracks, t => Assert.Equal(group, t.GroupId));
            Assert.Equal(pasted.ToHashSet(), TimelineOps.TimeLinked(session.Project, new[] { pasted[0] }));
        }

        [Fact]
        public void A_cut_speed_item_returns_to_the_group_its_row_left()
        {
            var source = Recording();
            var project = NewProject(source);
            var group = Guid.NewGuid();
            Clip(project, Row(project, "Screen", group), 0, 10, Screen(source));
            Clip(project, Row(project, "Mic", group, TrackKind.Audio), 0, 10, Sound(source));
            var speed = Clip(project, Row(project, "Speed", group, TrackKind.Effect), 0, 10, new SpeedContent { Factor = 2 });
            var session = SessionOf(project);

            Assert.True(session.CutItems(new[] { speed.Id }));
            Assert.Equal(2, session.Project.Tracks.Count);

            session.PasteAt(0);
            Assert.Equal(group, RowNamed(session, "Speed").GroupId);
            Assert.Equal(group, RowNamed(session, "Screen").GroupId);
        }

        /// <summary>The group a speed row came from has no other row in this project, and the block
        /// it was copied with gets a fresh group of its own, so the row is in no group.</summary>
        [Fact]
        public void A_speed_row_pasted_where_its_group_is_not_stays_out_of_the_blocks_new_group()
        {
            var source = Recording();
            var theirs = NewProject(source);
            var group = Guid.NewGuid();
            var items = new[]
            {
                Clip(theirs, Row(theirs, "Screen", group), 0, 10, Screen(source)),
                Clip(theirs, Row(theirs, "Cursor", group), 0, 10, Cursor(source)),
                Clip(theirs, Row(theirs, "Speed", group, TrackKind.Effect), 0, 10, new SpeedContent { Factor = 2 }),
            };
            Assert.True(SessionOf(theirs).CopyItems(items.Select(i => i.Id)));

            var ours = NewProject();
            Clip(ours, Row(ours, "Card"), 0, 10, Card());
            var session = SessionOf(ours);
            Assert.Equal(3, session.PasteAt(0).Count);

            Assert.Null(RowNamed(session, "Speed").GroupId);
            var fresh = RowNamed(session, "Screen copy").GroupId;
            Assert.NotNull(fresh);
            Assert.NotEqual(group, fresh);
            Assert.Equal(fresh, RowNamed(session, "Cursor copy").GroupId);
        }

        // -------------------------------------------------------------------------------- gap paste

        /// <summary>One row with two cards to copy, the second at 3 s, and a row with a gap from 4 s
        /// to <paramref name="gapEnd"/>.</summary>
        private static EditorSession TwoCardsAndAGap(double secondLength, double gapEnd, out Track gapRow, out Item first, out Item second)
        {
            var project = NewProject();
            var from = Row(project, "From");
            first = Clip(project, from, 0, 2, Card());
            second = Clip(project, from, 3, secondLength, Card());
            gapRow = Row(project, "Gap");
            Clip(project, gapRow, 0, 4, Card());
            Clip(project, gapRow, gapEnd, 30, Card());
            return SessionOf(project);
        }

        [Theory]
        [InlineData(2.0, 7.0, null)]    // begins where the gap ends
        [InlineData(2.0, 6.5, null)]    // begins past it
        [InlineData(2.0, 7.05, null)]   // would end under the shortest clip an edit may make
        [InlineData(2.0, 7.5, 0.5)]     // the last is end-trimmed
        [InlineData(2.0, 9.0, 2.0)]     // fits whole
        [InlineData(0.05, 7.06, 0.05)]  // short itself, but fits whole, so nothing makes it shorter
        public void A_gap_trims_the_last_clip_to_fit_and_drops_those_with_no_room(double secondLength, double gapEnd, double? kept)
        {
            var session = TwoCardsAndAGap(secondLength, gapEnd, out var gapRow, out var first, out var second);
            Assert.True(session.CopyItems(new[] { first.Id, second.Id }));
            var gap = session.FindGap(gapRow.Id, Sec(5)).Value;

            Assert.True(session.CanPasteIntoGap(gap));
            var pasted = session.PasteIntoGap(gap).Select(id => Find(session, id)).OrderBy(i => i.TimelineStartTicks).ToList();

            Assert.Equal(kept == null ? 1 : 2, pasted.Count);
            Assert.All(pasted, i => Assert.Equal(gapRow.Id, i.TrackId));
            Assert.Equal(Sec(4), pasted[0].TimelineStartTicks);
            if (kept != null)
                Assert.Equal((Sec(7), Sec(kept.Value)), (pasted[1].TimelineStartTicks, pasted[1].DurationTicks));
            Assert.Empty(session.Project.Validate());
        }

        /// <summary>Paste at Cursor into the gap [4s, 9s) with a 2 s card: it starts at the click
        /// when it fits from there, and otherwise slides back just enough to end with the gap.</summary>
        [Theory]
        [InlineData(5.0, 5.0)]  // fits from the click
        [InlineData(4.0, 4.0)]  // the gap's start
        [InlineData(8.5, 7.0)]  // would run past the gap: ends with it instead
        [InlineData(9.0, 7.0)]
        public void Paste_at_cursor_starts_at_the_click_or_as_near_it_as_fits(double click, double start)
        {
            var session = TwoCardsAndAGap(2.0, 9.0, out var gapRow, out var first, out _);
            Assert.True(session.CopyItems(new[] { first.Id }));
            var gap = session.FindGap(gapRow.Id, Sec(5)).Value;

            var pasted = Find(session, Assert.Single(session.PasteIntoGap(gap, Sec(click))));

            Assert.Equal(gapRow.Id, pasted.TrackId);
            Assert.Equal((Sec(start), Sec(2)), (pasted.TimelineStartTicks, pasted.DurationTicks));
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Paste_at_cursor_of_a_clip_longer_than_the_gap_starts_at_the_gap_and_is_trimmed()
        {
            var session = TwoCardsAndAGap(10.0, 7.0, out var gapRow, out _, out var second);
            Assert.True(session.CopyItems(new[] { second.Id }));
            var gap = session.FindGap(gapRow.Id, Sec(5)).Value;

            var pasted = Find(session, Assert.Single(session.PasteIntoGap(gap, Sec(6))));

            Assert.Equal((Sec(4), Sec(3)), (pasted.TimelineStartTicks, pasted.DurationTicks));
        }

        [Fact]
        public void Paste_at_cursor_of_several_rows_starts_at_the_click()
        {
            var session = GroupedRecording(out _, out var screen, out _, out var cursor, out _, out var sound);
            var audioRow = sound.TrackId;
            session.TrimItemEnd(sound.Id, -Sec(5));
            Assert.True(session.CopyItems(new[] { sound.Id }));
            session.PasteAt(Sec(8)); // the audio row: [0, 5) and [8, 13)
            var gap = session.FindGap(audioRow, Sec(6)).Value;

            Assert.True(session.CopyItems(new[] { screen.Id, cursor.Id }));
            var pasted = session.PasteIntoGap(gap, Sec(6.5)).Select(id => Find(session, id)).ToList();

            Assert.Equal(2, pasted.Count);
            Assert.All(pasted, i => Assert.Equal(Sec(6.5), i.TimelineStartTicks));
        }

        [Fact]
        public void A_clipboard_of_several_rows_anchors_at_the_gap_and_goes_to_its_own_rows_untrimmed()
        {
            var session = GroupedRecording(out _, out var screen, out _, out var cursor, out _, out var sound);
            var audioRow = sound.TrackId;
            session.TrimItemEnd(sound.Id, -Sec(5)); // the audio row: [0, 5) ...
            Assert.True(session.CopyItems(new[] { sound.Id }));
            session.PasteAt(Sec(6)); // ... then [6, 11), leaving a gap a second wide
            var gap = session.FindGap(audioRow, Sec(5.5)).Value;

            Assert.True(session.CopyItems(new[] { screen.Id, cursor.Id }));
            Assert.Equal((2, 2), session.ClipboardSize);
            Assert.True(session.CanPasteIntoGap(gap));
            var pasted = session.PasteIntoGap(gap).Select(id => Find(session, id)).ToList();

            Assert.Equal(2, pasted.Count);
            Assert.All(pasted, i => Assert.Equal((Sec(5), Sec(10)), (i.TimelineStartTicks, i.DurationTicks)));
            Assert.DoesNotContain(pasted, i => i.TrackId == audioRow);
            Assert.Contains(pasted, i => session.Project.TrackOf(i).Name == "Screen copy");
        }

        [Fact]
        public void A_gap_refuses_what_its_row_cannot_host_and_changes_nothing()
        {
            // audio from a project this one has never seen: nothing here names its stream, so only
            // the row kinds can say it does not belong, and its source must not stay behind
            var theirs = Recording();
            var their = NewProject(theirs);
            var sound = Clip(their, Row(their, "Sound", kind: TrackKind.Audio), 0, 5, Sound(theirs));
            Assert.True(SessionOf(their).CopyItems(new[] { sound.Id }));

            var project = NewProject();
            var row = Row(project, "Row");
            Clip(project, row, 0, 1, Card());
            Clip(project, row, 5, 1, Card());
            var session = SessionOf(project);
            var gap = session.FindGap(row.Id, Sec(3)).Value;
            var before = session.Project.ToJson();

            Assert.False(session.CanPasteIntoGap(gap));
            Assert.Empty(session.PasteIntoGap(gap));

            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void A_gap_too_short_or_no_longer_there_takes_nothing()
        {
            var project = NewProject();
            var row = Row(project, "Row");
            var card = Clip(project, row, 0, 1, Card());
            Clip(project, row, 1.05, 1, Card());
            Clip(project, row, 5, 1, Card());
            var session = SessionOf(project);
            Assert.True(session.CopyItems(new[] { card.Id }));
            var narrow = session.FindGap(row.Id, Sec(1.02)).Value;
            var wide = session.FindGap(row.Id, Sec(3)).Value;

            Assert.False(session.CanPasteIntoGap(narrow));
            Assert.Empty(session.PasteIntoGap(narrow));
            Assert.True(session.CanPasteIntoGap(wide));

            Assert.True(session.CloseGap(wide));

            Assert.False(session.CanPasteIntoGap(wide));
            Assert.Empty(session.PasteIntoGap(wide));
        }

        [Fact]
        public void A_speed_item_goes_into_a_gap_only_on_the_speed_row()
        {
            var project = NewProject();
            var speedRow = Row(project, "Speed", kind: TrackKind.Effect);
            var speed = Clip(project, speedRow, 0, 1, new SpeedContent());
            Clip(project, speedRow, 5, 1, new SpeedContent());
            var zoomRow = Row(project, "Zoom", kind: TrackKind.Effect);
            Clip(project, zoomRow, 0, 1, new ZoomContent { Zoom = 2 });
            Clip(project, zoomRow, 5, 1, new ZoomContent { Zoom = 2 });
            var cardRow = Row(project, "Card");
            Clip(project, cardRow, 0, 1, Card());
            Clip(project, cardRow, 5, 1, Card());
            var session = SessionOf(project);
            Assert.True(session.CopyItems(new[] { speed.Id }));

            Assert.False(session.CanPasteIntoGap(session.FindGap(zoomRow.Id, Sec(3)).Value));
            Assert.False(session.CanPasteIntoGap(session.FindGap(cardRow.Id, Sec(3)).Value));

            var gap = session.FindGap(speedRow.Id, Sec(3)).Value;
            Assert.True(session.CanPasteIntoGap(gap));
            var pasted = Find(session, Assert.Single(session.PasteIntoGap(gap)));
            Assert.Equal((speedRow.Id, Sec(1)), (pasted.TrackId, pasted.TimelineStartTicks));
            Assert.Empty(session.Project.Validate());
        }

        // ------------------------------------------------------------------------------------ speed

        [Fact]
        public void Speed_items_go_to_the_speed_row_whatever_block_they_came_in()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var speedRow = Row(project, "Speed", group, TrackKind.Effect);
            var speed = Clip(project, speedRow, 0, 5, new SpeedContent { Factor = 3 });
            var cardRow = Row(project, "Card", group);
            var card = Clip(project, cardRow, 0, 5, Card());
            Clip(project, cardRow, 10, 5, Card());
            var session = SessionOf(project);
            Assert.True(session.CopyItems(new[] { speed.Id, card.Id }));

            // the card's row is taken at 10 s and the speed row is not: the block the speed row was
            // in gets a new row, and the speed item goes to the speed row all the same
            var pasted = session.PasteAt(Sec(10));

            Assert.Equal(2, pasted.Count);
            var speedCopy = pasted.Select(id => Find(session, id)).Single(i => i.Content is SpeedContent);
            Assert.Equal(speedRow.Id, speedCopy.TrackId);
            Assert.Equal(3, ((SpeedContent)speedCopy.Content).Factor);
            Assert.Single(session.Project.Tracks, t => TimelineOps.IsSpeedTrack(session.Project, t));
            Assert.Single(On(session, RowNamed(session, "Card copy")));
            Assert.Null(RowNamed(session, "Card copy").GroupId);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_speed_item_makes_the_speed_row_when_the_project_has_none_on_top_of_everything()
        {
            var theirs = NewProject();
            var speed = Clip(theirs, Row(theirs, "Speed", kind: TrackKind.Effect), 0, 5, new SpeedContent());
            Assert.True(SessionOf(theirs).CopyItems(new[] { speed.Id }));

            var session = GroupedRecording(out _, out _, out _, out _, out _, out _);
            var statuses = new List<string>();
            session.StatusRaised += (_, text) => statuses.Add(text);

            Assert.Single(session.PasteAt(Sec(1)));

            Assert.Equal("Speed", session.Project.Tracks.OrderBy(t => t.Order).Last().Name);
            Assert.True(TimelineOps.IsSpeedTrack(session.Project, RowNamed(session, "Speed")));
            Assert.Empty(statuses); // the one speed row is not a row made because others were occupied
        }

        [Fact]
        public void An_occupied_speed_row_refuses_the_whole_paste_and_says_so()
        {
            var theirs = Recording();
            var their = NewProject(theirs);
            var speed = Clip(their, Row(their, "Speed", kind: TrackKind.Effect), 0, 5, new SpeedContent());
            var screen = Clip(their, Row(their, "Screen"), 0, 5, Screen(theirs));
            Assert.True(SessionOf(their).CopyItems(new[] { speed.Id, screen.Id }));

            var project = NewProject();
            Clip(project, Row(project, "Their Speed", kind: TrackKind.Effect), 3, 5, new SpeedContent());
            Clip(project, Row(project, "Card"), 0, 1, Card());
            var session = SessionOf(project);
            var statuses = new List<string>();
            session.StatusRaised += (_, text) => statuses.Add(text);
            var before = session.Project.ToJson();

            Assert.Empty(session.PasteAt(0));

            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
            Assert.Equal("Can't paste: a Speed effect is already there.", Assert.Single(statuses));

            // only over what is there: back-to-back with it is free
            Assert.Equal(2, session.PasteAt(Sec(8)).Count);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_zoom_item_is_a_clip_like_any_other()
        {
            var session = GroupedRecording(out _, out _, out _, out _, out _, out _);
            var zoom = session.AddZoomEffect(Sec(1), Sec(2));
            Assert.True(session.CopyItems(new[] { zoom.Id }));

            session.PasteAt(Sec(5));
            Assert.Equal(2, session.Project.Items.Count(i => i.TrackId == zoom.TrackId));

            session.PasteAt(Sec(5));
            Assert.Equal(2, session.Project.Tracks.Count(t => t.Kind == TrackKind.Effect));
            Assert.Empty(session.Project.Validate());
        }

        // -------------------------------------------------------------------- clips on the output clock

        /// <summary>x2 speed over [0, 40), and an audio row with a 5 s output-clock clip at 50 s, past
        /// the speed item, where project time is output time.</summary>
        private static EditorSession ExemptPastASpeedItem(out Track row, out Item exempt, double? alsoOnTheRowAt = null)
        {
            var source = Recording();
            var project = NewProject(source);
            Clip(project, Row(project, "Speed", kind: TrackKind.Effect), 0, 40, new SpeedContent { Factor = 2 });
            row = Row(project, "Voice", kind: TrackKind.Audio);
            exempt = Clip(project, row, 50, 5, Sound(source, exempt: true));
            if (alsoOnTheRowAt is { } at)
                Clip(project, row, at, 1, Sound(source));
            return SessionOf(project);
        }

        [Fact]
        public void Lift_stores_the_output_span_of_a_clip_on_the_output_clock()
        {
            var source = Recording();
            var project = NewProject(source);
            Clip(project, Row(project, "Speed", kind: TrackKind.Effect), 0, 40, new SpeedContent { Factor = 2 });
            var row = Row(project, "Voice", kind: TrackKind.Audio);
            var exempt = Clip(project, row, 0, 10, Sound(source, exempt: true));
            var plain = Clip(project, Row(project, "Sound", kind: TrackKind.Audio), 0, 10, Sound(source));

            var clip = TimelineOps.Lift(project, new[] { exempt.Id, plain.Id });

            Assert.Equal(Sec(5), clip.Items.Single(i => i.Id == exempt.Id).DurationTicks);
            Assert.Equal(Sec(10), clip.Items.Single(i => i.Id == plain.Id).DurationTicks);
            Assert.Equal(Sec(10), exempt.DurationTicks); // the lift leaves the project alone
        }

        [Fact]
        public void A_clip_on_the_output_clock_takes_the_project_span_its_new_place_needs()
        {
            var session = ExemptPastASpeedItem(out var row, out var exempt);
            Assert.True(session.CopyItems(new[] { exempt.Id }));

            var under = Find(session, Assert.Single(session.PasteAt(Sec(10))));

            Assert.Equal(row.Id, under.TrackId);
            Assert.Equal((Sec(10), Sec(10)), (under.TimelineStartTicks, under.DurationTicks));

            // and back out from under it: the copy's output span is still 5 s
            Assert.True(session.CopyItems(new[] { under.Id }));
            var past = Find(session, Assert.Single(session.PasteAt(Sec(70))));
            Assert.Equal(Sec(5), past.DurationTicks);
        }

        [Fact]
        public void The_free_span_test_uses_the_project_span_so_a_take_is_never_truncated_to_pass_it()
        {
            // at 10 s the row is free for the 5 s the clip lasts now, but not for the 10 s it needs there
            var session = ExemptPastASpeedItem(out var row, out var exempt, alsoOnTheRowAt: 17);
            Assert.True(session.CopyItems(new[] { exempt.Id }));

            var pasted = Find(session, Assert.Single(session.PasteAt(Sec(10))));

            Assert.NotEqual(row.Id, pasted.TrackId);
            Assert.Equal((Sec(10), Sec(10)), (pasted.TimelineStartTicks, pasted.DurationTicks));
        }

        [Fact]
        public void The_warp_a_clip_lands_under_includes_the_speed_items_pasted_with_it()
        {
            var source = Recording();
            var project = NewProject(source);
            var speed = Clip(project, Row(project, "Speed", kind: TrackKind.Effect), 0, 10, new SpeedContent { Factor = 2 });
            var exempt = Clip(project, Row(project, "Voice", kind: TrackKind.Audio), 0, 5, Sound(source, exempt: true));
            var session = SessionOf(project);
            Assert.True(session.CutItems(new[] { speed.Id, exempt.Id }));

            var pasted = session.PasteAt(Sec(20)).Select(id => Find(session, id)).ToList();

            var speedCopy = Assert.Single(pasted, i => i.Content is SpeedContent);
            var voiceCopy = Assert.Single(pasted, i => i.Content is MediaContent);
            Assert.Equal((Sec(20), Sec(10)), (speedCopy.TimelineStartTicks, speedCopy.DurationTicks));

            // 2.5 s of output, which under x2 is 5 s of project: the speed item is pasted first
            Assert.Equal((Sec(20), Sec(5)), (voiceCopy.TimelineStartTicks, voiceCopy.DurationTicks));
        }

        [Fact]
        public void A_clip_on_the_output_clock_that_a_pasted_speed_item_will_stretch_is_not_pasted_over()
        {
            // the voice is [0, 5) now. x2 over it needs [0, 10) to play the same 5 s. A clip pasted at
            // 6 s looks free, and would cut the voice short when the session re-fits it.
            var source = Recording();
            var project = NewProject(source);
            var speed = Clip(project, Row(project, "Speed", kind: TrackKind.Effect), 50, 10, new SpeedContent { Factor = 2 });
            var row = Row(project, "Voice", kind: TrackKind.Audio);
            var voice = Clip(project, row, 0, 5, Sound(source, exempt: true));
            var other = Clip(project, row, 56, 1, Sound(source));
            var session = SessionOf(project);
            Assert.True(session.CopyItems(new[] { speed.Id, other.Id }));

            var pasted = session.PasteAt(0).Select(id => Find(session, id)).ToList();

            Assert.Equal(Sec(10), Find(session, voice.Id).DurationTicks);
            var sound = Assert.Single(pasted, i => i.Content is MediaContent);
            Assert.NotEqual(row.Id, sound.TrackId);
            Assert.Equal(Sec(6), sound.TimelineStartTicks);
        }

        private static (long Start, long End, long SourceIn)[] Spans(EditorSession session, IEnumerable<Guid> ids) =>
            ids.Select(id => Find(session, id)).OrderBy(i => i.TimelineStartTicks)
                .Select(i => (i.TimelineStartTicks, i.TimelineEndTicks, ((MediaContent)i.Content).SourceInTicks)).ToArray();

        [Theory]
        [InlineData(10, 14, 20)] // all under x2, so 2 s and 3 s of output need 4 s and 6 s
        [InlineData(38, 41, 44)] // the first piece has 2 s of project left under x2, then plays out past it
        public void A_split_take_pasted_under_a_speed_item_stays_whole_and_continuous(double at, double middle, double end)
        {
            var session = ExemptPastASpeedItem(out var row, out var exempt);
            session.SplitItemAt(exempt.Id, Sec(52));
            Assert.True(session.CopyItems(session.Project.Items.Where(i => i.TrackId == row.Id).Select(i => i.Id).ToList()));

            var pasted = session.PasteAt(Sec(at));

            Assert.Equal(new[] { (Sec(at), Sec(middle), 0L), (Sec(middle), Sec(end), Sec(2)) }, Spans(session, pasted));
            Assert.All(pasted, id => Assert.Equal(row.Id, Find(session, id).TrackId));
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_split_take_pasted_out_from_under_a_speed_item_has_no_silence_inside_it()
        {
            var session = ExemptPastASpeedItem(out var row, out var exempt);
            session.SplitItemAt(exempt.Id, Sec(52));
            Assert.True(session.CopyItems(session.Project.Items.Where(i => i.TrackId == row.Id).Select(i => i.Id).ToList()));
            var under = session.PasteAt(Sec(10));
            Assert.True(session.CopyItems(under));

            var pasted = session.PasteAt(Sec(60));

            Assert.Equal(new[] { (Sec(60), Sec(62), 0L), (Sec(62), Sec(65), Sec(2)) }, Spans(session, pasted));
        }

        [Fact]
        public void Takes_on_one_row_keep_the_time_between_them_on_the_output_clock()
        {
            var source = Recording();
            var project = NewProject(source);
            Clip(project, Row(project, "Speed", kind: TrackKind.Effect), 0, 40, new SpeedContent { Factor = 2 });
            var row = Row(project, "Voice", kind: TrackKind.Audio);
            var ids = new[]
            {
                Clip(project, row, 50, 2, Sound(source, exempt: true)),
                Clip(project, row, 53, 2, Sound(source, exempt: true)),
            }.Select(i => i.Id).ToList();
            var session = SessionOf(project);
            Assert.True(session.CopyItems(ids));

            var pasted = session.PasteAt(Sec(10));

            // 2 s, a 1 s pause, 2 s, each doubled. By project time the second would start at 13 s
            // and cut into the first.
            Assert.Equal(new[] { (Sec(10), Sec(14), 0L), (Sec(16), Sec(20), 0L) }, Spans(session, pasted));
        }

        [Fact]
        public void The_free_span_test_uses_the_laid_out_spans_of_a_split_take()
        {
            // the row is free for the 2 s and 3 s the pieces last now, but not for the 4 s and 6 s
            // they need under x2
            var session = ExemptPastASpeedItem(out var row, out var exempt, alsoOnTheRowAt: 18);
            session.SplitItemAt(exempt.Id, Sec(52));
            var parts = session.Project.Items.Where(i => i.TrackId == row.Id && i.TimelineStartTicks >= Sec(50)).Select(i => i.Id).ToList();
            Assert.True(session.CopyItems(parts));

            var pasted = session.PasteAt(Sec(10));

            Assert.All(pasted, id => Assert.NotEqual(row.Id, Find(session, id).TrackId));
            Assert.Equal(new[] { (Sec(10), Sec(14), 0L), (Sec(14), Sec(20), Sec(2)) }, Spans(session, pasted));
        }

        /// <summary>The accepted limit: the plain clip stays on the project clock, and something on
        /// that row has to give.</summary>
        [Fact]
        public void A_take_that_lands_on_a_plain_clip_copied_from_its_row_is_end_trimmed_rather_than_refusing_the_paste()
        {
            var source = Recording();
            var project = NewProject(source);
            Clip(project, Row(project, "Speed", kind: TrackKind.Effect), 0, 40, new SpeedContent { Factor = 2 });
            var row = Row(project, "Voice", kind: TrackKind.Audio);
            var ids = new[]
            {
                Clip(project, row, 50, 2, Sound(source, exempt: true)),
                Clip(project, row, 53, 1, Sound(source)),
            }.Select(i => i.Id).ToList();
            var session = SessionOf(project);
            Assert.True(session.CopyItems(ids));

            var pasted = session.PasteAt(Sec(10));

            Assert.Equal(new[] { (Sec(10), Sec(13), 0L), (Sec(13), Sec(14), 0L) }, Spans(session, pasted));
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_split_take_copied_and_pasted_at_the_same_time_under_a_speed_item_lands_on_the_same_spans()
        {
            var session = ExemptPastASpeedItem(out var row, out var exempt);
            session.SplitItemAt(exempt.Id, Sec(52));
            Assert.True(session.CopyItems(session.Project.Items.Where(i => i.TrackId == row.Id).Select(i => i.Id).ToList()));
            var under = session.PasteAt(Sec(10));
            var before = Spans(session, under);
            Assert.True(session.CopyItems(under));

            var again = session.PasteAt(Sec(10));

            Assert.NotEqual(row.Id, Find(session, again[0]).TrackId);
            Assert.Equal(before, Spans(session, again));
        }

        // ---------------------------------------------------------------------------- overlays

        /// <summary>A grouped screen row with a hole at [10, 20) and a clip to copy at 40 s, 70 s into
        /// the recording, under a cursor spanning the hole and aligned with the screen.</summary>
        private static EditorSession HoleUnderACursor(bool grouped, out Track screenRow, out Track cursorRow, out Item donor)
        {
            var source = Recording();
            var project = NewProject(source);
            var group = grouped ? Guid.NewGuid() : (Guid?)null;
            screenRow = Row(project, "Screen", group);
            cursorRow = Row(project, "Cursor", group);
            Clip(project, screenRow, 0, 10, Screen(source));
            Clip(project, screenRow, 20, 10, Screen(source, 20));
            donor = Clip(project, screenRow, 40, 5, Screen(source, 70));
            Clip(project, cursorRow, 0, 30, Cursor(source));
            return SessionOf(project);
        }

        private static void AssertCursorFollowsThePastedClip(EditorSession session, Track cursorRow)
        {
            Assert.Equal(Sec(5), SourceTimeOn(session, cursorRow, 5));
            Assert.Equal(Sec(72), SourceTimeOn(session, cursorRow, 14));
            Assert.Equal(Sec(25), SourceTimeOn(session, cursorRow, 25));
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_screen_clip_pasted_at_the_playhead_into_a_hole_hands_a_spanning_cursor_its_time()
        {
            var session = HoleUnderACursor(grouped: true, out var screenRow, out var cursorRow, out var donor);
            Assert.True(session.CopyItems(new[] { donor.Id }));

            var pasted = Find(session, Assert.Single(session.PasteAt(Sec(12))));

            Assert.Equal(screenRow.Id, pasted.TrackId);
            AssertCursorFollowsThePastedClip(session, cursorRow);

            // the rest of the hole has no screen clip, and the cursor keeps the clock it had there
            Assert.Equal(Sec(18), SourceTimeOn(session, cursorRow, 18));
        }

        [Fact]
        public void A_screen_clip_pasted_into_a_gap_hands_a_spanning_cursor_its_time()
        {
            var session = HoleUnderACursor(grouped: true, out var screenRow, out var cursorRow, out var donor);
            Assert.True(session.CopyItems(new[] { donor.Id }));
            var gap = session.FindGap(screenRow.Id, Sec(15)).Value;

            session.PasteIntoGap(gap);

            Assert.Equal(Sec(72), SourceTimeOn(session, cursorRow, 12));
            Assert.Equal(Sec(5), SourceTimeOn(session, cursorRow, 5));
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Undoing_the_paste_gives_the_cursor_back_its_clock()
        {
            var session = HoleUnderACursor(grouped: true, out _, out _, out var donor);
            Assert.True(session.CopyItems(new[] { donor.Id }));
            var before = session.Project.ToJson();

            session.PasteAt(Sec(12));
            session.Undo();

            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void An_ungrouped_row_keeps_its_cursor_where_it_was()
        {
            var session = HoleUnderACursor(grouped: false, out _, out var cursorRow, out var donor);
            Assert.True(session.CopyItems(new[] { donor.Id }));

            session.PasteAt(Sec(12));

            Assert.Equal(Sec(14), SourceTimeOn(session, cursorRow, 14));
        }

        [Fact]
        public void A_screen_clip_on_new_rows_leaves_the_cursor_it_overlaps_alone()
        {
            var session = HoleUnderACursor(grouped: true, out _, out var cursorRow, out var donor);
            Assert.True(session.CopyItems(new[] { donor.Id }));

            session.PasteAt(Sec(5)); // onto the first screen clip, so on a row of its own

            Assert.Single(On(session, cursorRow));
            Assert.Equal(Sec(7), SourceTimeOn(session, cursorRow, 7));
        }

        [Fact]
        public void Pasted_overlays_keep_their_own_clocks_while_those_already_there_follow_the_screen()
        {
            var source = Recording();
            var project = NewProject(source);
            var group = Guid.NewGuid();
            var screenRow = Row(project, "Screen", group);
            var spanning = Row(project, "Spanning", group);
            var nudged = Row(project, "Nudged", group);
            Clip(project, spanning, 0, 30, Cursor(source));
            var screen = Clip(project, screenRow, 40, 5, Screen(source, 70));
            var overlay = Clip(project, nudged, 40, 5, Cursor(source, 10));
            var session = SessionOf(project);
            Assert.True(session.CopyItems(new[] { screen.Id, overlay.Id }));

            session.PasteAt(Sec(12));

            Assert.Equal(Sec(72), SourceTimeOn(session, spanning, 14));
            Assert.Equal(Sec(12), SourceTimeOn(session, nudged, 14));
        }

        [Fact]
        public void Ripple_delete_of_a_pasted_clip_on_a_grouped_row_cuts_its_group_and_shifts_everything()
        {
            var source = Recording();
            var project = NewProject(source);
            var group = Guid.NewGuid();
            var screenRow = Row(project, "Screen", group);
            var soundRow = Row(project, "Sound", group, TrackKind.Audio);
            var cardRow = Row(project, "Card");
            var donor = Clip(project, screenRow, 0, 3, Screen(source));
            var later = Clip(project, screenRow, 20, 5, Screen(source, 20));
            Clip(project, soundRow, 0, 30, Sound(source));
            var card = Clip(project, cardRow, 25, 1, Card());
            var session = SessionOf(project);
            Assert.True(session.CopyItems(new[] { donor.Id }));

            var pasted = Assert.Single(session.PasteAt(Sec(10)));
            session.RippleDeleteItem(pasted);

            Assert.DoesNotContain(session.Project.Items, i => i.Id == pasted);
            Assert.Equal(Sec(17), Find(session, later.Id).TimelineStartTicks);
            Assert.Equal(Sec(27), On(session, soundRow).Sum(i => i.DurationTicks));
            Assert.Equal(Sec(22), Find(session, card.Id).TimelineStartTicks);
        }

        // ------------------------------------------------------------------------------- what it says

        private static List<string> Listen(EditorSession session)
        {
            var statuses = new List<string>();
            session.StatusRaised += (_, text) => statuses.Add(text);
            return statuses;
        }

        [Fact]
        public void A_paste_onto_free_rows_has_nothing_to_say()
        {
            var session = GroupedRecording(out _, out var screen, out _, out var cursor, out _, out _);
            var statuses = Listen(session);
            Assert.True(session.CopyItems(new[] { screen.Id, cursor.Id }));

            session.PasteAt(Sec(20));

            Assert.Empty(statuses);
        }

        [Fact]
        public void A_paste_that_made_rows_says_why()
        {
            var session = GroupedRecording(out _, out var screen, out _, out var cursor, out _, out _);
            var statuses = Listen(session);
            Assert.True(session.CopyItems(new[] { screen.Id, cursor.Id }));

            session.PasteAt(Sec(3));

            Assert.Equal("Pasted on 2 new tracks: the original tracks are occupied here.", Assert.Single(statuses));
        }

        [Theory]
        [InlineData(0, null)]                // over the screen clip
        [InlineData(8, CursorLine)]          // runs past it
        [InlineData(40, CursorLine)]         // nowhere near it
        public void A_pasted_cursor_with_a_stretch_it_will_not_draw_on_says_so(double at, string expected)
        {
            var source = Recording();
            var project = NewProject(source);
            Clip(project, Row(project, "Screen"), 0, 10, Screen(source));
            var row = Row(project, "Cursor");
            var cursor = Clip(project, row, 20, 5, Cursor(source));
            var session = SessionOf(project);
            var statuses = Listen(session);
            Assert.True(session.CopyItems(new[] { cursor.Id }));

            session.PasteAt(Sec(at));

            if (expected == null)
                Assert.Empty(statuses);
            else
                Assert.Equal(expected, Assert.Single(statuses));
        }

        private const string CursorLine = "The pasted cursor shows only over a clip of its recording.";

        [Fact]
        public void A_cursor_on_a_clip_on_a_hidden_row_still_has_its_host()
        {
            var source = Recording();
            var project = NewProject(source);
            var screenRow = Row(project, "Screen");
            screenRow.Hidden = true;
            Clip(project, screenRow, 0, 10, Screen(source));
            var cursor = Clip(project, Row(project, "Cursor"), 20, 5, Cursor(source));
            var session = SessionOf(project);
            var statuses = Listen(session);
            Assert.True(session.CopyItems(new[] { cursor.Id }));

            session.PasteAt(0);

            Assert.Empty(statuses);
        }

        [Fact]
        public void Both_things_to_say_are_said_together()
        {
            var source = Recording();
            var project = NewProject(source);
            var row = Row(project, "Cursor");
            var cursor = Clip(project, row, 0, 5, Cursor(source));
            var session = SessionOf(project);
            var statuses = Listen(session);
            Assert.True(session.CopyItems(new[] { cursor.Id }));

            session.PasteAt(Sec(1));

            Assert.Equal("Pasted on a new track: the original track is occupied here. " + CursorLine, Assert.Single(statuses));
        }

        // ------------------------------------------------------------------------------- duplicate

        [Fact]
        public void Duplicate_is_a_paste_of_the_row_where_it_already_is()
        {
            var session = GroupedRecording(out var source, out var screen, out _, out _, out _, out _);
            session.SplitItemAt(screen.Id, Sec(4));
            var row = session.Project.Tracks.Single(t => t.Id == screen.TrackId);
            row.Hidden = true;
            var originals = On(session, row).OrderBy(i => i.TimelineStartTicks).ToList();
            var before = session.Project.ToJson();

            // the same thing by hand on a copy of the project
            var byHand = Project.FromJson(before);
            TimelineOps.Paste(byHand, TimelineOps.Lift(byHand, originals.Select(i => i.Id)), 0);
            byHand.Normalize();

            Assert.True(session.DuplicateTrack(row.Id));

            var copyRow = RowNamed(session, "Screen copy");
            Assert.Equal(row.Order + 1, copyRow.Order);
            Assert.Null(copyRow.GroupId);
            Assert.True(copyRow.Hidden);
            Assert.Equal(byHand.Tracks.Select(t => (t.Name, t.Order, t.Hidden)), session.Project.Tracks.Select(t => (t.Name, t.Order, t.Hidden)));

            var copies = On(session, copyRow).OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(originals.Count, copies.Count);
            for (var i = 0; i < copies.Count; i++)
            {
                Assert.NotEqual(originals[i].Id, copies[i].Id);
                Assert.Equal((originals[i].TimelineStartTicks, originals[i].DurationTicks, ((MediaContent)originals[i].Content).SourceInTicks),
                    (copies[i].TimelineStartTicks, copies[i].DurationTicks, ((MediaContent)copies[i].Content).SourceInTicks));
            }

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void A_duplicate_of_a_row_that_starts_late_stays_where_it_is()
        {
            var project = NewProject();
            var row = Row(project, "Row");
            var card = Clip(project, row, 12, 3, Card());
            var session = SessionOf(project);

            Assert.True(session.DuplicateTrack(row.Id));

            Assert.Equal(Sec(12), Assert.Single(On(session, RowNamed(session, "Row copy"))).TimelineStartTicks);
            Assert.Equal(Sec(12), Find(session, card.Id).TimelineStartTicks);
        }

        [Fact]
        public void A_duplicate_says_nothing_and_leaves_the_clipboard_alone()
        {
            var session = GroupedRecording(out _, out var screen, out _, out var cursor, out _, out _);
            var statuses = Listen(session);
            Assert.True(session.CopyItems(new[] { screen.Id, cursor.Id }));

            Assert.True(session.DuplicateTrack(screen.TrackId));

            Assert.Empty(statuses);
            Assert.Equal((2, 2), session.ClipboardSize);
        }

        [Fact]
        public void Duplicating_an_empty_row_or_an_unknown_one_does_nothing()
        {
            var project = NewProject();
            var empty = Row(project, "Empty");
            Clip(project, Row(project, "Card"), 0, 1, Card());
            var session = SessionOf(project);
            var before = session.Project.ToJson();

            Assert.False(session.DuplicateTrack(empty.Id));
            Assert.False(session.DuplicateTrack(Guid.NewGuid()));

            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
        }

        // ---------------------------------------------------------------------------------- rows

        /// <summary>Goes by the type's properties, so a new field on a row is carried or named
        /// here.</summary>
        [Fact]
        public void A_row_copy_carries_every_setting_but_its_identity_and_its_group()
        {
            var original = new Track();
            foreach (var property in typeof(Track).GetProperties())
            {
                object value = property.PropertyType switch
                {
                    var t when t == typeof(bool) => true,
                    var t when t == typeof(double) => 0.25,
                    var t when t == typeof(int) => 7,
                    var t when t == typeof(string) => "Row",
                    var t when t == typeof(Guid) => Guid.NewGuid(),
                    var t when t == typeof(Guid?) => (Guid?)Guid.NewGuid(),
                    var t when t == typeof(TrackKind) => TrackKind.Audio,
                    _ => throw new InvalidOperationException($"No test value for {property.Name}."),
                };
                property.SetValue(original, value);
            }

            var copy = original.Clone();

            foreach (var property in typeof(Track).GetProperties())
            {
                switch (property.Name)
                {
                    case nameof(Track.Id):
                        Assert.NotEqual(original.Id, copy.Id);
                        break;
                    case nameof(Track.Name):
                        Assert.Equal("Row copy", copy.Name);
                        break;
                    case nameof(Track.GroupId):
                        Assert.Null(copy.GroupId);
                        break;
                    default:
                        Assert.Equal(property.GetValue(original), property.GetValue(copy));
                        break;
                }
            }
        }

        [Fact]
        public void A_nameless_row_copy_stays_nameless()
        {
            Assert.Null(new Track().Clone().Name);
            Assert.Equal("", new Track { Name = "" }.Clone().Name);
        }

        [Fact]
        public void InsertTrackAt_steps_up_every_row_at_or_past_the_slot()
        {
            var project = NewProject();
            var low = new Track { Id = Guid.NewGuid(), Order = 1 };
            var tied = new Track { Id = Guid.NewGuid(), Order = 3 };
            var also = new Track { Id = Guid.NewGuid(), Order = 3 };
            var high = new Track { Id = Guid.NewGuid(), Order = 8 };
            project.Tracks.AddRange(new[] { low, tied, also, high });
            var added = new Track { Id = Guid.NewGuid() };

            Assert.Same(added, TimelineOps.InsertTrackAt(project, added, 3));

            Assert.Equal((1, 4, 4, 9, 3), (low.Order, tied.Order, also.Order, high.Order, added.Order));
            Assert.Contains(added, project.Tracks);
        }

        // ------------------------------------------------------------------------------ the warp

        [Fact]
        public void A_duplicate_under_a_warp_keeps_a_clip_on_the_output_clock_within_a_tick()
        {
            var session = ExemptPastASpeedItem(out var row, out var exempt);
            session.MoveItems(new[] { exempt.Id }, -Sec(47)); // [3, ...) under the x2 item
            var moved = Find(session, exempt.Id);
            Assert.Equal(Sec(10), moved.DurationTicks);

            Assert.True(session.DuplicateTrack(row.Id));

            var copy = Assert.Single(On(session, RowNamed(session, "Voice copy")));
            Assert.Equal(moved.TimelineStartTicks, copy.TimelineStartTicks);
            Assert.InRange(copy.DurationTicks, moved.DurationTicks - 1, moved.DurationTicks + 1);
            Assert.Equal(moved.DurationTicks, Find(session, exempt.Id).DurationTicks);
            Assert.Equal(Sec(5), TimeWarp.Build(session.Project).ToOutput(moved.TimelineEndTicks) - TimeWarp.Build(session.Project).ToOutput(moved.TimelineStartTicks));
        }
    }
}
