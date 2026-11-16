using System;
using System.Collections.Generic;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;

namespace Prisc.UI.VideoEditor.Timeline
{
    /// <summary>
    /// The clips the timeline outlines as "moves with this": what a drag of the selection would bring
    /// along, the selection left out. Recomputed lazily after <see cref="Invalidate"/>, and held still
    /// while a gesture is open, since a gesture's steps must not change what is outlined.
    /// </summary>
    public sealed class TimelineLinkedOutlines
    {
        private HashSet<Guid> _clips = new HashSet<Guid>();
        private bool _stale = true;

        /// <summary>Each group's number, read at the same moment as the clips.</summary>
        public IReadOnlyDictionary<Guid, int> GroupNumbers { get; private set; } = new Dictionary<Guid, int>();

        /// <summary>The selection or the project changed, so the outlines may have too.</summary>
        public void Invalidate() => _stale = true;

        /// <summary>The outlined clips, brought up to date unless a gesture is open.</summary>
        public HashSet<Guid> Clips(EditorSession session)
        {
            if (_stale && !session.IsGestureActive)
            {
                var selection = session.SelectedItemIds;
                _clips = TimelineOps.TimeLinked(session.Project, selection);
                _clips.ExceptWith(selection);
                GroupNumbers = TimelineViewMath.GroupNumbers(session.Project);
                _stale = false;
            }

            return _clips;
        }

        /// <summary>Opens a gesture with the outlines frozen for the selection as it stands. A press
        /// can replace the selection and open its drag with no paint in between, and the lazy
        /// recompute would otherwise hold the previous selection's outlines for the whole drag.</summary>
        public EditGesture BeginGesture(EditorSession session, string label, object origin)
        {
            Clips(session);
            return session.BeginGesture(label, origin);
        }
    }
}
