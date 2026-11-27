namespace Prisc.Geometry2D
{
    /// <summary>
    /// The time-normalised streamline both editors smooth a pointer with: an exponential moving
    /// average on the position, <c>p = lerp(p, sample, blend)</c>, whose per-sample blend is
    /// derived from the time that sample covers so the smoothing reads the same at any input
    /// rate. The image editor's brush streamlines its live samples with it
    /// (<c>FreehandStroke</c>); the video editor's cursor track smooths the recorded pointer path
    /// with it (<c>CursorSmoothing</c>).
    /// </summary>
    public static class Streamline
    {
        /// <summary>The frame a one-frame blend is tuned against: one 60 Hz frame.</summary>
        public const double RefFrameMs = 1000.0 / 60;

        /// <summary>
        /// The blend for a sample <paramref name="k"/> reference frames after the previous one,
        /// given the blend <paramref name="t"/> tuned for exactly one frame. What the smoothing
        /// looks like on a moving pointer is its lag, (1 − t)/t frames of motion behind a steady
        /// hand, so this is the blend that keeps that lag at any rate: t at one frame,
        /// k·t/(k·t + 1 − t) in general. The exponential form 1 − (1 − t)^k matches step
        /// responses instead and over-smooths fine input by half. 0 at k = 0 (no time, no move),
        /// 1 at t = 1 (no smoothing).
        /// </summary>
        public static double Blend(double t, double k) => k * t / (k * t + 1 - t);

        /// <summary>The one-frame blend whose lag is <paramref name="lagFrames"/> reference
        /// frames — the inverse of (1 − t)/t. 1 (no smoothing) at no lag.</summary>
        public static double BlendForLag(double lagFrames) => lagFrames <= 0 ? 1 : 1 / (1 + lagFrames);
    }
}
