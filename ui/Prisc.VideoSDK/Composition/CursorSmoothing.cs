using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Prisc.Geometry2D;
using Prisc.VideoSDK.Model;

namespace Prisc.VideoSDK.Composition
{
    /// <summary>
    /// The cursor track's <see cref="CursorContent.Smoothing"/>: the recorded pointer path run
    /// through the brush's streamline (<see cref="Streamline"/> — the same time-normalised EMA the
    /// image editor's freehand brush smooths its pointer with), with three changes a recording
    /// needs and a live brush cannot have:
    /// <list type="bullet">
    /// <item>zero phase. The EMA runs forward over the path and then backward over the result, so
    /// the two lags cancel: the cursor is smoothed but never trails the pixels it points at. A
    /// live brush has no future samples to do this with, which is why it pins its tip to the raw
    /// pointer instead;</item>
    /// <item>pinned at clicks. Wherever a button is held, and for a ramp either side of it, the
    /// drawn position eases back onto the raw one, so a click lands — and its highlight fires —
    /// exactly where the user clicked, and a drag stays on whatever it is dragging;</item>
    /// <item>restarted after a hidden stretch, so a cursor that reappears does not glide in from
    /// wherever it hid.</item>
    /// </list>
    /// The result is a pure function of (capture, strength), computed for the whole capture at
    /// once and cached, so scrub, preview and render draw the same position for the same row
    /// whichever order they ask in.
    /// </summary>
    internal static class CursorSmoothing
    {
        /// <summary>Each pass's lag at full strength. The two passes together spread the path
        /// over roughly ± this much time.</summary>
        internal const double MaxLagMs = 200;

        /// <summary>The shortest ease between the pinned raw position and the smoothed path; the
        /// ease is otherwise twice the lag, long enough that the hand-over never reads as a hop.</summary>
        internal const double MinPinRampMs = 60;

        /// <summary>How many strengths each capture keeps smoothed paths for: the preview and the
        /// render usually agree on one, and a spinner being dragged only ever needs the latest.</summary>
        private const int CacheEntries = 4;

        /// <summary>Each pass's lag for a strength (0..1, linear).</summary>
        internal static double LagMsFor(double strength) =>
            double.IsNaN(strength) ? 0 : Math.Clamp(strength, 0, 1) * MaxLagMs;

        /// <summary>The position to draw the cursor at for frame row <paramref name="rowIndex"/>
        /// (physical px, the capture's virtual-desktop space, as <see cref="InputFrame.X"/>):
        /// the raw row at no smoothing.</summary>
        public static (double X, double Y) PositionAt(InputCapture capture, int rowIndex, double strength)
        {
            var row = capture.Frames[rowIndex];
            if (!(LagMsFor(strength) > 0))
                return (row.X, row.Y);

            var path = PathFor(capture, strength);
            return (path.X[rowIndex], path.Y[rowIndex]);
        }

        // ------------------------------------------------------------------------------- cache

        private sealed class SmoothedPath
        {
            public int Key;
            public double[] X;
            public double[] Y;
        }

        private sealed class PathCache
        {
            public readonly List<SmoothedPath> Entries = new List<SmoothedPath>(CacheEntries);
        }

        private static readonly ConditionalWeakTable<InputCapture, PathCache> Caches =
            new ConditionalWeakTable<InputCapture, PathCache>();

        private static SmoothedPath PathFor(InputCapture capture, double strength)
        {
            int key = (int)Math.Round(Math.Clamp(strength, 0, 1) * 10000);
            var cache = Caches.GetValue(capture, _ => new PathCache());
            lock (cache)
            {
                for (int i = 0; i < cache.Entries.Count; i++)
                {
                    var hit = cache.Entries[i];
                    if (hit.Key != key)
                        continue;
                    if (i > 0)
                    {
                        cache.Entries.RemoveAt(i);
                        cache.Entries.Insert(0, hit);
                    }
                    return hit;
                }

                int n = capture.Frames.Count;
                var path = new SmoothedPath { Key = key, X = new double[n], Y = new double[n] };
                Smooth(capture.Frames, capture.Events, key / 10000.0, path.X, path.Y);
                if (cache.Entries.Count == CacheEntries)
                    cache.Entries.RemoveAt(CacheEntries - 1);
                cache.Entries.Insert(0, path);
                return path;
            }
        }

        // ------------------------------------------------------------------------------ smooth

        /// <summary>Fills <paramref name="xs"/>/<paramref name="ys"/> (one slot per frame row) with
        /// the smoothed path; see the class summary. At no strength they are the raw rows.</summary>
        internal static void Smooth(IReadOnlyList<InputFrame> frames, IReadOnlyList<InputEvent> events,
            double strength, double[] xs, double[] ys)
        {
            int n = frames.Count;
            for (int i = 0; i < n; i++)
            {
                xs[i] = frames[i].X;
                ys[i] = frames[i].Y;
            }

            double lagMs = LagMsFor(strength);
            if (!(lagMs > 0) || n < 2)
                return;

            double t = Streamline.BlendForLag(lagMs / Streamline.RefFrameMs);

            // each run of visible rows is its own stroke: a hidden row draws nothing, and the
            // first row after it starts the filter afresh on the raw position
            for (int a = 0; a < n;)
            {
                if (frames[a].Cursor == CursorKind.Hidden)
                {
                    a++;
                    continue;
                }

                int b = a;
                while (b + 1 < n && frames[b + 1].Cursor != CursorKind.Hidden)
                    b++;
                SmoothRun(frames, t, lagMs, a, b, xs, ys);
                a = b + 1;
            }

            Pin(frames, events, Math.Max(MinPinRampMs, 2 * lagMs), xs, ys);
        }

        /// <summary>
        /// The streamline forward over rows [a, b], then backward over its own output. Each end
        /// is padded first with the run's own rows point-reflected through the end row (time and
        /// position both, scipy's <c>filtfilt</c> "odd" padding) over <paramref name="lagMs"/>·4 of
        /// time: without it each pass starts cold on its first row, and a run that ends mid-motion
        /// (the recording stops, the cursor hides) would hand the backward pass the forward
        /// pass's lagged end — the cursor would visibly fall behind there. A straight-line end
        /// reflects into its own continuation, so steady motion runs through the end exactly.
        /// </summary>
        private static void SmoothRun(IReadOnlyList<InputFrame> frames, double t, double lagMs, int a, int b,
            double[] xs, double[] ys)
        {
            if (b <= a)
                return;

            double padMs = 4 * lagMs;
            int head = 0, tail = 0;
            while (head < b - a && frames[a + head + 1].TimeMs - frames[a].TimeMs <= padMs)
                head++;
            while (tail < b - a && frames[b].TimeMs - frames[b - tail - 1].TimeMs <= padMs)
                tail++;

            int count = b - a + 1, m = head + count + tail;
            var pt = new double[m];
            var px = new double[m];
            var py = new double[m];
            for (int j = 0; j < head; j++)
            {
                int src = a + head - j; // farthest first
                pt[j] = 2 * frames[a].TimeMs - frames[src].TimeMs;
                px[j] = 2 * xs[a] - xs[src];
                py[j] = 2 * ys[a] - ys[src];
            }
            for (int j = 0; j < count; j++)
            {
                pt[head + j] = frames[a + j].TimeMs;
                px[head + j] = xs[a + j];
                py[head + j] = ys[a + j];
            }
            for (int j = 0; j < tail; j++)
            {
                int src = b - 1 - j; // nearest first
                pt[head + count + j] = 2 * frames[b].TimeMs - frames[src].TimeMs;
                px[head + count + j] = 2 * xs[b] - xs[src];
                py[head + count + j] = 2 * ys[b] - ys[src];
            }

            double x = px[0], y = py[0];
            for (int j = 1; j < m; j++)
            {
                double s = Streamline.Blend(t, Frames(pt[j] - pt[j - 1]));
                x += (px[j] - x) * s;
                y += (py[j] - y) * s;
                px[j] = x;
                py[j] = y;
            }

            for (int j = m - 2; j >= 0; j--)
            {
                double s = Streamline.Blend(t, Frames(pt[j + 1] - pt[j]));
                x += (px[j] - x) * s;
                y += (py[j] - y) * s;
                px[j] = x;
                py[j] = y;
            }

            for (int j = 0; j < count; j++)
            {
                xs[a + j] = px[head + j];
                ys[a + j] = py[head + j];
            }
        }

        /// <summary>A row gap in reference frames. Unclamped above, unlike the brush's: a gap in
        /// a recording is real time the pointer had to get where the next row says it is.</summary>
        private static double Frames(double dtMs) => Math.Max(0, dtMs) / Streamline.RefFrameMs;

        /// <summary>
        /// Eases the smoothed path back onto the raw rows around every held button: weight 1
        /// inside a held interval (a mouse-down to the release of the last held button, or a row
        /// that reports a button down), falling off with a smoothstep over
        /// <paramref name="rampMs"/> either side of it.
        /// </summary>
        private static void Pin(IReadOnlyList<InputFrame> frames, IReadOnlyList<InputEvent> events,
            double rampMs, double[] xs, double[] ys)
        {
            var held = HeldIntervals(frames, events);
            if (held.Count == 0)
                return;

            int next = 0; // first interval that has not ended before this row
            for (int i = 0; i < frames.Count; i++)
            {
                double time = frames[i].TimeMs;
                while (next < held.Count && held[next].End < time)
                    next++;

                double d = double.PositiveInfinity;
                if (next > 0)
                    d = time - held[next - 1].End;
                if (next < held.Count)
                    d = Math.Min(d, Math.Max(0, held[next].Start - time));
                if (d >= rampMs)
                    continue;

                double u = d / rampMs;
                double w = 1 - u * u * (3 - 2 * u);
                xs[i] += (frames[i].X - xs[i]) * w;
                ys[i] += (frames[i].Y - ys[i]) * w;
            }
        }

        /// <summary>The held-button intervals, sorted and merged: from the mouse events (exact,
        /// sub-frame — a click shorter than a frame still pins) and from rows reporting a button
        /// down (which covers a capture whose events were lost). A release with no press before
        /// it pins its own instant; a press never released runs to the end.</summary>
        private static List<(double Start, double End)> HeldIntervals(IReadOnlyList<InputFrame> frames,
            IReadOnlyList<InputEvent> events)
        {
            var spans = new List<(double Start, double End)>();

            int depth = 0;
            double start = 0;
            foreach (var e in events)
            {
                if (e.Kind == InputEventKind.MouseDown)
                {
                    if (depth++ == 0)
                        start = e.TimeMs;
                }
                else if (e.Kind == InputEventKind.MouseUp)
                {
                    if (depth == 0)
                        spans.Add((e.TimeMs, e.TimeMs));
                    else if (--depth == 0)
                        spans.Add((start, e.TimeMs));
                }
            }
            if (depth > 0)
                spans.Add((start, double.PositiveInfinity));

            foreach (var f in frames)
            {
                if (f.Buttons != 0)
                    spans.Add((f.TimeMs, f.TimeMs));
            }

            spans.Sort((p, q) => p.Start.CompareTo(q.Start));
            var merged = new List<(double Start, double End)>(spans.Count);
            foreach (var span in spans)
            {
                if (merged.Count > 0 && span.Start <= merged[^1].End)
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, span.End));
                else
                    merged.Add(span);
            }
            return merged;
        }
    }
}
