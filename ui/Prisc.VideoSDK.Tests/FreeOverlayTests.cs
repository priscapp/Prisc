using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Prisc.UI.VideoEditor.Timeline;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Cursor and Keys as ordinary tracks, across more than one recording: which recording an add goes
    /// to, the feed a style edit reaches, copying and pasting, and the spans of a Cursor bar the
    /// timeline dims.
    /// </summary>
    [Collection("EditorSession clipboard")] // the copy/paste tests share its one static clipboard
    public class FreeOverlayTests
    {
        private static long Sec(long s) => s * TimeSpan.TicksPerSecond;

        private static Source Recording(string capture = null) => new Source
        {
            Id = Guid.NewGuid(),
            Path = $@"C:\rec\{Guid.NewGuid():N}.mp4",
            InputCapturePath = capture ?? TestPath.ExistingCapture,
            Streams =
            {
                new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, DurationTicks = Sec(100) },
                new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480, DurationTicks = Sec(100) },
                new SourceStream { Index = 2, Kind = StreamKind.Audio, DurationTicks = Sec(100) },
            },
        };

        private static Track Row(Project project, TrackKind kind = TrackKind.Video, Guid? group = null)
        {
            var track = new Track
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                Name = "Row",
                Order = project.Tracks.Count,
                GroupId = group,
            };
            project.Tracks.Add(track);
            return track;
        }

        private static Item Clip(Project project, Track row, long startSec, long lengthSec, ItemContent content)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = row.Id,
                TimelineStartTicks = Sec(startSec),
                DurationTicks = Sec(lengthSec),
                Content = content,
            };
            project.Items.Add(item);
            return item;
        }

        private static MediaContent Screen(Source source, long inSec = 0) =>
            new MediaContent { SourceId = source.Id, StreamIndex = 0, SourceInTicks = Sec(inSec) };

        private static Project NewProject(params Source[] sources)
        {
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            };
            project.Sources.AddRange(sources);
            return project;
        }

        private static EditorSession SessionOf(Project project)
        {
            project.Normalize();
            Assert.Empty(project.Validate());
            return new EditorSession(project, null, save => save());
        }

        private static Guid SourceOf(Item item) => ((SourceClockContent)item.Content).SourceId;

        private static int OrderOf(EditorSession session, Guid rowId) =>
            session.Project.Tracks.Single(t => t.Id == rowId).Order;

        private static Track RowOf(EditorSession session, Guid rowId) =>
            session.Project.Tracks.Single(t => t.Id == rowId);

        // --------------------------------------------------------------------------------- adding

        /// <summary>The first recording used to be the only one that could have a row.</summary>
        [Fact]
        public void Each_recording_takes_rows_of_its_own_above_its_own_screen_row()
        {
            var (first, second) = (Recording(), Recording());
            var project = NewProject(first, second);
            var firstRow = Row(project);
            var secondRow = Row(project);
            Clip(project, firstRow, 0, 10, Screen(first));
            Clip(project, secondRow, 0, 10, Screen(second));
            var session = SessionOf(project);

            var firstCursor = session.AddCursorTrack();
            var secondCursor = session.AddCursorTrack();
            var firstKeys = session.AddKeyboardTrack();
            var secondKeys = session.AddKeyboardTrack();

            Assert.Equal(first.Id, SourceOf(firstCursor));
            Assert.Equal(second.Id, SourceOf(secondCursor));
            Assert.Equal(first.Id, SourceOf(firstKeys));
            Assert.Equal(second.Id, SourceOf(secondKeys));
            Assert.False(session.CanAddCursorTrack);
            Assert.False(session.CanAddKeyboardTrack);
            Assert.Null(session.AddCursorTrack());

            // screen, keys, cursor for each, one above the other
            Assert.Equal(
                new[] { firstRow.Id, firstKeys.TrackId, firstCursor.TrackId, secondRow.Id, secondKeys.TrackId, secondCursor.TrackId },
                session.Project.Tracks.OrderBy(t => t.Order).Select(t => t.Id).ToArray());

            // two groups, each with its own recording's rows
            var firstGroup = RowOf(session, firstRow.Id).GroupId;
            var secondGroup = RowOf(session, secondRow.Id).GroupId;
            Assert.NotNull(firstGroup);
            Assert.NotNull(secondGroup);
            Assert.NotEqual(firstGroup, secondGroup);
            Assert.Equal(firstGroup, RowOf(session, firstCursor.TrackId).GroupId);
            Assert.Equal(firstGroup, RowOf(session, firstKeys.TrackId).GroupId);
            Assert.Equal(secondGroup, RowOf(session, secondCursor.TrackId).GroupId);
            Assert.Equal(secondGroup, RowOf(session, secondKeys.TrackId).GroupId);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_recording_whose_capture_file_is_gone_is_passed_over()
        {
            var missing = Recording(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.jsonl"));
            var present = Recording();
            var project = NewProject(missing, present);
            Clip(project, Row(project), 0, 10, Screen(missing));
            Clip(project, Row(project), 0, 10, Screen(present));
            var session = SessionOf(project);

            Assert.Equal(present.Id, SourceOf(session.AddCursorTrack()));
            Assert.False(session.CanAddCursorTrack);
        }

        [Fact]
        public void A_recording_with_no_screen_clip_is_passed_over()
        {
            var webcamOnly = Recording();
            var present = Recording();
            var project = NewProject(webcamOnly, present);
            Clip(project, Row(project), 0, 10, new MediaContent { SourceId = webcamOnly.Id, StreamIndex = 1 });
            Clip(project, Row(project), 0, 10, Screen(present));
            var session = SessionOf(project);

            Assert.Equal(present.Id, SourceOf(session.AddKeyboardTrack()));
            Assert.False(session.CanAddKeyboardTrack);
        }

        /// <summary>A recording counts as having a row by its clips, wherever they are: one moved or
        /// duplicated away from its screen row still counts, and another kind does not.</summary>
        [Fact]
        public void One_kind_on_a_recording_does_not_stop_the_other_and_another_recording_does_not_either()
        {
            var (first, second) = (Recording(), Recording());
            var project = NewProject(first, second);
            Clip(project, Row(project), 0, 10, Screen(first));
            Clip(project, Row(project), 0, 10, Screen(second));
            Clip(project, Row(project), 0, 10, new CursorContent { SourceId = first.Id });
            var session = SessionOf(project);

            Assert.True(session.CanAddCursorTrack);
            Assert.Equal(second.Id, SourceOf(session.AddCursorTrack()));
            Assert.Equal(first.Id, SourceOf(session.AddKeyboardTrack()));
        }

        /// <summary>A row shared with another recording's clip and a webcam stream: only this
        /// recording's screen clips get an overlay, each with that clip's span and clock.</summary>
        [Fact]
        public void The_segments_are_this_recordings_screen_clips_on_that_row_with_their_clocks()
        {
            var (first, second) = (Recording(), Recording());
            var project = NewProject(first, second);
            var row = Row(project);
            Clip(project, row, 0, 5, new MediaContent { SourceId = second.Id, StreamIndex = 0, SourceInTicks = Sec(40) });
            Clip(project, row, 5, 5, new MediaContent { SourceId = first.Id, StreamIndex = 1, SourceInTicks = Sec(40) });
            Clip(project, row, 10, 5, new MediaContent { SourceId = first.Id, StreamIndex = 0, SourceInTicks = Sec(7), Speed = 2.0 });
            Clip(project, row, 20, 5, new MediaContent { SourceId = first.Id, StreamIndex = 0, SourceInTicks = Sec(3), Freeze = true });
            var session = SessionOf(project);

            session.AddCursorTrack();

            var cursors = session.Project.Items.Where(i => i.Content is CursorContent).OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(new[] { Sec(10), Sec(20) }, cursors.Select(i => i.TimelineStartTicks));
            Assert.All(cursors, c => Assert.Equal(first.Id, SourceOf(c)));
            var (sped, held) = ((CursorContent)cursors[0].Content, (CursorContent)cursors[1].Content);
            Assert.Equal((Sec(7), 2.0, false), (sped.SourceInTicks, sped.Speed, sped.Freeze));
            Assert.Equal((Sec(3), true), (held.SourceInTicks, held.Freeze));
        }

        /// <summary>The row goes directly above the screen row, not above a keys row the user took
        /// away, which would strand it on the far side of whatever is between them.</summary>
        [Fact]
        public void A_cursor_goes_above_keys_only_when_keys_are_what_sits_on_the_screen_row()
        {
            var recording = Recording();
            var project = NewProject(recording);
            var screen = Row(project);
            var webcam = Row(project);
            Clip(project, screen, 0, 10, Screen(recording));
            Clip(project, webcam, 0, 10, new MediaContent { SourceId = recording.Id, StreamIndex = 1 });
            var session = SessionOf(project);

            var keys = session.AddKeyboardTrack();
            Assert.True(session.MoveTrackLayer(keys.TrackId, towardsFront: true)); // screen, webcam, keys

            var cursor = session.AddCursorTrack();

            Assert.Equal(new[] { screen.Id, cursor.TrackId, webcam.Id, keys.TrackId },
                session.Project.Tracks.OrderBy(t => t.Order).Select(t => t.Id).ToArray());
        }

        // ------------------------------------------------------------------------------- the feed

        /// <summary>The cursor rows of two recordings can end up one row, by a paste: style stops at
        /// the recording, as it stops at the stream for media.</summary>
        [Fact]
        public void A_style_edit_stops_at_the_feed_for_every_kind_of_clip()
        {
            var (first, second) = (Recording(), Recording());
            var project = NewProject(first, second);
            var cursors = Row(project);
            var a1 = Clip(project, cursors, 0, 5, new CursorContent { SourceId = first.Id });
            var a2 = Clip(project, cursors, 5, 5, new CursorContent { SourceId = first.Id });
            var b1 = Clip(project, cursors, 10, 5, new CursorContent { SourceId = second.Id });
            var otherRow = Row(project);
            var a3 = Clip(project, otherRow, 0, 5, new CursorContent { SourceId = first.Id });
            var keys = Row(project);
            var k1 = Clip(project, keys, 0, 5, new KeyboardContent { SourceId = first.Id });
            var k2 = Clip(project, keys, 5, 5, new KeyboardContent { SourceId = first.Id });
            var k3 = Clip(project, keys, 10, 5, new KeyboardContent { SourceId = second.Id });
            var screens = Row(project);
            var s1 = Clip(project, screens, 0, 5, Screen(first));
            var s2 = Clip(project, screens, 5, 5, Screen(first));
            var cam = Clip(project, screens, 10, 5, new MediaContent { SourceId = first.Id, StreamIndex = 1 });
            var cards = Row(project);
            var t1 = Clip(project, cards, 0, 5, new TextContent { Text = "a" });
            var t2 = Clip(project, cards, 5, 5, new TextContent { Text = "b" });
            var session = SessionOf(project);

            HashSet<Guid> Reached(Item item)
            {
                var reached = new HashSet<Guid>();
                session.EditFeed(item.Id, i => reached.Add(i.Id), $"probe:{Guid.NewGuid()}", null);
                return reached;
            }

            Assert.Equal(new[] { a1.Id, a2.Id }.ToHashSet(), Reached(a2));
            Assert.Equal(new[] { b1.Id }.ToHashSet(), Reached(b1));
            Assert.Equal(new[] { a3.Id }.ToHashSet(), Reached(a3));
            Assert.Equal(new[] { k1.Id, k2.Id }.ToHashSet(), Reached(k1));
            Assert.Equal(new[] { k3.Id }.ToHashSet(), Reached(k3));
            Assert.Equal(new[] { s1.Id, s2.Id }.ToHashSet(), Reached(s1));
            Assert.Equal(new[] { cam.Id }.ToHashSet(), Reached(cam));
            Assert.Equal(new[] { t2.Id }.ToHashSet(), Reached(t2));
            Assert.Equal(new[] { t1.Id }.ToHashSet(), Reached(t1));
        }

        [Fact]
        public void A_feed_edit_is_one_mutation_and_one_undo_entry()
        {
            var recording = Recording();
            var project = NewProject(recording);
            var row = Row(project);
            var a = Clip(project, row, 0, 5, new CursorContent { SourceId = recording.Id });
            var b = Clip(project, row, 5, 5, new CursorContent { SourceId = recording.Id });
            var session = SessionOf(project);
            var before = session.Project.ToJson();
            var changes = 0;
            session.ProjectChanged += (_, _) => changes++;

            session.EditFeed(b.Id, i => ((CursorContent)i.Content).Size = 2.0, "size", null);

            Assert.Equal(1, changes);
            Assert.All(session.Project.Items, i => Assert.Equal(2.0, ((CursorContent)i.Content).Size));

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        /// <summary>Spinner mashing is one undo entry across a feed, from whichever segment is
        /// selected; a selection change to another feed on the row starts a new one.</summary>
        [Fact]
        public void A_feed_edit_coalesces_within_the_feed_and_not_across_feeds_on_a_row()
        {
            var (first, second) = (Recording(), Recording());
            var project = NewProject(first, second);
            var row = Row(project);
            var a1 = Clip(project, row, 0, 5, new CursorContent { SourceId = first.Id });
            var a2 = Clip(project, row, 5, 5, new CursorContent { SourceId = first.Id });
            var b1 = Clip(project, row, 10, 5, new CursorContent { SourceId = second.Id });
            var b2 = Clip(project, row, 15, 5, new CursorContent { SourceId = second.Id });
            var session = SessionOf(project);

            double SizeOf(Item item) => ((CursorContent)session.Project.Items.Single(i => i.Id == item.Id).Content).Size;

            session.EditFeed(a1.Id, i => ((CursorContent)i.Content).Size = 2.0, "size", null);
            session.EditFeed(a2.Id, i => ((CursorContent)i.Content).Size = 3.0, "size", null);
            session.EditFeed(b1.Id, i => ((CursorContent)i.Content).Size = 4.0, "size", null);

            Assert.Equal((3.0, 4.0), (SizeOf(a1), SizeOf(b2)));

            session.Undo();
            Assert.Equal((3.0, 1.0), (SizeOf(a1), SizeOf(b2)));

            session.Undo();
            Assert.Equal((1.0, 1.0), (SizeOf(a1), SizeOf(b2)));
        }

        [Fact]
        public void A_feed_edit_on_an_item_not_in_the_project_throws()
        {
            var project = NewProject();
            Clip(project, Row(project), 0, 5, new TextContent { Text = "a" });
            var session = SessionOf(project);

            Assert.Throws<ArgumentException>(() => session.EditFeed(Guid.NewGuid(), _ => { }, "k", null));
        }

        // ------------------------------------------------------------------------- copy and paste

        private static TimelineGap GapOn(EditorSession session, Track row, long atSec) =>
            session.FindGap(row.Id, Sec(atSec)).Value;

        /// <summary>A recording with a cursor and a keys row, each with a clip either side of a hole,
        /// and a text row and a screen row with holes of their own.</summary>
        private static EditorSession Holes(out Track cursors, out Track keys, out Track screens, out Track cards,
            out Item cursor, out Item key, out Item card, out Item screen)
        {
            var recording = Recording();
            var project = NewProject(recording);
            cursors = Row(project);
            cursor = Clip(project, cursors, 0, 5, new CursorContent { SourceId = recording.Id, SourceInTicks = Sec(9), Speed = 2.0 });
            Clip(project, cursors, 20, 5, new CursorContent { SourceId = recording.Id });
            keys = Row(project);
            key = Clip(project, keys, 0, 5, new KeyboardContent { SourceId = recording.Id, FontSize = 33 });
            Clip(project, keys, 20, 5, new KeyboardContent { SourceId = recording.Id });
            screens = Row(project);
            screen = Clip(project, screens, 0, 5, Screen(recording));
            Clip(project, screens, 20, 5, Screen(recording));
            cards = Row(project);
            card = Clip(project, cards, 0, 5, new TextContent { Text = "a" });
            Clip(project, cards, 20, 5, new TextContent { Text = "b" });
            return SessionOf(project);
        }

        [Fact]
        public void A_cursor_clip_copies_and_pastes_into_a_gap_on_a_cursor_row_with_its_clock()
        {
            var session = Holes(out var cursors, out _, out _, out _, out var cursor, out _, out _, out _);
            Assert.True(session.CopyItems(new[] { cursor.Id }));
            var before = session.Project.ToJson();

            var gap = GapOn(session, cursors, 10);
            Assert.True(session.CanPasteIntoGap(gap));
            var id = Assert.Single(session.PasteIntoGap(gap));

            var pasted = session.Project.Items.Single(i => i.Id == id);
            Assert.NotEqual(cursor.Id, pasted.Id);
            Assert.Equal(cursors.Id, pasted.TrackId);
            Assert.Equal(Sec(5), pasted.TimelineStartTicks);
            var clock = Assert.IsType<CursorContent>(pasted.Content);
            Assert.Equal((Sec(9), 2.0), (clock.SourceInTicks, clock.Speed));
            Assert.Empty(session.Project.Validate());

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void A_keys_clip_copies_and_pastes_into_a_gap_on_a_keys_row()
        {
            var session = Holes(out _, out var keys, out _, out _, out _, out var key, out _, out _);
            Assert.True(session.CopyItems(new[] { key.Id }));

            var id = Assert.Single(session.PasteIntoGap(GapOn(session, keys, 10)));

            var pasted = session.Project.Items.Single(i => i.Id == id);
            Assert.Equal(33, Assert.IsType<KeyboardContent>(pasted.Content).FontSize);
            Assert.Empty(session.Project.Validate());
        }

        /// <summary>Rows stay what they are in both directions, and the session refuses what the menu
        /// greys.</summary>
        [Fact]
        public void A_paste_never_mixes_an_overlay_row_with_anything_else()
        {
            var session = Holes(out var cursors, out var keys, out var screens, out var cards,
                out var cursor, out var key, out var card, out var screen);

            void AssertOnlyFits(Item clip, Track home)
            {
                Assert.True(session.CopyItems(new[] { clip.Id }));
                foreach (var row in new[] { cursors, keys, screens, cards })
                {
                    var gap = GapOn(session, row, 10);
                    var fits = row == home || (row == screens && home == cards) || (row == cards && home == screens);
                    Assert.Equal(fits, session.CanPasteIntoGap(gap));
                    if (!fits)
                    {
                        var json = session.Project.ToJson();
                        Assert.Empty(session.PasteIntoGap(gap));
                        Assert.Equal(json, session.Project.ToJson());
                    }
                }
            }

            AssertOnlyFits(cursor, cursors);
            AssertOnlyFits(key, keys);
            AssertOnlyFits(screen, screens);
            AssertOnlyFits(card, cards);
        }

        [Fact]
        public void A_cursor_pasted_into_another_project_brings_its_recording_and_capture()
        {
            var session = Holes(out _, out _, out _, out _, out var cursor, out _, out _, out _);
            Assert.True(session.CopyItems(new[] { cursor.Id }));

            var elsewhere = Recording();
            var other = NewProject(elsewhere);
            var cursors = Row(other);
            Clip(other, cursors, 0, 5, new CursorContent { SourceId = elsewhere.Id });
            Clip(other, cursors, 20, 5, new CursorContent { SourceId = elsewhere.Id });
            var otherSession = SessionOf(other);

            Assert.DoesNotContain(otherSession.Project.Sources, s => s.Id == SourceOf(cursor));

            Assert.NotEmpty(otherSession.PasteIntoGap(GapOn(otherSession, cursors, 10)));

            var brought = otherSession.Project.Sources.Single(s => s.Id == SourceOf(cursor));
            Assert.Equal(TestPath.ExistingCapture, brought.InputCapturePath);
            Assert.Empty(otherSession.Project.Validate());
        }

        // ------------------------------------------------------------------------ dimmed Cursor bars

        private static Project HostProject(out Source source, out Item cursor, out Track screenRow)
        {
            source = Recording();
            var project = NewProject(source);
            screenRow = Row(project);
            cursor = Clip(project, Row(project), 0, 40, new CursorContent { SourceId = source.Id });
            return project;
        }

        [Fact]
        public void CursorHostSpans_are_the_screen_clips_it_overlaps_limited_to_the_clip()
        {
            var project = HostProject(out var source, out var cursor, out var row);
            Clip(project, row, 5, 5, Screen(source));
            Clip(project, row, 20, 30, Screen(source)); // runs past the cursor's end

            Assert.Equal(new[] { (Sec(5), Sec(10)), (Sec(20), Sec(40)) }, TimelineOps.CursorHostSpans(project, cursor));
        }

        [Fact]
        public void CursorHostSpans_merge_touching_and_overlapping_clips_across_rows()
        {
            var project = HostProject(out var source, out var cursor, out var row);
            Clip(project, row, 0, 5, Screen(source));
            Clip(project, row, 5, 5, Screen(source));
            var upper = Row(project);
            Clip(project, upper, 8, 6, Screen(source));
            Clip(project, upper, 20, 1, Screen(source));

            Assert.Equal(new[] { (Sec(0), Sec(14)), (Sec(20), Sec(21)) }, TimelineOps.CursorHostSpans(project, cursor));
        }

        /// <summary>A short clip inside a long one must not cut the span back to its own end.</summary>
        [Fact]
        public void CursorHostSpans_keep_a_span_through_a_clip_inside_it()
        {
            var project = HostProject(out var source, out var cursor, out var row);
            Clip(project, row, 0, 20, Screen(source));
            Clip(project, Row(project), 5, 5, Screen(source));

            Assert.Equal(new[] { (Sec(0), Sec(20)) }, TimelineOps.CursorHostSpans(project, cursor));
        }

        [Fact]
        public void CursorHostSpans_count_a_hidden_row_and_nothing_but_this_recordings_screen()
        {
            var project = HostProject(out var source, out var cursor, out var row);
            var other = Recording();
            project.Sources.Add(other);
            var hidden = Row(project);
            hidden.Hidden = true;
            Clip(project, hidden, 0, 5, Screen(source));
            Clip(project, row, 10, 5, new MediaContent { SourceId = source.Id, StreamIndex = 1 });     // webcam
            Clip(project, row, 20, 5, Screen(other));                                                  // another recording
            Clip(project, Row(project, TrackKind.Audio), 30, 5, new MediaContent { SourceId = source.Id, StreamIndex = 0 }); // not a video row

            Assert.Equal(new[] { (Sec(0), Sec(5)) }, TimelineOps.CursorHostSpans(project, cursor));
        }

        [Fact]
        public void CursorHostSpans_are_empty_for_anything_but_a_cursor_with_a_source()
        {
            var project = HostProject(out var source, out var cursor, out var row);
            var screen = Clip(project, row, 0, 5, Screen(source));

            Assert.Empty(TimelineOps.CursorHostSpans(project, screen));
            Assert.Empty(TimelineOps.CursorHostSpans(project, null));
            Assert.NotEmpty(TimelineOps.CursorHostSpans(project, cursor));

            project.Sources.Clear();
            Assert.Empty(TimelineOps.CursorHostSpans(project, cursor));
        }

        /// <summary>The bar and the picture have one rule, on any arrangement: a time is in a span
        /// exactly when the resolver finds the cursor a clip to draw through. Random rows of clips of
        /// both recordings and streams, some hidden, sampled across the cursor and on every clip edge,
        /// so a half-open slip shows.</summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void CursorHostSpans_agree_with_the_resolver_at_every_sampled_time(int seed)
        {
            var rng = new Random(seed);
            var project = HostProject(out var source, out var cursor, out _);
            var other = Recording();
            project.Sources.Add(other);
            for (var r = 0; r < 4; r++)
            {
                var row = Row(project);
                row.Hidden = rng.Next(3) == 0;
                for (var start = rng.Next(0, 6); start < 45; start += rng.Next(2, 14))
                {
                    var length = rng.Next(1, 3);
                    Clip(project, row, start, length,
                        new MediaContent { SourceId = rng.Next(3) == 0 ? other.Id : source.Id, StreamIndex = rng.Next(3) == 0 ? 1 : 0 });
                    start += length;
                }
            }
            project.Normalize();

            var spans = TimelineOps.CursorHostSpans(project, cursor);

            Assert.NotEmpty(spans);
            var samples = project.Items.SelectMany(i => new[] { i.TimelineStartTicks, i.TimelineEndTicks, i.TimelineEndTicks - 1 })
                .Concat(Enumerable.Range(0, 400).Select(n => n * Sec(1) / 10))
                .Where(t => t >= cursor.TimelineStartTicks && t < cursor.TimelineEndTicks);
            foreach (var t in samples)
            {
                var resolved = FrameComposer.ResolveCursorHost(project, source, cursor, t) != null;
                Assert.Equal(resolved, spans.Any(s => t >= s.Start && t < s.End));
            }
        }

        /// <summary>The dimmed part is what is left, so a bar with a host throughout dims nothing and
        /// one with none dims whole.</summary>
        [Fact]
        public void Uncovered_is_what_the_spans_leave_of_the_bar()
        {
            (long, long)[] Left(params (long, long)[] covered) =>
                TimelineViewMath.Uncovered(10, 50, covered).ToArray();

            Assert.Equal(new[] { (10L, 50L) }, Left());
            Assert.Empty(Left((10, 50)));
            Assert.Equal(new[] { (10L, 20L) }, Left((20, 50)));
            Assert.Equal(new[] { (20L, 50L) }, Left((10, 20)));
            Assert.Equal(new[] { (10L, 20L), (30L, 40L) }, Left((20, 30), (40, 50)));
            Assert.Equal(new[] { (10L, 20L), (30L, 40L), (45L, 50L) }, Left((20, 30), (40, 45)));
        }

        /// <summary>The whole path, as the timeline asks it: a right-click Split and Delete on the
        /// screen leaves a hole under a cursor clip that spans it.</summary>
        [Fact]
        public void A_screen_clip_deleted_from_under_a_cursor_is_what_dims()
        {
            var recording = Recording();
            var project = NewProject(recording);
            var row = Row(project);
            Clip(project, row, 0, 30, Screen(recording));
            var session = SessionOf(project);
            var cursor = session.AddCursorTrack();
            var clip = session.Project.Items.Single(i => i.TrackId == row.Id);

            Assert.Empty(TimelineViewMath.Uncovered(cursor.TimelineStartTicks, cursor.TimelineEndTicks,
                TimelineOps.CursorHostSpans(session.Project, cursor)));

            session.SplitItemAt(clip.Id, Sec(10));
            session.SplitItemAt(session.Project.Items.Last(i => i.TrackId == row.Id).Id, Sec(20));
            session.DeleteItems(new[] { session.Project.Items.Single(i => i.TrackId == row.Id && i.TimelineStartTicks == Sec(10)).Id });

            var live = session.Project.Items.Single(i => i.Id == cursor.Id);
            Assert.Equal(new[] { (Sec(10), Sec(20)) }, TimelineViewMath.Uncovered(live.TimelineStartTicks, live.TimelineEndTicks,
                TimelineOps.CursorHostSpans(session.Project, live)));
        }
    }
}
