using System;
using Prisc.VideoSDK.Model;
using SkiaSharp;

namespace Prisc.VideoSDK.Composition
{
    /// <summary>
    /// The keycap half of the keystroke overlay: one parametric cap — an outer key with a key top
    /// inset into it (<see cref="KeystrokeLook"/>'s padding and drop), both rounded — its legend,
    /// and the vector icons the familiar wide keys carry. Every icon is an <see cref="SKPath"/> on
    /// purpose: the overlay draws with the platform default typeface, and the symbols these keys
    /// want (⇧ ↵ ⌫ ⇥) are exactly the glyphs a default typeface is likely to be missing, which
    /// renders as a tofu box.
    ///
    /// The cap's whole vertical footprint fits inside one row of
    /// <see cref="KeystrokeRenderer.KeyboardMetrics"/> — the row less the key track padding top
    /// and bottom — so a row of caps measures the same as a row of text whatever the look, and
    /// the editor's gizmo stays truthful.
    /// </summary>
    internal sealed class Keycap : IDisposable
    {
        private readonly SKFont _centered;
        private readonly SKFont _legend;
        private readonly KeystrokeLook _look;

        /// <param name="look">An already clamped look (<see cref="KeystrokeLook.Clamped"/>).</param>
        public Keycap(SKTypeface typeface, float fontPx, float rowHeight, KeystrokeLook look)
        {
            _look = look;
            TrackPadding = (float)(look.KeyTrackPadding * rowHeight);
            OuterHeight = Math.Max(1f, rowHeight - 2 * TrackPadding);
            float h = OuterHeight;
            OuterRadius = (float)(look.OuterRadius * h);
            TopRadius = (float)(look.TopRadius * h);
            // a hairline rim (Simple's) would antialias away at small sizes and leave borderless
            // swatches, so any non-zero padding is at least one pixel — measured through Width
            // below, so measuring and drawing still agree
            PadX = RimPx(look.PaddingX, h);
            PadY = RimPx(look.PaddingY, h);
            // like the original seat (at least a pixel), a drop never vanishes at small sizes
            Drop = look.Drop > 0 ? Math.Max(1f, (float)(look.Drop * h)) : 0f;
            TopHeight = Math.Max(1f, h - 2 * PadY - Drop);

            LegendPadX = fontPx * 0.24f;
            LegendPadY = Math.Min(fontPx * 0.16f, TopHeight * 0.12f);
            IconSize = TopHeight * 0.40f;

            // legends are smaller than the typing they sit beside, as they are on a real keyboard
            // — and never taller than a shallow key top can carry
            _centered = new SKFont(typeface, Math.Min(fontPx * 0.78f, TopHeight * 0.62f)) { Subpixel = true };
            _legend = new SKFont(typeface, Math.Min(fontPx * 0.60f, TopHeight * 0.42f)) { Subpixel = true };
        }

        /// <summary>A padding fraction of the outer height <paramref name="h"/> in px: 0 stays 0, any
        /// other value is at least one pixel (or <see cref="KeystrokeLook.MaxPadding"/> of a key
        /// too small for that).</summary>
        private static float RimPx(double fraction, float h)
        {
            float px = (float)(fraction * h);
            return fraction > 0 ? Math.Max(px, Math.Min(1f, h * (float)KeystrokeLook.MaxPadding)) : px;
        }

        /// <summary>Space between the key track's edge and the caps on it, in px — horizontally as
        /// well as vertically (see <see cref="KeystrokeLook.KeyTrackPadding"/>).</summary>
        public float TrackPadding { get; }

        /// <summary>The cap's full vertical footprint, outer key included.</summary>
        public float OuterHeight { get; }

        /// <summary>Height of the key top.</summary>
        public float TopHeight { get; }

        /// <summary>The visible outer key under the key top, beyond the even rim.</summary>
        public float Drop { get; }

        private float OuterRadius { get; }

        private float TopRadius { get; }

        private float PadX { get; }

        private float PadY { get; }

        private float LegendPadX { get; }

        private float LegendPadY { get; }

        private float IconSize { get; }

        /// <summary>Wide keys wear their legend bottom-left with an icon top-right, the way the
        /// real keys are molded; everything else centers its legend. Both boards' names are
        /// listed: the label already carries which keyboard it came off (Bksp is a PC, Delete a
        /// Mac; Alt a PC, Option a Mac), and the two vocabularies do not collide, so one set
        /// covers both without a platform being threaded down into the drawing.</summary>
        private static bool IsWide(string label) => label is
            "Ctrl" or "Shift" or "Alt" or "Win" or "Enter" or "Tab" or "Space" or "Bksp" or "Caps"
            or "Cmd" or "Option" or "Return" or "Delete";

        /// <summary>Arrow keys are the one cap drawn as an icon alone — the glyph <i>is</i> the
        /// legend, and spelling "Left" on a square cap reads worse than the arrow does.</summary>
        private static bool IsArrow(string label) => label is "Up" or "Down" or "Left" or "Right";

        /// <summary>The cap's full width, outer key included: the key top's own width (square at
        /// least, wider for a wide key or a long legend) plus the rim either side.</summary>
        public float Width(string label) => TopWidth(label) + 2 * PadX;

        private float TopWidth(string label)
        {
            if (string.IsNullOrEmpty(label) || IsArrow(label))
                return TopHeight;
            if (IsWide(label))
                return Math.Max(TopHeight * 1.7f, _legend.MeasureText(label) + IconSize + 3 * LegendPadX);
            return Math.Max(TopHeight, _centered.MeasureText(label) + 2 * LegendPadX);
        }

        /// <summary>Draws the cap with its left edge at <paramref name="left"/>, its footprint
        /// centered on <paramref name="centerY"/>: the outer key, the key top inset into it, then
        /// the legend and icon on the key top.</summary>
        public void Draw(SKCanvas canvas, string label, float left, float centerY, double alpha)
        {
            float width = Width(label);
            float top = centerY - OuterHeight / 2;
            var outer = new SKRect(left, top, left + width, top + OuterHeight);
            var face = new SKRect(outer.Left + PadX, outer.Top + PadY,
                outer.Right - PadX, outer.Top + PadY + TopHeight);

            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

            paint.Color = Fade(new SKColor(_look.KeyOuterColor), alpha);
            using (var rr = new SKRoundRect(outer, OuterRadius, OuterRadius))
                canvas.DrawRoundRect(rr, paint);

            paint.Color = Fade(new SKColor(_look.KeyTopColor), alpha);
            using (var rr = new SKRoundRect(face, TopRadius, TopRadius))
                canvas.DrawRoundRect(rr, paint);

            var ink = Fade(new SKColor(_look.KeyTextColor), alpha);
            if (IsArrow(label))
            {
                float size = TopHeight * 0.55f;
                DrawIcon(canvas, label, new SKRect(
                    face.MidX - size / 2, face.MidY - size / 2,
                    face.MidX + size / 2, face.MidY + size / 2), ink);
                return;
            }

            if (IsWide(label))
            {
                paint.Color = ink;
                var metrics = _legend.Metrics;
                canvas.DrawText(label, face.Left + LegendPadX, face.Bottom - LegendPadY - metrics.Descent,
                    SKTextAlign.Left, _legend, paint);
                DrawIcon(canvas, label, new SKRect(
                    face.Right - LegendPadX - IconSize, face.Top + LegendPadY * 0.7f,
                    face.Right - LegendPadX, face.Top + LegendPadY * 0.7f + IconSize), ink);
                return;
            }

            paint.Color = ink;
            canvas.DrawText(label, face.MidX, Baseline(_centered, face.MidY),
                SKTextAlign.Center, _centered, paint);
        }

        /// <summary>The baseline that centers a keycap's ink on <paramref name="centerY"/>, off the
        /// font's own cap height — the labels are capitals and capital-shaped words ("H", "Ctrl",
        /// "Shift"), so the box their ink actually fills is the cap box.
        ///
        /// Not the ascent/descent em box: how much headroom a font reserves above its capitals for
        /// accents is the font's own business and varies wildly between families, which centered
        /// the text on Windows (Segoe UI) and left it riding ~9% of the font size high on macOS
        /// (Helvetica). Cap height is measured from the capitals themselves, so it agrees across
        /// families. Fonts that do not report one fall back to the em box.</summary>
        public static float Baseline(SKFont font, float centerY)
        {
            var metrics = font.Metrics;
            if (metrics.CapHeight > 0)
                return centerY + metrics.CapHeight / 2;
            return centerY - (metrics.Ascent + metrics.Descent) / 2;
        }

        public static SKColor Fade(SKColor color, double alpha)
        {
            double a = color.Alpha / 255.0 * alpha;
            return color.WithAlpha((byte)Math.Clamp(Math.Round(a * 255), 0, 255));
        }

        public void Dispose()
        {
            _centered.Dispose();
            _legend.Dispose();
        }

        // -------------------------------------------------------------------------------- icons

        /// <summary>Draws <paramref name="label"/>'s icon inside <paramref name="box"/>, or
        /// nothing when the key has none. Paths are built in the unit square and mapped onto the
        /// box, so one description serves every font size.</summary>
        private static void DrawIcon(SKCanvas canvas, string label, SKRect box, SKColor ink)
        {
            var fill = new SKPath();
            var stroke = new SKPath();
            float strokeWidth = 0.13f;

            switch (label)
            {
                case "Shift":
                    Arrow(fill, 0.06f, 0.94f, 0);
                    break;
                case "Caps":
                    Arrow(fill, 0.03f, 0.72f, 0);
                    fill.AddRect(new SKRect(0.28f, 0.82f, 0.72f, 0.97f));
                    break;
                case "Up":
                    Arrow(fill, 0.06f, 0.94f, 0);
                    break;
                case "Down":
                    Arrow(fill, 0.06f, 0.94f, 180);
                    break;
                case "Left":
                    Arrow(fill, 0.06f, 0.94f, 270);
                    break;
                case "Right":
                    Arrow(fill, 0.06f, 0.94f, 90);
                    break;
                // ↩ — Windows legends this key Enter, macOS Return; same molded arrow
                case "Enter":
                case "Return":
                    stroke.MoveTo(0.92f, 0.10f);
                    stroke.LineTo(0.92f, 0.62f);
                    stroke.LineTo(0.30f, 0.62f);
                    fill.MoveTo(0.34f, 0.38f);
                    fill.LineTo(0.34f, 0.86f);
                    fill.LineTo(0.04f, 0.62f);
                    fill.Close();
                    break;
                // ⌫ — "Bksp" on a PC, "Delete" on a Mac (whose forward delete is spelled out
                // as "Fwd Del" and deliberately carries no icon, so the two cannot be mistaken)
                case "Bksp":
                case "Delete":
                    stroke.MoveTo(0.34f, 0.14f);
                    stroke.LineTo(0.97f, 0.14f);
                    stroke.LineTo(0.97f, 0.86f);
                    stroke.LineTo(0.34f, 0.86f);
                    stroke.LineTo(0.03f, 0.50f);
                    stroke.Close();
                    stroke.MoveTo(0.52f, 0.34f);
                    stroke.LineTo(0.80f, 0.66f);
                    stroke.MoveTo(0.80f, 0.34f);
                    stroke.LineTo(0.52f, 0.66f);
                    strokeWidth = 0.09f;
                    break;
                case "Tab":
                    fill.MoveTo(0.04f, 0.18f);
                    fill.LineTo(0.58f, 0.50f);
                    fill.LineTo(0.04f, 0.82f);
                    fill.Close();
                    fill.AddRect(new SKRect(0.72f, 0.12f, 0.92f, 0.88f));
                    break;
                case "Win":
                    fill.AddRect(new SKRect(0.06f, 0.06f, 0.46f, 0.46f));
                    fill.AddRect(new SKRect(0.54f, 0.06f, 0.94f, 0.46f));
                    fill.AddRect(new SKRect(0.06f, 0.54f, 0.46f, 0.94f));
                    fill.AddRect(new SKRect(0.54f, 0.54f, 0.94f, 0.94f));
                    break;
                case "Ctrl":
                    stroke.MoveTo(0.12f, 0.68f);
                    stroke.LineTo(0.50f, 0.28f);
                    stroke.LineTo(0.88f, 0.68f);
                    break;
                // ⌥ — the PC's Alt key wears the option glyph already, so the Mac name that
                // means the same key joins the same case rather than repeating the path
                case "Alt":
                case "Option":
                    stroke.MoveTo(0.04f, 0.24f);
                    stroke.LineTo(0.38f, 0.24f);
                    stroke.LineTo(0.88f, 0.80f);
                    stroke.MoveTo(0.58f, 0.24f);
                    stroke.LineTo(0.96f, 0.24f);
                    break;
                case "Space":
                    stroke.MoveTo(0.08f, 0.34f);
                    stroke.LineTo(0.08f, 0.70f);
                    stroke.LineTo(0.92f, 0.70f);
                    stroke.LineTo(0.92f, 0.34f);
                    break;
                case "Cmd":
                    // ⌘ is the St John's Arms: a square whose four corners each open into a loop.
                    // Built as the four three-quarter arcs plus the straight runs between their
                    // endpoints, so the corners of the square ARE the loop centres (0.19 in from
                    // each edge) — draw full circles instead and the inner quarters cross the
                    // square, which reads as a flower rather than the command glyph. Thinner
                    // stroke than the rest: at icon size the loops are only ~4px across, and the
                    // default weight closes them into blobs.
                    stroke.AddArc(new SKRect(0.00f, 0.00f, 0.38f, 0.38f), 90, 270);
                    stroke.AddArc(new SKRect(0.62f, 0.00f, 1.00f, 0.38f), 180, 270);
                    stroke.AddArc(new SKRect(0.00f, 0.62f, 0.38f, 1.00f), 0, 270);
                    stroke.AddArc(new SKRect(0.62f, 0.62f, 1.00f, 1.00f), 270, 270);
                    stroke.MoveTo(0.38f, 0.19f);
                    stroke.LineTo(0.62f, 0.19f);
                    stroke.MoveTo(0.38f, 0.81f);
                    stroke.LineTo(0.62f, 0.81f);
                    stroke.MoveTo(0.19f, 0.38f);
                    stroke.LineTo(0.19f, 0.62f);
                    stroke.MoveTo(0.81f, 0.38f);
                    stroke.LineTo(0.81f, 0.62f);
                    strokeWidth = 0.08f;
                    break;
            }

            try
            {
                if (fill.IsEmpty && stroke.IsEmpty)
                    return;

                var map = SKMatrix.CreateScale(box.Width, box.Height)
                    .PostConcat(SKMatrix.CreateTranslation(box.Left, box.Top));

                using var paint = new SKPaint { IsAntialias = true, Color = ink };
                if (!fill.IsEmpty)
                {
                    fill.Transform(map);
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawPath(fill, paint);
                }

                if (!stroke.IsEmpty)
                {
                    stroke.Transform(map);
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = strokeWidth * Math.Min(box.Width, box.Height);
                    paint.StrokeCap = SKStrokeCap.Round;
                    paint.StrokeJoin = SKStrokeJoin.Round;
                    canvas.DrawPath(stroke, paint);
                }
            }
            finally
            {
                fill.Dispose();
                stroke.Dispose();
            }
        }

        /// <summary>An up-arrow (head plus stem) spanning <paramref name="top"/>..<paramref
        /// name="bottom"/> of the unit box, rotated clockwise by <paramref name="degrees"/>.</summary>
        private static void Arrow(SKPath path, float top, float bottom, float degrees)
        {
            float head = top + (bottom - top) * 0.52f;
            var arrow = new SKPath();
            arrow.MoveTo(0.50f, top);
            arrow.LineTo(0.94f, head);
            arrow.LineTo(0.68f, head);
            arrow.LineTo(0.68f, bottom);
            arrow.LineTo(0.32f, bottom);
            arrow.LineTo(0.32f, head);
            arrow.LineTo(0.06f, head);
            arrow.Close();

            if (degrees != 0)
                arrow.Transform(SKMatrix.CreateRotationDegrees(degrees, 0.5f, 0.5f));

            path.AddPath(arrow);
            arrow.Dispose();
        }
    }
}
