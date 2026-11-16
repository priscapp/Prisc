using System;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>A drag in one call: the clip plus what is time-linked to it, as
    /// <c>TimelineSurface</c> freezes the set at press.</summary>
    internal static class TimeLinkedMoves
    {
        public static long MoveLinked(Project project, Guid itemId, long deltaTicks) =>
            TimelineOps.Move(project, TimelineOps.TimeLinked(project, new[] { itemId }), deltaTicks);

        public static long MoveLinked(this EditorSession session, Guid itemId, long deltaTicks, object origin = null) =>
            session.MoveItems(TimelineOps.TimeLinked(session.Project, new[] { itemId }), deltaTicks, origin);
    }
}
