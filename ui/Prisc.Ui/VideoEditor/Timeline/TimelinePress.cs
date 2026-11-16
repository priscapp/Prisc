using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using Prisc.VideoSDK.Model;

namespace Prisc.UI.VideoEditor.Timeline
{
    /// <summary>What a left press landed on, as far as the press decision cares.</summary>
    public enum TimelinePressTarget
    {
        Empty,
        ClipBody,
        ClipEdge,
    }

    public enum TimelinePressSelection
    {
        Keep,
        Clear,

        /// <summary>The pressed clip becomes the whole selection.</summary>
        Replace,

        /// <summary>The pressed clip goes in or out of the selection.</summary>
        Toggle,
    }

    public enum TimelinePressDrag
    {
        None,
        Scrub,
        Move,
        Trim,
    }

    /// <summary>What a left press does.</summary>
    /// <param name="Selection">What happens to the selection, at the press.</param>
    /// <param name="Drag">What the pointer drives until the release.</param>
    /// <param name="CollapseOnRelease">A release without movement leaves the pressed clip as the
    /// only selection. The press itself had to keep the set, since it may yet drag all of it.</param>
    /// <param name="MoveSelectionOnly">The move takes exactly the selection, not what is time-linked
    /// to it.</param>
    /// <param name="KeepSnapUntilAltUp">Alt at the press chose what moves, so it does not also bypass
    /// snapping until it has been released once (see <see cref="TimelinePress.BypassesSnap"/>).</param>
    public readonly record struct TimelinePressDecision(
        TimelinePressSelection Selection,
        TimelinePressDrag Drag,
        bool CollapseOnRelease = false,
        bool MoveSelectionOnly = false,
        bool KeepSnapUntilAltUp = false);

    /// <summary>
    /// The timeline's press rules as pure functions: the surface reads the hit and the modifiers,
    /// asks here, and carries out the answer. Nothing here sees a pointer or a control, so every rule
    /// is a row in a unit test.
    /// </summary>
    public static class TimelinePress
    {
        /// <summary>How far the pointer has to travel along the timeline before a press on a clip
        /// body becomes a move. Moves only: a trim follows the pointer from the first pixel, because
        /// a trim is what the user grabbed the edge for, whereas a press on a body may just be a
        /// click.</summary>
        public const double MoveThresholdPx = 3;

        /// <summary>The modifier that adds to a selection: Ctrl, which is Cmd on macOS (Ctrl+click is
        /// the context menu there), or Shift until a range gesture exists.</summary>
        public static bool IsToggleModifier(KeyModifiers modifiers, bool isMacOS) =>
            modifiers.HasFlag(KeyModifiers.Shift) ||
            modifiers.HasFlag(isMacOS ? KeyModifiers.Meta : KeyModifiers.Control);

        /// <param name="target">What the press landed on.</param>
        /// <param name="clipSelected">Whether the pressed clip is in the selection.</param>
        /// <param name="selectionCount">How many clips are selected.</param>
        /// <param name="modifiers">The modifiers held at the press.</param>
        /// <param name="isMacOS">Which modifier adds to the selection.</param>
        public static TimelinePressDecision Decide(TimelinePressTarget target, bool clipSelected, int selectionCount,
            KeyModifiers modifiers, bool isMacOS)
        {
            if (IsToggleModifier(modifiers, isMacOS))
            {
                return new TimelinePressDecision(
                    target == TimelinePressTarget.Empty ? TimelinePressSelection.Keep : TimelinePressSelection.Toggle,
                    TimelinePressDrag.None);
            }

            if (target == TimelinePressTarget.Empty)
                return new TimelinePressDecision(TimelinePressSelection.Clear, TimelinePressDrag.Scrub);

            var selection = clipSelected ? TimelinePressSelection.Keep : TimelinePressSelection.Replace;
            var collapse = clipSelected && selectionCount > 1;

            if (target == TimelinePressTarget.ClipEdge)
                return new TimelinePressDecision(selection, TimelinePressDrag.Trim, collapse);

            var alt = modifiers.HasFlag(KeyModifiers.Alt);
            return new TimelinePressDecision(selection, TimelinePressDrag.Move, collapse,
                MoveSelectionOnly: alt, KeepSnapUntilAltUp: alt);
        }

        /// <summary>What a move of the selection takes, frozen at the press: the selection plus what is
        /// time-linked to it, or exactly the selection.</summary>
        public static HashSet<Guid> MoveSet(Project project, IReadOnlyCollection<Guid> selection, bool selectionOnly) =>
            selectionOnly ? selection.ToHashSet() : TimelineOps.TimeLinked(project, selection);

        /// <summary>Whether the pointer has left the dead zone around the press. Horizontal only,
        /// because a clip never changes rows on a drag.</summary>
        public static bool IsPastMoveThreshold(double pointerDeltaX) => Math.Abs(pointerDeltaX) > MoveThresholdPx;

        /// <summary>Whether this pointer step puts the drag to work: a move past the dead zone, a trim
        /// on any step at all, since a trim is what the user grabbed the edge for.</summary>
        public static bool Engages(TimelinePressDrag drag, double pointerDeltaX) =>
            drag == TimelinePressDrag.Trim || IsPastMoveThreshold(pointerDeltaX);

        /// <summary>Whether a release leaves the pressed clip as the only selection: the press asked
        /// for that, and the pointer never did anything with it. Jitter inside a move's dead zone
        /// has not, and any step of a trim has.</summary>
        public static bool CollapsesOnRelease(TimelinePressDecision press, bool engaged) =>
            press.CollapseOnRelease && !engaged;

        /// <summary>
        /// Whether this drag step ignores snapping. Alt held does that, unless it has been held since
        /// the press that used it to pick the move set: then snapping stays on until Alt has been
        /// released once, so holding Alt throughout gives one clip that still snaps, and releasing and
        /// pressing it again gives the usual unsnapped nudge. Call on every step, and whenever Alt may
        /// have gone up, since the flag clears here.
        /// </summary>
        public static bool BypassesSnap(bool altHeld, ref bool keepSnapUntilAltUp)
        {
            if (!altHeld)
                keepSnapUntilAltUp = false;

            return altHeld && !keepSnapUntilAltUp;
        }
    }
}
