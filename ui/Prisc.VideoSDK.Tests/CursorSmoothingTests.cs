using System;
using System.Collections.Generic;
using System.Text;
using Prisc.VideoSDK.Composition;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    // CursorSmoothing: the brush's streamline run both ways over the recorded pointer path —
    // smooths jitter, keeps time (no lag), pins to the raw pointer around clicks, restarts after
    // a hidden stretch, and reads the same at any capture rate.
    public class CursorSmoothingTests
    {
        private const double FrameMs = 1000.0 / 60;
        private static readonly int[] NoKeys = Array.Empty<int>();

        private static InputFrame Row(double t, int x, int y, CursorKind kind = CursorKind.Arrow, int buttons = 0) =>
            new InputFrame(t, x, y, buttons, NoKeys, kind);

        private static (double[] X, double[] Y) Smooth(IReadOnlyList<InputFrame> frames, double strength,
            IReadOnlyList<InputEvent> events = null)
        {
            var xs = new double[frames.Count];
            var ys = new double[frames.Count];
            CursorSmoothing.Smooth(frames, events ?? Array.Empty<InputEvent>(), strength, xs, ys);
            return (xs, ys);
        }

        /// <summary>A pointer sweeping right at 3 px/frame with ±4 px of alternating vertical
        /// jitter around y = 100 — a steady hand on a shaky sensor.</summary>
        private static InputFrame[] JitteryLine(int count, double frameMs = FrameMs)
        {
            var rows = new InputFrame[count];
            for (int i = 0; i < count; i++)
                rows[i] = Row(i * frameMs, 3 * i, 100 + (i % 2 == 0 ? 4 : -4));
            return rows;
        }

        [Fact]
        public void No_strength_draws_the_raw_path()
        {
            var rows = JitteryLine(60);
            var (xs, ys) = Smooth(rows, 0);
            for (int i = 0; i < rows.Length; i++)
            {
                Assert.Equal(rows[i].X, xs[i]);
                Assert.Equal(rows[i].Y, ys[i]);
            }
        }

        [Fact]
        public void Smoothing_flattens_jitter_without_lagging_the_motion()
        {
            var rows = JitteryLine(240);
            var (xs, ys) = Smooth(rows, 0.5);

            // away from the run's ends: the jitter is mostly gone, and the zero-phase pair of
            // passes leaves the cursor on time (a one-way EMA would trail by ~lag·velocity,
            // 100 ms at 180 px/s = 18 px)
            for (int i = 60; i < 180; i++)
            {
                Assert.InRange(ys[i], 99, 101);
                Assert.InRange(xs[i] - rows[i].X, -1, 1);
            }
        }

        [Fact]
        public void A_run_ending_mid_motion_stays_on_time_to_its_last_row()
        {
            // the recording stops while the pointer is still sweeping: without end padding the
            // backward pass would start from the forward pass's lagged end, 36 px behind
            var rows = JitteryLine(120);
            var (xs, _) = Smooth(rows, 1.0);
            for (int i = 0; i < rows.Length; i++)
                Assert.InRange(xs[i] - rows[i].X, -1, 1);
        }

        [Fact]
        public void A_click_lands_exactly_on_the_raw_position_and_eases_back_out()
        {
            var rows = JitteryLine(240);
            double clickMs = rows[120].TimeMs;
            var events = new[]
            {
                new InputEvent(clickMs - 1, InputEventKind.MouseDown, 1, null, rows[120].X, rows[120].Y),
                new InputEvent(clickMs + 1, InputEventKind.MouseUp, 1, null, rows[120].X, rows[120].Y),
            };
            var (xs, ys) = Smooth(rows, 1.0, events);

            Assert.Equal(rows[120].X, xs[120], 9);
            Assert.Equal(rows[120].Y, ys[120], 9);

            // the pin's ramp is 2 × the 200 ms lag: well past it the path is smoothed again
            int far = 120 + (int)Math.Ceiling(2 * CursorSmoothing.MaxLagMs / FrameMs) + 4;
            Assert.InRange(ys[far], 98.5, 101.5);
            Assert.InRange(ys[120 - (far - 120)], 98.5, 101.5);
        }

        [Fact]
        public void A_held_drag_follows_the_raw_pointer()
        {
            var rows = JitteryLine(240);
            for (int i = 100; i <= 140; i++)
                rows[i] = Row(rows[i].TimeMs, rows[i].X, rows[i].Y, buttons: 1);

            var (xs, ys) = Smooth(rows, 1.0);
            for (int i = 100; i <= 140; i++)
            {
                Assert.Equal(rows[i].X, xs[i], 9);
                Assert.Equal(rows[i].Y, ys[i], 9);
            }
        }

        [Fact]
        public void A_cursor_reappearing_after_hiding_does_not_glide_in()
        {
            var rows = new List<InputFrame>();
            for (int i = 0; i < 30; i++)
                rows.Add(Row(i * FrameMs, 10, 10));
            rows.Add(Row(30 * FrameMs, 10, 10, CursorKind.Hidden));
            for (int i = 31; i < 60; i++)
                rows.Add(Row(i * FrameMs, 500, 400));

            var (xs, ys) = Smooth(rows, 1.0);
            for (int i = 0; i < rows.Count; i++)
            {
                Assert.Equal(rows[i].X, xs[i], 9);
                Assert.Equal(rows[i].Y, ys[i], 9);
            }
        }

        [Fact]
        public void The_smoothing_reads_the_same_at_any_capture_rate()
        {
            // the same circular motion sampled at 30 and at 120 fps
            static InputFrame[] Circle(double fps)
            {
                int count = (int)(4 * fps);
                var rows = new InputFrame[count];
                for (int i = 0; i < count; i++)
                {
                    double t = i * 1000 / fps;
                    double a = 2 * Math.PI * t / 1500;
                    rows[i] = Row(t, (int)Math.Round(500 + 200 * Math.Cos(a)), (int)Math.Round(500 + 200 * Math.Sin(a)));
                }
                return rows;
            }

            var slow = Circle(30);
            var fast = Circle(120);
            var (sx, sy) = Smooth(slow, 0.75);
            var (fx, fy) = Smooth(fast, 0.75);

            // compare at the shared instants in the middle second (fast row 4i = slow row i). The
            // two discretisations of the same lag differ by a few percent of a 200 px circle; a
            // per-sample blend that ignored the rate would differ by ~50 px
            int checkedRows = 0;
            for (int i = 45; i < 75; i++)
            {
                Assert.InRange(sx[i] - fx[4 * i], -10, 10);
                Assert.InRange(sy[i] - fy[4 * i], -10, 10);
                checkedRows++;
            }
            Assert.Equal(30, checkedRows);

            // and the smoothing is doing something: a 200 px circle is pulled inward
            double r = Math.Sqrt((sx[60] - 500) * (sx[60] - 500) + (sy[60] - 500) * (sy[60] - 500));
            Assert.InRange(r, 100, 198);
        }

        [Fact]
        public void PositionAt_reads_the_cached_path_and_the_raw_row_at_no_strength()
        {
            var json = new StringBuilder();
            json.AppendLine("{\"type\":\"header\",\"version\":2,\"region\":[0,0,1000,1000],\"fps_num\":60,\"fps_den\":1,\"platform\":\"windows\",\"monitors\":[]}");
            var rows = JitteryLine(120);
            foreach (var r in rows)
                json.AppendLine($"{{\"type\":\"frame\",\"t\":{r.TimeMs.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"x\":{r.X},\"y\":{r.Y},\"b\":0,\"c\":\"arrow\"}}");
            var capture = InputCapture.Parse(Encoding.UTF8.GetBytes(json.ToString()));
            Assert.Equal(rows.Length, capture.Frames.Count);

            var (xs, ys) = Smooth(capture.Frames, 0.5, capture.Events);
            for (int pass = 0; pass < 2; pass++) // second pass hits the cache
            {
                var (x, y) = CursorSmoothing.PositionAt(capture, 60, 0.5);
                Assert.Equal(xs[60], x, 9);
                Assert.Equal(ys[60], y, 9);
            }

            Assert.Equal(((double)rows[61].X, (double)rows[61].Y), CursorSmoothing.PositionAt(capture, 61, 0));
            Assert.Equal(((double)rows[61].X, (double)rows[61].Y), CursorSmoothing.PositionAt(capture, 61, double.NaN));
        }
    }
}
