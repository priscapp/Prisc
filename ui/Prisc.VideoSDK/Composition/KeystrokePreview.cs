using System;
using System.Collections.Generic;
using Prisc.VideoSDK.Model;
using SkiaSharp;

namespace Prisc.VideoSDK.Composition
{
    /// <summary>
    /// A sample keystroke row drawn by the overlay's own pipeline (<see cref="KeystrokeRenderer"/>)
    /// and fitted into a box — what the style and theme picker tiles show, so a tile is the very
    /// pixels the render will produce rather than a stand-in. The sample is a chord's keycaps
    /// (joined by "+" unless <c>chord</c> is false) followed by a typed word on its typing track,
    /// so one tile shows both groups a look styles.
    /// </summary>
    public static class KeystrokePreview
    {
        /// <summary>The default sample's keycaps: one wide key with an icon, one square key.</summary>
        public static readonly IReadOnlyList<string> DefaultKeys = new[] { "Ctrl", "A" };

        /// <summary>The default sample's typed word.</summary>
        public const string DefaultTyped = "Hi";

        /// <summary>The font size the sample is laid out at before it is fitted.</summary>
        private const float ReferencePx = 40;

        /// <summary>
        /// Draws the sample in <paramref name="look"/>, as large as fits inside
        /// <paramref name="bounds"/> and centered in it. <paramref name="keys"/> are keycap
        /// legends as the overlay spells them (<c>Ctrl</c>, <c>Shift</c>, <c>Alt</c>, <c>Win</c>,
        /// <c>Cmd</c>, <c>Option</c>, <c>Enter</c>, <c>Up</c>, <c>A</c>, …; null for
        /// <see cref="DefaultKeys"/>, empty for none); <paramref name="typed"/> is the typed text
        /// after them (null or empty for none). <paramref name="maxFontPx"/>, when positive, caps
        /// the fitted font size so a big tile does not blow the sample up past legibility norms.
        /// </summary>
        public static void Draw(SKCanvas canvas, SKRect bounds, KeystrokeLook look,
            IReadOnlyList<string> keys = null, string typed = DefaultTyped, bool chord = true,
            float maxFontPx = 0, double opacity = 1)
        {
            if (canvas == null || bounds.Width <= 0 || bounds.Height <= 0)
                return;

            var atoms = Atoms(keys ?? DefaultKeys, typed, chord);
            if (atoms.Count == 0)
                return;

            float fontPx = FitFontPx(atoms, look, bounds.Size, maxFontPx);
            if (fontPx <= 0)
                return;

            var size = KeystrokeRenderer.MeasureLine(atoms, look, fontPx);
            KeystrokeRenderer.DrawSingleLine(canvas, atoms, look, fontPx,
                bounds.MidX - size.Width / 2, bounds.MidY - size.Height / 2, opacity);
        }

        /// <summary><see cref="Draw(SKCanvas, SKRect, KeystrokeLook, IReadOnlyList{string}, string,
        /// bool, float, double)"/> for a style and theme (and the custom look, used only by the
        /// <c>custom</c> style) — see <see cref="KeystrokeStyles.Resolve(string, string,
        /// KeystrokeLook)"/>.</summary>
        public static void Draw(SKCanvas canvas, SKRect bounds, string style, string theme,
            KeystrokeLook custom = null, IReadOnlyList<string> keys = null, string typed = DefaultTyped,
            bool chord = true, float maxFontPx = 0, double opacity = 1) =>
            Draw(canvas, bounds, KeystrokeStyles.Resolve(style, theme, custom), keys, typed, chord,
                maxFontPx, opacity);

        /// <summary>The sample's natural size at <paramref name="fontPx"/> — its width and its row
        /// height — for a host that wants to size a tile to the sample's aspect.</summary>
        public static SKSize Measure(KeystrokeLook look, IReadOnlyList<string> keys = null,
            string typed = DefaultTyped, bool chord = true, float fontPx = ReferencePx) =>
            KeystrokeRenderer.MeasureLine(Atoms(keys ?? DefaultKeys, typed, chord), look, fontPx);

        private static float FitFontPx(IReadOnlyList<KeyAtom> atoms, KeystrokeLook look, SKSize box,
            float maxFontPx)
        {
            var natural = KeystrokeRenderer.MeasureLine(atoms, look, ReferencePx);
            if (natural.Width <= 0 || natural.Height <= 0)
                return 0;

            float fontPx = ReferencePx * Math.Min(box.Width / natural.Width, box.Height / natural.Height);
            if (maxFontPx > 0)
                fontPx = Math.Min(fontPx, maxFontPx);

            // text does not scale exactly linearly (hinting, rounding): settle any overshoot
            var fitted = KeystrokeRenderer.MeasureLine(atoms, look, fontPx);
            if (fitted.Width > box.Width && fitted.Width > 0)
                fontPx *= box.Width / fitted.Width;
            return fontPx;
        }

        private static List<KeyAtom> Atoms(IReadOnlyList<string> keys, string typed, bool chord)
        {
            var atoms = new List<KeyAtom>();
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key))
                    continue;
                if (chord && atoms.Count > 0)
                    atoms.Add(new KeyAtom(KeyAtomKind.Plus, "+"));
                atoms.Add(new KeyAtom(KeyAtomKind.Cap, key));
            }

            if (!string.IsNullOrEmpty(typed))
            {
                foreach (var word in typed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    atoms.Add(new KeyAtom(KeyAtomKind.Word, word));
            }

            return atoms;
        }
    }
}
