using System;
using System.Collections.Generic;
using System.Linq;
using Prisc.UI.Controls;

namespace Prisc.UI.VideoEditor.Timeline
{
    /// <summary>
    /// The geometry behind dragging a track header by its grip: which rows a dragged row may land
    /// among, which boundary a pointer y is asking for, where to draw the line saying so, and what
    /// the drop means to the model. Pure — no Avalonia types — so the panel that draws the drag and
    /// the tests that assert it run the same code.
    ///
    /// <para>Everything here works in <b>display</b> space: the top-to-bottom row order
    /// <see cref="TimelineRowLayout.Build"/> lays out, which for video rows is the exact reverse of
    /// the model's ascending <c>(Order, Id)</c> layer order. Drop positions are <i>insertion
    /// slots</i>, so they run from the first row of a block to one past its last.</para>
    /// </summary>
    internal static class TimelineReorder
    {
        /// <summary>The rows the row at <paramref name="rowIndex"/> may be reordered among: its
        /// block — the pinned speed row, the video block (video, zoom, cursor and keys rows), or the
        /// audio block — which <see cref="TimelineRowLayout.Build"/> lays out contiguously in that
        /// order. A row cannot cross between blocks — its kind is a property of the track, not of
        /// where it sits — and the speed row is a block of one, which is what denies it a grip.
        /// Inclusive at both ends.</summary>
        public static (int Start, int End) GroupRange(IReadOnlyList<TimelineRow> rows, int rowIndex)
        {
            RequireRow(rows, rowIndex);

            var block = BlockOf(rows, rowIndex);

            var start = rowIndex;
            while (start > 0 && BlockOf(rows, start - 1) == block)
                start--;

            var end = rowIndex;
            while (end + 1 < rows.Count && BlockOf(rows, end + 1) == block)
                end++;

            return (start, end);
        }

        /// <summary>Whether the row at <paramref name="rowIndex"/> has anywhere to go: its block
        /// holds another row to change places with. The header panel builds the grip on
        /// this.</summary>
        public static bool CanDrag(IReadOnlyList<TimelineRow> rows, int rowIndex)
        {
            var (start, end) = GroupRange(rows, rowIndex);
            return end > start;
        }

        /// <summary>The insertion slot a drop at <paramref name="y"/> is asking for: the boundary
        /// nearest the pointer, clamped to the dragged row's own block. A pointer above the block
        /// (or off the top of the panel) gives its first slot, one below gives its last.</summary>
        public static int DropIndexAt(IReadOnlyList<TimelineRow> rows, int rowIndex, double y)
        {
            var (start, end) = GroupRange(rows, rowIndex);
            return RowReorderMath.DropSlot(start, end, y, i => (rows[i].Top, rows[i].Height));
        }

        /// <summary>Where the drop indicator sits for <paramref name="dropIndex"/> — the top edge
        /// of the row that would be pushed down, or the bottom edge of the block when the drop is
        /// past its last row.</summary>
        public static double IndicatorY(IReadOnlyList<TimelineRow> rows, int rowIndex, int dropIndex)
        {
            var (start, end) = GroupRange(rows, rowIndex);
            return RowReorderMath.IndicatorY(start, end, dropIndex, i => (rows[i].Top, rows[i].Height));
        }

        /// <summary>
        /// The drop as the index <see cref="Prisc.VideoSDK.Editing.EditorSession.MoveTrackToIndex"/>
        /// counts in, or null when the row would land back where it started (a click, or a drag
        /// that came home). The row is lifted out before it is put back, so a drop below its own slot
        /// lands one higher than the boundary the indicator sat on. Video-block rows are drawn
        /// highest layer first, so their drop is flipped back into the model's ascending space;
        /// audio rows are listed in model order. The speed row is a block of one, so it is always
        /// null.
        /// </summary>
        public static int? TargetLayerIndex(IReadOnlyList<TimelineRow> rows, int rowIndex, int dropIndex)
        {
            var (start, end) = GroupRange(rows, rowIndex);
            var target = RowReorderMath.TargetRow(rowIndex, Math.Clamp(dropIndex, start, end + 1));
            if (target == rowIndex)
                return null;

            return BlockOf(rows, rowIndex) == TimelineRowBlock.Audio ? target - start : end - target;
        }

        /// <summary>Which block a row belongs to — the layout's own classification, so the
        /// blocks a drag is confined to are exactly the ones the layout gaps apart.</summary>
        private static TimelineRowBlock BlockOf(IReadOnlyList<TimelineRow> rows, int rowIndex) =>
            TimelineRowLayout.BlockOf(rows[rowIndex].Kind);

        private static void RequireRow(IReadOnlyList<TimelineRow> rows, int rowIndex)
        {
            ArgumentNullException.ThrowIfNull(rows);

            if (rowIndex < 0 || rowIndex >= rows.Count)
                throw new ArgumentOutOfRangeException(nameof(rowIndex));
        }
    }
}
