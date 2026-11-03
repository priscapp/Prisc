using System;
using Prisc.VideoSDK.Model;
using SkiaSharp;

namespace Prisc.VideoSDK.Composition
{
    /// <summary>
    /// The repeat counter that follows a key or shortcut pressed several times in a row
    /// (<c>Bksp ×3</c>, <c>Ctrl+Z ×4</c>). It wears the keycap's own colors — a key top colored
    /// pill, the "×" and digits in the legend color — so it belongs to the theme, but it reads as
    /// a count rather than one more key by its shape: a full pill, shorter than a keycap, whose
    /// outer key color shows as a solid shadow offset down and to the right instead of a keycap's
    /// rim and drop. The count pops (a quick scale bump) each time it goes up, and the layout keeps
    /// the popped size clear of its neighbors (<see cref="Overhang"/>).
    /// </summary>
    internal sealed class RepeatBadge : IDisposable
    {
        /// <summary>How long the count-up pop runs, project-time ms.</summary>
        internal const double PopMs = 220;

        /// <summary>The pop's scale at the instant of the press, easing back to 1.</summary>
        internal const float PopScale = 1.3f;

        /// <summary>The smallest legend-on-key-top contrast the badge keeps the keycap legend
        /// color for; below it (a custom look whose legend matches its caps) the count falls back
        /// to near-black or white.</summary>
        internal const double MinInkContrast = 2.0;

        private readonly SKTypeface _bold;
        private readonly SKFont _digits;
        private readonly KeystrokeLook _look;

        /// <param name="look">An already clamped look (<see cref="KeystrokeLook.Clamped"/>).</param>
        public RepeatBadge(SKTypeface typeface, Keycap caps, KeystrokeLook look)
        {
            _look = look;
            PillHeight = Math.Max(1f, caps.OuterHeight * 0.62f);
            Shadow = Math.Max(1f, PillHeight * 0.1f);
            _bold = SKTypeface.FromFamilyName(typeface.FamilyName, SKFontStyle.Bold) ?? SKTypeface.CreateDefault();
            _digits = new SKFont(_bold, PillHeight * 0.68f) { Subpixel = true };

            float capHeight = _digits.Metrics.CapHeight > 0 ? _digits.Metrics.CapHeight : _digits.Size * 0.7f;
            CrossSize = capHeight * 0.62f;
            PadX = PillHeight * 0.30f;
            InnerGap = PillHeight * 0.08f;
        }

        /// <summary>The pill's own height, a share of the keycap's.</summary>
        public float PillHeight { get; }

        /// <summary>How far the solid shadow sits down and to the right of the pill.</summary>
        public float Shadow { get; }

        private float CrossSize { get; }

        private float PadX { get; }

        private float InnerGap { get; }

        private float PillWidth(string count) =>
            Math.Max(PillHeight * 1.45f, 2 * PadX + CrossSize + InnerGap + _digits.MeasureText(count ?? ""));

        /// <summary>The badge's laid-out width for a count of <paramref name="count"/> (the
        /// digits, as <see cref="KeyAtom.Text"/> carries them): the pill plus its shadow offset.
        /// Never narrower than a short capsule, so "×2" does not shrink into a circle.</summary>
        public float Width(string count) => PillWidth(count) + Shadow;

        /// <summary>How far the badge reaches past its laid-out box on each side at the top of
        /// its pop — the room the layout leaves either side so a popping badge never touches the
        /// keycap it counts or whatever follows it.</summary>
        public float Overhang(string count) => Width(count) * (PopScale - 1) / 2;

        /// <summary>The pill: the key top color.</summary>
        internal static uint FillColor(KeystrokeLook look) => look.KeyTopColor;

        /// <summary>The solid shadow: the outer key color.</summary>
        internal static uint ShadowColor(KeystrokeLook look) => look.KeyOuterColor;

        /// <summary>The "×" and digits: the keycap legend color, or near-black/white when that
        /// would not read on the key top (see <see cref="MinInkContrast"/>).</summary>
        internal static uint InkColor(KeystrokeLook look)
        {
            uint fill = FillColor(look);
            if (KeystrokeRenderer.Contrast(look.KeyTextColor, fill) >= MinInkContrast)
                return look.KeyTextColor;

            const uint ink = 0xFF1E1E1E, paper = 0xFFFFFFFF;
            return KeystrokeRenderer.Contrast(ink, fill) >= KeystrokeRenderer.Contrast(paper, fill) ? ink : paper;
        }

        /// <summary>The pop's scale <paramref name="ageMs"/> after the press that set the
        /// count: <see cref="PopScale"/> at once, easing out (cubic) to 1 over
        /// <see cref="PopMs"/>.</summary>
        internal static float PopAt(double ageMs)
        {
            if (!(ageMs >= 0) || ageMs >= PopMs)
                return 1f;
            double rest = 1 - ageMs / PopMs;
            return (float)(1 + (PopScale - 1) * rest * rest * rest);
        }

        /// <summary>Draws the badge with its laid-out left edge at <paramref name="left"/>, the
        /// pill and its shadow together centered on <paramref name="centerY"/>; the pop scales it
        /// about its own center without moving anything around it.</summary>
        public void Draw(SKCanvas canvas, string count, double ageMs, float left, float centerY, double alpha)
        {
            float width = PillWidth(count);
            float top = centerY - (PillHeight + Shadow) / 2;
            var pill = new SKRect(left, top, left + width, top + PillHeight);
            float radius = PillHeight / 2;

            int save = canvas.Save();
            float pop = PopAt(ageMs);
            if (pop != 1f)
                canvas.Scale(pop, pop, pill.MidX + Shadow / 2, pill.MidY + Shadow / 2);

            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

            var shadow = pill;
            shadow.Offset(Shadow, Shadow);
            paint.Color = Keycap.Fade(new SKColor(ShadowColor(_look)), alpha);
            using (var rr = new SKRoundRect(shadow, radius, radius))
                canvas.DrawRoundRect(rr, paint);

            paint.Color = Keycap.Fade(new SKColor(FillColor(_look)), alpha);
            using (var rr = new SKRoundRect(pill, radius, radius))
                canvas.DrawRoundRect(rr, paint);

            // "×" then the digits, centered as a group on the pill
            float digits = _digits.MeasureText(count ?? "");
            float content = CrossSize + InnerGap + digits;
            float x = pill.MidX - content / 2;

            paint.Color = Keycap.Fade(new SKColor(InkColor(_look)), alpha);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = Math.Max(1f, CrossSize * 0.24f);
            paint.StrokeCap = SKStrokeCap.Round;
            float half = CrossSize / 2, cx = x + half, cy = pill.MidY;
            canvas.DrawLine(cx - half, cy - half, cx + half, cy + half, paint);
            canvas.DrawLine(cx - half, cy + half, cx + half, cy - half, paint);

            paint.Style = SKPaintStyle.Fill;
            canvas.DrawText(count ?? "", x + CrossSize + InnerGap, Keycap.Baseline(_digits, pill.MidY),
                SKTextAlign.Left, _digits, paint);

            canvas.RestoreToCount(save);
        }

        public void Dispose()
        {
            _digits.Dispose();
            _bold.Dispose();
        }
    }
}
