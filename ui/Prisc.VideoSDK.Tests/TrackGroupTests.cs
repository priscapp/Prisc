using System;
using System.Collections.Generic;
using System.Linq;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Groups as a tag on rows: what a drag brings along (<see cref="TimelineOps.TimeLinked"/>), and
    /// what Ctrl+K, Close Gap and Ripple Delete do across a group's rows. An ungrouped row is a group
    /// of one throughout.
    /// </summary>
    public class TrackGroupTests
    {
        private static long Sec(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            Sources =
            {
                new Source
                {
                    Id = Guid.NewGuid(),
                    Path = @"C:\rec\input.mp4",
                    Streams =
                    {
                        new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, DurationTicks = Sec(600) },
                        new SourceStream { Index = 1, Kind = StreamKind.Audio, DurationTicks = Sec(600) },
                    },
                },
            },
        };

        private static Track Row(Project project, Guid? group = null, TrackKind kind = TrackKind.Video)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = kind, Order = project.Tracks.Count, GroupId = group };
            project.Tracks.Add(track);
            return track;
        }

        /// <summary>A clip of the recording at <c>[start, start + duration)</c> in seconds, playing
        /// the source at the same time it sits on the timeline.</summary>
        private static Item Clip(Project project, Track row, double start, double duration)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = row.Id,
                TimelineStartTicks = Sec(start),
                DurationTicks = Sec(duration),
                Content = new MediaContent
                {
                    SourceId = project.Sources[0].Id,
                    StreamIndex = row.Kind == TrackKind.Audio ? 1 : 0,
                    SourceInTicks = Sec(start),
                },
            };
            project.Items.Add(item);
            return item;
        }

        private static Item Live(EditorSession session, Item item) =>
            session.Project.Items.Single(i => i.Id == item.Id);

        private static IEnumerable<Guid> Ids(params Item[] items) => items.Select(i => i.Id);

        private static void AssertLinked(Project project, Item seed, params Item[] expected) =>
            Assert.Equal(Ids(expected).OrderBy(i => i), TimelineOps.TimeLinked(project, new[] { seed.Id }).OrderBy(i => i));

        // ---------------------------------------------------------------------------------- links

        [Fact]
        public void GroupTracks_is_the_row_and_the_rows_sharing_its_group()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Row(project, group);
            var b = Row(project, group);
            var other = Row(project, Guid.NewGuid());
            var loose = Row(project);

            Assert.Equal(new[] { a, b }, TimelineOps.GroupTracks(project, b.Id));
            Assert.Equal(new[] { other }, TimelineOps.GroupTracks(project, other.Id));
            Assert.Equal(new[] { loose }, TimelineOps.GroupTracks(project, loose.Id));
            Assert.Empty(TimelineOps.GroupTracks(project, Guid.NewGuid()));
        }

        /// <summary>The drag rule itself: overlapping clips on grouped rows, transitively, and
        /// nothing else, however grouped.</summary>
        [Fact]
        public void TimeLinked_chains_through_overlaps_and_leaves_clips_that_do_not_overlap()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var rowA = Row(project, group);
            var rowB = Row(project, group);
            var rowC = Row(project, group);

            var a = Clip(project, rowA, 0, 5);
            var b = Clip(project, rowB, 4, 5);
            var c = Clip(project, rowC, 8, 4);
            var laterOnA = Clip(project, rowA, 20, 5);
            var laterOnC = Clip(project, rowC, 12, 1); // touches c's end: half-open, so no link

            AssertLinked(project, a, a, b, c);
            AssertLinked(project, c, a, b, c); // the chain runs either way
            AssertLinked(project, laterOnA, laterOnA);
            AssertLinked(project, laterOnC, laterOnC);
        }

        [Fact]
        public void TimeLinked_of_an_ungrouped_seed_is_just_the_seed()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var loose = Clip(project, Row(project), 0, 10);
            var grouped = Clip(project, Row(project, group), 0, 10);
            var partner = Clip(project, Row(project, group), 0, 10);

            AssertLinked(project, loose, loose);
            // the grouped clip overlaps the loose one too, but that row is not in its group
            AssertLinked(project, grouped, grouped, partner);
        }

        [Fact]
        public void TimeLinked_is_the_union_of_its_seeds_and_stops_at_each_group()
        {
            var project = NewProject();
            var one = Guid.NewGuid();
            var two = Guid.NewGuid();
            var oneA = Clip(project, Row(project, one), 0, 10);
            var oneB = Clip(project, Row(project, one), 0, 10);
            var twoA = Clip(project, Row(project, two), 0, 10);
            var twoB = Clip(project, Row(project, two), 0, 10);
            var loose = Clip(project, Row(project), 0, 10);

            AssertLinked(project, oneA, oneA, oneB);

            var linked = TimelineOps.TimeLinked(project, new[] { oneA.Id, twoA.Id, loose.Id, Guid.NewGuid() });

            Assert.Equal(Ids(oneA, oneB, twoA, twoB, loose).OrderBy(i => i), linked.OrderBy(i => i));
        }

        /// <summary>After Ctrl+K across all rows each side of the cut drags separately.</summary>
        [Fact]
        public void The_two_sides_of_a_cut_are_not_linked()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var screen = Row(project, group);
            var audio = Row(project, group, TrackKind.Audio);
            var leftScreen = Clip(project, screen, 0, 10);
            var leftAudio = Clip(project, audio, 0, 10);

            TimelineOps.SplitAcross(project, Sec(4));

            var rightScreen = project.Items.Single(i => i.TrackId == screen.Id && i.Id != leftScreen.Id);
            var rightAudio = project.Items.Single(i => i.TrackId == audio.Id && i.Id != leftAudio.Id);
            AssertLinked(project, leftScreen, leftScreen, leftAudio);
            AssertLinked(project, rightAudio, rightScreen, rightAudio);
        }

        /// <summary>A right-click split leaves the halves chained through the other rows: that is
        /// what its tooltip warns about, and why Ctrl+K is the separating cut.</summary>
        [Fact]
        public void A_right_click_split_keeps_the_halves_chained_through_other_rows()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var screen = Row(project, group);
            var left = Clip(project, screen, 0, 10);
            var audio = Clip(project, Row(project, group, TrackKind.Audio), 0, 10);

            Assert.True(TimelineOps.SplitItem(project, left.Id, Sec(4)));

            var right = project.Items.Single(i => i.TrackId == screen.Id && i.Id != left.Id);
            AssertLinked(project, left, left, right, audio);
        }

        /// <summary>The set is frozen at press: passing over a clip does not pull it in, although a
        /// new drag after dropping there would.</summary>
        [Fact]
        public void A_frozen_drag_does_not_absorb_what_it_passes_over()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var dragged = Clip(project, Row(project, group), 0, 5);
            var passed = Clip(project, Row(project, group), 10, 5);
            var session = new EditorSession(project, null, null);

            var frozen = TimelineOps.TimeLinked(session.Project, new[] { dragged.Id });
            using (var gesture = session.BeginGesture("Move"))
            {
                Assert.Equal(Sec(8), session.MoveItems(frozen, Sec(8)));
                AssertLinked(session.Project, Live(session, dragged), Live(session, dragged), Live(session, passed));

                Assert.Equal(Sec(10), session.MoveItems(frozen, Sec(10)));
                gesture.Commit();
            }

            Assert.Equal(Sec(18), Live(session, dragged).TimelineStartTicks);
            Assert.Equal(Sec(10), Live(session, passed).TimelineStartTicks);
        }

        // ---------------------------------------------------------------------------------- moves

        [Fact]
        public void Move_moves_exactly_the_clips_it_is_given()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var moved = Clip(project, Row(project, group), 0, 5);
            var overlapping = Clip(project, Row(project, group), 0, 5);

            Assert.Equal(Sec(2), TimelineOps.Move(project, new[] { moved.Id }, Sec(2)));

            Assert.Equal(Sec(2), moved.TimelineStartTicks);
            Assert.Equal(0, overlapping.TimelineStartTicks);
            Assert.Equal(0, TimelineOps.Move(project, Array.Empty<Guid>(), Sec(2)));
        }

        /// <summary>No clip of the set may reach before the origin, so the earliest one sets the
        /// delta for all of them and their spacing survives.</summary>
        [Fact]
        public void The_origin_clamp_is_taken_across_the_whole_set()
        {
            var project = NewProject();
            var early = Clip(project, Row(project), 1, 2);
            var late = Clip(project, Row(project), 5, 2);

            Assert.Equal(-Sec(1), TimelineOps.Move(project, Ids(early, late), -Sec(3)));

            Assert.Equal(0, early.TimelineStartTicks);
            Assert.Equal(Sec(4), late.TimelineStartTicks);
        }

        /// <summary>A step that collides on any row is undone for the whole set, and the drag picks
        /// up from its last valid position on the next step.</summary>
        [Fact]
        public void A_collision_rolls_the_whole_set_back_and_the_drag_goes_on()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var screen = Row(project, group);
            var audio = Row(project, group, TrackKind.Audio);
            var dragged = Clip(project, screen, 0, 5);
            var draggedAudio = Clip(project, audio, 0, 5);
            var inTheWay = Clip(project, audio, 7, 3); // only the audio row is blocked
            var session = new EditorSession(project, null, null);
            var failures = 0;
            session.ValidationFailed += (_, _) => failures++;

            var frozen = TimelineOps.TimeLinked(session.Project, new[] { dragged.Id });
            using (var gesture = session.BeginGesture("Move"))
            {
                Assert.Equal(Sec(1), session.MoveItems(frozen, Sec(1)));

                Assert.Equal(0, session.MoveItems(frozen, Sec(4))); // audio would reach 7s
                Assert.Equal(1, failures);
                Assert.Equal(Sec(1), Live(session, dragged).TimelineStartTicks);
                Assert.Equal(Sec(1), Live(session, draggedAudio).TimelineStartTicks);

                Assert.Equal(Sec(1), session.MoveItems(frozen, Sec(1)));
                gesture.Commit();
            }

            Assert.Equal(Sec(2), Live(session, dragged).TimelineStartTicks);
            Assert.Equal(Sec(2), Live(session, draggedAudio).TimelineStartTicks);
            Assert.Equal(Sec(7), Live(session, inTheWay).TimelineStartTicks);

            session.Undo();
            Assert.Equal(0, Live(session, dragged).TimelineStartTicks);
        }

        /// <summary>Audio clip on the output clock, in a group with the speed row: a drag moves the
        /// speed item and the clip together, so nothing about the clip's real-time span changes.
        /// Fitting against the warp from before the move would give it a different duration.</summary>
        [Fact]
        public void A_set_moving_a_speed_item_with_an_exempt_clip_lands_on_the_warp_it_left_behind()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var speed = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = Row(project, group, TrackKind.Effect).Id,
                TimelineStartTicks = Sec(2),
                DurationTicks = Sec(4),
                Content = new SpeedContent { Factor = 2 },
            };
            project.Items.Add(speed);
            var clip = Clip(project, Row(project, group, TrackKind.Audio), 3, 3.5);
            ((MediaContent)clip.Content).SpeedWarpExempt = true;

            // [3s, 6s) under 2x plus [6s, 6.5s) plain: 2s of real time
            var linked = TimelineOps.TimeLinked(project, new[] { clip.Id });
            Assert.Contains(speed.Id, linked);

            Assert.Equal(Sec(1), TimelineOps.Move(project, linked, Sec(1)));

            Assert.Equal(Sec(3), speed.TimelineStartTicks);
            Assert.Equal(Sec(4), clip.TimelineStartTicks);
            Assert.Equal(Sec(3.5), clip.DurationTicks);

            // and through the session, in a drag, with its own re-fit on top
            var session = new EditorSession(project, null, null);
            using (var gesture = session.BeginGesture("Move"))
            {
                session.MoveItems(linked, Sec(1));
                session.MoveItems(linked, Sec(1));
                gesture.Commit();
            }

            Assert.Equal(Sec(6), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Sec(3.5), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        // ---------------------------------------------------------------------------------- Ctrl+K

        private const string RefusedCut = "Couldn't cut one group: a clip edge is too close to the playhead.";

        [Fact]
        public void A_refused_group_stays_whole_while_other_groups_are_cut_and_the_status_says_so()
        {
            var project = NewProject();
            var refused = Guid.NewGuid();
            var cut = Guid.NewGuid();
            // the second clip of this group starts a hair before the playhead
            var refusedA = Clip(project, Row(project, refused), 0, 10);
            var refusedB = Clip(project, Row(project, refused), 4.95, 5);
            var cutA = Clip(project, Row(project, cut), 0, 10);
            var cutB = Clip(project, Row(project, cut), 0, 10);
            var loose = Clip(project, Row(project), 0, 10);

            var session = new EditorSession(project, null, null);
            var before = session.Project.ToJson();
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);

            Assert.True(session.SplitAtPlayhead(Sec(5)));

            Assert.Equal(new[] { RefusedCut }, status);
            Assert.Equal(Sec(10), Live(session, refusedA).DurationTicks);
            Assert.Equal(Sec(5), Live(session, refusedB).DurationTicks);
            Assert.Single(session.Project.Items, i => i.TrackId == refusedA.TrackId);
            Assert.Single(session.Project.Items, i => i.TrackId == refusedB.TrackId);

            foreach (var left in new[] { cutA, cutB, loose })
            {
                Assert.Equal(Sec(5), Live(session, left).DurationTicks);
                Assert.Equal(2, session.Project.Items.Count(i => i.TrackId == left.TrackId));
            }

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void Ungrouped_rows_are_cut_or_refused_each_on_their_own()
        {
            var project = NewProject();
            var whole = Clip(project, Row(project), 0, 10);
            var tooClose = Clip(project, Row(project), 4.95, 5);
            var session = new EditorSession(project, null, null);
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);

            Assert.True(session.SplitAtPlayhead(Sec(5)));

            Assert.Equal(Sec(5), Live(session, whole).DurationTicks);
            Assert.Equal(Sec(5), Live(session, tooClose).DurationTicks);
            Assert.Single(session.Project.Items, i => i.TrackId == tooClose.TrackId);
            Assert.Equal(new[] { RefusedCut }, status);
        }

        [Fact]
        public void With_everything_refused_nothing_changes_and_the_status_still_speaks()
        {
            var project = NewProject();
            Clip(project, Row(project), 0, 10);
            var session = new EditorSession(project, null, null);
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);

            Assert.False(session.SplitAtPlayhead(Sec(0.05)));

            Assert.Equal(new[] { RefusedCut }, status);
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void A_clean_cut_and_a_cut_of_nothing_say_nothing()
        {
            var project = NewProject();
            Clip(project, Row(project), 0, 10);
            var session = new EditorSession(project, null, null);
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);

            Assert.True(session.SplitAtPlayhead(Sec(5)));
            Assert.False(session.SplitAtPlayhead(Sec(50)));

            Assert.Empty(status);
        }

        /// <summary>The flow the Split menu's tip promises: a right-click split leaves the two sides
        /// chained through the other rows, and Ctrl+K at the same place separates them. The row
        /// already cut there is not a sliver that refuses the group.</summary>
        [Fact]
        public void Ctrl_K_separates_the_two_sides_of_a_right_click_split()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var screen = Clip(project, Row(project, group), 0, 10);
            var mic = Clip(project, Row(project, group, TrackKind.Audio), 0, 10);
            var session = new EditorSession(project, null, null);
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);

            Assert.True(session.SplitItemAt(screen.Id, Sec(5)));
            var right = session.Project.Items.Single(i => i.TrackId == screen.TrackId && i.TimelineStartTicks == Sec(5));
            Assert.Equal(3, TimelineOps.TimeLinked(session.Project, new[] { right.Id }).Count);

            Assert.True(session.SplitAtPlayhead(Sec(5)));

            Assert.Empty(status);
            Assert.Equal(Sec(5), Live(session, mic).DurationTicks);
            Assert.Equal(2, session.Project.Items.Count(i => i.TrackId == screen.TrackId));
            Assert.Equal(2, session.Project.Items.Count(i => i.TrackId == mic.TrackId));
            Assert.Equal(2, TimelineOps.TimeLinked(session.Project, new[] { right.Id }).Count);
        }

        [Fact]
        public void A_clip_starting_at_the_playhead_needs_no_cut_and_refuses_nothing()
        {
            var project = NewProject();
            var screen = Clip(project, Row(project), 0, 10);
            var session = new EditorSession(project, null, null);
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);
            var text = session.AddText(Sec(5), Sec(2));

            Assert.True(session.SplitAtPlayhead(Sec(5)));

            Assert.Empty(status);
            Assert.Equal(Sec(5), Live(session, screen).DurationTicks);
            Assert.Equal(Sec(5), Live(session, text).TimelineStartTicks);
            Assert.Equal(Sec(2), Live(session, text).DurationTicks);
        }

        [Fact]
        public void Cutting_twice_at_one_playhead_does_nothing_the_second_time_and_says_nothing()
        {
            var project = NewProject();
            Clip(project, Row(project), 0, 10);
            var session = new EditorSession(project, null, null);
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);

            Assert.True(session.SplitAtPlayhead(Sec(5)));
            var before = session.Project.ToJson();

            Assert.False(session.SplitAtPlayhead(Sec(5)));

            Assert.Empty(status);
            Assert.Equal(before, session.Project.ToJson());
        }

        /// <summary>A cut group's rows keep their one id: nothing about the group changes.</summary>
        [Fact]
        public void A_cut_mints_no_group()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Clip(project, Row(project, group), 0, 10);
            var b = Clip(project, Row(project, group), 0, 10);

            TimelineOps.SplitAcross(project, Sec(5));

            Assert.Equal(group, project.TrackOf(a).GroupId);
            Assert.Equal(group, project.TrackOf(b).GroupId);
            Assert.All(project.Items, i => Assert.Null(i.LegacyLinkGroupId));
        }

        // ------------------------------------------------------------------------------- close gap

        /// <summary>The regression a time-linked close would bring: a recording with later
        /// back-to-back slices has to close up whole, not open a new gap beside the first of
        /// them.</summary>
        [Fact]
        public void Close_gap_slides_every_later_slice_on_the_group_rows()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var screen = Row(project, group);
            var audio = Row(project, group, TrackKind.Audio);
            var before = new[] { Clip(project, screen, 0, 5), Clip(project, audio, 0, 5) };
            var slices = new[]
            {
                Clip(project, screen, 8, 4), Clip(project, audio, 8, 4),
                Clip(project, screen, 12, 4), Clip(project, audio, 12, 4),
            };
            var gap = TimelineOps.FindGap(project, screen.Id, Sec(6)).Value;

            Assert.True(TimelineOps.CloseGap(project, gap));

            Assert.All(before, i => Assert.Equal(0, i.TimelineStartTicks));
            Assert.Equal(new[] { Sec(5), Sec(5), Sec(9), Sec(9) }, slices.Select(i => i.TimelineStartTicks));
            Assert.Empty(project.Validate());
        }

        /// <summary><c>Validate</c> only checks overlap within a row, so with nothing later on the
        /// straddler's row the slide would pass it and silently put the rows out of step. Hence an
        /// explicit refusal, and Ripple Close Gap beside it.</summary>
        [Fact]
        public void Close_gap_refuses_when_a_clip_on_another_group_row_is_in_the_gap()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var screen = Row(project, group);
            Clip(project, screen, 0, 5);
            var after = Clip(project, screen, 8, 4);
            var straddler = Clip(project, Row(project, group), 3, 3); // [3s, 6s), and nothing past it
            var session = new EditorSession(project, null, null);
            var gap = session.FindGap(screen.Id, Sec(6)).Value;
            var json = session.Project.ToJson();

            Assert.False(TimelineOps.CloseGap(session.Project, gap));
            Assert.Equal(json, session.Project.ToJson());
            Assert.False(session.CanCloseGap(gap));
            Assert.False(session.CloseGap(gap));
            Assert.Equal(json, session.Project.ToJson());
            Assert.False(session.CanUndo);

            Assert.True(session.RippleCloseGap(gap));
            Assert.Equal(Sec(5), Live(session, after).TimelineStartTicks);
            Assert.Equal(Sec(2), Live(session, straddler).DurationTicks);
        }

        /// <summary>The everyday edit that used to desync: two right-click splits and a delete on
        /// the screen alone, with the cursor still spanning the hole.</summary>
        [Fact]
        public void Close_gap_refuses_after_a_split_split_delete_on_the_screen_under_a_spanning_cursor()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var screen = Row(project, group);
            var first = Clip(project, screen, 0, 30);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = Row(project, group).Id,
                DurationTicks = Sec(30),
                Content = new CursorContent { SourceId = project.Sources[0].Id },
            });
            var session = new EditorSession(project, null, null);

            Assert.True(session.SplitItemAt(first.Id, Sec(10)));
            var middle = session.Project.Items.Single(i => i.TrackId == screen.Id && i.TimelineStartTicks == Sec(10));
            Assert.True(session.SplitItemAt(middle.Id, Sec(20)));
            session.DeleteItems(new[] { middle.Id });

            var gap = session.FindGap(screen.Id, Sec(15)).Value;
            Assert.False(session.CanCloseGap(gap));
            Assert.False(session.CloseGap(gap));

            Assert.True(session.RippleCloseGap(gap));
            Assert.Empty(session.Project.Validate());
            Assert.Equal(Sec(20), session.Project.Items.Single(i => i.Content is CursorContent && i.TimelineStartTicks == 0).DurationTicks
                                  + session.Project.Items.Where(i => i.Content is CursorContent && i.TimelineStartTicks != 0).Sum(i => i.DurationTicks));
        }

        /// <summary>Clips touching the gap's edges are not in it: half-open spans again.</summary>
        [Fact]
        public void Close_gap_is_not_blocked_by_a_clip_that_only_touches_the_gap()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var screen = Row(project, group);
            Clip(project, screen, 0, 5);
            var after = Clip(project, screen, 8, 4);
            var other = Row(project, group);
            var endsAtGap = Clip(project, other, 2, 3);     // ends at the gap's start
            var startsAtEnd = Clip(project, other, 8, 2);   // starts at the gap's end

            Assert.True(TimelineOps.CloseGap(project, TimelineOps.FindGap(project, screen.Id, Sec(6)).Value));

            Assert.Equal(Sec(5), after.TimelineStartTicks);
            Assert.Equal(Sec(2), endsAtGap.TimelineStartTicks);
            Assert.Equal(Sec(5), startsAtEnd.TimelineStartTicks);
        }

        /// <summary>Ctrl+G must not change what Close Gap does to clips on one row, so an ungrouped
        /// row closes up whole too. Other rows stay put.</summary>
        [Fact]
        public void Close_gap_on_an_ungrouped_row_moves_all_its_later_clips_and_no_other_row()
        {
            var project = NewProject();
            var row = Row(project);
            var a = Clip(project, row, 0, 5);
            var b = Clip(project, row, 8, 2);
            var c = Clip(project, row, 11, 2);
            var elsewhere = Clip(project, Row(project), 9, 5);

            Assert.True(TimelineOps.CloseGap(project, TimelineOps.FindGap(project, row.Id, Sec(6)).Value));

            Assert.Equal(0, a.TimelineStartTicks);
            Assert.Equal(Sec(5), b.TimelineStartTicks);
            Assert.Equal(Sec(8), c.TimelineStartTicks); // a later gap keeps its width
            Assert.Equal(Sec(9), elsewhere.TimelineStartTicks);
        }

        [Fact]
        public void Close_gap_refits_an_exempt_clip_on_another_group_row()
        {
            // 2x over [0, 6s) ends at output 3s. Audio on the output clock at 8s is 5s of output
            // in, 2s long. Slid 3s to 5s it starts at output 2.5s and reaches output 4.5s, which
            // is 7.5s of project: 2.5s long.
            var project = NewProject();
            var group = Guid.NewGuid();
            var screen = Row(project, group);
            Clip(project, screen, 0, 5);
            Clip(project, screen, 8, 4);
            var voice = Clip(project, Row(project, group, TrackKind.Audio), 8, 2);
            ((MediaContent)voice.Content).SpeedWarpExempt = true;
            var session = new EditorSession(project, null, null);
            session.AddSpeedEffect(0, Sec(6));
            Assert.Equal(Sec(2), Live(session, voice).DurationTicks);
            var gap = session.FindGap(screen.Id, Sec(6)).Value;

            Assert.True(session.CloseGap(gap));

            Assert.Equal(Sec(5), Live(session, voice).TimelineStartTicks);
            Assert.Equal(Sec(2.5), Live(session, voice).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        // ----------------------------------------------------------------------------- ripple delete

        [Fact]
        public void Ripple_delete_cuts_the_group_rows_and_shifts_every_row()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var screen = Row(project, group);
            var left = Clip(project, screen, 0, 4);
            var target = Clip(project, screen, 4, 2);
            var right = Clip(project, screen, 6, 4);
            var audio = Clip(project, Row(project, group, TrackKind.Audio), 0, 10);
            var looseRow = Row(project);
            var looseOver = Clip(project, looseRow, 2, 6);   // [2s, 8s), under the span
            var looseLater = Clip(project, looseRow, 12, 2);

            TimelineOps.RippleDelete(project, target.Id);

            Assert.DoesNotContain(project.Items, i => i.Id == target.Id);
            Assert.Equal(0, left.TimelineStartTicks);
            Assert.Equal(Sec(4), right.TimelineStartTicks);

            // the grouped audio lost the same 2s, which is what keeps it in step
            var audioParts = project.Items.Where(i => i.TrackId == audio.TrackId).OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(2, audioParts.Count);
            Assert.Equal((0L, Sec(4)), (audioParts[0].TimelineStartTicks, audioParts[0].DurationTicks));
            Assert.Equal((Sec(4), Sec(4)), (audioParts[1].TimelineStartTicks, audioParts[1].DurationTicks));
            Assert.Equal(Sec(6), ((MediaContent)audioParts[1].Content).SourceInTicks);

            // an ungrouped row loses nothing, and only what is past the cut shifts
            Assert.Equal((Sec(2), Sec(6)), (looseOver.TimelineStartTicks, looseOver.DurationTicks));
            Assert.Equal(Sec(10), looseLater.TimelineStartTicks);
            Assert.Empty(project.Validate());
        }

        /// <summary>The cut is by row, not by where a clip came from: anything on a grouped row
        /// under the span goes, a clip that arrived there after the group formed included.</summary>
        [Fact]
        public void Ripple_delete_includes_a_clip_pasted_onto_a_grouped_row()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var target = Clip(project, Row(project, group), 4, 2);
            var otherRow = Row(project, group);
            var pasted = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = otherRow.Id,
                TimelineStartTicks = Sec(4),
                DurationTicks = Sec(2),
                Content = new TextContent { Text = "pasted" },
            };
            project.Items.Add(pasted);

            TimelineOps.RippleDelete(project, target.Id);

            Assert.Empty(project.Items);
        }

        /// <summary>An ungrouped row with a clip straddling the cut and one right behind it: the
        /// shift runs the second into the first, so the delete has to be refused, and the menu has to
        /// know before it is offered.</summary>
        [Fact]
        public void CanRippleDeleteItem_is_false_when_an_ungrouped_back_to_back_row_would_collide()
        {
            var project = NewProject();
            var target = Clip(project, Row(project), 2, 2);
            var backToBack = Row(project);
            Clip(project, backToBack, 0, 5);
            var second = Clip(project, backToBack, 5, 5);
            var free = Clip(project, Row(project), 20, 2);
            var session = new EditorSession(project, null, null);
            var before = session.Project.ToJson();

            Assert.False(session.CanRippleDeleteItem(target.Id));

            session.RippleDeleteItem(target.Id);
            Assert.Equal(before, session.Project.ToJson());
            Assert.Equal(Sec(5), Live(session, second).TimelineStartTicks);

            // the question touches nothing, and other clips can still go
            Assert.False(session.CanUndo);
            Assert.True(session.CanRippleDeleteItem(free.Id));
            Assert.True(session.CanRippleDeleteItem(second.Id));
        }

        /// <summary>
        /// Two groups over the same time. The target is the [10, 15) piece of the first group's clip.
        /// The other group's screen, cursor and mic all run to 32 s and show the same moment wherever
        /// they overlap, and start where the test says, as a trim of one of them leaves them.
        /// </summary>
        private static (EditorSession Session, Item Target, Item Screen, Item Cursor, Item Mic) RippleAcrossGroups(
            double overlaysFrom, double screenFrom, double? laterOnScreenRow = null)
        {
            var project = NewProject();
            var ownGroup = Guid.NewGuid();
            var other = Guid.NewGuid();
            var target = Clip(project, Row(project, ownGroup), 0, 40);
            Clip(project, Row(project, ownGroup, TrackKind.Audio), 0, 40);

            // source time is timeline time plus 88 s on all three
            Item Other(TrackKind kind, double start, double end, ItemContent content)
            {
                var item = Clip(project, Row(project, other, kind), start, end - start);
                item.Content = content;
                return item;
            }

            var sourceId = project.Sources[0].Id;
            var screen = Other(TrackKind.Video, screenFrom, 32, new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = Sec(screenFrom + 88) });
            var cursor = Other(TrackKind.Video, overlaysFrom, 32, new CursorContent { SourceId = sourceId, SourceInTicks = Sec(overlaysFrom + 88) });
            var mic = Other(TrackKind.Audio, overlaysFrom, 32, new MediaContent { SourceId = sourceId, StreamIndex = 1, SourceInTicks = Sec(overlaysFrom + 88) });
            if (laterOnScreenRow is { } later)
                Clip(project, project.Tracks.Single(t => t.Id == screen.TrackId), later, 2);

            var session = new EditorSession(project, null, null);
            Assert.True(session.SplitItemAt(target.Id, Sec(10)));
            var middle = session.Project.Items.Single(i => i.TrackId == target.TrackId && i.TimelineStartTicks == Sec(10));
            Assert.True(session.SplitItemAt(middle.Id, Sec(15)));
            return (session, middle, screen, cursor, mic);
        }

        private static long ClockAt(EditorSession session, Item item, long t)
        {
            var live = Live(session, item);
            return Composition.FrameComposer.SourceTimeTicks((SourceClockContent)live.Content, live, t);
        }

        /// <summary>Clips of another group starting inside the deleted span at different times take
        /// one delta, the one their earliest can move, or the clamp would put them out of step.</summary>
        [Fact]
        public void Ripple_delete_keeps_another_groups_clips_in_step_when_they_start_inside_the_span_at_different_times()
        {
            var (session, target, screen, cursor, mic) = RippleAcrossGroups(overlaysFrom: 12, screenFrom: 13);

            Assert.True(session.CanRippleDeleteItem(target.Id));
            session.RippleDeleteItem(target.Id);

            Assert.Empty(session.Project.Validate());
            Assert.Equal(Sec(10), Live(session, cursor).TimelineStartTicks);
            Assert.Equal(Sec(10), Live(session, mic).TimelineStartTicks);
            Assert.Equal(Sec(11), Live(session, screen).TimelineStartTicks);
            Assert.Equal(ClockAt(session, cursor, Sec(14)), ClockAt(session, screen, Sec(14)));
            Assert.Equal(ClockAt(session, mic, Sec(14)), ClockAt(session, screen, Sec(14)));
        }

        /// <summary>The same with the earliest clip starting before the span: it cannot move, since
        /// only what is at or past the span does, so nothing linked to it does either.</summary>
        [Fact]
        public void Ripple_delete_leaves_another_groups_linked_clips_where_they_are_when_one_starts_before_the_span()
        {
            var (session, target, screen, cursor, mic) = RippleAcrossGroups(overlaysFrom: 8, screenFrom: 12);

            Assert.True(session.CanRippleDeleteItem(target.Id));
            session.RippleDeleteItem(target.Id);

            Assert.Equal(Sec(8), Live(session, cursor).TimelineStartTicks);
            Assert.Equal(Sec(8), Live(session, mic).TimelineStartTicks);
            Assert.Equal(Sec(12), Live(session, screen).TimelineStartTicks);
            Assert.Equal(ClockAt(session, cursor, Sec(14)), ClockAt(session, screen, Sec(14)));
        }

        /// <summary>That stays refused when what follows on the row would run into the clips that
        /// stayed, and the check agrees with the edit.</summary>
        [Fact]
        public void Ripple_delete_is_refused_when_a_later_clip_runs_into_another_groups_clips_that_stayed()
        {
            var (session, target, _, _, _) = RippleAcrossGroups(overlaysFrom: 8, screenFrom: 12, laterOnScreenRow: 33);
            var before = session.Project.ToJson();

            Assert.False(session.CanRippleDeleteItem(target.Id));
            session.RippleDeleteItem(target.Id);

            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void CanRippleDeleteItem_is_true_when_the_group_takes_the_same_cut()
        {
            // the same two rows, grouped: the straddler is cut too, so nothing collides
            var project = NewProject();
            var group = Guid.NewGuid();
            var target = Clip(project, Row(project, group), 2, 2);
            var backToBack = Row(project, group);
            Clip(project, backToBack, 0, 5);
            Clip(project, backToBack, 5, 5);
            var session = new EditorSession(project, null, null);

            Assert.True(session.CanRippleDeleteItem(target.Id));

            session.RippleDeleteItem(target.Id);
            Assert.DoesNotContain(session.Project.Items, i => i.Id == target.Id);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Ripple_delete_refits_a_later_exempt_clip_sliding_under_a_speed_item()
        {
            // the numbers of Close_gap_refits_an_exempt_clip_on_another_group_row
            var project = NewProject();
            var target = Clip(project, Row(project), 0, 3);
            var voice = Clip(project, Row(project, kind: TrackKind.Audio), 8, 2);
            ((MediaContent)voice.Content).SpeedWarpExempt = true;
            var session = new EditorSession(project, null, null);
            session.AddSpeedEffect(0, Sec(6));
            Assert.Equal(Sec(2), Live(session, voice).DurationTicks);

            session.RippleDeleteItem(target.Id);

            Assert.Equal(Sec(5), Live(session, voice).TimelineStartTicks);
            Assert.Equal(Sec(2.5), Live(session, voice).DurationTicks);
        }

        // --------------------------------------------------------------------------- lone groups

        /// <summary>The mutation dissolves the group, so the row's badge goes and undo brings
        /// both back.</summary>
        [Fact]
        public void Deleting_a_partners_last_clip_dissolves_a_two_row_group()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Clip(project, Row(project, group), 0, 5);
            var b = Clip(project, Row(project, group), 0, 5);
            var session = new EditorSession(project, null, null);
            var before = session.Project.ToJson();

            session.DeleteItems(new[] { b.Id });

            Assert.All(session.Project.Tracks, t => Assert.Null(t.GroupId));
            AssertLinked(session.Project, Live(session, a), Live(session, a));

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
            Assert.Equal(group, session.Project.TrackOf(Live(session, a)).GroupId);
        }

        [Fact]
        public void A_row_a_delete_empties_goes_and_its_group_lives_on_while_two_rows_hold_clips()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Clip(project, Row(project, group), 0, 5);
            var b = Clip(project, Row(project, group), 0, 5);
            var c = Clip(project, Row(project, group), 0, 5);
            var session = new EditorSession(project, null, null);

            session.DeleteItems(new[] { c.Id });
            Assert.DoesNotContain(session.Project.Tracks, t => t.Id == c.TrackId);
            Assert.Equal(group, session.Project.TrackOf(Live(session, a)).GroupId);
            Assert.Equal(group, session.Project.TrackOf(Live(session, b)).GroupId);

            session.DeleteItems(new[] { b.Id });
            Assert.All(session.Project.Tracks, t => Assert.Null(t.GroupId));
        }

        /// <summary>A row that holds nothing (one a file was saved with, say) keeps its group id but
        /// does not count towards keeping the group alive.</summary>
        [Fact]
        public void A_row_with_no_clips_does_not_keep_a_group_alive_and_holds_its_id_while_others_do()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Clip(project, Row(project, group), 0, 5);
            var b = Clip(project, Row(project, group), 0, 5);
            var empty = Row(project, group);
            var session = new EditorSession(project, null, null);

            Assert.Equal(group, session.Project.Tracks.Single(t => t.Id == empty.Id).GroupId);
            Assert.Equal(group, session.Project.TrackOf(Live(session, a)).GroupId);

            session.DeleteItems(new[] { b.Id });
            Assert.All(session.Project.Tracks, t => Assert.Null(t.GroupId));
            Assert.Contains(session.Project.Tracks, t => t.Id == empty.Id); // not the delete's to remove
        }

        /// <summary>Any row can be grouped, and a zoom or speed clip then moves with what it
        /// overlaps. Neither the model nor a drag has an exception for them.</summary>
        [Fact]
        public void Speed_and_zoom_rows_can_be_in_a_group()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var clip = Clip(project, Row(project, group), 0, 10);
            Item Effect(ItemContent content)
            {
                var item = new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = Row(project, group, TrackKind.Effect).Id,
                    TimelineStartTicks = Sec(2),
                    DurationTicks = Sec(2),
                    Content = content,
                };
                project.Items.Add(item);
                return item;
            }

            var speed = Effect(new SpeedContent { Factor = 2 });
            var zoom = Effect(new ZoomContent { Zoom = 2, FocusX = 0.5, FocusY = 0.5 });

            Assert.Empty(project.Validate());
            AssertLinked(project, clip, clip, speed, zoom);

            var session = new EditorSession(project, null, null);
            Assert.Equal(Sec(1), session.MoveLinked(clip.Id, Sec(1)));
            Assert.Equal(Sec(3), Live(session, speed).TimelineStartTicks);
            Assert.Equal(Sec(3), Live(session, zoom).TimelineStartTicks);
        }
    }
}
