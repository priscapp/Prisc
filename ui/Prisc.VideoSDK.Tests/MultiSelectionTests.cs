using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Prisc.UI.VideoEditor.Inspector;
using Prisc.UI.VideoEditor.Timeline;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>Selecting several clips, the commands that act on them, the track-level group
    /// commands, and what the inspector shows for all of that.</summary>
    public class MultiSelectionTests
    {
        private static long Sec(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

        private static Track Row(Project project, Guid? group = null, TrackKind kind = TrackKind.Video)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = kind, Order = project.Tracks.Count, GroupId = group, Name = "Row" };
            project.Tracks.Add(track);
            return track;
        }

        private static Item Clip(Project project, Track row, double start = 0, double duration = 10)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = row.Id,
                TimelineStartTicks = Sec(start),
                DurationTicks = Sec(duration),
                Transform = new Transform { X = 0.5, Y = 0.5, Scale = 1 },
                Content = new TextContent { Text = "x", Size = 80, Color = "#FFFFFFFF" },
            };
            project.Items.Add(item);
            return item;
        }

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
        };

        private static Guid? GroupOf(EditorSession session, Track row) =>
            session.Project.Tracks.Single(t => t.Id == row.Id).GroupId;

        private static IEnumerable<Guid> Ids(params Track[] rows) => rows.Select(t => t.Id);

        // ------------------------------------------------------------------------------ selection

        [Fact]
        public void Toggling_adds_and_removes_and_the_sole_item_is_null_unless_exactly_one()
        {
            var project = NewProject();
            var a = Clip(project, Row(project));
            var b = Clip(project, Row(project));
            var session = new EditorSession(project, null, null);
            var events = 0;
            session.SelectionChanged += (_, _) => events++;

            Assert.Null(session.SoleSelectedItem);

            session.ToggleSelected(a.Id);
            Assert.Equal(a.Id, session.SoleSelectedItem?.Id);
            Assert.True(session.IsSelected(a.Id));

            session.ToggleSelected(b.Id);
            Assert.Null(session.SoleSelectedItem);
            Assert.Equal(new[] { a.Id, b.Id }.OrderBy(i => i), session.SelectedItemIds.OrderBy(i => i));

            session.ToggleSelected(a.Id);
            Assert.Equal(b.Id, session.SoleSelectedItem?.Id);
            Assert.False(session.IsSelected(a.Id));

            session.ToggleSelected(b.Id);
            Assert.Empty(session.SelectedItemIds);
            Assert.Equal(4, events);
            Assert.False(session.CanUndo); // no undo entry for any of it
        }

        [Fact]
        public void Select_replaces_a_multi_selection_even_with_one_of_its_own()
        {
            var project = NewProject();
            var a = Clip(project, Row(project));
            var b = Clip(project, Row(project));
            var session = new EditorSession(project, null, null);
            session.SelectMany(new[] { a.Id, b.Id });

            session.Select(a.Id);

            Assert.Equal(new[] { a.Id }, session.SelectedItemIds);
        }

        [Fact]
        public void SelectMany_ignores_unknown_ids_and_says_nothing_when_nothing_changes()
        {
            var project = NewProject();
            var a = Clip(project, Row(project));
            var b = Clip(project, Row(project));
            var session = new EditorSession(project, null, null);
            var events = 0;
            session.SelectionChanged += (_, _) => events++;

            session.SelectMany(new[] { a.Id, b.Id, Guid.NewGuid() });
            Assert.Equal(new[] { a.Id, b.Id }.OrderBy(i => i), session.SelectedItemIds.OrderBy(i => i));
            Assert.Equal(1, events);

            session.SelectMany(new[] { b.Id, a.Id });
            Assert.Equal(1, events);
        }

        /// <summary>Every way the selection changes ends a coalescing run: two edits with one key,
        /// inside the window, either side of it are two undo steps.</summary>
        [Theory]
        [InlineData("Select")]
        [InlineData("ToggleSelected")]
        [InlineData("SelectMany")]
        [InlineData("ClearSelection")]
        public void A_selection_change_ends_a_coalescing_run(string change)
        {
            var project = NewProject();
            var a = Clip(project, Row(project));
            var b = Clip(project, Row(project));
            var session = new EditorSession(project, null, null) { Clock = () => 0 };
            session.Select(a.Id);

            session.EditItem(a.Id, i => i.Transform.X = 0.1, "key");
            Action act = change switch
            {
                "Select" => () => session.Select(b.Id),
                "ToggleSelected" => () => session.ToggleSelected(b.Id),
                "SelectMany" => () => session.SelectMany(new[] { a.Id, b.Id }),
                _ => session.ClearSelection,
            };
            act();
            session.EditItem(a.Id, i => i.Transform.X = 0.2, "key");

            session.Undo();
            Assert.Equal(0.1, session.Project.Items.Single(i => i.Id == a.Id).Transform.X);
            session.Undo();
            Assert.Equal(0.5, session.Project.Items.Single(i => i.Id == a.Id).Transform.X);
        }

        [Fact]
        public void A_dead_id_leaves_a_sole_item_on_commit_and_stays_out_after_undo()
        {
            var project = NewProject();
            var a = Clip(project, Row(project));
            var session = new EditorSession(project, null, null);

            session.Select(a.Id);
            session.ToggleSelected(Guid.NewGuid());
            Assert.Null(session.SoleSelectedItem);

            session.EditItem(a.Id, i => i.Transform.X = 0.1);
            Assert.Equal(a.Id, session.SoleSelectedItem?.Id);

            session.Undo();
            Assert.Equal(new[] { a.Id }, session.SelectedItemIds);
            Assert.Equal(a.Id, session.SoleSelectedItem?.Id);
        }

        [Fact]
        public void SelectedTrackIds_is_each_row_once_lowest_order_first()
        {
            var project = NewProject();
            var low = Row(project);
            var high = Row(project);
            var a = Clip(project, high, 0, 1);
            var b = Clip(project, high, 2, 1);
            var c = Clip(project, low);
            Clip(project, Row(project));
            var session = new EditorSession(project, null, null);

            session.SelectMany(new[] { a.Id, b.Id, c.Id });

            Assert.Equal(Ids(low, high), session.SelectedTrackIds);
        }

        [Fact]
        public void Select_linked_clips_extends_the_selection_to_what_a_drag_would_bring()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Clip(project, Row(project, group));
            var other = Row(project, group);
            var linked = Clip(project, other, 5, 10);
            var apart = Clip(project, other, 30, 1);
            var loose = Clip(project, Row(project));
            var session = new EditorSession(project, null, null);

            session.Select(loose.Id);
            Assert.False(session.HasLinkedClips);

            session.Select(a.Id);
            Assert.True(session.HasLinkedClips);
            session.SelectLinkedClips();

            Assert.Equal(new[] { a.Id, linked.Id }.OrderBy(i => i), session.SelectedItemIds.OrderBy(i => i));
            Assert.False(session.HasLinkedClips);
            Assert.False(session.IsSelected(apart.Id));
        }

        // --------------------------------------------------------------------------------- delete

        [Fact]
        public void Deleting_several_is_one_undo_entry_and_restores_rows_clips_and_selection()
        {
            var project = NewProject();
            var kept = Clip(project, Row(project));
            var a = Clip(project, Row(project));
            var b = Clip(project, Row(project));
            var session = new EditorSession(project, null, null);
            var added = session.AddText(Sec(20), Sec(2));
            var before = session.Project.ToJson();
            session.SelectMany(new[] { a.Id, b.Id, added.Id });

            session.DeleteItems(session.SelectedItemIds);

            Assert.Equal(new[] { kept.Id }, session.Project.Items.Select(i => i.Id));
            Assert.Empty(session.SelectedItemIds);
            // every row the delete emptied goes with its last clip, whatever made it
            Assert.Equal(new[] { kept.TrackId }, session.Project.Tracks.Select(t => t.Id));

            session.Undo();

            Assert.Equal(before, session.Project.ToJson());
            Assert.Equal(new[] { a.Id, b.Id, added.Id }.OrderBy(i => i), session.SelectedItemIds.OrderBy(i => i));
            session.Undo();
            Assert.False(session.CanUndo); // that was the add: delete took exactly one step
        }

        [Fact]
        public void Deleting_ignores_ids_that_are_gone_and_nothing_deleted_is_no_undo_entry()
        {
            var project = NewProject();
            var a = Clip(project, Row(project));
            var session = new EditorSession(project, null, null);

            session.DeleteItems(new[] { Guid.NewGuid() });
            Assert.False(session.CanUndo);

            session.DeleteItems(new[] { a.Id, Guid.NewGuid() });
            Assert.Empty(session.Project.Items);
        }

        // ---------------------------------------------------------------------------------- group

        [Fact]
        public void Grouping_rows_with_no_group_mints_one_and_reports_it()
        {
            var project = NewProject();
            var a = Row(project);
            var b = Row(project);
            var c = Row(project);
            Clip(project, a);
            Clip(project, b);
            Clip(project, c);
            var session = new EditorSession(project, null, null);
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);
            var kinds = new List<ProjectChangeKind>();
            session.ProjectChanged += (_, e) => kinds.Add(e.Kind);

            session.GroupTracks(Ids(a, b));

            Assert.NotNull(GroupOf(session, a));
            Assert.Equal(GroupOf(session, a), GroupOf(session, b));
            Assert.Null(GroupOf(session, c));
            Assert.Equal(new[] { "Grouped 2 tracks." }, status);
            // the header badges re-read on Mapping, and a group never reaches the player
            Assert.Equal(new[] { ProjectChangeKind.Mapping }, kinds);

            session.Undo();
            Assert.Null(GroupOf(session, a));
            Assert.False(session.CanUndo);
        }

        /// <summary>Grouping never removes a row from a group: the rows selected on two groups bring
        /// their partners, and the group of the lowest-order selected row that has one survives.</summary>
        [Fact]
        public void Grouping_merges_every_group_it_touches_keeping_the_lowest_selected_rows_id()
        {
            var project = NewProject();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var loose = Row(project);
            var one = Row(project, second);
            var onePartner = Row(project, second);
            var two = Row(project, first);
            var twoPartner = Row(project, first);
            var unrelated = Row(project);
            foreach (var row in project.Tracks.ToList())
                Clip(project, row);
            var session = new EditorSession(project, null, null);
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);

            session.GroupTracks(Ids(loose, two, one));

            foreach (var row in new[] { loose, one, onePartner, two, twoPartner })
                Assert.Equal(second, GroupOf(session, row));
            Assert.Null(GroupOf(session, unrelated));
            Assert.Equal(new[] { "Grouped 5 tracks." }, status);
            Assert.Empty(session.Project.Validate());
        }

        /// <summary>By order, not by where the rows happen to sit in the list.</summary>
        [Fact]
        public void The_lowest_order_decides_which_group_survives_a_merge()
        {
            var kept = Guid.NewGuid();
            var absorbed = Guid.NewGuid();
            var high = new Track { Id = Guid.NewGuid(), Order = 5, GroupId = absorbed };
            var low = new Track { Id = Guid.NewGuid(), Order = 1, GroupId = kept };
            var project = new Project { Tracks = { high, low } };

            TimelineOps.MergeIntoGroup(project, Ids(high, low));

            Assert.Equal(kept, high.GroupId);
            Assert.Equal(kept, low.GroupId);
        }

        [Fact]
        public void Extending_a_group_keeps_its_id()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Row(project);
            var b = Row(project, group);
            var c = Row(project, group);
            foreach (var row in project.Tracks.ToList())
                Clip(project, row);
            var session = new EditorSession(project, null, null);

            Assert.True(session.CanGroupTracks(Ids(a, b)));
            session.GroupTracks(Ids(a, b));

            Assert.Equal(group, GroupOf(session, a));
            Assert.Equal(group, GroupOf(session, c));
        }

        [Fact]
        public void Grouping_needs_two_known_rows_not_already_one_group()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Row(project, group);
            var b = Row(project, group);
            var c = Row(project, group);
            var loose = Row(project);
            var loose2 = Row(project);
            foreach (var row in project.Tracks.ToList())
                Clip(project, row);
            var session = new EditorSession(project, null, null);
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);

            Assert.False(session.CanGroupTracks(Array.Empty<Guid>()));
            Assert.False(session.CanGroupTracks(Ids(loose)));
            Assert.False(session.CanGroupTracks(new[] { loose.Id, Guid.NewGuid() }));
            Assert.False(session.CanGroupTracks(Ids(a, b))); // part of one group is still one group
            Assert.False(session.CanGroupTracks(Ids(a, b, c)));
            Assert.True(session.CanGroupTracks(Ids(loose, loose2)));
            Assert.True(session.CanGroupTracks(Ids(a, loose)));

            session.GroupTracks(Ids(a, b));

            Assert.False(session.CanUndo);
            Assert.Empty(status);
        }

        [Fact]
        public void Speed_and_zoom_rows_can_be_grouped_by_the_command()
        {
            var project = NewProject();
            var video = Row(project);
            Clip(project, video);
            var session = new EditorSession(project, null, null);
            var speed = session.AddSpeedEffect(Sec(1), Sec(2));
            var zoom = session.AddZoomEffect(Sec(4), Sec(2));
            var rows = new[] { video.Id, speed.TrackId, zoom.TrackId };

            Assert.True(session.CanGroupTracks(rows));
            session.GroupTracks(rows);

            var groups = session.Project.Tracks.Where(t => rows.Contains(t.Id)).Select(t => t.GroupId).Distinct();
            Assert.NotNull(Assert.Single(groups));
            Assert.True(session.CanUndo);
            Assert.Empty(session.Project.Validate());
        }

        // -------------------------------------------------------------------------------- ungroup

        [Fact]
        public void Ungrouping_takes_out_only_the_rows_named()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Row(project, group);
            var b = Row(project, group);
            var c = Row(project, group);
            var loose = Row(project);
            foreach (var row in project.Tracks.ToList())
                Clip(project, row);
            var session = new EditorSession(project, null, null);
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);

            session.UngroupTracks(Ids(a, loose));

            Assert.Null(GroupOf(session, a));
            Assert.Equal(group, GroupOf(session, b));
            Assert.Equal(group, GroupOf(session, c));
            // the row with no group was no part of it, so the count is honest
            Assert.Equal(new[] { "Ungrouped 1 track." }, status);
        }

        [Fact]
        public void A_group_left_with_one_row_holding_clips_dissolves_on_all_its_rows()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Row(project, group);
            var b = Row(project, group);
            var empty = Row(project, group);
            Clip(project, a);
            Clip(project, b);
            var session = new EditorSession(project, null, null);

            Assert.Equal(2, TimelineOps.OccupiedGroupRows(session.Project, empty.Id));

            session.UngroupTracks(Ids(a));

            Assert.Null(GroupOf(session, a));
            Assert.Null(GroupOf(session, b));
            Assert.Null(GroupOf(session, empty));
        }

        [Fact]
        public void An_empty_row_keeps_no_group_alive_for_the_two_that_still_count()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Row(project, group);
            var b = Row(project, group);
            var c = Row(project, group);
            Clip(project, a);
            Clip(project, b);
            var session = new EditorSession(project, null, null); // c holds nothing

            Assert.Equal(group, GroupOf(session, a));
            Assert.Equal(group, GroupOf(session, c));
            Assert.Equal(2, TimelineOps.OccupiedGroupRows(session.Project, a.Id));
            // no badge on the row that holds nothing, and the other rows' count leaves it out
            Assert.True(TimelineViewMath.ShowsGroupBadge(session.Project, session.Project.Tracks.Single(t => t.Id == a.Id)));
            Assert.False(TimelineViewMath.ShowsGroupBadge(session.Project, session.Project.Tracks.Single(t => t.Id == c.Id)));
        }

        [Fact]
        public void Deleting_a_partners_last_clip_clears_the_other_rows_badge()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Row(project, group);
            var b = Row(project, group);
            Clip(project, a);
            var partner = Clip(project, b);
            var session = new EditorSession(project, null, null);
            Assert.True(TimelineViewMath.ShowsGroupBadge(session.Project, session.Project.Tracks.Single(t => t.Id == a.Id)));

            session.DeleteItems(new[] { partner.Id });

            Assert.False(TimelineViewMath.ShowsGroupBadge(session.Project, session.Project.Tracks.Single(t => t.Id == a.Id)));
        }

        [Fact]
        public void Ungroup_all_dissolves_each_named_rows_whole_group_and_no_other()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var other = Guid.NewGuid();
            var a = Row(project, group);
            var b = Row(project, group);
            var empty = Row(project, group);
            var c = Row(project, other);
            var d = Row(project, other);
            foreach (var row in new[] { a, b, c, d })
                Clip(project, row);
            var session = new EditorSession(project, null, null);
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);

            session.UngroupAll(Ids(a));

            Assert.Null(GroupOf(session, a));
            Assert.Null(GroupOf(session, b));
            Assert.Null(GroupOf(session, empty));
            Assert.Equal(other, GroupOf(session, c));
            Assert.Equal(new[] { "Ungrouped 2 tracks." }, status);

            session.Undo();
            Assert.Equal(group, GroupOf(session, b));
        }

        [Fact]
        public void Ungrouping_rows_with_no_group_is_no_edit()
        {
            var project = NewProject();
            var loose = Row(project);
            Clip(project, loose);
            var session = new EditorSession(project, null, null);
            var status = new List<string>();
            session.StatusRaised += (_, text) => status.Add(text);

            Assert.False(session.CanUngroupTracks(Ids(loose)));
            session.UngroupTracks(Ids(loose));
            session.UngroupAll(Ids(loose));

            Assert.False(session.CanUndo);
            Assert.Empty(status);
        }

        // ------------------------------------------------------------------------------- numbering

        [Fact]
        public void Groups_are_numbered_by_their_lowest_row_and_follow_the_order()
        {
            var project = NewProject();
            var low = Guid.NewGuid();
            var high = Guid.NewGuid();
            var a = new Track { Id = Guid.NewGuid(), Order = 2, GroupId = high };
            var b = new Track { Id = Guid.NewGuid(), Order = 0 };
            var c = new Track { Id = Guid.NewGuid(), Order = 1, GroupId = low };
            var d = new Track { Id = Guid.NewGuid(), Order = 3, GroupId = low };
            project.Tracks.AddRange(new[] { a, b, c, d });

            var numbers = TimelineViewMath.GroupNumbers(project);
            Assert.Equal(0, numbers[low]);
            Assert.Equal(1, numbers[high]);
            Assert.Equal(2, numbers.Count);

            // reordered, the colors may shift
            a.Order = 0;
            b.Order = 2;
            numbers = TimelineViewMath.GroupNumbers(project);
            Assert.Equal(0, numbers[high]);
            Assert.Equal(1, numbers[low]);
        }

        [Fact]
        public void Six_groups_get_six_numbers()
        {
            var project = NewProject();
            for (var i = 0; i < 7; i++)
                project.Tracks.Add(new Track { Id = Guid.NewGuid(), Order = i, GroupId = Guid.NewGuid() });

            var slots = TimelineViewMath.GroupNumbers(project).Values.Select(n => n % TimelineViewMath.GroupColorCount).ToList();

            Assert.Equal(6, slots.Take(6).Distinct().Count());
            Assert.Equal(slots[0], slots[6]); // the seventh is the first to share
        }

        // -------------------------------------------------------------------------------- inspector

        private static (EditorSession Session, SelectedItemViewModel Vm, Track A, Track B, Track C, Item ClipA, Item ClipB, Item ClipC) NewInspector()
        {
            var project = NewProject();
            var group = Guid.NewGuid();
            var a = Row(project, group);
            var b = Row(project, group);
            var c = Row(project);
            var clipA = Clip(project, a);
            var clipB = Clip(project, b);
            var clipC = Clip(project, c);
            var session = new EditorSession(project, null, null);
            return (session, new SelectedItemViewModel { Session = session }, a, b, c, clipA, clipB, clipC);
        }

        [Fact]
        public void Several_clips_show_a_count_and_no_item_sections()
        {
            var (session, vm, _, _, _, clipA, clipB, clipC) = NewInspector();

            session.SelectMany(new[] { clipA.Id, clipB.Id, clipC.Id });

            Assert.True(vm.ShowMultiSelection);
            Assert.False(vm.ShowNothingSelected);
            Assert.False(vm.ShowProperties);
            Assert.False(vm.HasSelection);
            Assert.False(vm.ShowTransform);
            Assert.False(vm.ShowText);
            Assert.False(vm.ShowSpeed);
            Assert.Equal("3 clips selected", vm.MultiSelectionTitle);
            Assert.Equal("on 3 tracks", vm.MultiSelectionCaption);

            session.SelectMany(new[] { clipA.Id, clipA.Id });
            Assert.False(vm.ShowMultiSelection);
            Assert.True(vm.ShowProperties);

            session.ClearSelection();
            Assert.True(vm.ShowNothingSelected);
            Assert.False(vm.ShowMultiSelection);
        }

        [Fact]
        public void Several_clips_on_one_row_read_as_one_track()
        {
            var project = NewProject();
            var row = Row(project);
            var a = Clip(project, row, 0, 1);
            var b = Clip(project, row, 2, 1);
            var session = new EditorSession(project, null, null);
            var vm = new SelectedItemViewModel { Session = session };

            session.SelectMany(new[] { a.Id, b.Id });

            Assert.Equal("2 clips selected", vm.MultiSelectionTitle);
            Assert.Equal("on 1 track", vm.MultiSelectionCaption);
        }

        /// <summary>The hazard the sole item closes: a spinner must not silently reach one arbitrary
        /// member of a multi-selection.</summary>
        [Fact]
        public void An_edit_through_the_inspector_reaches_nothing_with_several_selected()
        {
            var (session, vm, _, _, _, clipA, clipB, _) = NewInspector();
            session.Select(clipA.Id);
            session.ToggleSelected(clipB.Id);
            var before = session.Project.ToJson();

            vm.PositionX = 0.1;

            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void The_group_commands_act_on_every_selected_row()
        {
            var (session, vm, a, b, c, clipA, clipB, clipC) = NewInspector();
            ICommand group = vm.CommandGroupTracks, ungroup = vm.CommandUngroup, all = vm.CommandUngroupAll;

            session.SelectMany(new[] { clipA.Id, clipC.Id });
            Assert.True(group.CanExecute(null));
            Assert.True(ungroup.CanExecute(null));
            Assert.True(all.CanExecute(null));

            group.Execute(null);
            Assert.Equal(GroupOf(session, a), GroupOf(session, c));
            Assert.False(group.CanExecute(null)); // now one group

            ungroup.Execute(null);
            Assert.Null(GroupOf(session, a));
            Assert.Null(GroupOf(session, c));
            Assert.Null(GroupOf(session, b)); // alone, so it dissolved
            Assert.False(ungroup.CanExecute(null));
            Assert.False(all.CanExecute(null));

            session.Undo();
            session.SelectMany(new[] { clipA.Id, clipB.Id });
            all.Execute(null);
            Assert.Null(GroupOf(session, c));
        }

        [Fact]
        public void A_can_execute_change_is_announced_as_the_selection_moves()
        {
            var (session, vm, _, _, _, clipA, _, clipC) = NewInspector();
            var raised = 0;
            vm.CommandGroupTracks.CanExecuteChanged += (_, _) => raised++;

            session.SelectMany(new[] { clipA.Id, clipC.Id });

            Assert.True(raised > 0);
        }

        [Fact]
        public void The_track_section_says_what_the_row_is_grouped_with()
        {
            var (session, vm, a, _, _, clipA, _, clipC) = NewInspector();

            session.Select(clipA.Id);
            Assert.True(vm.IsGrouped);
            Assert.False(vm.IsNotGrouped);
            Assert.Equal("Grouped with 1 other track", vm.TrackGroupText);

            session.Select(clipC.Id);
            Assert.False(vm.IsGrouped);
            Assert.True(vm.IsNotGrouped);
            Assert.Equal(TimelineViewMath.NotGroupedCaption(OperatingSystem.IsMacOS()), vm.NotGroupedCaption);

            session.GroupTracks(new[] { a.Id, session.Project.TrackOf(clipC).Id });
            Assert.True(vm.IsGrouped);
            Assert.Equal("Grouped with 2 other tracks", vm.TrackGroupText);
        }

        [Fact]
        public void The_track_section_counts_only_rows_holding_clips_and_wears_the_groups_number()
        {
            var project = NewProject();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            Clip(project, Row(project, first));
            Clip(project, Row(project, first));
            var a = Clip(project, Row(project, second));
            Clip(project, Row(project, second));
            Row(project, second); // empty
            var session = new EditorSession(project, null, null);
            var vm = new SelectedItemViewModel { Session = session };

            session.Select(a.Id);

            Assert.Equal("Grouped with 1 other track", vm.TrackGroupText);
            Assert.Equal(1, vm.TrackGroupNumber);
        }
    }
}
