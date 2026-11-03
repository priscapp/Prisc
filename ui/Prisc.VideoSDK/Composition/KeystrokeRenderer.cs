using System;
using System.Collections.Generic;
using Prisc.VideoSDK.Model;
using SkiaSharp;

namespace Prisc.VideoSDK.Composition
{
    /// <summary>Where each keystroke line sits against the block's anchor x: its left edge, its
    /// center, or its right edge.</summary>
    public enum KeystrokeAlignment
    {
        Left,
        Center,
        Right,
    }

    /// <summary>
    /// Which way the keystroke rows grow from the anchor. <see cref="Up"/> is the editor's
    /// bottom-anchored block: the anchor is the newest row's bottom edge and finished rows are
    /// pushed upward. <see cref="Down"/> mirrors it for a block hung from the top of a screen: the
    /// anchor is the newest row's top edge and finished rows are pushed downward. Either way the
    /// newest row holds still against the anchor and the oldest row, the one about to leave, is
    /// the one farthest from it, so a row leaving never moves the rows still showing.
    /// </summary>
    public enum KeystrokeStacking
    {
        Up,
        Down,
    }

    /// <summary>
    /// The keystroke overlay's one Skia drawing pipeline: rows of typed text on typing tracks and special
    /// keys as <see cref="Keycap"/>s, stacked from an anchor, each row playing its own entry and
    /// exit. Shared by the video editor's keyboard track (<c>FrameComposer.DrawKeyboard</c>, preview
    /// and render alike) and the live overlay Draw on Screen puts over the desktop
    /// (<see cref="LiveKeystrokes"/>) — the two only differ in where their rows come from (a
    /// recorded capture or a live hook) and in where the block is anchored.
    /// </summary>
    internal static class KeystrokeRenderer
    {
        /// <summary>Row animation length when a transition animates but carries no
        /// duration.</summary>
        internal const int DefaultRowMs = 250;

        /// <summary>
        /// The vertical geometry of one keyboard row, derived from the font size in canvas pixels:
        /// the padding above and below the line, the gap between stacked rows and the row's own
        /// height. It lives in one place because two callers measure with it — the drawing below,
        /// and the editor's transform gizmo, which boxes the block through
        /// <see cref="FrameComposer.MeasureKeyboardHeight"/> and would otherwise drift from the
        /// drawn rows the first time a proportion here was touched. Nothing a
        /// <see cref="KeystrokeLook"/> sets reaches it: every track and keycap is drawn inside
        /// <see cref="RowHeight"/>, so the style can never change what the gizmo measures.
        /// </summary>
        internal readonly struct KeyboardMetrics
        {
            public KeyboardMetrics(float fontPx, float lineSpacing)
            {
                PadV = fontPx * 0.30f;
                Gap = fontPx * 0.25f;
                RowHeight = lineSpacing + 2 * PadV;
            }

            /// <summary>Vertical padding inside a row, above and below the line.</summary>
            public float PadV { get; }

            /// <summary>Vertical space between two stacked rows.</summary>
            public float Gap { get; }

            /// <summary>One row's height: the line pitch plus its vertical padding. Typing tracks,
            /// key tracks and keycaps all fit inside it.</summary>
            public float RowHeight { get; }

            /// <summary>The block height of <paramref name="rows"/> stacked rows, gaps
            /// included.</summary>
            public float BlockHeight(int rows) =>
                rows <= 0 ? 0 : rows * RowHeight + (rows - 1) * Gap;
        }

        /// <summary>Row animation length in ms for one transition, or 0 when the row is to
        /// appear/vanish instantly. A kind that animates but carries no duration (hand written
        /// json, or an older project) gets a sane one rather than snapping.</summary>
        internal static int RowMs(Transition transition)
        {
            if (transition == null || !TransitionMath.IsAnimated(transition.Kind))
                return 0;
            return transition.DurationTicks > 0
                ? (int)(transition.DurationTicks / TimeSpan.TicksPerMillisecond)
                : DefaultRowMs;
        }

        /// <summary>
        /// Draws <paramref name="rows"/> (oldest first, as <see cref="KeyboardLayout.VisibleRowsAt"/>
        /// returns them) from the anchor point, newest row against it, in <paramref name="look"/>
        /// (<see cref="KeystrokeStyles.Resolve(KeyboardContent)"/>). Typing draws as text on a
        /// typing track; every special key and every chord member draws as a <see cref="Keycap"/>
        /// instead, <b>outside</b> any typing track and on a key track of its own — a row is one
        /// track per run of typed words or of keycaps (see <see cref="DrawLine"/>).
        ///
        /// Rows animate individually with <paramref name="entry"/>/<paramref name="exit"/> — a new
        /// row plays the entry as it arrives, a finished one plays the exit once its linger is up.
        /// A vertically sliding entry also takes only its eased share of the stack, so the rows
        /// beyond it glide aside as the new one lands rather than jumping. Lines wrap at
        /// <paramref name="wrapWidth"/> canvas px and a wrapped row keeps its reading order whichever
        /// way the block stacks.
        /// </summary>
        internal static void Draw(SKCanvas target, IReadOnlyList<KeyboardRow> rows, KeystrokeLook look,
            Transition entry, Transition exit, float fontPx, float anchorX, float anchorY, float wrapWidth,
            KeystrokeAlignment alignment, KeystrokeStacking stacking, double opacity)
        {
            if (rows == null || rows.Count == 0 || fontPx <= 0 || opacity <= 0)
                return;

            look = (look ?? KeystrokeStyles.Resolve((KeyboardContent)null)).Clamped();
            using var typeface = SKTypeface.CreateDefault();
            using var font = new SKFont(typeface, fontPx) { Subpixel = true };
            var metrics = new KeyboardMetrics(fontPx, font.Spacing);
            using var caps = new Keycap(typeface, fontPx, metrics.RowHeight, look);
            var atomMetrics = new AtomMetrics(font, caps, fontPx, metrics, look);
            // the wrap box is the caller's box outright: the tracks' padding is measured inside
            // the line, so subtracting it here would reserve it twice
            wrapWidth = Math.Max(fontPx, wrapWidth);

            // wrap each row into its drawn lines, oldest row first: a wrapped run animates as one
            var wrapped = new List<(List<List<KeyAtom>> Lines, ItemEffects Fx, float Advance)>();
            foreach (var row in rows)
            {
                var (fx, advance) = RowEffects(entry, exit, row);
                if (fx.Opacity <= 0)
                    continue;
                var lines = KeyboardLayout.WrapAtoms(row.Atoms, atomMetrics, wrapWidth);
                if (lines.Count > 0)
                    wrapped.Add((lines, fx, advance));
            }

            // newest row first, out from the anchor. Up walks each row's lines last to first so the
            // first line ends up on top; Down walks them first to last for the same reading order.
            float edge = anchorY;
            float step = metrics.RowHeight + metrics.Gap;
            for (int r = wrapped.Count - 1; r >= 0; r--)
            {
                var (lines, fx, advance) = wrapped[r];
                double alpha = opacity * fx.Opacity;
                for (int k = 0; k < lines.Count; k++)
                {
                    var atoms = lines[stacking == KeystrokeStacking.Up ? lines.Count - 1 - k : k];
                    float width = KeyboardLayout.LineWidth(atoms, atomMetrics);
                    float dx = (float)(fx.OffsetXFrac * width);
                    float dy = (float)(fx.OffsetYFrac * metrics.RowHeight);
                    float left = alignment switch
                    {
                        KeystrokeAlignment.Left => anchorX,
                        KeystrokeAlignment.Right => anchorX - width,
                        _ => anchorX - width / 2,
                    };
                    float top = stacking == KeystrokeStacking.Up ? edge - metrics.RowHeight : edge;
                    var rect = new SKRect(left + dx, top + dy, left + width + dx, top + metrics.RowHeight + dy);

                    if (alpha > 0)
                        DrawLine(target, atoms, atomMetrics, font, caps, metrics, rect, look, alpha);

                    edge += (stacking == KeystrokeStacking.Up ? -1 : 1) * advance * step;
                }
            }
        }

        /// <summary>One row's animation state: what to multiply/offset it by, and the share of its
        /// slot the stack beyond it should already have moved into.</summary>
        private static (ItemEffects Fx, float Advance) RowEffects(Transition entry, Transition exit, KeyboardRow row)
        {
            var entryKind = entry?.Kind ?? TransitionKind.None;
            var exitKind = exit?.Kind ?? TransitionKind.None;
            double entryShown = TransitionMath.IsAnimated(entryKind)
                ? Easing.Apply(entry.Easing, row.EntryRaw) : 1;
            double exitShown = TransitionMath.IsAnimated(exitKind)
                ? Easing.Apply(exit.Easing, row.ExitRaw) : 1;

            // only a vertical slide compresses the stack; the others settle their slot at once
            float advance = entryKind is TransitionKind.SlideUp or TransitionKind.SlideDown
                ? (float)Math.Clamp(entryShown, 0, 1)
                : 1f;

            return (TransitionMath.EvaluateShown(entryKind, entryShown, exitKind, exitShown), advance);
        }

        /// <summary>
        /// Widths for the wrap and the drawing alike: typed words measure in the row font, keycaps
        /// in their own geometry, and neighbors are spaced by what sits between them — a typed
        /// space inside a typing track, a cap gap (or the tighter "+" gap) on a key track, and
        /// both tracks' padding plus a cap gap wherever the row switches from one to the other.
        ///
        /// Each track's horizontal padding is carried <b>here</b>, not added by the drawing: a row
        /// is one typing track per contiguous run of typed words and one key track per contiguous
        /// run of keycaps (a chord's "+" included), so the padding falls at each run's two ends —
        /// in <see cref="Edge"/> when the run starts or ends the line, and folded into
        /// <see cref="Gap"/> at every word↔keycap boundary. Measuring and drawing therefore walk
        /// the identical arithmetic, which is what keeps a wrapped line inside its box. The key
        /// track's padding applies whether or not its color draws anything, so a look's geometry
        /// never depends on a color's alpha.
        /// </summary>
        private sealed class AtomMetrics : IKeyAtomMetrics
        {
            private readonly SKFont _font;
            private readonly Keycap _caps;
            private readonly float _space;
            private readonly float _capGap;
            private readonly float _plusGap;
            private readonly float _typingPad;
            private readonly float _trackPad;

            public AtomMetrics(SKFont font, Keycap caps, float fontPx, KeyboardMetrics metrics,
                KeystrokeLook look)
            {
                _font = font;
                _caps = caps;
                _space = font.MeasureText(" ");
                _capGap = fontPx * 0.16f;
                _plusGap = fontPx * 0.05f;
                _typingPad = (float)(look.TypingPadding * metrics.RowHeight);
                _trackPad = caps.TrackPadding;
            }

            public float Width(KeyAtom atom) => atom.Kind == KeyAtomKind.Cap
                ? _caps.Width(atom.Text)
                : _font.MeasureText(atom.Text);

            public float Gap(KeyAtom left, KeyAtom right)
            {
                bool leftWord = left.Kind == KeyAtomKind.Word, rightWord = right.Kind == KeyAtomKind.Word;
                if (leftWord && rightWord)
                    return _space;

                // one track ends and the other begins here, so both paddings are part of the gap
                if (leftWord != rightWord)
                    return Edge(left) + Edge(right) + _capGap;

                // neighbors on the same key track
                bool plus = left.Kind == KeyAtomKind.Plus || right.Kind == KeyAtomKind.Plus;
                return plus ? _plusGap : _capGap;
            }

            public float Edge(KeyAtom atom) => atom.Kind == KeyAtomKind.Word ? _typingPad : _trackPad;
        }

        /// <summary>
        /// Draws one laid-out line inside <paramref name="rect"/>. The line is split into runs:
        /// each contiguous run of typed words sits on a typing track
        /// (<see cref="KeystrokeLook.TypingTrackColor"/>) with its own padding at both ends, and
        /// each contiguous run of keycaps and chord "+"s sits on a key track
        /// (<see cref="KeystrokeLook.KeyTrackColor"/>, often transparent). A keycap therefore
        /// never sits inside a typing track: <c>somt⌫e</c> is typing·key·typing, and a line that
        /// starts or ends on a keycap grows no empty typing track beside it. Both tracks span the
        /// full row height, which is all <see cref="KeyboardMetrics"/> measures.
        ///
        /// Positions come from the very arithmetic <see cref="KeyboardLayout.LineWidth"/> measured
        /// the line with, so the runs cannot drift out of the rect the wrap sized. Everything
        /// centers on the row's midline by the font's real metrics.
        /// </summary>
        private static void DrawLine(SKCanvas target, IReadOnlyList<KeyAtom> atoms,
            IKeyAtomMetrics atomMetrics, SKFont font, Keycap caps, KeyboardMetrics metrics,
            SKRect rect, KeystrokeLook look, double alpha)
        {
            var pen = new float[atoms.Count];
            float x = rect.Left + atomMetrics.Edge(atoms[0]);
            for (int i = 0; i < atoms.Count; i++)
            {
                if (i > 0)
                    x += atomMetrics.Width(atoms[i - 1]) + atomMetrics.Gap(atoms[i - 1], atoms[i]);
                pen[i] = x;
            }

            using var paint = new SKPaint { IsAntialias = true };

            // tracks first, behind what sits on them
            var typingTrack = Keycap.Fade(new SKColor(look.TypingTrackColor), alpha);
            var keyTrack = Keycap.Fade(new SKColor(look.KeyTrackColor), alpha);
            float typingRadius = (float)(look.TypingRadius * metrics.RowHeight);
            float keyRadius = (float)(look.KeyTrackRadius * metrics.RowHeight);
            for (int i = 0; i < atoms.Count;)
            {
                bool word = atoms[i].Kind == KeyAtomKind.Word;
                int end = i;
                while (end < atoms.Count && (atoms[end].Kind == KeyAtomKind.Word) == word)
                    end++;

                var color = word ? typingTrack : keyTrack;
                if (color.Alpha > 0)
                {
                    var track = new SKRect(
                        pen[i] - atomMetrics.Edge(atoms[i]), rect.Top,
                        pen[end - 1] + atomMetrics.Width(atoms[end - 1]) + atomMetrics.Edge(atoms[end - 1]),
                        rect.Bottom);
                    float radius = word ? typingRadius : keyRadius;
                    paint.Color = color;
                    using var rr = new SKRoundRect(track, radius, radius);
                    target.DrawRoundRect(rr, paint);
                }

                i = end;
            }

            var ink = Keycap.Fade(new SKColor(look.TypingTextColor), alpha);
            float baseline = Keycap.Baseline(font, rect.MidY);

            for (int i = 0; i < atoms.Count; i++)
            {
                var atom = atoms[i];
                switch (atom.Kind)
                {
                    case KeyAtomKind.Cap:
                        caps.Draw(target, atom.Text, pen[i], rect.MidY, alpha);
                        break;
                    case KeyAtomKind.Plus:
                        DrawPlus(target, atom.Text, pen[i], baseline, font, look, alpha);
                        break;
                    default:
                        paint.Color = ink;
                        target.DrawText(atom.Text, pen[i], baseline, SKTextAlign.Left, font, paint);
                        break;
                }
            }
        }

        /// <summary>
        /// A chord's "+". It belongs to the keys, not to the typing, so it takes the key colors —
        /// and has to stay legible on any theme and over any backdrop:
        /// <list type="bullet">
        /// <item>on an opaque key track (<see cref="PlusOnOpaqueTrack"/>) it is a solid glyph in
        /// <see cref="PlusOnTrack"/>'s color;</item>
        /// <item>otherwise the video shows through behind it, so it is drawn in
        /// <see cref="KeystrokeLook.KeyTextColor"/> inside a thin halo of
        /// <see cref="KeystrokeLook.KeyOuterColor"/> — the legend-on-rim pairing every theme
        /// already keeps legible, which reads on a light backdrop and a dark one alike. The halo
        /// stays thinner than the glyph's stem so the "+" still reads as a solid sign rather than
        /// an outline where the video matches the fill.</item>
        /// </list>
        /// </summary>
        private static void DrawPlus(SKCanvas target, string text, float x, float baseline,
            SKFont font, KeystrokeLook look, double alpha)
        {
            using var paint = new SKPaint { IsAntialias = true };
            if (PlusOnOpaqueTrack(look))
            {
                paint.Color = Keycap.Fade(new SKColor(PlusOnTrack(look)), alpha);
                target.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
                return;
            }

            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = font.Size * PlusHaloWidth;
            paint.StrokeJoin = SKStrokeJoin.Round;
            paint.Color = Keycap.Fade(new SKColor(look.KeyOuterColor), alpha);
            target.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);

            paint.Style = SKPaintStyle.Fill;
            paint.Color = Keycap.Fade(new SKColor(look.KeyTextColor), alpha);
            target.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);
        }

        /// <summary>The halo's stroke width around a "+" that sits over video, a fraction of the
        /// font size (half of it shows outside the glyph).</summary>
        internal const float PlusHaloWidth = 0.06f;

        /// <summary>The key track alpha from which the "+" trusts the track as its backdrop. Below
        /// it the video behind still decides what the "+" sits on, so it keeps its halo.</summary>
        internal const uint OpaqueTrackAlpha = 0xE6;

        /// <summary>The contrast a "+" on an opaque track must reach before it falls back to a
        /// neutral ink: WCAG's minimum for non-text graphics.</summary>
        internal const double MinPlusContrast = 3.0;

        /// <summary>Whether the "+" is drawn plain on the key track (true) or haloed over the
        /// video (false).</summary>
        internal static bool PlusOnOpaqueTrack(KeystrokeLook look) => (look.KeyTrackColor >> 24) >= OpaqueTrackAlpha;

        /// <summary>The "+" color on an opaque key track: whichever of the key text, key top and
        /// key outer colors has the highest WCAG contrast ratio against the track — or, when none
        /// reaches <see cref="MinPlusContrast"/> (white legends on a mid-grey tray), near-black or
        /// white, whichever reads better.</summary>
        internal static uint PlusOnTrack(KeystrokeLook look)
        {
            uint best = look.KeyTextColor;
            foreach (var candidate in new[] { look.KeyTopColor, look.KeyOuterColor })
            {
                if (Contrast(candidate, look.KeyTrackColor) > Contrast(best, look.KeyTrackColor))
                    best = candidate;
            }
            if (Contrast(best, look.KeyTrackColor) >= MinPlusContrast)
                return best;

            const uint ink = 0xFF1E1E1E, paper = 0xFFFFFFFF;
            return Contrast(ink, look.KeyTrackColor) >= Contrast(paper, look.KeyTrackColor) ? ink : paper;
        }

        /// <summary>The WCAG contrast ratio of two colors' RGB (alpha ignored).</summary>
        internal static double Contrast(uint a, uint b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static double Luminance(uint argb)
        {
            static double Linear(uint channel)
            {
                double c = channel / 255.0;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear((argb >> 16) & 0xFF)
                + 0.7152 * Linear((argb >> 8) & 0xFF)
                + 0.0722 * Linear(argb & 0xFF);
        }

        // ------------------------------------------------------------------ single-line preview

        /// <summary>The natural size of one undecorated line of <paramref name="atoms"/> at
        /// <paramref name="fontPx"/>: its laid-out width and the row height.</summary>
        internal static SKSize MeasureLine(IReadOnlyList<KeyAtom> atoms, KeystrokeLook look, float fontPx)
        {
            if (atoms == null || atoms.Count == 0 || fontPx <= 0)
                return SKSize.Empty;

            look = (look ?? KeystrokeStyles.Resolve((KeyboardContent)null)).Clamped();
            using var typeface = SKTypeface.CreateDefault();
            using var font = new SKFont(typeface, fontPx) { Subpixel = true };
            var metrics = new KeyboardMetrics(fontPx, font.Spacing);
            using var caps = new Keycap(typeface, fontPx, metrics.RowHeight, look);
            var atomMetrics = new AtomMetrics(font, caps, fontPx, metrics, look);
            return new SKSize(KeyboardLayout.LineWidth(atoms, atomMetrics), metrics.RowHeight);
        }

        /// <summary>Draws one line of <paramref name="atoms"/> unwrapped and unanimated, its
        /// left edge at <paramref name="left"/> and its row top at <paramref name="top"/> — the
        /// pipeline the overlay draws every line with, for <see cref="KeystrokePreview"/>.</summary>
        internal static void DrawSingleLine(SKCanvas target, IReadOnlyList<KeyAtom> atoms,
            KeystrokeLook look, float fontPx, float left, float top, double alpha)
        {
            if (target == null || atoms == null || atoms.Count == 0 || fontPx <= 0 || alpha <= 0)
                return;

            look = (look ?? KeystrokeStyles.Resolve((KeyboardContent)null)).Clamped();
            using var typeface = SKTypeface.CreateDefault();
            using var font = new SKFont(typeface, fontPx) { Subpixel = true };
            var metrics = new KeyboardMetrics(fontPx, font.Spacing);
            using var caps = new Keycap(typeface, fontPx, metrics.RowHeight, look);
            var atomMetrics = new AtomMetrics(font, caps, fontPx, metrics, look);
            float width = KeyboardLayout.LineWidth(atoms, atomMetrics);
            var rect = new SKRect(left, top, left + width, top + metrics.RowHeight);
            DrawLine(target, atoms, atomMetrics, font, caps, metrics, rect, look, alpha);
        }
    }
}
