using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Media;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Playback;
using Prisc.VideoSDK.Thumbs;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// A seek to the instant just before a keyframe has to land on the keyframe before that one.
    /// The instant is a real one: a freeze of the last frame before a cut holds "the cut, less one
    /// tick". One tick is far finer than a stream time base, so a rounded conversion lands exactly
    /// on the later keyframe, nothing at or before the target is ever decoded, and the frame on
    /// offer is one the edit removed. Runs through the real decoders. Skips when the FFmpeg natives
    /// are absent.
    /// </summary>
    public class SeekBeforeKeyframeTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const long Second = 10_000_000;
        private const long OneFrame = Second / Fps + Second / 1000;

        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }
        }

        private static void RequireFFmpeg() =>
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

        private string EncodeFixture(int seconds)
        {
            string path = Path.Combine(Path.GetTempPath(), $"prisc-seek-keyframe-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);

            using var writer = new Mp4Writer(path, new Mp4WriterOptions { Width = W, Height = H, FpsNum = Fps, FpsDen = 1 });
            var bgra = new byte[W * H * 4];
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                for (int n = 0; n < seconds * Fps; n++)
                {
                    byte gray = (byte)(10 + 20 * (n / Fps));
                    for (int i = 0; i < bgra.Length; i += 4)
                    {
                        bgra[i] = bgra[i + 1] = bgra[i + 2] = gray;
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

        /// <summary>The fixture's latest keyframe past the first that leaves <paramref name="room"/>
        /// of stream after it. Read from the file, since the encoder places them.</summary>
        private static long KeyframeWithRoomAfter(string path, long endTicks, long room)
        {
            using var decoder = new ThumbnailDecoder(path, 0) { KeyframesOnly = true };
            long found = 0;
            while (decoder.DecodeNext(out long pts))
            {
                if (pts > 0 && pts + room <= endTicks)
                    found = pts;
            }

            Assert.True(found > 0, "the fixture has no usable keyframe");
            return found;
        }

        private static void AssertIsTheFrameBefore(long keyframe, long pts) =>
            Assert.True(pts < keyframe && pts >= keyframe - OneFrame,
                $"expected the frame before the keyframe at {keyframe}, got {pts}");

        // ------------------------------------------------------------------------------ decoders

        [Fact]
        public void The_render_decoder_lands_before_the_keyframe()
        {
            RequireFFmpeg();
            string path = EncodeFixture(12);
            long keyframe = KeyframeWithRoomAfter(path, 12 * Second, 0);

            using var pool = new FrameBufferPool();
            using var decoder = new SyncStreamDecoder(path, 0, pool);
            decoder.Seek(keyframe - 1);

            Assert.True(decoder.DecodeNext(out long pts, out var buffer, out _, out _, out _));
            buffer.Return();
            Assert.True(pts < keyframe, $"landed at {pts}, at or past the keyframe at {keyframe}");
        }

        [Fact]
        public void The_thumbnail_decoder_lands_before_the_keyframe()
        {
            RequireFFmpeg();
            string path = EncodeFixture(12);
            long keyframe = KeyframeWithRoomAfter(path, 12 * Second, 0);

            using var decoder = new ThumbnailDecoder(path, 0) { KeyframesOnly = false };
            decoder.Seek(keyframe - 1);

            Assert.True(decoder.DecodeNext(out long pts));
            Assert.True(pts < keyframe, $"landed at {pts}, at or past the keyframe at {keyframe}");
        }

        /// <summary>A render that has already read past the held frame (the freeze sits after later
        /// material of its recording) replays from a seek, and must not pick up the keyframe.</summary>
        [Fact]
        public void A_render_reading_back_to_just_before_a_keyframe_gets_the_frame_before_it()
        {
            RequireFFmpeg();
            string path = EncodeFixture(12);
            long keyframe = KeyframeWithRoomAfter(path, 12 * Second, Second);

            var project = NewProject();
            var sourceId = AddSource(project, path);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var frames = new SequentialFrameSource(project, cache);

            Assert.True(frames.TryGetFrame(sourceId, 0, keyframe + Second / 2, out _));
            Assert.True(frames.TryGetFrame(sourceId, 0, keyframe - 1, out var held));

            AssertIsTheFrameBefore(keyframe, held.PtsTicks);
        }

        // ------------------------------------------------------------------------------- preview

        /// <summary>The freeze the gap menu makes: clip, then a hold of its last frame across the
        /// deleted span, then the rest. The cut starts on a keyframe, and that keyframe lies inside
        /// the cut, so a seek landing on it presents nothing the frame source will take.</summary>
        private static Project HoldBeforeKeyframe(string path, long keyframe, out Guid sourceId)
        {
            var project = NewProject();
            sourceId = AddSource(project, path);
            var row = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            project.Tracks.Add(row);

            AddClip(project, row, sourceId, 0, keyframe, 0, freeze: false);
            AddClip(project, row, sourceId, keyframe, 2 * Second, keyframe - 1, freeze: true);
            AddClip(project, row, sourceId, keyframe + 2 * Second, Second, keyframe + Second, freeze: false);
            return project;
        }

        [Fact]
        public async Task A_paused_seek_into_a_hold_just_before_a_keyframe_shows_the_held_frame()
        {
            RequireFFmpeg();
            string path = EncodeFixture(12);
            long keyframe = KeyframeWithRoomAfter(path, 12 * Second, 2 * Second);
            var project = HoldBeforeKeyframe(path, keyframe, out var sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, new VideoOpenOptions { EnableHardwareDecode = false });

            // in from either side, as a person clicking along the ruler would: what was up before
            // must not be what stays up
            foreach (long at in new[] { keyframe + Second, keyframe + 5 * Second / 2, keyframe + Second / 2 })
            {
                await player.SeekAsync(new TimeSpan(at), SeekMode.Exact);

                if (at > keyframe + 2 * Second)
                {
                    Assert.True(WaitUntil(() => LanePts(player, cache, sourceId) is { } p && p > keyframe),
                        $"the seek past the hold did not land: pts={LanePts(player, cache, sourceId)}");
                    continue;
                }

                Assert.True(WaitUntil(() => LanePts(player, cache, sourceId) is { } p
                                            && p < keyframe && p >= keyframe - OneFrame),
                    $"seek to {at}: expected the frame before the keyframe at {keyframe}, the frame up is "
                    + LanePts(player, cache, sourceId));
            }
        }

        // ------------------------------------------------------------------------------ helpers

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings { WidthPx = W, HeightPx = H, FpsNum = Fps, FpsDen = 1, SampleRate = 48000 },
        };

        private static Guid AddSource(Project project, string path)
        {
            var id = Guid.NewGuid();
            project.Sources.Add(new Source
            {
                Id = id,
                Path = path,
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = W, Height = H, AvgFrameRateNum = Fps, AvgFrameRateDen = 1 } },
            });
            return id;
        }

        private static void AddClip(Project project, Track row, Guid sourceId, long start, long length, long sourceIn, bool freeze)
        {
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = row.Id,
                TimelineStartTicks = start,
                DurationTicks = length,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = sourceIn, Freeze = freeze },
            });
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                    return true;
                Thread.Sleep(10);
            }

            return condition();
        }

        /// <summary>Pumps the frame source, as the preview's draw operation does, and returns the
        /// latest surfaced pts.</summary>
        private static long? LanePts(CompositionPlayer player, FrameTextureCache cache, Guid sourceId)
        {
            if (!player.TryGetFrameSource(out var source, out long tl))
                return null;
            source.Pump(cache);
            return source.TryGetFrame(sourceId, 0, tl, 0, out var frame) ? frame.PtsTicks : null;
        }
    }
}
