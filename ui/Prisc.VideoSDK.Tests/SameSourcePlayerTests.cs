using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Prisc.VideoSDK.Ai;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Media;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Playback;
using Prisc.UI.VideoEditor;
using SkiaSharp;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The preview playing one recording twice, through the real demux and decode pipelines: a span
    /// repeated on a row, and two clips showing different moments at once. Every frame of the
    /// fixture is told apart by its pts, and by its gray where pixels are compared. Skips when the
    /// FFmpeg natives are absent. Timing assertions poll with generous deadlines.
    /// </summary>
    public class SameSourcePlayerTests : IDisposable
    {
        private const int W = 64, H = 64, Fps = 30;
        private const long Second = 10_000_000;
        private const long FrameTicks = Second / Fps;

        /// <summary>Close enough to be the frame asked for: a frame, and a little slop.</summary>
        private const long OneFrame = FrameTicks + Second / 1000;

        private static void RequireFFmpeg() =>
            Assert.SkipUnless(TestFFmpeg.Available, TestFFmpeg.SkipReason);

        private static VideoOpenOptions Options() => new VideoOpenOptions { EnableHardwareDecode = false };

        // ----------------------------------------------------------------------------- fixtures

        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
            {
                try { File.Delete(f); }
                catch { /* best effort */ }
            }
        }

        /// <summary>A video-only recording whose second n is solid gray <c>10 + 20n</c>: bright
        /// enough steps to survive the color round trip, and to read a frame's second off pixels.</summary>
        private string EncodeFixture(int seconds, int fps = Fps)
        {
            string path = Path.Combine(Path.GetTempPath(), $"prisc-same-source-test-{Guid.NewGuid():N}.mp4");
            _tempFiles.Add(path);

            using var writer = new Mp4Writer(path, new Mp4WriterOptions { Width = W, Height = H, FpsNum = fps, FpsDen = 1 });
            var bgra = new byte[W * H * 4];
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                for (int n = 0; n < seconds * fps; n++)
                {
                    byte gray = (byte)(10 + 20 * (n / fps));
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

        private static Track AddRow(Project project)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = project.Tracks.Count };
            project.Tracks.Add(track);
            return track;
        }

        private static Item AddClip(Project project, Track row, Guid sourceId, double start, double length, double sourceIn)
        {
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = row.Id,
                TimelineStartTicks = Ticks(start),
                DurationTicks = Ticks(length),
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = Ticks(sourceIn) },
            };
            project.Items.Add(item);
            return item;
        }

        private static long Ticks(double seconds) => (long)(seconds * Second);

        // ------------------------------------------------------------------------------ helpers

        private static bool WaitUntil(Func<bool> condition, int timeoutMs = 15000)
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
        /// lane's latest surfaced pts.</summary>
        private static long? LanePts(CompositionPlayer player, FrameTextureCache cache, Guid sourceId, int lane)
        {
            if (!player.TryGetFrameSource(out var source, out long tl))
                return null;
            source.Pump(cache);
            return source.TryGetFrame(sourceId, 0, tl, lane, out var frame) ? frame.PtsTicks : null;
        }

        private static bool Lands(long? pts, double seconds) => pts is { } p && Math.Abs(p - Ticks(seconds)) <= OneFrame;

        /// <summary>Records each request the composer makes against the real frame source, and
        /// what came back.</summary>
        private sealed class ComposerProbe : IFrameSource
        {
            private readonly IFrameSource _inner;

            public ComposerProbe(IFrameSource inner) => _inner = inner;

            public List<(int Lane, long Requested, long Pts)> Requests { get; } = new List<(int, long, long)>();

            public bool TryGetFrame(Guid sourceId, int streamIndex, long sourceTimeTicks, out FrameRef frame)
                => throw new InvalidOperationException("the composer must always say which lane it wants");

            public bool TryGetFrame(Guid sourceId, int streamIndex, long sourceTimeTicks, int lane, out FrameRef frame)
            {
                bool got = _inner.TryGetFrame(sourceId, streamIndex, sourceTimeTicks, lane, out frame);
                if (got)
                    Requests.Add((lane, sourceTimeTicks, frame.PtsTicks));
                return got;
            }
        }

        /// <summary>One composed frame's requests: pump, then compose at the player's instant.</summary>
        private static List<(int Lane, long Requested, long Pts)> ComposeOnce(CompositionPlayer player,
            Project project, FrameTextureCache cache, SKSurface surface, out long atTicks)
        {
            player.TryGetFrameSource(out var source, out atTicks);
            source.Pump(cache);
            var probe = new ComposerProbe(source);
            FrameComposer.Compose(project, atTicks, probe, surface.Canvas, W, H);
            return probe.Requests;
        }

        // ----------------------------------------------------------------- a span played twice

        /// <summary>Source [1 s, 3 s) plays twice on one row: at t=1 in the original, at t=3 as a
        /// paste. Source [3 s, 4 s) is cut out, and the row ends in source [4 s, 5 s).</summary>
        private static Project RepeatOnOneRow(string path, out Guid sourceId)
        {
            var project = NewProject();
            sourceId = AddSource(project, path);
            var row = AddRow(project);
            AddClip(project, row, sourceId, 0, 3, 0);
            AddClip(project, row, sourceId, 3, 2, 1);
            AddClip(project, row, sourceId, 5, 1, 4);
            return project;
        }

        [Fact]
        public async Task A_paused_seek_into_a_repeat_keeps_the_playhead_there()
        {
            RequireFFmpeg();
            var project = RepeatOnOneRow(EncodeFixture(6), out var sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());

            await player.SeekAsync(new TimeSpan(Ticks(4)), SeekMode.Exact);

            // source 2 s, and the frame's adoption must not drag the playhead back to t=2, which
            // is where that frame first plays
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), 2)),
                $"the seek did not land: pts={LanePts(player, cache, sourceId, 0)}");
            Thread.Sleep(100); // let the adoption, which runs after the frame arrives, have its say
            Assert.True(Math.Abs(player.Position.Ticks - Ticks(4)) <= OneFrame,
                $"the playhead left the paste: {player.Position}");
        }

        [Fact]
        public async Task A_paused_seek_to_a_paste_starting_between_frames_keeps_the_playhead_there()
        {
            // the clip opens on the frame before its in-point, which only the original plays:
            // adopting that frame's pts would throw the playhead 2 s back
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(6));
            var row = AddRow(project);
            AddClip(project, row, sourceId, 0, 3, 0);
            AddClip(project, row, sourceId, 3, 2, 1 + 1.0 / (Fps * 2));

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());

            await player.SeekAsync(new TimeSpan(Ticks(3)), SeekMode.Exact);

            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), 1)),
                $"pts={LanePts(player, cache, sourceId, 0)}");
            Thread.Sleep(100);
            Assert.Equal(new TimeSpan(Ticks(3)), player.Position);
        }

        [Fact]
        public async Task A_paused_seek_into_the_original_stays_in_the_original()
        {
            RequireFFmpeg();
            var project = RepeatOnOneRow(EncodeFixture(6), out var sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());

            await player.SeekAsync(new TimeSpan(Ticks(2)), SeekMode.Exact);

            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), 2)));
            Thread.Sleep(100);
            Assert.True(Math.Abs(player.Position.Ticks - Ticks(2)) <= OneFrame, $"playhead at {player.Position}");
        }

        [Fact]
        public async Task Playing_through_a_repeat_presents_its_frames_and_nothing_cut()
        {
            RequireFFmpeg();
            var project = RepeatOnOneRow(EncodeFixture(6), out var sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());

            await player.SeekAsync(new TimeSpan(Ticks(2.7)), SeekMode.Exact);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), 2.7)));
            player.Play();

            // while the playhead is well inside the paste, frames must move through source [1, 3):
            // mapped to the original they would be 2 s late and dropped, freezing the clip
            var inPaste = new HashSet<long>();
            var all = new List<long>();
            Assert.True(WaitUntil(() =>
            {
                var pts = LanePts(player, cache, sourceId, 0);
                long at = player.Position.Ticks;
                if (pts is { } p)
                {
                    all.Add(p);
                    if (at >= Ticks(3.4) && at < Ticks(4.9))
                        inPaste.Add(p);
                }

                return at >= Ticks(5.3);
            }), $"never reached the last clip: pos={player.Position}");

            Assert.True(inPaste.Count >= 8, $"the paste barely played: {inPaste.Count} distinct frames");
            foreach (long p in inPaste)
                Assert.InRange(p, Ticks(1), Ticks(3));
            foreach (long p in all)
                Assert.False(p >= Ticks(3) && p < Ticks(4), $"a frame from the cut reached the canvas: {p}");
        }

        [Fact]
        public async Task A_frame_step_inside_a_repeat_stays_in_the_repeat()
        {
            RequireFFmpeg();
            var project = RepeatOnOneRow(EncodeFixture(6), out var sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await player.SeekAsync(new TimeSpan(Ticks(4)), SeekMode.Exact);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), 2)));

            await player.StepFrameAsync(1);

            Assert.True(Math.Abs(player.Position.Ticks - Ticks(4)) <= 2 * OneFrame, $"playhead at {player.Position}");
        }

        [Fact]
        public async Task A_frame_step_off_the_end_of_a_repeat_goes_on_to_the_next_clip()
        {
            // the next frame after the paste is one the original plays, 2 s behind the playhead
            RequireFFmpeg();
            var project = RepeatOnOneRow(EncodeFixture(6), out var sourceId);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await player.SeekAsync(new TimeSpan(Ticks(5) - 2 * FrameTicks), SeekMode.Exact);
            Assert.True(WaitUntil(() => LanePts(player, cache, sourceId, 0) >= Ticks(2.9)));

            for (int step = 0; step < 3; step++)
            {
                long before = player.Position.Ticks;
                await player.StepFrameAsync(1);
                Assert.True(player.Position.Ticks >= before, $"the step went back: {before} -> {player.Position.Ticks}");
            }

            Assert.True(player.Position.Ticks >= Ticks(5) - FrameTicks, $"playhead at {player.Position}");
            Assert.True(WaitUntil(() => LanePts(player, cache, sourceId, 0) >= Ticks(4)),
                $"the next clip's frame never landed: pts={LanePts(player, cache, sourceId, 0)}");
        }

        [Fact]
        public async Task A_frame_step_off_a_repeat_with_nothing_after_it_never_goes_back()
        {
            // the row ends in a paste: the frames after it are played only by the original, 7 s back
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(10));
            var row = AddRow(project);
            AddClip(project, row, sourceId, 0, 8, 0);
            AddClip(project, row, sourceId, 8, 2, 1);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await player.SeekAsync(new TimeSpan(Ticks(10) - 2 * FrameTicks), SeekMode.Exact);
            Assert.True(WaitUntil(() => LanePts(player, cache, sourceId, 0) >= Ticks(2.9)));

            for (int step = 0; step < 6; step++)
            {
                long before = player.Position.Ticks;
                await player.StepFrameAsync(1);
                Assert.True(player.Position.Ticks >= before, $"the step went back: {before} -> {player.Position.Ticks}");
            }

            Assert.Equal(Ticks(10), player.Position.Ticks);
        }

        [Fact]
        public async Task A_frame_step_after_playing_stays_with_the_picture()
        {
            // a plain row with no repeat and no seam to hop. Once playback stops, the next frame can
            // sit a few ms behind the clock, and the step must not take that for the end of the clip
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(12));
            var row = AddRow(project);
            AddClip(project, row, sourceId, 0, 6, 0);
            AddClip(project, row, sourceId, 6, 6, 6);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());

            for (int trial = 0; trial < 24; trial++)
            {
                await player.SeekAsync(new TimeSpan(Ticks(1)), SeekMode.Exact);
                player.Play();
                await Task.Delay(60 + 20 * (trial % 8));

                // odd trials are the arrow key while playing: the step pauses and steps at once
                if (trial % 2 == 0)
                {
                    player.Pause();
                    await Task.Delay(30);
                }

                long before = player.Position.Ticks;
                await player.StepFrameAsync(1);

                Assert.True(Math.Abs(player.Position.Ticks - before) <= Ticks(0.5),
                    $"trial {trial}: the step moved the playhead {before} -> {player.Position.Ticks}");
            }
        }

        [Fact]
        public async Task A_frame_step_from_a_scrub_follows_the_picture_back_to_it()
        {
            // a fast seek holds the clock on its target and shows the keyframe before it, which puts
            // the next frame well behind the playhead on demand
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(12));
            var row = AddRow(project);
            AddClip(project, row, sourceId, 0, 6, 0);
            AddClip(project, row, sourceId, 6, 6, 6);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await player.SeekAsync(new TimeSpan(Ticks(1.5)), SeekMode.Fast);
            Assert.True(WaitUntil(() => LanePts(player, cache, sourceId, 0) < Ticks(1.5)));

            await player.StepFrameAsync(1);

            Assert.True(player.Position.Ticks < Ticks(1.5), $"playhead at {player.Position}");
            Assert.True(WaitUntil(() => Math.Abs((LanePts(player, cache, sourceId, 0) ?? 0) - player.Position.Ticks) <= OneFrame),
                $"pos={player.Position} pts={LanePts(player, cache, sourceId, 0)}");
        }

        [Fact]
        public async Task A_decoder_running_past_the_furthest_source_never_surfaces_it_before_the_hop()
        {
            RequireFFmpeg();

            // the file runs on past 1 s, the edit does not, and the row then jumps back
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(4));
            var row = AddRow(project);
            AddClip(project, row, sourceId, 0, 1, 0);
            AddClip(project, row, sourceId, 1, 3, 0.2);
            project.Items[1].DurationTicks = Ticks(0.7); // source [0.2, 0.9): stops short of the first clip's end

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await player.SeekAsync(new TimeSpan(Ticks(0.5)), SeekMode.Exact);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), 0.5)));

            player.Play();

            // pumping flat out: the frame in question lives only until the hop lands
            var seen = new List<long>();
            var deadline = Stopwatch.StartNew();
            while (player.Position.Ticks < Ticks(1.3) && deadline.ElapsedMilliseconds < 8000)
            {
                if (LanePts(player, cache, sourceId, 0) is { } p)
                    seen.Add(p);
            }

            Assert.True(player.Position.Ticks >= Ticks(1.3), $"pos={player.Position}");
            foreach (long p in seen)
                Assert.True(p < Ticks(1), $"material past the edit reached the canvas: {p}");
            Assert.Contains(seen, p => p >= Ticks(0.2) && p < Ticks(0.9));
        }

        // ------------------------------------------------------------------- two clips at once

        /// <summary>The whole recording, with source [5 s, 7 s) over it at t=1.</summary>
        private static Project OverlapOf(string path, out Guid sourceId, double fileLength)
        {
            var project = NewProject();
            sourceId = AddSource(project, path);
            AddClip(project, AddRow(project), sourceId, 0, fileLength, 0);
            AddClip(project, AddRow(project), sourceId, 1, 2, 5);
            return project;
        }

        [Fact]
        public async Task Paused_each_lane_holds_the_frame_the_composer_asks_for()
        {
            RequireFFmpeg();
            var project = OverlapOf(EncodeFixture(8), out var sourceId, 8);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var surface = factory.CreateSurface(W, H);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            Assert.Equal(2, player.DecoderOpenCount);

            await player.SeekAsync(new TimeSpan(Ticks(2)), SeekMode.Exact);

            List<(int Lane, long Requested, long Pts)> requests = null;
            Assert.True(WaitUntil(() =>
            {
                requests = ComposeOnce(player, project, cache, surface, out _);
                return requests.Count == 2 && requests.TrueForAll(r => Math.Abs(r.Pts - r.Requested) <= OneFrame);
            }), "the composer's requests: " + string.Join(", ", requests ?? new()));

            Assert.Equal(0, requests[0].Lane);
            Assert.Equal(1, requests[1].Lane);
            Assert.True(Math.Abs(requests[0].Pts - Ticks(2)) <= OneFrame, $"lane 0 at {requests[0].Pts}");
            Assert.True(Math.Abs(requests[1].Pts - Ticks(6)) <= OneFrame, $"lane 1 at {requests[1].Pts}");
            Assert.True(Math.Abs(player.Position.Ticks - Ticks(2)) <= OneFrame, $"playhead at {player.Position}");
        }

        /// <summary>The known limit of section 7.2, in the preview: past the cap no decoder opens, and
        /// the clip over it is handed the frame the last lane holds, whichever of the two clips
        /// sharing that lane it belongs to. A render gets both right through its seek path
        /// (<see cref="SequentialFrameSourceTests"/>).</summary>
        [Fact]
        public async Task Past_the_lane_cap_a_clip_gets_the_last_lanes_frame_and_no_decoder()
        {
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(8));
            for (int i = 0; i <= StreamLanes.MaxLanes; i++)
                AddClip(project, AddRow(project), sourceId, 0, 2, i);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var surface = factory.CreateSurface(W, H);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            Assert.Equal(StreamLanes.MaxLanes, player.DecoderOpenCount);

            await player.SeekAsync(new TimeSpan(Ticks(1)), SeekMode.Exact);

            List<(int Lane, long Requested, long Pts)> requests = null;
            Assert.True(WaitUntil(() =>
            {
                requests = ComposeOnce(player, project, cache, surface, out _);
                return requests.Count == StreamLanes.MaxLanes + 1
                    && requests.Take(StreamLanes.MaxLanes - 1).All(r => Math.Abs(r.Pts - r.Requested) <= OneFrame);
            }), "the composer's requests: " + string.Join(", ", requests ?? new()));

            Assert.Equal(new[] { 0, 1, 2, StreamLanes.MaxLanes - 1, StreamLanes.MaxLanes - 1 }, requests.Select(r => r.Lane));

            // the two clips sharing the last lane asked for different moments and got one frame
            var (shared, excess) = (requests[^2], requests[^1]);
            Assert.NotEqual(shared.Requested, excess.Requested);
            Assert.Equal(shared.Pts, excess.Pts);
            Assert.True(Math.Abs(shared.Pts - shared.Requested) <= OneFrame || Math.Abs(excess.Pts - excess.Requested) <= OneFrame,
                $"the shared frame at {shared.Pts} is neither clip's: {shared.Requested}, {excess.Requested}");
        }

        [Fact]
        public async Task Playing_both_lanes_keep_up_with_what_the_composer_asks_for()
        {
            RequireFFmpeg();
            var project = OverlapOf(EncodeFixture(8), out var sourceId, 8);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var surface = factory.CreateSurface(W, H);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());

            await player.SeekAsync(new TimeSpan(Ticks(0.8)), SeekMode.Exact);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), 0.8)));
            player.Play();

            // in the overlap's settled middle, each composed frame reads two different moments
            var lane0 = new HashSet<long>();
            var lane1 = new HashSet<long>();
            Assert.True(WaitUntil(() =>
            {
                var requests = ComposeOnce(player, project, cache, surface, out long at);
                if (at >= Ticks(1.5) && at < Ticks(2.9) && requests.Count == 2)
                {
                    foreach (var r in requests)
                        Assert.True(Math.Abs(r.Pts - r.Requested) < Second / 2,
                            $"lane {r.Lane} at t={at}: asked {r.Requested}, shows {r.Pts}");
                    lane0.Add(requests[0].Pts);
                    lane1.Add(requests[1].Pts);
                }

                return at >= Ticks(3.2);
            }));

            Assert.True(lane0.Count >= 8, $"lane 0 barely moved: {lane0.Count} frames");
            Assert.True(lane1.Count >= 8, $"lane 1 barely moved: {lane1.Count} frames");
        }

        [Fact]
        public async Task A_clip_edge_on_a_lane_hops_only_that_lane()
        {
            RequireFFmpeg();
            var project = OverlapOf(EncodeFixture(8), out var sourceId, 8);
            (Guid, int, int) primary = (sourceId, 0, 0), lane1 = (sourceId, 0, 1);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await player.SeekAsync(new TimeSpan(Ticks(0.6)), SeekMode.Exact);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), 0.6)));

            int primarySeeks = player.VideoSeekCount(primary);
            int laneSeeks = player.VideoSeekCount(lane1);

            player.Play();
            Assert.True(WaitUntil(() => player.Position.Ticks >= Ticks(1.5)), $"pos={player.Position}");
            Assert.True(WaitUntil(() => player.VideoSeekCount(lane1) > laneSeeks), "entering the clip did not hop its lane");
            Assert.Equal(primarySeeks, player.VideoSeekCount(primary));
        }

        [Fact]
        public async Task A_non_primary_seam_hops_it_without_seeking_the_primary_or_moving_the_clock()
        {
            RequireFFmpeg();

            // a second recording over the first, cut in two; the first plays straight through
            var project = NewProject();
            var primaryId = AddSource(project, EncodeFixture(4));
            var otherId = AddSource(project, EncodeFixture(6));
            AddClip(project, AddRow(project), primaryId, 0, 4, 0);
            var row = AddRow(project);
            AddClip(project, row, otherId, 0, 1, 0);
            AddClip(project, row, otherId, 1, 3, 3);
            (Guid, int, int) primary = (primaryId, 0, 0), other = (otherId, 0, 0);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await player.SeekAsync(new TimeSpan(Ticks(0.5)), SeekMode.Exact);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, otherId, 0), 0.5)));

            int primarySeeks = player.VideoSeekCount(primary);
            int otherSeeks = player.VideoSeekCount(other);

            player.Play();
            long last = 0;
            Assert.True(WaitUntil(() =>
            {
                long at = player.Position.Ticks;
                Assert.True(at >= last, $"the clock went back across the seam: {last} -> {at}");
                last = at;
                return at >= Ticks(1.5);
            }), $"pos={player.Position}");

            // the hop is counted when its pipe runs it, which can trail the clock under load
            Assert.True(WaitUntil(() => player.VideoSeekCount(other) > otherSeeks), "the seam never hopped its stream");
            Assert.Equal(primarySeeks, player.VideoSeekCount(primary));
            Assert.Equal(otherSeeks + 1, player.VideoSeekCount(other));
            Assert.True(WaitUntil(() => LanePts(player, cache, otherId, 0) is >= 30_000_000),
                $"the second clip's frames never arrived: pts={LanePts(player, cache, otherId, 0)}");
        }

        [Fact]
        public async Task A_primary_seam_leaves_a_stream_with_no_seam_of_its_own_decoding()
        {
            RequireFFmpeg();
            var project = NewProject();
            var primaryId = AddSource(project, EncodeFixture(6));
            var otherId = AddSource(project, EncodeFixture(4));
            var row = AddRow(project);
            AddClip(project, row, primaryId, 0, 1, 0);
            AddClip(project, row, primaryId, 1, 3, 3);
            AddClip(project, AddRow(project), otherId, 0, 4, 0);
            (Guid, int, int) primary = (primaryId, 0, 0), other = (otherId, 0, 0);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await player.SeekAsync(new TimeSpan(Ticks(0.5)), SeekMode.Exact);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, primaryId, 0), 0.5)));

            int primarySeeks = player.VideoSeekCount(primary);
            int otherSeeks = player.VideoSeekCount(other);

            player.Play();
            Assert.True(WaitUntil(() => player.Position.Ticks >= Ticks(1.5)));

            Assert.Equal(primarySeeks + 1, player.VideoSeekCount(primary));
            Assert.Equal(otherSeeks, player.VideoSeekCount(other));
        }

        [Fact]
        public async Task A_frame_step_across_a_non_primary_seam_lands_that_stream()
        {
            RequireFFmpeg();
            var project = NewProject();
            var primaryId = AddSource(project, EncodeFixture(4));
            var otherId = AddSource(project, EncodeFixture(6));
            AddClip(project, AddRow(project), primaryId, 0, 4, 0);
            var row = AddRow(project);
            AddClip(project, row, otherId, 0, 1, 0);
            AddClip(project, row, otherId, 1, 3, 3);
            (Guid, int, int) other = (otherId, 0, 0);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await player.SeekAsync(new TimeSpan(Ticks(1) - FrameTicks), SeekMode.Exact);
            Assert.True(WaitUntil(() => LanePts(player, cache, otherId, 0) >= Ticks(0.9)));
            int seeks = player.VideoSeekCount(other);

            await player.StepFrameAsync(1);

            Assert.True(player.Position.Ticks >= Ticks(1), $"playhead at {player.Position}");
            Assert.True(player.VideoSeekCount(other) > seeks, "the step crossed the seam and did not land it");
            Assert.True(WaitUntil(() => LanePts(player, cache, otherId, 0) >= Ticks(3)),
                $"pts={LanePts(player, cache, otherId, 0)}");
        }

        /// <summary>Exact seek, then waits for the lane to hold the source instant.</summary>
        private static async Task SeekAndLand(CompositionPlayer player, FrameTextureCache cache, Guid sourceId,
            double at, int lane, double source)
        {
            await player.SeekAsync(new TimeSpan(Ticks(at)), SeekMode.Exact);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, lane), source)),
                $"the seek to t={at} did not land: lane {lane} pts={LanePts(player, cache, sourceId, lane)}");
        }

        /// <summary>Asserts a run of forward steps walks the playhead one frame each, while the lane
        /// with the clip at the playhead walks with it.</summary>
        private static async Task AssertStepsFrameByFrame(CompositionPlayer player, FrameTextureCache cache,
            Guid sourceId, int lane, double from, double source, int steps)
        {
            for (int step = 1; step <= steps; step++)
            {
                await player.StepFrameAsync(1);

                double expected = from + (double)step / Fps;
                Assert.True(Math.Abs(player.Position.Ticks - Ticks(expected)) <= OneFrame,
                    $"step {step}: playhead at {player.Position}, expected t={expected}");
                double shown = source + (double)step / Fps;
                Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, lane), shown)),
                    $"step {step}: lane {lane} pts={LanePts(player, cache, sourceId, lane)}, expected s{shown}");
            }
        }

        [Fact]
        public async Task A_frame_step_in_a_hole_of_the_primary_lane_follows_the_clip_at_the_playhead()
        {
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(26));
            var row = AddRow(project);
            AddClip(project, row, sourceId, 0, 4, 0);
            AddClip(project, row, sourceId, 10, 4, 10);
            AddClip(project, AddRow(project), sourceId, 3, 5, 20);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await SeekAndLand(player, cache, sourceId, 6, 1, 26 - 3);

            await AssertStepsFrameByFrame(player, cache, sourceId, 1, 6, 23, 5);
        }

        [Fact]
        public async Task A_frame_step_past_the_end_of_the_primary_lanes_clip_follows_the_clip_at_the_playhead()
        {
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(33));
            AddClip(project, AddRow(project), sourceId, 0, 10, 0);
            AddClip(project, AddRow(project), sourceId, 8, 12, 20);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await SeekAndLand(player, cache, sourceId, 15, 1, 27);

            await AssertStepsFrameByFrame(player, cache, sourceId, 1, 15, 27, 5);
        }

        [Fact]
        public async Task A_frame_step_with_the_primary_clip_still_to_come_follows_the_clip_at_the_playhead()
        {
            // the lowest row's clip starts later, so the primary is lane 1 and idle at the playhead
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(29));
            AddClip(project, AddRow(project), sourceId, 5, 5, 0);
            AddClip(project, AddRow(project), sourceId, 0, 8, 20);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await SeekAndLand(player, cache, sourceId, 1, 0, 21);

            await AssertStepsFrameByFrame(player, cache, sourceId, 0, 1, 21, 5);
        }

        [Fact]
        public async Task A_frame_step_into_a_clip_on_the_primary_lane_lands_it_at_its_start()
        {
            // stepping up to the seam, the primary's first frames are still to come: an idle lane
            // waits on the first of them, and the hop into the clip presents it
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(29));
            AddClip(project, AddRow(project), sourceId, 5, 5, 0);
            AddClip(project, AddRow(project), sourceId, 0, 8, 20);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await SeekAndLand(player, cache, sourceId, 5 - 2.0 / Fps, 0, 25 - 2.0 / Fps);

            for (int step = 0; step < 4; step++)
                await player.StepFrameAsync(1);

            Assert.True(Math.Abs(player.Position.Ticks - Ticks(5 + 2.0 / Fps)) <= OneFrame, $"playhead at {player.Position}");
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 1), 2.0 / Fps)),
                $"lane 1 pts={LanePts(player, cache, sourceId, 1)}");
        }

        [Fact]
        public async Task A_frame_step_off_a_lone_clip_does_not_skip_the_next_clip_of_an_idle_lane()
        {
            // an idle lane must not walk through its next clip while the steps go by above it: at
            // the end of the clip above, the primary lane's frame is what says where the playhead
            // goes next, and it has to be the first frame of [10, 14), not one a second into it
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(26));
            var row = AddRow(project);
            AddClip(project, row, sourceId, 0, 4, 0);
            AddClip(project, row, sourceId, 10, 4, 10);
            AddClip(project, AddRow(project), sourceId, 3, 5, 20);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await SeekAndLand(player, cache, sourceId, 7, 1, 24);

            for (int step = 0; step < 40 && player.Position.Ticks < Ticks(10); step++)
            {
                long before = player.Position.Ticks;
                await player.StepFrameAsync(1);
                Assert.True(player.Position.Ticks >= before, $"the step went back: {before} -> {player.Position.Ticks}");
            }

            Assert.True(player.Position.Ticks >= Ticks(10) && player.Position.Ticks <= Ticks(10) + 2 * OneFrame,
                $"playhead at {player.Position}");
            Assert.True(WaitUntil(() => Math.Abs((LanePts(player, cache, sourceId, 0) ?? 0) - player.Position.Ticks) <= OneFrame),
                $"pos={player.Position} pts={LanePts(player, cache, sourceId, 0)}");
        }

        [Fact]
        public async Task A_frame_step_back_into_a_clip_at_the_offset_it_left_lands_it()
        {
            // the lane waits through its gap while the clip above carries the playhead, and the next
            // clip continues the same offset, so only its having waited says it has to hop
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(24));
            var row = AddRow(project);
            AddClip(project, row, sourceId, 0, 1, 0);
            AddClip(project, row, sourceId, 1.5, 1.5, 1.5);
            AddClip(project, AddRow(project), sourceId, 0.9, 2.1, 20);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await SeekAndLand(player, cache, sourceId, 0.95, 0, 0.95);

            for (int step = 0; step < 40 && player.Position.Ticks < Ticks(1.5); step++)
                await player.StepFrameAsync(1);

            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), 1.5)),
                $"pos={player.Position} pts={LanePts(player, cache, sourceId, 0)}");
            await AssertStepsFrameByFrame(player, cache, sourceId, 0, 1.5, 1.5, 3);
        }

        [Fact]
        public async Task A_frame_step_walks_every_clip_at_the_playhead()
        {
            RequireFFmpeg();
            var project = OverlapOf(EncodeFixture(8), out var sourceId, 8);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await SeekAndLand(player, cache, sourceId, 2, 1, 6);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), 2)));
            int seeks = player.VideoSeekCount((sourceId, 0, 0)) + player.VideoSeekCount((sourceId, 0, 1));

            for (int step = 1; step <= 3; step++)
            {
                await player.StepFrameAsync(1);

                double along = (double)step / Fps;
                Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), 2 + along)),
                    $"step {step}: lane 0 pts={LanePts(player, cache, sourceId, 0)}");
                Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 1), 6 + along)),
                    $"step {step}: lane 1 pts={LanePts(player, cache, sourceId, 1)}");
            }

            // walked there on its own frames, with no seam to hop
            Assert.Equal(seeks, player.VideoSeekCount((sourceId, 0, 0)) + player.VideoSeekCount((sourceId, 0, 1)));
        }

        [Fact]
        public async Task A_frame_step_with_two_clips_at_the_playhead_goes_by_the_primary()
        {
            // told apart by frame rate: the other recording's frames are half as long
            RequireFFmpeg();
            var project = NewProject();
            var primaryId = AddSource(project, EncodeFixture(4));
            var otherId = AddSource(project, EncodeFixture(4, Fps * 2));
            AddClip(project, AddRow(project), primaryId, 0, 4, 0);
            AddClip(project, AddRow(project), otherId, 0, 4, 0);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await SeekAndLand(player, cache, primaryId, 1, 0, 1);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, otherId, 0), 1)));

            await AssertStepsFrameByFrame(player, cache, primaryId, 0, 1, 1, 5);
        }

        [Fact]
        public async Task A_frame_step_right_after_a_seek_starts_from_the_seeks_frame()
        {
            // paused, the decoder sits parked with a frame in hand. The seek's flush lets it queue
            // that frame, and a step asked for straight after must neither present it nor take the
            // seek's own frame for the step
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(12));
            AddClip(project, AddRow(project), sourceId, 0, 12, 0);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());

            for (int trial = 0; trial < 6; trial++)
            {
                await SeekAndLand(player, cache, sourceId, 1 + trial, 0, 1 + trial);
                await Task.Delay(50); // long enough for the decoder to fill its queue and park

                double to = 7 - trial + 0.5;
                await player.SeekAsync(new TimeSpan(Ticks(to)), SeekMode.Exact);
                await player.StepFrameAsync(1);

                Assert.True(Math.Abs(player.Position.Ticks - Ticks(to + 1.0 / Fps)) <= OneFrame,
                    $"trial {trial}: playhead at {player.Position}, expected t={to + 1.0 / Fps}");
                Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), to + 1.0 / Fps)),
                    $"trial {trial}: pts={LanePts(player, cache, sourceId, 0)}, expected s{to + 1.0 / Fps}");
            }
        }

        [Fact]
        public async Task A_frame_step_after_an_edit_swaps_the_lanes_follows_the_clip_at_the_playhead()
        {
            // the edit keeps the pipes and changes what they play: the pipe the open made primary
            // has nothing at the playhead any more, and the new primary does
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(29));
            var low = AddClip(project, AddRow(project), sourceId, 0, 5, 0);
            AddClip(project, AddRow(project), sourceId, 2, 8, 20);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            int opens = player.DecoderOpenCount;

            // the row above now starts first, so it takes lane 0 and the low clip takes lane 1
            var edited = Project.FromJson(project.ToJson());
            var moved = edited.Items.Find(i => i.Id == low.Id);
            moved.TimelineStartTicks = Ticks(5);
            moved.DurationTicks = Ticks(10);
            await player.UpdateProject(edited);
            Assert.Equal(opens, player.DecoderOpenCount);
            Assert.Equal(1, StreamLanes.LaneOf(StreamLanes.Assign(edited), low.Id));

            await SeekAndLand(player, cache, sourceId, 12, 1, 7);

            await AssertStepsFrameByFrame(player, cache, sourceId, 1, 12, 7, 3);
        }

        [Fact]
        public async Task A_crop_draws_isolated_copy_reads_the_lane_its_clip_plays_on()
        {
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(36));
            AddClip(project, AddRow(project), sourceId, 0, 20, 0);
            var paste = AddClip(project, AddRow(project), sourceId, 5, 5, 30);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var surface = factory.CreateSurface(W, H);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await player.SeekAsync(new TimeSpan(Ticks(7)), SeekMode.Exact);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 1), 32)));

            // on its own the copy has one clip showing at the instant, so it would be lane 0: the
            // original's moment under the crop being drawn
            var isolated = CropDrawMath.BuildIsolatedProject(project, paste.Id);
            Assert.Equal(0, StreamLanes.LaneOf(StreamLanes.Assign(isolated), paste.Id));

            player.TryGetFrameSource(out var source, out long at);
            source.Pump(cache);
            var probe = new ComposerProbe(source);
            FrameComposer.Compose(isolated, at, probe, surface.Canvas, W, H, StreamLanes.Assign(player.Project));

            var request = Assert.Single(probe.Requests);
            Assert.Equal(1, request.Lane);
            Assert.True(Math.Abs(request.Requested - Ticks(32)) <= OneFrame, $"asked {request.Requested}");
            Assert.True(Math.Abs(request.Pts - Ticks(32)) <= OneFrame, $"shows {request.Pts}");
        }

        [Fact]
        public async Task Opening_lands_a_lane_that_starts_far_into_the_file_with_a_seek()
        {
            // the primary starts at the head of the file, so only the lane calls for it: decoding
            // and discarding to source 20 s would hold the first paint up behind a long decode
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(10));
            AddClip(project, AddRow(project), sourceId, 0, 10, 0);
            AddClip(project, AddRow(project), sourceId, 1, 1, 5);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());

            Assert.True(WaitUntil(() => player.VideoSeekCount((sourceId, 0, 1)) > 0), "the open never seeked the lane");
        }

        [Fact]
        public async Task Playing_straight_on_from_the_open_never_seeks()
        {
            // the open knows which segment each pipe starts on, so the first tick has no seam to
            // hop, and the lane waiting for its clip has none yet either
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(4));
            AddClip(project, AddRow(project), sourceId, 0, 4, 0);
            AddClip(project, AddRow(project), sourceId, 2, 1, 1);
            (Guid, int, int) lane0 = (sourceId, 0, 0), lane1 = (sourceId, 0, 1);

            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());

            player.Play();
            Assert.True(WaitUntil(() => player.Position.Ticks >= Ticks(1)), $"pos={player.Position}");

            Assert.Equal(0, player.VideoSeekCount(lane0));
            Assert.Equal(0, player.VideoSeekCount(lane1));
        }

        [Fact]
        public async Task An_idle_lane_does_not_decode_to_the_end_of_the_file()
        {
            RequireFFmpeg();

            // the lane's one clip is over at t=1; far more file follows than any queue holds
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(10));
            AddClip(project, AddRow(project), sourceId, 0, 4, 0);
            AddClip(project, AddRow(project), sourceId, 0.3, 0.5, 2);
            (Guid, int, int) lane1 = (sourceId, 0, 1);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());

            player.Play();
            Assert.True(WaitUntil(() => player.Position.Ticks >= Ticks(2.5)), $"pos={player.Position}");

            Assert.False(player.VideoDemuxAtEof(lane1), "the idle lane read on to the end of the file");

            // and it holds its frame. Frames past the out-point map to the clip's end, so in the
            // clip's last moments a few can land, on a lane nothing draws from a moment later.
            var held = LanePts(player, cache, sourceId, 1);
            Assert.NotNull(held);
            Assert.InRange(held.Value, Ticks(2), Ticks(5));
            Thread.Sleep(200);
            Assert.Equal(held, LanePts(player, cache, sourceId, 1));
        }

        [Fact]
        public async Task Lane_0_decodes_on_through_a_gap_and_comes_out_with_no_hop()
        {
            // one line of source with a gap in the row: only lanes above 0 stop between clips, and
            // lane 0 stays the one path it has always been
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(6));
            var row = AddRow(project);
            AddClip(project, row, sourceId, 0, 1, 0);
            AddClip(project, row, sourceId, 2, 2, 2);
            (Guid, int, int) lane0 = (sourceId, 0, 0);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await player.SeekAsync(new TimeSpan(Ticks(0.2)), SeekMode.Exact);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 0), 0.2)));
            int seeks = player.VideoSeekCount(lane0);

            player.Play();
            Assert.True(WaitUntil(() => player.Position.Ticks >= Ticks(0.95)), $"pos={player.Position}");
            player.GetStatistics(); // restarts the interval

            Assert.True(WaitUntil(() => player.Position.Ticks >= Ticks(1.7)), $"pos={player.Position}");
            long decoded = player.GetStatistics().Video[0].DecodedInInterval;
            Assert.True(decoded >= 15, $"lane 0 stood still in the gap: {decoded} frames decoded");

            Assert.True(WaitUntil(() => player.Position.Ticks >= Ticks(2.5)), $"pos={player.Position}");
            Assert.Equal(seeks, player.VideoSeekCount(lane0));
            Assert.True(WaitUntil(() => LanePts(player, cache, sourceId, 0) >= Ticks(2)),
                $"pts={LanePts(player, cache, sourceId, 0)}");
        }

        [Fact]
        public async Task Each_clip_entering_a_lane_hops_even_at_the_offset_it_left()
        {
            RequireFFmpeg();

            // two clips of one lane on one line of source: the lane stopped at the end of the first,
            // so the second enters through a hop and does not wait for a decoder to catch up
            var project = NewProject();
            // the clips are long enough that a loaded machine's late tick still lands inside them:
            // a hop that only runs once the playhead has left its clip has nothing to land
            var sourceId = AddSource(project, EncodeFixture(10));
            AddClip(project, AddRow(project), sourceId, 0, 6, 0);
            var row = AddRow(project);
            AddClip(project, row, sourceId, 0.5, 1.5, 4.5);
            AddClip(project, row, sourceId, 3, 1, 7);
            (Guid, int, int) lane1 = (sourceId, 0, 1);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());

            Assert.True(WaitUntil(() => LanePts(player, cache, sourceId, 1) is not null));
            int seeks = player.VideoSeekCount(lane1);

            player.Play();
            Assert.True(WaitUntil(() => player.VideoSeekCount(lane1) > seeks), "entering the first clip did not hop");

            Assert.True(WaitUntil(() => player.Position.Ticks >= Ticks(2.5)), $"pos={player.Position}");
            int afterFirst = player.VideoSeekCount(lane1);
            Assert.True(WaitUntil(() => player.VideoSeekCount(lane1) > afterFirst), "entering the second clip did not hop");
            Assert.True(WaitUntil(() => LanePts(player, cache, sourceId, 1) >= Ticks(7)),
                $"pts={LanePts(player, cache, sourceId, 1)}");
        }

        // -------------------------------------------------------------------------- live edits

        [Fact]
        public async Task An_edit_that_starts_an_overlap_rebuilds_once_and_later_drags_do_not()
        {
            RequireFFmpeg();
            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(8));
            AddClip(project, AddRow(project), sourceId, 0, 4, 0);
            var paste = AddClip(project, AddRow(project), sourceId, 4, 2, 6);

            using var factory = new CpuSurfaceFactory();
            using var cache = new FrameTextureCache(factory);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            Assert.Equal(1, player.DecoderOpenCount); // back to back: one decoder

            // the drag crosses onto the first clip: a second decoder, on the background path
            paste.TimelineStartTicks = Ticks(1);
            await player.UpdateProject(project);
            Assert.Equal(3, player.DecoderOpenCount);
            Assert.True(WaitUntil(() => Lands(LanePts(player, cache, sourceId, 1), 6)),
                $"the new lane presented nothing: pts={LanePts(player, cache, sourceId, 1)}");

            // moving within the overlap keeps the same lanes: the mapping swaps, synchronously
            paste.TimelineStartTicks = Ticks(1.5);
            var applied = player.UpdateProject(project);
            Assert.True(applied.IsCompleted, "a drag inside the overlap must take the cheap path");
            await applied;
            Assert.Equal(3, player.DecoderOpenCount);

            // and dragging back off it drops the second decoder
            paste.TimelineStartTicks = Ticks(4);
            await player.UpdateProject(project);
            Assert.Equal(4, player.DecoderOpenCount);
        }

        [Fact]
        public async Task A_matte_rides_with_each_lane()
        {
            RequireFFmpeg();
            string cacheDir = Path.Combine(Path.GetTempPath(), $"prisc-same-source-matte-{Guid.NewGuid():N}");
            Directory.CreateDirectory(cacheDir);
            try
            {
                string path = EncodeFixture(8);
                var project = OverlapOf(path, out var sourceId, 8);
                foreach (var item in project.Items)
                    item.Effect = new VideoEffect { Kind = VideoEffectKind.BgRemove };

                string mattePath = AiSidecars.MattePath(cacheDir, sourceId, 0);
                File.Copy(path, mattePath);
                Assert.True(AiSidecars.TryWriteCompanion(mattePath, AiSidecars.DescribeSource(path, W, H)));

                using var factory = new CpuSurfaceFactory();
                using var cache = new FrameTextureCache(factory);
                using var player = new CompositionPlayer();
                await player.OpenAsync(project, new VideoOpenOptions { EnableHardwareDecode = false, SidecarCacheDir = cacheDir });
                Assert.Equal(4, player.DecoderOpenCount); // a frame pipe and a matte pipe per lane

                Assert.True(WaitUntil(() =>
                {
                    player.TryGetFrameSource(out var source, out _);
                    source.Pump(cache);
                    return cache.TryGetMask(sourceId, 0, out _, out long mask0)
                        && cache.TryGetMask(sourceId, 0, out _, out long mask1, lane: 1)
                        && mask0 != mask1; // the lane 1 matte tracks its own clip's source time
                }), "each lane should hold a matte of its own");
            }
            finally
            {
                try { Directory.Delete(cacheDir, recursive: true); }
                catch { /* best effort */ }
            }
        }

        // ------------------------------------------------------------------ preview and render

        /// <summary>The composed canvas, as gray at each clip's center.</summary>
        private static (byte Left, byte Right) GraysOf(CpuSurfaceFactory factory, SKSurface surface)
        {
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

        [Theory]
        [InlineData(0.5)] // the left clip alone
        [InlineData(1.5)] // both, at different source times
        [InlineData(2.5)]
        [InlineData(3.5)] // the right clip's row has moved on to a repeat of itself
        public async Task Preview_and_render_compose_the_same_frame(double at)
        {
            RequireFFmpeg();

            var project = NewProject();
            var sourceId = AddSource(project, EncodeFixture(8));
            var left = AddClip(project, AddRow(project), sourceId, 0, 5, 0);
            left.Transform = new Transform { X = 0.25, Y = 0.5, Scale = 0.5 };
            var row = AddRow(project);
            var first = AddClip(project, row, sourceId, 1, 2, 5);
            first.Transform = new Transform { X = 0.75, Y = 0.5, Scale = 0.5 };
            var repeat = AddClip(project, row, sourceId, 3, 2, 5);
            repeat.Transform = new Transform { X = 0.75, Y = 0.5, Scale = 0.5 };

            using var factory = new CpuSurfaceFactory();

            // render: forward from the start, as a render would run
            using var renderCache = new FrameTextureCache(factory);
            using var render = new SequentialFrameSource(project, renderCache);
            using var renderSurface = factory.CreateSurface(W, H);
            for (long t = 0; t <= Ticks(at); t += FrameTicks)
                FrameComposer.Compose(project, t, render, renderSurface.Canvas, W, H);
            FrameComposer.Compose(project, Ticks(at), render, renderSurface.Canvas, W, H);
            var rendered = GraysOf(factory, renderSurface);

            // preview: a paused seek to that instant, composed once its frames land
            using var previewCache = new FrameTextureCache(factory);
            using var previewSurface = factory.CreateSurface(W, H);
            using var player = new CompositionPlayer();
            await player.OpenAsync(project, Options());
            await player.SeekAsync(new TimeSpan(Ticks(at)), SeekMode.Exact);
            (byte Left, byte Right) previewed = default;
            Assert.True(WaitUntil(() =>
            {
                player.TryGetFrameSource(out var source, out _);
                source.Pump(previewCache);
                FrameComposer.Compose(project, Ticks(at), source, previewSurface.Canvas, W, H);
                previewed = GraysOf(factory, previewSurface);
                return Math.Abs(previewed.Left - rendered.Left) <= 6 && Math.Abs(previewed.Right - rendered.Right) <= 6;
            }, 8000), $"at t={at}: rendered {rendered}, previewed {previewed}");
        }
    }
}
