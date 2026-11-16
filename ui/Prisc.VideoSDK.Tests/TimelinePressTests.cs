using System;
using System.IO;
using System.Linq;
using Avalonia.Input;
using Prisc.UI.VideoEditor;
using Prisc.UI.VideoEditor.Timeline;
using Prisc.VideoSDK.Model;
using Xunit;
using Drag = Prisc.UI.VideoEditor.Timeline.TimelinePressDrag;
using Sel = Prisc.UI.VideoEditor.Timeline.TimelinePressSelection;
using Target = Prisc.UI.VideoEditor.Timeline.TimelinePressTarget;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>The timeline's press rules (design 2.1), as a table of the pure decision the surface
    /// carries out.</summary>
    public class TimelinePressTests
    {
        private const KeyModifiers None = KeyModifiers.None;
        private const KeyModifiers Ctrl = KeyModifiers.Control;
        private const KeyModifiers Cmd = KeyModifiers.Meta;
        private const KeyModifiers Shift = KeyModifiers.Shift;
        private const KeyModifiers Alt = KeyModifiers.Alt;

        /// <summary>
        /// The press handler and the menu are Avalonia-bound, and no window runs in these tests, so
        /// this reads the source: a toggle press has to reach <see cref="TimelinePress.Decide"/>
        /// through the chrome over the clips (design 2.1), and the clip menu's Cut has to grey out
        /// when the session would refuse it, like Paste beside it. The real-UI run checks the rest.
        /// </summary>
        [Fact]
        public void The_surface_lets_a_toggle_press_through_its_chrome_and_asks_before_offering_Cut()
        {
            var ui = new DirectoryInfo(AppContext.BaseDirectory);
            while (!Directory.Exists(Path.Combine(ui.FullName, "Prisc.Ui")))
                ui = ui.Parent;
            var source = File.ReadAllText(Path.Combine(ui.FullName, "Prisc.Ui", "VideoEditor", "Timeline", "TimelineSurface.cs"));

            Assert.Contains("var toggling = TimelinePress.IsToggleModifier(e.KeyModifiers, OperatingSystem.IsMacOS());", source);
            Assert.Contains("if (!toggling && HitJump(pos) is { } jump)", source);
            Assert.Contains("if (!toggling && hit.Kind == TimelineHitKind.Empty && GapAt(pos) is { } gap", source);
            Assert.Contains("ClipCountLabel(\"Cut\", selectionCount), _session.CanCut,", source);
        }

        [Theory]
        // a plain press on an unselected clip replaces, whatever was selected
        [InlineData(Target.ClipBody, false, 0, None, Sel.Replace, Drag.Move, false, false, false)]
        [InlineData(Target.ClipBody, false, 3, None, Sel.Replace, Drag.Move, false, false, false)]
        // a press on a selected clip keeps the set, and only a set has anything to collapse
        [InlineData(Target.ClipBody, true, 1, None, Sel.Keep, Drag.Move, false, false, false)]
        [InlineData(Target.ClipBody, true, 3, None, Sel.Keep, Drag.Move, true, false, false)]
        // an edge trims: selection kept if the clip was in it, otherwise replaced
        [InlineData(Target.ClipEdge, false, 3, None, Sel.Replace, Drag.Trim, false, false, false)]
        [InlineData(Target.ClipEdge, true, 1, None, Sel.Keep, Drag.Trim, false, false, false)]
        [InlineData(Target.ClipEdge, true, 3, None, Sel.Keep, Drag.Trim, true, false, false)]
        // Ctrl or Shift toggles a clip, body or edge, with no drag of any kind
        [InlineData(Target.ClipBody, false, 2, Ctrl, Sel.Toggle, Drag.None, false, false, false)]
        [InlineData(Target.ClipBody, true, 2, Ctrl, Sel.Toggle, Drag.None, false, false, false)]
        [InlineData(Target.ClipEdge, false, 2, Ctrl, Sel.Toggle, Drag.None, false, false, false)]
        [InlineData(Target.ClipEdge, true, 2, Shift, Sel.Toggle, Drag.None, false, false, false)]
        [InlineData(Target.ClipBody, false, 2, Shift, Sel.Toggle, Drag.None, false, false, false)]
        // ...and on empty space does nothing: no clear, no scrub
        [InlineData(Target.Empty, false, 2, Ctrl, Sel.Keep, Drag.None, false, false, false)]
        [InlineData(Target.Empty, false, 2, Shift, Sel.Keep, Drag.None, false, false, false)]
        // empty space clears and scrubs, and Alt means nothing there
        [InlineData(Target.Empty, false, 2, None, Sel.Clear, Drag.Scrub, false, false, false)]
        [InlineData(Target.Empty, false, 2, Alt, Sel.Clear, Drag.Scrub, false, false, false)]
        // Alt at the press on a body: the selection alone moves, and snapping stays armed
        [InlineData(Target.ClipBody, false, 2, Alt, Sel.Replace, Drag.Move, false, true, true)]
        [InlineData(Target.ClipBody, true, 2, Alt, Sel.Keep, Drag.Move, true, true, true)]
        // Alt on an edge has no meaning at the press
        [InlineData(Target.ClipEdge, true, 1, Alt, Sel.Keep, Drag.Trim, false, false, false)]
        // a toggle beats Alt: the press never starts a move
        [InlineData(Target.ClipBody, false, 2, Ctrl | Alt, Sel.Toggle, Drag.None, false, false, false)]
        public void A_press_decides(Target target, bool selected, int count, KeyModifiers modifiers,
            Sel selection, Drag drag, bool collapse, bool selectionOnly, bool keepSnap)
        {
            Assert.Equal(new TimelinePressDecision(selection, drag, collapse, selectionOnly, keepSnap),
                TimelinePress.Decide(target, selected, count, modifiers, isMacOS: false));
        }

        /// <summary>Cmd stands in for Ctrl on macOS, where Ctrl+click is the context menu and never
        /// reaches the decision as a plain press.</summary>
        [Theory]
        [InlineData(Cmd, true, true)]
        [InlineData(Ctrl, true, false)]
        [InlineData(Shift, true, true)]
        [InlineData(Ctrl, false, true)]
        [InlineData(Cmd, false, false)]
        [InlineData(Shift, false, true)]
        [InlineData(Alt, false, false)]
        [InlineData(None, true, false)]
        public void The_toggle_modifier_is_the_platforms(KeyModifiers modifiers, bool isMacOS, bool toggles)
        {
            Assert.Equal(toggles, TimelinePress.IsToggleModifier(modifiers, isMacOS));
            Assert.Equal(toggles ? Sel.Toggle : Sel.Replace,
                TimelinePress.Decide(Target.ClipBody, false, 0, modifiers, isMacOS).Selection);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(2.9, false)]
        [InlineData(3, false)] // "past" 3 px, not at it
        [InlineData(3.01, true)]
        [InlineData(-2.9, false)]
        [InlineData(-3.01, true)]
        public void A_move_needs_to_pass_three_pixels(double deltaX, bool past) =>
            Assert.Equal(past, TimelinePress.IsPastMoveThreshold(deltaX));

        [Theory]
        [InlineData(TimelinePressDrag.Move, 0, false)]
        [InlineData(TimelinePressDrag.Move, 3, false)] // jitter is a click
        [InlineData(TimelinePressDrag.Move, -3, false)]
        [InlineData(TimelinePressDrag.Move, 3.5, true)]
        [InlineData(TimelinePressDrag.Move, -3.5, true)]
        [InlineData(TimelinePressDrag.Trim, 0, true)] // any step of a trim counts
        [InlineData(TimelinePressDrag.Trim, 1, true)]
        public void A_pointer_step_engages_a_move_past_the_threshold_and_a_trim_at_once(TimelinePressDrag drag, double deltaX, bool engages) =>
            Assert.Equal(engages, TimelinePress.Engages(drag, deltaX));

        [Theory]
        [InlineData(true, false, true)] // a click, or jitter under the threshold, on a selected clip
        [InlineData(true, true, false)] // it dragged: the set is what moved
        [InlineData(false, false, false)] // the press never kept a set
        [InlineData(false, true, false)]
        public void A_release_collapses_only_when_the_press_asked_and_the_pointer_did_nothing(bool asked, bool engaged, bool collapses)
        {
            var press = new TimelinePressDecision(TimelinePressSelection.Keep, TimelinePressDrag.Move, CollapseOnRelease: asked);
            Assert.Equal(collapses, TimelinePress.CollapsesOnRelease(press, engaged));
        }

        [Fact]
        public void A_press_on_a_selected_edge_collapses_until_any_step_of_the_trim()
        {
            var press = TimelinePress.Decide(TimelinePressTarget.ClipEdge, clipSelected: true, selectionCount: 2, KeyModifiers.None, isMacOS: false);

            Assert.True(TimelinePress.CollapsesOnRelease(press, engaged: false));
            Assert.False(TimelinePress.CollapsesOnRelease(press, TimelinePress.Engages(press.Drag, 0)));
        }

        [Fact]
        public void Alt_held_through_the_press_keeps_snapping_until_it_has_been_released_once()
        {
            var keepSnap = TimelinePress.Decide(Target.ClipBody, false, 0, Alt, false).KeepSnapUntilAltUp;

            Assert.False(TimelinePress.BypassesSnap(true, ref keepSnap));
            Assert.False(TimelinePress.BypassesSnap(true, ref keepSnap)); // still held: still snaps

            Assert.False(TimelinePress.BypassesSnap(false, ref keepSnap)); // up: the flag goes
            Assert.False(keepSnap);

            Assert.True(TimelinePress.BypassesSnap(true, ref keepSnap)); // down again: the usual bypass
            Assert.False(TimelinePress.BypassesSnap(false, ref keepSnap)); // and up: snapping again
        }

        [Fact]
        public void Alt_pressed_during_a_plain_drag_bypasses_at_once()
        {
            var keepSnap = TimelinePress.Decide(Target.ClipBody, false, 0, None, false).KeepSnapUntilAltUp;

            Assert.False(TimelinePress.BypassesSnap(false, ref keepSnap));
            Assert.True(TimelinePress.BypassesSnap(true, ref keepSnap));
        }

        [Fact]
        public void Alt_on_an_edge_bypasses_snapping_from_the_first_step()
        {
            var keepSnap = TimelinePress.Decide(Target.ClipEdge, false, 0, Alt, false).KeepSnapUntilAltUp;

            Assert.True(TimelinePress.BypassesSnap(true, ref keepSnap));
        }

        [Fact]
        public void A_move_takes_what_is_linked_unless_alt_said_the_selection_alone()
        {
            var group = Guid.NewGuid();
            var a = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, GroupId = group };
            var b = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 1, GroupId = group };
            Item Clip(Track row, long start, long duration) => new Item
            {
                Id = Guid.NewGuid(),
                TrackId = row.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = new TextContent { Text = "x" },
            };

            var selected = Clip(a, 0, 100);
            var overlapping = Clip(b, 50, 100);
            var elsewhere = Clip(b, 500, 100);
            var project = new Project { Tracks = { a, b }, Items = { selected, overlapping, elsewhere } };

            Assert.Equal(new[] { selected.Id, overlapping.Id }.OrderBy(i => i),
                TimelinePress.MoveSet(project, new[] { selected.Id }, selectionOnly: false).OrderBy(i => i));
            Assert.Equal(new[] { selected.Id },
                TimelinePress.MoveSet(project, new[] { selected.Id }, selectionOnly: true));
        }

        // ---------------------------------------------------------------------------- command gate

        [Theory]
        // a text field keeps every key, edit or not
        [InlineData(true, false, false, false, false)]
        [InlineData(true, true, false, false, false)]
        // anything else runs...
        [InlineData(false, false, false, false, true)]
        [InlineData(false, true, false, false, true)]
        // ...and a gesture or a voice take stops edits, not the rest
        [InlineData(false, true, true, false, false)]
        [InlineData(false, true, false, true, false)]
        [InlineData(false, false, true, false, true)]
        [InlineData(false, false, false, true, true)]
        public void The_key_gate_allows(bool textFocused, bool mutates, bool gesture, bool voiceTake, bool allowed) =>
            Assert.Equal(allowed, GatedKeyCommand.Allows(textFocused, mutates, gesture, voiceTake));

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(false, true, false)] // the unhandled key is what reaches the text box
        [InlineData(true, false, false)]
        public void A_gated_command_needs_both_the_gate_and_its_own_can_execute(bool gate, bool inner, bool expected)
        {
            var command = new UI.Helpers.RelayCommand { CanExecute = _ => inner };

            Assert.Equal(expected, new GatedKeyCommand(command, () => gate).CanExecute(null));
        }

        [Fact]
        public void A_gated_command_runs_and_announces_through_the_one_it_wraps()
        {
            var runs = 0;
            var command = new UI.Helpers.RelayCommand { Executed = _ => runs++ };
            var gated = new GatedKeyCommand(command, () => true);
            var raised = 0;
            gated.CanExecuteChanged += (_, _) => raised++;

            gated.Execute(null);
            command.RaiseCanExecuteChanged();

            Assert.Equal(1, runs);
            Assert.Equal(1, raised);
        }
    }
}
