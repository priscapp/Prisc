using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Prisc.VideoSDK.Ai;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Media;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Playback;
using SkiaSharp;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    // Real-decode round trip: Mp4Writer encodes a fixture whose frames are distinguishable
    // (grayscale ramp), SequentialFrameSource decodes it back and the frame-at-time-t pull is
    // asserted against known frame instants. Skips when the FFmpeg natives are absent (same
    // resolver as EncoderTests).
    public class SequentialFrameSourceTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30, Frames = 24;
        private const int GrayStep = 10; // frame n is solid gray n*10

        private static bool FFmpegAvailable => TestFFmpeg.Available;


        private static void RequireFFmpeg() =>
            Assert.SkipUnless(FFmpegAvailable,
                TestFFmpeg.SkipReason);

        // ----------------------------------------------------------------------------- fixture

        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }
        }

        /// <summary>Encodes a CFR mp4 whose frame n is solid gray n*GrayStep — bright enough
        /// steps to survive the BGRA→yuv420p→BGRA round trip unambiguously.</summary>
        private string EncodeFixture()
        {
            string path = Path.Combine(Path.GetTempPath(), $"prisc-seqsrc-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);

            using var writer = new Mp4Writer(path, new Mp4WriterOptions
            {
                Width = W,
                Height = H,
                FpsNum = Fps,
                FpsDen = 1,
            });

            var bgra = new byte[W * H * 4];
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                for (int n = 0; n < Frames; n++)
                {
                    byte v = (byte)(n * GrayStep);
                    for (int i = 0; i < bgra.Length; i += 4)
                    {
                        bgra[i] = v;
                        bgra[i + 1] = v;
                        bgra[i + 2] = v;
                        bgra[i + 3] = 0xFF;
                    }
                    writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), W * 4, W, H, n);
                }
            }
            finally
            {
                pin.Free();
            }

            writer.Finish();
            return path;
        }

        private static Project ProjectFor(string path, out Guid sourceId)
        {
            sourceId = Guid.NewGuid();
            var p = new Project
            {
                Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = Fps, FpsDen = 1, SampleRate = 48000 },
            };
            p.Sources.Add(new Source
            {
                Id = sourceId,
                Path = path,
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 } },
            });
            return p;
        }

        private static byte GrayOf(SKImage image)
        {
            var native = Marshal.AllocHGlobal(4);
            try
            {
                var info = new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
                Assert.True(image.ReadPixels(info, native, 4, image.Width / 2, image.Height / 2));
                var px = new byte[4];
                Marshal.Copy(native, px, 0, 4);
                return px[1]; // G of the gray
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        private static long FrameTicks(int n) => TimeBase.FrameIndexToTicks(n, Fps, 1);

        // ------------------------------------------------------------------------------- tests

        [Fact]
        public void Delivers_monotonic_frames_at_their_instants()
        {
            RequireFFmpeg();
            string path = EncodeFixture();
            var project = ProjectFor(path, out var sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);

            long lastPts = long.MinValue;
            for (int n = 0; n < Frames; n++)
            {
                Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(n), out var frame));
                Assert.NotNull(frame.Image);

                // pts is the frame's own instant (mp4 time base rescales to the same rounded tick)
                Assert.InRange(frame.PtsTicks, FrameTicks(n) - 10_000, FrameTicks(n) + 10_000);
                Assert.True(frame.PtsTicks >= lastPts, "frame delivery went backwards");
                lastPts = frame.PtsTicks;

                int expected = n * GrayStep;
                Assert.InRange(GrayOf(frame.Image), Math.Max(0, expected - 12), Math.Min(255, expected + 12));
            }

            // the cache holds exactly the one live stream entry
            Assert.Equal(1, cache.Count);
        }

        [Fact]
        public void Holds_frame_between_instants_and_past_eof()
        {
            RequireFFmpeg();
            string path = EncodeFixture();
            var project = ProjectFor(path, out var sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);

            // halfway between frame 3 and 4 → frame 3 holds
            long midTicks = (FrameTicks(3) + FrameTicks(4)) / 2;
            Assert.True(source.TryGetFrame(sourceId, 0, midTicks, out var frame));
            Assert.InRange(frame.PtsTicks, FrameTicks(3) - 10_000, FrameTicks(3) + 10_000);
            Assert.InRange(GrayOf(frame.Image), 3 * GrayStep - 12, 3 * GrayStep + 12);

            // far past the last frame → the last frame holds (no failure, no rewind)
            Assert.True(source.TryGetFrame(sourceId, 0, 10 * TimeBase.TicksPerSecond, out frame));
            long lastInstant = FrameTicks(Frames - 1);
            Assert.InRange(frame.PtsTicks, lastInstant - 10_000, lastInstant + 10_000);
            int lastGray = (Frames - 1) * GrayStep;
            Assert.InRange(GrayOf(frame.Image), lastGray - 12, lastGray + 12);

            // and holds again on a later request
            Assert.True(source.TryGetFrame(sourceId, 0, 20 * TimeBase.TicksPerSecond, out frame));
            Assert.InRange(frame.PtsTicks, lastInstant - 10_000, lastInstant + 10_000);
        }

        [Fact]
        public void Regressing_request_repositions_and_unknown_source_throws()
        {
            RequireFFmpeg();
            string path = EncodeFixture();
            var project = ProjectFor(path, out var sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);

            Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(5), out _));
            Assert.Equal(0, source.RepositionCount);

            // a timeline that reads this stream out of source order seeks back instead of failing
            Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(1), out var frame));
            Assert.Equal(1, source.RepositionCount);
            Assert.InRange(frame.PtsTicks, FrameTicks(1) - 10_000, FrameTicks(1) + 10_000);
            Assert.InRange(GrayOf(frame.Image), GrayStep - 12, GrayStep + 12);

            // forward again from there, no further seek
            Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(2), out frame));
            Assert.Equal(1, source.RepositionCount);
            Assert.InRange(GrayOf(frame.Image), 2 * GrayStep - 12, 2 * GrayStep + 12);

            Assert.Throws<ArgumentException>(() => source.TryGetFrame(Guid.NewGuid(), 0, 0, out _));
        }

        /// <summary>A matte sidecar written on the same PTS grid as its source, at a different
        /// resolution, with frame n solid gray n*GrayStep — distinguishable mattes, so the
        /// pairing (latest-PTS-at-or-before per requested instant) is directly observable.</summary>
        private string EncodeMatteSidecar(string cacheDir, Guid sourceId, string sourcePath, int size)
        {
            string mattePath = AiSidecars.MattePath(cacheDir, sourceId, 0);
            using (var writer = new Mp4Writer(mattePath, new Mp4WriterOptions
            {
                Width = size,
                Height = size,
                FpsNum = Fps,
                FpsDen = 1,
            }))
            {
                var bgra = new byte[size * size * 4];
                var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
                try
                {
                    for (int n = 0; n < Frames; n++)
                    {
                        byte v = (byte)(n * GrayStep);
                        for (int i = 0; i < bgra.Length; i += 4)
                        {
                            bgra[i] = v;
                            bgra[i + 1] = v;
                            bgra[i + 2] = v;
                            bgra[i + 3] = 0xFF;
                        }
                        writer.SubmitVideoFrame(pin.AddrOfPinnedObject(), size * 4, size, size, n);
                    }
                }
                finally
                {
                    pin.Free();
                }
                writer.Finish();
            }

            Assert.True(AiSidecars.TryWriteCompanion(mattePath,
                AiSidecars.DescribeSource(sourcePath, size, size)));
            return mattePath;
        }

        /// <summary>Adds the segmented-effect item that makes the stream want a matte.</summary>
        private static void AddSegmentedItem(Project project, Guid sourceId)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = FrameTicks(Frames),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0 },
                Effect = new VideoEffect { Kind = VideoEffectKind.BgRemove },
            });
        }

        [Fact]
        public void Attaches_masks_from_a_valid_matte_sidecar_paired_by_pts()
        {
            RequireFFmpeg();
            string cacheDir = Path.Combine(Path.GetTempPath(), $"prisc-seqsrc-matte-{Guid.NewGuid():N}");
            Directory.CreateDirectory(cacheDir);
            try
            {
                string path = EncodeFixture();
                var project = ProjectFor(path, out var sourceId);
                AddSegmentedItem(project, sourceId);
                EncodeMatteSidecar(cacheDir, sourceId, path, size: 32); // half the frame size

                using var factory = new CpuSurfaceFactory();
                using var cache = new FrameTextureCache(factory);
                using var source = new SequentialFrameSource(project, cache, null, cacheDir);

                // masks pair by PTS at the frames' own instants, at their own resolution
                Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(5), out var frame));
                Assert.NotNull(frame.Mask);
                Assert.Equal(32, frame.Mask.Width);
                Assert.InRange(GrayOf(frame.Mask), 5 * GrayStep - 12, 5 * GrayStep + 12);

                Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(6), out frame));
                Assert.InRange(GrayOf(frame.Mask), 6 * GrayStep - 12, 6 * GrayStep + 12);

                // a backwards request repositions the matte in step with the frames
                Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(2), out frame));
                Assert.InRange(GrayOf(frame.Mask), 2 * GrayStep - 12, 2 * GrayStep + 12);
                Assert.InRange(GrayOf(frame.Image), 2 * GrayStep - 12, 2 * GrayStep + 12);
            }
            finally
            {
                try { Directory.Delete(cacheDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        [Fact]
        public void Each_lane_pairs_its_frames_with_a_matte_of_its_own()
        {
            RequireFFmpeg();
            string cacheDir = Path.Combine(Path.GetTempPath(), $"prisc-seqsrc-matte-{Guid.NewGuid():N}");
            Directory.CreateDirectory(cacheDir);
            try
            {
                string path = EncodeFixture();
                var project = ProjectFor(path, out var sourceId);
                AddSegmentedItem(project, sourceId);
                EncodeMatteSidecar(cacheDir, sourceId, path, size: 32);

                using var factory = new CpuSurfaceFactory();
                using var cache = new FrameTextureCache(factory);
                using var source = new SequentialFrameSource(project, cache, null, cacheDir);

                // lane 1 several frames ahead of lane 0, each asked twice: the second answer comes
                // from the cache, and must be its own lane's matte
                for (int pass = 0; pass < 2; pass++)
                {
                    Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(3), 0, out var low));
                    Assert.InRange(GrayOf(low.Mask), 3 * GrayStep - 12, 3 * GrayStep + 12);
                    Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(15), 1, out var high));
                    Assert.InRange(GrayOf(high.Image), 15 * GrayStep - 12, 15 * GrayStep + 12);
                    Assert.InRange(GrayOf(high.Mask), 15 * GrayStep - 12, 15 * GrayStep + 12);
                }

                Assert.Equal(0, source.RepositionCount);
            }
            finally
            {
                try { Directory.Delete(cacheDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        [Fact]
        public void A_sidecar_without_a_valid_companion_delivers_maskless_frames()
        {
            RequireFFmpeg();
            string cacheDir = Path.Combine(Path.GetTempPath(), $"prisc-seqsrc-matte-{Guid.NewGuid():N}");
            Directory.CreateDirectory(cacheDir);
            try
            {
                string path = EncodeFixture();
                var project = ProjectFor(path, out var sourceId);
                AddSegmentedItem(project, sourceId);
                string mattePath = EncodeMatteSidecar(cacheDir, sourceId, path, size: 32);
                File.Delete(AiSidecars.CompanionPath(mattePath)); // now stale by the validity rule

                using var factory = new CpuSurfaceFactory();
                using var cache = new FrameTextureCache(factory);
                using var source = new SequentialFrameSource(project, cache, null, cacheDir);

                Assert.True(source.TryGetFrame(sourceId, 0, FrameTicks(3), out var frame));
                Assert.NotNull(frame.Image);
                Assert.Null(frame.Mask);
            }
            finally
            {
                try { Directory.Delete(cacheDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        // ------------------------------------------------------------- one source played twice

        private static Item AddClip(Project project, double centerX, long start, long duration, long sourceIn,
            Guid sourceId)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = project.Tracks.Count };
            project.Tracks.Add(track);
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                // side by side: each clip's picture can be read off the canvas on its own
                Transform = new Transform { X = centerX, Y = 0.5, Scale = 0.5 },
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = sourceIn },
            };
            project.Items.Add(item);
            return item;
        }

        /// <summary>The canvas's gray at each clip's center, at a timeline instant.</summary>
        private static (byte Left, byte Right) ComposeGrays(Project project, IFrameSource source,
            CpuSurfaceFactory factory, long t)
        {
            using var surface = factory.CreateSurface(W, H);
            FrameComposer.Compose(project, t, source, surface.Canvas, W, H);
            int rowBytes = W * 4;
            var native = Marshal.AllocHGlobal(rowBytes * H);
            try
            {
                Assert.True(factory.TryReadPixels(surface, W, H, native, rowBytes));
                var pixels = new byte[rowBytes * H];
                Marshal.Copy(native, pixels, 0, pixels.Length);
                int row = (H / 2) * rowBytes;
                return (pixels[row + (W / 4) * 4 + 1], pixels[row + (W * 3 / 4) * 4 + 1]);
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        [Fact]
        public void Overlapping_clips_never_reposition_and_show_what_each_decodes_alone()
        {
            RequireFFmpeg();
            string path = EncodeFixture();

            // the left clip plays source frames 0.., the right plays source frames 10.. from
            // timeline frame 6, overlapping it throughout
            var both = ProjectFor(path, out var bothId);
            AddClip(both, 0.25, 0, FrameTicks(Frames), 0, bothId);
            AddClip(both, 0.75, FrameTicks(6), FrameTicks(12), FrameTicks(10), bothId);

            var left = ProjectFor(path, out var leftId);
            AddClip(left, 0.25, 0, FrameTicks(Frames), 0, leftId);
            var right = ProjectFor(path, out var rightId);
            AddClip(right, 0.75, FrameTicks(6), FrameTicks(12), FrameTicks(10), rightId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var aloneCache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(both, cache);
            using var leftAlone = new SequentialFrameSource(left, aloneCache);
            using var rightAlone = new SequentialFrameSource(right, aloneCache);

            for (int n = 0; n < Frames; n++)
            {
                var (l, r) = ComposeGrays(both, source, factory, FrameTicks(n));
                Assert.InRange(l, n * GrayStep - 12, n * GrayStep + 12);
                Assert.Equal(ComposeGrays(left, leftAlone, factory, FrameTicks(n)).Left, l);
                Assert.Equal(ComposeGrays(right, rightAlone, factory, FrameTicks(n)).Right, r);
                if (n >= 6 && n < 18)
                    Assert.InRange(r, (n + 4) * GrayStep - 12, (n + 4) * GrayStep + 12);
            }

            Assert.Equal(0, source.RepositionCount);
            Assert.Equal(2, cache.Count); // a frame per lane
        }

        [Fact]
        public void A_duplicate_row_reads_one_lane_and_never_repositions()
        {
            RequireFFmpeg();
            var project = ProjectFor(EncodeFixture(), out var sourceId);
            AddClip(project, 0.25, 0, FrameTicks(Frames), 0, sourceId);
            AddClip(project, 0.75, 0, FrameTicks(Frames), 0, sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);

            for (int n = 0; n < Frames; n += 3)
            {
                var (l, r) = ComposeGrays(project, source, factory, FrameTicks(n));
                Assert.InRange(l, n * GrayStep - 12, n * GrayStep + 12);
                Assert.Equal(l, r);
            }

            Assert.Equal(0, source.RepositionCount);
            Assert.Equal(1, cache.Count);
        }

        [Fact]
        public void Lane_0_of_a_repeat_on_one_row_seeks_back_once_exactly_as_it_did()
        {
            RequireFFmpeg();
            var project = ProjectFor(EncodeFixture(), out var sourceId);
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            project.Tracks.Add(track);
            foreach (var (start, sourceIn) in new[] { (0, 0), (12, 3) })
            {
                project.Items.Add(new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = track.Id,
                    TimelineStartTicks = FrameTicks(start),
                    DurationTicks = FrameTicks(12),
                    Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = FrameTicks(sourceIn) },
                });
            }

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);
            using var surface = factory.CreateSurface(W, H);
            for (int n = 0; n < 24; n++)
                FrameComposer.Compose(project, FrameTicks(n), source, surface.Canvas, W, H);

            Assert.Equal(1, source.RepositionCount);
        }

        [Fact]
        public void Past_the_lane_cap_a_render_still_gets_the_right_frames_through_its_seek_path()
        {
            // the known limit, pinned: a clip past the cap shares the last lane, and only the
            // preview pays with a wrong frame. Here two clips take turns seeking lane 3.
            RequireFFmpeg();
            var project = ProjectFor(EncodeFixture(), out var sourceId);
            for (int i = 0; i < StreamLanes.MaxLanes; i++)
                AddClip(project, 0.25, 0, FrameTicks(8), FrameTicks(i), sourceId);
            AddClip(project, 0.75, 0, FrameTicks(8), FrameTicks(10), sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);

            for (int n = 0; n < 8; n++)
            {
                var (l, r) = ComposeGrays(project, source, factory, FrameTicks(n));
                // on top at the left is the last clip of the first four, source frame 3 + n
                Assert.InRange(l, (3 + n) * GrayStep - 12, (3 + n) * GrayStep + 12);
                Assert.InRange(r, (10 + n) * GrayStep - 12, (10 + n) * GrayStep + 12);
            }

            Assert.True(source.RepositionCount > 0, "the two clips in lane 3 read it out of order");
            Assert.Equal(StreamLanes.MaxLanes, cache.Count);
        }

        [Fact]
        public void Composes_encoded_media_through_frame_composer()
        {
            RequireFFmpeg();
            string path = EncodeFixture();
            var project = ProjectFor(path, out var sourceId);

            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = 0,
                DurationTicks = FrameTicks(Frames),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = 0 },
            });

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var source = new SequentialFrameSource(project, cache);
            using var surface = factory.CreateSurface(W, H);

            FrameComposer.Compose(project, FrameTicks(10), source, surface.Canvas, W, H);

            int rowBytes = W * 4;
            var native = Marshal.AllocHGlobal(rowBytes * H);
            try
            {
                Assert.True(factory.TryReadPixels(surface, W, H, native, rowBytes));
                var pixels = new byte[rowBytes * H];
                Marshal.Copy(native, pixels, 0, pixels.Length);

                int center = (H / 2) * rowBytes + (W / 2) * 4;
                int expected = 10 * GrayStep;
                Assert.InRange(pixels[center + 1], expected - 12, expected + 12); // G of the gray
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }
    }
}
