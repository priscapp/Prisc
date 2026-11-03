using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Prisc.VideoSDK.Model;
using ModelTransform = Prisc.VideoSDK.Model.Transform;

namespace Prisc.UI.VideoEditor
{
    /// <summary>The four insets a finished crop drag produced, as fractions of the uncropped
    /// picture, plus the drawn region's shape in canvas pixels (height / width) and the canvas's own
    /// (height / width) — what an aspect-unlocked item needs to re-fit its explicit height to the
    /// region, so the cut is shown undistorted.</summary>
    public readonly record struct CropDrawResult(
        double Left, double Top, double Right, double Bottom,
        double RegionHeightOverWidth, double CanvasHeightOverWidth);

    /// <summary>
    /// Pure geometry for the preview's "Draw crop" mode: the isolated project it composes and the
    /// drag-rectangle math. Free of Avalonia types so it can be tested without a UI thread.
    /// </summary>
    public static class CropDrawMath
    {
        /// <summary>A drag shorter than this on either axis (preview DIPs) is a click, not a crop.</summary>
        public const double MinDragPx = 4;

        /// <summary>
        /// The project the preview composes while a crop is being drawn on <paramref name="itemId"/>:
        /// a deep copy (the input is the player's snapshot, which background threads read) with every
        /// other visual row hidden, every zoom effect dropped, and the item itself reduced to its
        /// whole source picture — no crop, no aspect preset or explicit height (so the box takes the
        /// source's own shape and nothing is trimmed away), upright, unmasked, opaque and with no
        /// transition moving it. Its center and width are kept, so the picture stays where the user
        /// placed it. Speed effects are kept: hiding them would move the instant on screen.
        /// Null when the item is not in the project.
        /// </summary>
        public static Project BuildIsolatedProject(Project project, Guid itemId)
        {
            if (project?.Items == null || project.Tracks == null || itemId == Guid.Empty)
                return null;

            var source = project.Items.FirstOrDefault(i => i.Id == itemId);
            if (source == null)
                return null;

            var copy = Project.FromJson(project.ToJson());

            foreach (var track in copy.Tracks)
            {
                if (track.Kind == TrackKind.Video && track.Id != source.TrackId)
                    track.Hidden = true;
            }

            copy.Items.RemoveAll(i => i.Content is ZoomContent);

            var item = copy.Items.FirstOrDefault(i => i.Id == itemId);
            if (item == null)
                return null;

            var t = item.Transform ??= new ModelTransform();
            t.Crop = null;
            t.CropWindow = null;
            t.Aspect = null;
            t.AspectStretch = false;
            t.ScaleY = null;
            t.Rotation = 0;
            t.Mask = null;
            t.Opacity = 1;
            item.Entry = null;
            item.Exit = null;
            item.Surround = null;
            return copy;
        }

        /// <summary>
        /// The drag rectangle from <paramref name="anchor"/> to <paramref name="pointer"/>, both
        /// already in the same space as <paramref name="bounds"/> (the item's picture). The anchor is
        /// clamped into the bounds; the far corner too, and when <paramref name="aspect"/> (width /
        /// height) is set the rectangle keeps that ratio — the larger pull wins, then the whole
        /// rectangle shrinks about the anchor until it fits.
        /// </summary>
        public static (double X, double Y, double W, double H) DragRect(
            double anchorX, double anchorY, double pointerX, double pointerY,
            double boundsX, double boundsY, double boundsW, double boundsH, double? aspect)
        {
            double ax = Math.Clamp(anchorX, boundsX, boundsX + boundsW);
            double ay = Math.Clamp(anchorY, boundsY, boundsY + boundsH);

            double dx = pointerX - ax;
            double dy = pointerY - ay;
            int sx = dx < 0 ? -1 : 1;
            int sy = dy < 0 ? -1 : 1;

            // room from the anchor to the bounds' edge in the direction of the pull
            double roomX = sx > 0 ? boundsX + boundsW - ax : ax - boundsX;
            double roomY = sy > 0 ? boundsY + boundsH - ay : ay - boundsY;

            double w = Math.Abs(dx);
            double h = Math.Abs(dy);

            if (aspect is > 0 and var ratio)
            {
                if (h * ratio > w)
                    w = h * ratio;
                else
                    h = w / ratio;

                double fit = 1;
                if (w > roomX)
                    fit = Math.Min(fit, roomX / w);
                if (h > roomY)
                    fit = Math.Min(fit, roomY / h);
                w *= fit;
                h *= fit;
            }
            else
            {
                w = Math.Min(w, roomX);
                h = Math.Min(h, roomY);
            }

            double x = sx > 0 ? ax : ax - w;
            double y = sy > 0 ? ay : ay - h;
            return (x, y, w, h);
        }

        /// <summary>The insets <paramref name="rect"/> cuts from <paramref name="bounds"/>, as
        /// fractions of the bounds, rounded to 1e-4 (a hundredth of a percent, finer than any
        /// spinner step) so the project file does not carry float noise.</summary>
        public static (double Left, double Top, double Right, double Bottom) Insets(
            double rectX, double rectY, double rectW, double rectH,
            double boundsX, double boundsY, double boundsW, double boundsH)
        {
            if (!(boundsW > 0) || !(boundsH > 0))
                return (0, 0, 0, 0);

            double l = (rectX - boundsX) / boundsW;
            double t = (rectY - boundsY) / boundsH;
            double r = (boundsX + boundsW - (rectX + rectW)) / boundsW;
            double b = (boundsY + boundsH - (rectY + rectH)) / boundsH;
            return (Round(l), Round(t), Round(r), Round(b));
        }

        private static double Round(double v) => Math.Clamp(Math.Round(v, 4), 0, 1);
    }

    /// <summary>
    /// The preview's "Draw crop" surface: laid over the whole preview while the mode is on, it
    /// owns every press (so neither the click-to-select nor the gizmo sees one), shows the item's
    /// whole picture outlined, and lets the user drag a dashed rectangle across it. Releasing a
    /// real drag raises <see cref="Drawn"/>; a plain click does nothing, so a stray press costs no
    /// edit. <see cref="VideoPreviewControl"/> positions it and feeds it the item's rect; Escape is
    /// the window's (it leaves the mode, dropping any drag with it).
    /// </summary>
    public sealed class CropDrawControl : Control
    {
        private static readonly Cursor CrossCursor = new Cursor(StandardCursorType.Cross);

        private bool _dragging;
        private Point _anchor;
        private Point _pointer;

        public CropDrawControl()
        {
            Cursor = CrossCursor;
            Focusable = true;
        }

        /// <summary>The letterboxed video rect, in this control's coordinates.</summary>
        public Rect CanvasRect { get; set; }

        /// <summary>The item's whole (uncropped) picture, in this control's coordinates; empty when
        /// the item is not on screen at the playhead, in which case presses do nothing.</summary>
        public Rect TargetRect { get; private set; }

        /// <summary>The ratio (width / height) a drawn rectangle is held to, or null for a free
        /// one: an item with an aspect preset draws a region of exactly that ratio, so the preset
        /// neither trims nor stretches the cut afterwards.</summary>
        public double? ConstrainAspect { get; private set; }

        /// <summary>Raised once per finished drag, on the UI thread.</summary>
        public event EventHandler<CropDrawResult> Drawn;

        public void SetTarget(Rect target, double? constrainAspect)
        {
            if (TargetRect != target || ConstrainAspect != constrainAspect)
            {
                TargetRect = target;
                ConstrainAspect = constrainAspect;
                InvalidateVisual();
            }
        }

        /// <summary>Drops a drag in flight without raising anything (the mode ending under it).</summary>
        public void Reset()
        {
            _dragging = false;
            InvalidateVisual();
        }

        private Rect CurrentRect()
        {
            var t = TargetRect;
            var (x, y, w, h) = CropDrawMath.DragRect(_anchor.X, _anchor.Y, _pointer.X, _pointer.Y,
                t.X, t.Y, t.Width, t.Height, ConstrainAspect);
            return new Rect(x, y, w, h);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            // every press is ours while the mode is on, a right click included: nothing under this
            // surface should act on a press the user aimed at the crop
            e.Handled = true;

            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            if (TargetRect.Width <= 0 || TargetRect.Height <= 0)
                return;

            var p = e.GetPosition(this);
            _anchor = new Point(
                Math.Clamp(p.X, TargetRect.Left, TargetRect.Right),
                Math.Clamp(p.Y, TargetRect.Top, TargetRect.Bottom));
            _pointer = _anchor;
            _dragging = true;
            Focus();
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging)
                return;

            _pointer = e.GetPosition(this);
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_dragging)
                return;

            e.Handled = true;
            _pointer = e.GetPosition(this);
            _dragging = false;
            e.Pointer.Capture(null);
            InvalidateVisual();

            var rect = CurrentRect();
            var t = TargetRect;
            if (rect.Width < CropDrawMath.MinDragPx || rect.Height < CropDrawMath.MinDragPx)
                return;

            var (l, top, r, b) = CropDrawMath.Insets(rect.X, rect.Y, rect.Width, rect.Height,
                t.X, t.Y, t.Width, t.Height);
            var canvas = CanvasRect;
            Drawn?.Invoke(this, new CropDrawResult(l, top, r, b,
                rect.Height / rect.Width,
                canvas.Width > 0 ? canvas.Height / canvas.Width : 0));
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (_dragging)
                Reset();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            // hit-testable everywhere, so a press on the letterbox is ours too
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

            var target = TargetRect;
            if (target.Width <= 0 || target.Height <= 0)
                return;

            var accent = new SolidColorBrush(AppStyles.AccentColor);
            var dash = new DashStyle(new double[] { 4, 3 }, 0);

            if (!_dragging)
            {
                // the extent the crop is drawn against
                context.DrawRectangle(null,
                    new Pen(new SolidColorBrush(Colors.White, 0.6), 1) { DashStyle = dash }, target);
                return;
            }

            var rect = CurrentRect();

            // dim what the crop will cut away, so the kept region reads at a glance
            var shade = new SolidColorBrush(Colors.Black, 0.45);
            using (context.PushClip(target))
            {
                context.FillRectangle(shade, new Rect(target.Left, target.Top, target.Width, rect.Top - target.Top));
                context.FillRectangle(shade, new Rect(target.Left, rect.Bottom, target.Width, target.Bottom - rect.Bottom));
                context.FillRectangle(shade, new Rect(target.Left, rect.Top, rect.Left - target.Left, rect.Height));
                context.FillRectangle(shade, new Rect(rect.Right, rect.Top, target.Right - rect.Right, rect.Height));
            }

            // a dark pen under the dashed accent one keeps the marquee legible on any picture
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Colors.Black, 0.5), 3), rect);
            context.DrawRectangle(null, new Pen(accent, 1.5) { DashStyle = dash }, rect);
        }
    }
}
