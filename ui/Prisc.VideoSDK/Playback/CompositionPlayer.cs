using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Prisc.VideoSDK.Ai;
using Prisc.VideoSDK.Audio;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Model;
using LaneKey = (System.Guid SourceId, int StreamIndex, int Lane);

namespace Prisc.VideoSDK.Playback
{
    /// <summary>
    /// Plays a <see cref="Project"/> for preview: the generalization of
    /// <see cref="FFmpegVideoPlayer"/> from "one file, skip ranges" to "a timeline of items over N
    /// media streams". One demux/decode pipeline runs per referenced <b>video</b>
    /// (sourceId, streamIndex, lane) — a lane being what one decoder can serve, see
    /// <see cref="StreamLanes"/> — reusing <see cref="Demuxer"/>/<see cref="VideoDecodeWorker"/>
    /// unchanged, against ONE shared <see cref="PlaybackClock"/> that runs in <b>output-timeline
    /// time</b>. Workers pace their source-stamped frames through the project's timeline↔source
    /// mapping (<see cref="ProjectTimelineMap"/>) and, when speed items bend time, the project's
    /// <see cref="TimeWarp"/>: the clock runs linearly in <b>warped output time</b> (scaled only
    /// by the user's <see cref="PlaybackRate"/>), and <see cref="Position"/> — the project-time
    /// contract everything external keeps speaking — is the warp's inverse of it, so a speed
    /// ramp glides continuously with no rate change or audio flush anywhere inside it; an
    /// identity warp keeps both domains coincident and every conversion a pass-through.
    /// Presented frames land in the
    /// <see cref="PlaybackFrameSource"/> the preview's draw operation composes from
    /// (<see cref="TryGetFrameSource"/>). Audio never rides the demuxers: every unmuted audio
    /// stream is mixed in timeline time by one <see cref="AudioMixWorker"/> running the literal
    /// render mixer (<see cref="Prisc.VideoSDK.Audio.AudioMixer"/>) over its own per-stream
    /// decoders — gaps are silence, per-item volume and transition ramps are per-sample, and
    /// preview therefore sounds like the render by construction. The player's own
    /// <see cref="Volume"/> is a pure master gain on the sink.
    ///
    /// Cuts: back-to-back items whose source in-points jump are played by decoding continuously
    /// until the seam, then hopping the pipeline with an internal exact seek — the same mechanism
    /// (and acceptable ~100ms hiccup) as <see cref="FFmpegVideoPlayer"/>'s skip ranges, with the
    /// ranges now derived from the project instead of set by the UI. The frame source additionally
    /// filters any cut-source frames presented inside the detection window, so cut material never
    /// reaches the canvas. Each pipeline has its own demuxer and watches its own seams, so streams
    /// of one file can be placed differently, and a pasted clip's edges stall nothing else. A lane
    /// above 0 decodes only while one of its clips covers the playhead.
    ///
    /// <see cref="UpdateProject"/> applies live edits: as long as the set of referenced streams is
    /// unchanged (every trim/cut/transform/volume edit), the new mapping is swapped in atomically
    /// and no decoder is reopened; only adding/removing/relinking streams rebuilds pipelines,
    /// which happens on a background task (latest update wins) — never on the caller's thread.
    /// </summary>
    public sealed class CompositionPlayer : IDisposable
    {
        /// <summary>How far the audio sink's played time may sit from the playhead and still be
        /// trusted as the master clock when re-attaching after an EOF detach (covers the sink's
        /// device latency plus a tick of stopwatch drift). Timing further out means the sink's
        /// base pts describes where production parked, not the playhead — attaching it would
        /// snap the clock backwards.</summary>
        private static readonly TimeSpan AudioReattachTolerance = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// The transport tick, which is also the rate <see cref="PositionChanged"/> is raised at
        /// while playing. Display rate, not "often enough to notice": the position comes from a
        /// clock that interpolates continuously, so there is no event to wait for — a subscriber
        /// drawing a playhead can only be as smooth as this tick, and at the old 100ms it visibly
        /// hopped. The work per tick is a handful of array walks (seam offset, audio attach state,
        /// end-of-timeline), nothing next to decoding, and the whole callback returns immediately
        /// unless the player is playing.
        /// </summary>
        private const int TickIntervalMs = 16;

        private readonly Action<Action> _dispatch;
        private readonly object _stateSync = new object();
        private readonly object _lifecycleSync = new object(); // serializes seeks vs update/reopen/dispose
        private readonly PlaybackFrameSource _frameSource = new PlaybackFrameSource();

        private volatile PipelineSet _pipelines;
        private volatile ProjectTimelineMap _map;
        private volatile TimeWarp _warp;
        private volatile Project _project;
        private PlaybackClock _clock;
        private VideoOpenOptions _options;

        /// <summary>The transport runs on its own thread, not a <see cref="Timer"/>: end-of-
        /// timeline is detected nowhere else, and a thread-pool callback can be starved for
        /// seconds by unrelated pool work (decode continuations, thumbnailing, a busy host
        /// process) — playback would then run past the end still reporting
        /// <see cref="PlayerState.Playing"/>, and the playhead would freeze.</summary>
        private Thread _tickThread;
        private readonly ManualResetEventSlim _tickStop = new ManualResetEventSlim(false);

        private volatile PlayerState _state = PlayerState.Idle;
        private double _volume = 1.0;
        private double _playbackRate = 1.0;
        private bool _audioDetached;
        private int _skipSeekBusy;
        /// <summary>Whether the most recent seek was <see cref="SeekMode.Fast"/> (scrubbing).
        /// Its immediately-presented frame is a keyframe that can sit far before the requested
        /// target, and the preview composes at the clock's instant — adopting that pts would
        /// bounce the composed picture between the scrub target and the keyframe on every scrub
        /// tick, so a Fast immediate leaves the clock on the target it was given.</summary>
        private volatile bool _lastSeekWasFast;
        private int _decoderOpens;
        private bool _disposed;
        private Task _openTask;

        // seek coalescing — same discipline as FFmpegVideoPlayer
        private readonly object _seekSync = new object();
        private TimeSpan _pendingSeekPos;
        private SeekMode _pendingSeekMode;
        private bool _pendingSeamHop;
        private bool _hasPendingSeek;
        private bool _seekLoopActive;
        private Task _seekTask = Task.CompletedTask;

        // update coalescing — a changed stream set rebuilds pipelines on a background task
        // (mirroring how OpenAsync offloads OpenCore); overlapping updates coalesce, latest
        // project wins. Lock order: _updateSync outer, _lifecycleSync inner.
        private readonly object _updateSync = new object();
        private Project _pendingUpdate;
        private bool _updateLoopActive;
        private Task _updateTask = Task.CompletedTask;

        /// <param name="eventDispatcher">Marshals <see cref="PositionChanged"/> /
        /// <see cref="StateChanged"/> onto the UI thread. Null = raise on the calling thread.</param>
        public CompositionPlayer(Action<Action> eventDispatcher = null)
        {
            _dispatch = eventDispatcher ?? (a => a());
        }

        // ------------------------------------------------------------------------ pipeline model

        private sealed class VideoPipe
        {
            public LaneKey Key;
            public Demuxer Demuxer;
            public PacketQueue Queue;
            public VideoDecodeWorker Worker;
            public PooledFrameSink Sink;

            /// <summary>Source-timeline offset of the segment this pipe was last synced to; a
            /// differing offset at the playing position means it crossed a seam.</summary>
            public long ActiveOffsetTicks = long.MinValue;

            /// <summary>Container seeks so far (test/diagnostic).</summary>
            public int SeekCount;
        }

        private sealed class PipelineSet
        {
            public VideoPipe[] AllVideo = Array.Empty<VideoPipe>();
            public VideoPipe Primary;
            public AudioMixWorker MixWorker;
            public NAudioSink AudioSink;
            public AudioRingBuffer AudioRing;
            public int MixRate;
            public string Signature;
        }

        // -------------------------------------------------------------------------------- state

        public PlayerState State => _state;

        /// <summary>The project currently playing (the instance last passed to Open/Update).</summary>
        public Project Project => _project;

        /// <summary>Timeline length — the duration transport/seeking operates in.</summary>
        public TimeSpan Duration => new TimeSpan(_map?.DurationTicks ?? 0);

        /// <summary>Warped (output-time) length for the transport's total readout; equals
        /// <see cref="Duration"/> while no speed item bends time. Seeking, <see cref="Position"/>
        /// and every internal clamp stay in project time.</summary>
        public long OutputDurationTicks => _warp?.OutputDurationTicks ?? _map?.DurationTicks ?? 0;

        /// <summary>Maps a project-time instant (the domain of <see cref="Position"/> and
        /// <see cref="SeekAsync"/>) into output time, so a transport can show elapsed against
        /// <see cref="OutputDurationTicks"/> in the same warped domain.</summary>
        public long ToOutputTicks(long projectTicks) => _warp?.ToOutput(projectTicks) ?? projectTicks;

        /// <summary>The frame source the preview's draw operation composes from. Stable for the
        /// player's lifetime (pipeline rebuilds re-register their streams into it).</summary>
        public PlaybackFrameSource FrameSource => _frameSource;

        /// <summary>Diagnostic/test hook: number of decoder pipelines constructed over the
        /// player's lifetime. Live edits that only change the mapping never increment it.</summary>
        public int DecoderOpenCount => Volatile.Read(ref _decoderOpens);

        /// <summary>Test/diagnostic: container seeks performed by the audio mix's source for the
        /// current pipeline set. A video cut-seam hop must never move it — the mix decodes
        /// straight through seams, which is what keeps preview audio sample-exact with render.</summary>
        internal int AudioRepositionCount => _pipelines?.MixWorker?.SourceRepositionCount ?? 0;

        /// <summary>Test/diagnostic: the transport's state in one block, for a failing timing test
        /// to print — which of the clock, the seek loop, the tick thread or a lane's threads is the
        /// one that stopped.</summary>
        internal string DescribeTransport()
        {
            var sb = new System.Text.StringBuilder();
            var clock = _clock;
            bool pending, loopActive, seamHop;
            TimeSpan pendingPos;
            lock (_seekSync)
            {
                pending = _hasPendingSeek;
                loopActive = _seekLoopActive;
                seamHop = _pendingSeamHop;
                pendingPos = _pendingSeekPos;
            }

            bool lifecycleFree = Monitor.TryEnter(_lifecycleSync, 0);
            if (lifecycleFree)
                Monitor.Exit(_lifecycleSync);

            sb.Append($"state={_state}, position={Position}, clockRunning={clock?.IsRunning}, clockPosition={clock?.Position}, ");
            sb.Append($"tickAlive={_tickThread?.IsAlive}, seekLoopActive={loopActive}, seekTask={_seekTask?.Status}, ");
            sb.Append($"pendingSeek={pending} (pos={pendingPos}, seamHop={seamHop}), skipSeekBusy={Volatile.Read(ref _skipSeekBusy)}, ");
            sb.Append($"lifecycleLockFree={lifecycleFree}, lastSeekWasFast={_lastSeekWasFast}");
            foreach (var pipe in _pipelines?.AllVideo ?? Array.Empty<VideoPipe>())
            {
                sb.Append($"\n  lane {pipe.Key.Lane}: seeks={Volatile.Read(ref pipe.SeekCount)}, activeOffset={Volatile.Read(ref pipe.ActiveOffsetTicks)}, ");
                sb.Append($"{pipe.Demuxer.Describe()}, {pipe.Worker.Describe()}");
            }

            return sb.ToString();
        }

        /// <summary>Test/diagnostic: container seeks performed by the video pipeline of a stream's
        /// lane for the current pipeline set, its matte pipe included.</summary>
        internal int VideoSeekCount(LaneKey key)
        {
            int seeks = 0;
            foreach (var pipe in _pipelines?.AllVideo ?? Array.Empty<VideoPipe>())
            {
                if (pipe.Key == key)
                    seeks += Volatile.Read(ref pipe.SeekCount);
            }

            return seeks;
        }

        /// <summary>Test/diagnostic: whether the video pipeline of a stream's lane has read its file
        /// to the end.</summary>
        internal bool VideoDemuxAtEof(LaneKey key)
        {
            foreach (var pipe in _pipelines?.AllVideo ?? Array.Empty<VideoPipe>())
            {
                if (pipe.Key == key && pipe.Demuxer.IsEof)
                    return true;
            }

            return false;
        }

        public event EventHandler PositionChanged;
        public event EventHandler<PlayerState> StateChanged;

        /// <summary>The exception behind the most recent transition to
        /// <see cref="PlayerState.Failed"/> (a failed open or live rebuild); null otherwise.
        /// Read it from a <see cref="StateChanged"/> handler — a failed background rebuild
        /// surfaces exclusively through the Failed state, never as a thrown exception.</summary>
        public Exception LastError { get; private set; }

        /// <summary>The playhead in project time — the warp's inverse of the output-time clock,
        /// so it eases smoothly through speed ramps while the clock underneath stays linear.</summary>
        public TimeSpan Position
        {
            get
            {
                var clock = _clock;
                var map = _map;
                if (clock == null || map == null)
                    return TimeSpan.Zero;
                long dur = map.DurationTicks;
                if (_state == PlayerState.Ended)
                    return new TimeSpan(dur);

                var pos = clock.Position;
                if (pos < TimeSpan.Zero)
                    return TimeSpan.Zero;
                long tl = ClockToProjectTicks(pos.Ticks);
                if (dur > 0 && tl > dur)
                    return new TimeSpan(dur);
                return new TimeSpan(tl);
            }
        }

        /// <summary>Master gain, applied at the sink. Per-item volume and transition ramps are
        /// baked into the mixed samples by <see cref="AudioMixWorker"/> — exactly as in render.</summary>
        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0, 1.0);
                var sink = _pipelines?.AudioSink;
                if (sink != null)
                    sink.Volume = _volume;
            }
        }

        /// <summary>Bounds on <see cref="PlaybackRate"/>: fast enough to skim, slow enough to
        /// study a frame sequence, and short of the extremes where the decoders can no longer
        /// feed the clock at all.</summary>
        public const double MinPlaybackRate = 0.1;
        public const double MaxPlaybackRate = 8.0;

        /// <summary>
        /// Playback speed, 1 = realtime. Video paces against the (now rate-scaled) clock and
        /// drops frames it can no longer present in time; audio is linearly resampled by the mix
        /// worker, so it keeps playing with its pitch shifted — no time stretching. Changing the
        /// rate re-bases the audio mapping through a seek at the current position, so it costs
        /// the same hiccup as a seek; the position itself never jumps.
        /// </summary>
        public double PlaybackRate
        {
            get => _playbackRate;
            set
            {
                double rate = Math.Clamp(value, MinPlaybackRate, MaxPlaybackRate);
                if (rate == _playbackRate)
                    return;

                // applied under the lifecycle lock so a rebuild (ReopenCore/BuildPipelines)
                // can't hand a fresh mix worker a stale rate while the clock already runs at
                // the new one.
                lock (_lifecycleSync)
                {
                    _playbackRate = rate;
                    var clock = _clock;
                    if (clock == null)
                        return; // not open yet — OpenCore applies it to the clock it builds

                    clock.Rate = rate; // rebases: the position is continuous across the change
                    var mix = _pipelines?.MixWorker;
                    if (mix == null)
                        return;

                    // the mix worker adopts the speed at its next chunk and flushes itself; the seek
                    // lands video on the same instant so the two agree from the first sample on.
                    mix.SetSpeed(rate);
                }
                if (_state is PlayerState.Paused or PlayerState.Playing or PlayerState.Ended)
                    _ = SeekAsync(Position, SeekMode.Exact);
            }
        }

        // --------------------------------------------------------------------------------- open

        /// <summary>Opens the project's media and presents the first frame (paused). One project
        /// per player instance; use <see cref="UpdateProject"/> for edits.</summary>
        public Task OpenAsync(Project project, VideoOpenOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(project);
            if (_disposed)
                throw new ObjectDisposedException(nameof(CompositionPlayer));
            if (_state != PlayerState.Idle)
                throw new InvalidOperationException("Player already has a project open; create a new player, or use UpdateProject for edits.");

            SetState(PlayerState.Opening);
            var task = Task.Run(() =>
            {
                try
                {
                    OpenCore(project, options ?? new VideoOpenOptions());
                    SetState(PlayerState.Paused);
                }
                catch (Exception ex)
                {
                    LastError = ex;
                    SetState(PlayerState.Failed);
                    throw;
                }
            });

            // kept so Dispose can defer teardown behind OpenCore (same race as FFmpegVideoPlayer:
            // closing the editor while it is still loading).
            _openTask = task;
            return task;
        }

        private void OpenCore(Project project, VideoOpenOptions options)
        {
            _options = options;
            _warp = TimeWarp.Build(project);
            _clock = new PlaybackClock { Rate = _playbackRate };

            var map = ProjectTimelineMap.Build(project);
            _project = project;
            _map = map;
            _frameSource.SetCutSchedules(BuildCutSchedules(map));

            var set = BuildPipelines(project, map);
            _pipelines = set;

            PrimeAndStart(set, map, positionTicks: 0);

            // A long leading trim would decode-discard from the file head; land it with a real
            // container seek instead (coalesced through the normal seek path). A lane above 0 reads
            // from wherever its first clip starts, which is usually a long way in.
            foreach (var stream in map.VideoStreams.Values)
            {
                if (stream.TimelineToSource(0) > 2 * TimeSpan.TicksPerSecond)
                {
                    _ = SeekAsync(TimeSpan.Zero, SeekMode.Exact);
                    break;
                }
            }

            _tickThread = new Thread(TickLoop) { Name = "prisc-transport", IsBackground = true };
            _tickThread.Start();
        }

        /// <summary>Serial-0 Prepare/OnSeeked pairing + thread start, mirroring
        /// <c>FFmpegVideoPlayer.OpenCore</c>: each stream immediately presents its frame at the
        /// given timeline position (Exact, so leading source material before an item's in-point
        /// is discarded, not shown).</summary>
        private void PrimeAndStart(PipelineSet set, ProjectTimelineMap map, long positionTicks)
        {
            foreach (var pipe in set.AllVideo)
            {
                pipe.Worker.PrepareSeek(new TimeSpan(VideoSourceTarget(map, pipe.Key, positionTicks)), SeekMode.Exact);
                pipe.Worker.OnSeeked(0);
                Volatile.Write(ref pipe.ActiveOffsetTicks, OffsetAt(map, pipe.Key, positionTicks));
            }

            // the mix worker runs in timeline time — the position is its seek target directly.
            set.MixWorker?.PrepareSeek(new TimeSpan(positionTicks));

            foreach (var pipe in set.AllVideo)
            {
                pipe.Demuxer.Start();
                pipe.Worker.Start();
            }

            set.MixWorker?.Start();
        }

        private PipelineSet BuildPipelines(Project project, ProjectTimelineMap map)
        {
            var audioStreams = CollectAudioStreams(project);
            int mixRate = MixRate(project);
            var set = new PipelineSet
            {
                Signature = StreamSignature(project, map, audioStreams, mixRate, _options.SidecarCacheDir),
                MixRate = mixRate,
            };
            if (map.VideoStreams.Count == 0 && audioStreams.Count == 0)
                return set; // media-less project (solids/text): transport still works off the clock

            FFmpegLoader.EnsureInitialized();

            var matteStreams = MatteGenerator.CollectMatteStreams(project);

            // primary first so it lands in stats slot 0 (audio never rides the demuxers — the mix
            // worker's per-stream decoders own their files)
            var keys = new List<LaneKey>(map.VideoStreams.Keys);
            if (map.PrimaryVideo is { } primary)
            {
                keys.Remove(primary);
                keys.Insert(0, primary);
            }

            var pipes = new List<VideoPipe>();

            try
            {
                foreach (var key in keys)
                {
                    var source = FindSource(project, key.SourceId)
                        ?? throw new InvalidOperationException($"Project has no source {key.SourceId}.");

                    var demuxer = new Demuxer();
                    try
                    {
                        demuxer.Open(source.Path);
                    }
                    catch
                    {
                        demuxer.Dispose();
                        throw;
                    }

                    var pipe = AddVideoPipe(key, demuxer, key.StreamIndex, pipes);
                    _frameSource.RegisterStream(key, pipe.Sink);
                    if (map.PrimaryVideo == key)
                        set.Primary = pipe;

                    if (matteStreams.Contains((key.SourceId, key.StreamIndex)))
                        BuildMattePipe(source, key, pipes);
                }

                if (audioStreams.Count > 0)
                {
                    set.AudioRing = new AudioRingBuffer(mixRate); // ~500ms float stereo
                    set.AudioSink = new NAudioSink(mixRate, 2, set.AudioRing, _options.CreateAudioOutput?.Invoke());
                    set.AudioSink.Volume = _volume;
                    set.MixWorker = new AudioMixWorker(project, set.AudioRing, set.AudioSink, mixRate, _warp,
                        _options.SidecarCacheDir);
                    if (_playbackRate != 1.0)
                        set.MixWorker.SetSpeed(_playbackRate); // a rebuild keeps the chosen speed
                    Interlocked.Increment(ref _decoderOpens); // the audio pipeline, as one
                    _clock.SetAudioSource(set.AudioSink);
                }
            }
            catch
            {
                // Construction failed part-way (missing/corrupt file, decoder init failure).
                // Everything built so far lives only in these locals — _pipelines is never
                // assigned, so DisposeCore could never reach it: open AVFormatContexts (each
                // holding an OS file handle on the source mp4), decoder contexts and sinks
                // already registered with the frame source would all leak for the process
                // lifetime. FFmpegVideoPlayer kept partial state reachable in fields; here the
                // partial build must be torn down eagerly instead, before the throw escapes.
                set.AllVideo = pipes.ToArray();
                _frameSource.Clear();
                _clock?.SetAudioSource(null);
                DisposePipelineSet(set);
                throw;
            }

            set.AllVideo = pipes.ToArray();
            set.Primary ??= pipes.Count > 0 ? pipes[0] : null;
            return set;
        }

        /// <summary>Builds one decode pipeline over an opened demuxer, which it now owns. The pipe
        /// joins <paramref name="pipes"/> BEFORE its decoder opens, so a failure part-way through
        /// the build leaves everything reachable for the caller's cleanup.</summary>
        private VideoPipe AddVideoPipe(LaneKey key, Demuxer demuxer, int streamIndex, List<VideoPipe> pipes)
        {
            var queue = new PacketQueue(32);
            demuxer.AttachQueue(streamIndex, queue);
            var sink = new PooledFrameSink();
            var pipe = new VideoPipe { Key = key, Demuxer = demuxer, Queue = queue, Sink = sink };
            pipes.Add(pipe);

            pipe.Worker = new VideoDecodeWorker(
                demuxer, streamIndex, queue, _options, _clock,
                () => sink,
                () => IsPipePlaying(key),
                OnImmediatePresented,
                srcPts => MapVideoPtsToClock(key, srcPts));
            Interlocked.Increment(ref _decoderOpens);
            return pipe;
        }

        /// <summary>Whether a pipeline presents on the clock right now. A lane above 0 holds its
        /// frame unless one of its clips covers the playhead, and its queue then blocks, so it does
        /// not decode on to the end of the file between clips. Entering a clip is a seam, whose hop
        /// wakes the presenter. Lane 0 keeps running through gaps, as it always has.</summary>
        private bool IsPipePlaying(LaneKey key)
        {
            if (_state != PlayerState.Playing)
                return false;
            return key.Lane == 0 || OffsetAt(_map, key, Position.Ticks) != long.MinValue;
        }

        /// <summary>
        /// Opens the person-matte pipeline for one lane of a video stream, when a valid matte sidecar
        /// exists (see <see cref="AiSidecars"/>). The pipe is keyed by the <b>parent</b> lane — the matte
        /// shares its PTS timeline by contract, so the parent's timeline mapping paces, seeks and
        /// seam-hops both identically — and its sink registers as the stream's mask feed
        /// (<see cref="PlaybackFrameSource.RegisterMaskStream"/>). A sidecar that is missing,
        /// stale or fails to open simply means no mask: the effect degrades in the composer,
        /// never here.
        /// </summary>
        private void BuildMattePipe(Source source, LaneKey key, List<VideoPipe> pipes)
        {
            var mattePath = AiSidecars.MattePath(_options.SidecarCacheDir, key.SourceId, key.StreamIndex);
            if (mattePath == null || !AiSidecars.IsValid(mattePath, source.Path))
                return;

            var demuxer = new Demuxer();
            try
            {
                demuxer.Open(mattePath);
            }
            catch
            {
                demuxer.Dispose();
                return; // a corrupt sidecar is a missing matte, not a failed open
            }

            _frameSource.RegisterMaskStream(key, AddVideoPipe(key, demuxer, 0, pipes).Sink);
        }

        // ------------------------------------------------------------------------- live editing

        /// <summary>
        /// Applies an edited project. When the set of referenced media streams is unchanged (all
        /// trim/cut/move/transform/volume edits), the timeline mapping and cut schedules are
        /// swapped atomically on the calling thread — decoders keep running and, while playing,
        /// the seam logic re-syncs the pipelines to the new mapping on the next tick; the
        /// returned task is already completed. Only a changed stream set (streams added/removed,
        /// files relinked) rebuilds the pipelines, preserving position and state — and that
        /// rebuild runs on a background task, because it tears decoder threads down and re-runs
        /// avformat/avcodec opens (hundreds of ms of blocking work that must never execute on
        /// the caller's UI thread). The returned task completes once the rebuild has been
        /// applied; overlapping updates coalesce (latest project wins). A failed rebuild lands
        /// the player in <see cref="PlayerState.Failed"/> (see <see cref="LastError"/>) instead
        /// of faulting the task.
        /// </summary>
        public Task UpdateProject(Project project)
        {
            ArgumentNullException.ThrowIfNull(project);

            lock (_updateSync)
            {
                // The synchronous fast path is only safe while no rebuild is queued or in
                // flight — once one is, every update must funnel through the queue so the
                // latest project always wins (a cheap edit racing ahead of a queued rebuild
                // would otherwise be clobbered by the stale rebuild finishing after it).
                if (!_updateLoopActive)
                {
                    lock (_lifecycleSync)
                    {
                        if (_disposed)
                            return Task.CompletedTask;
                        if (_state is PlayerState.Idle or PlayerState.Opening or PlayerState.Failed)
                            throw new InvalidOperationException("UpdateProject requires an open player (await OpenAsync first).");

                        var map = ProjectTimelineMap.Build(project);
                        var set = _pipelines;
                        if (set != null && StreamSignature(project, map, CollectAudioStreams(project), MixRate(project), _options.SidecarCacheDir) == set.Signature)
                        {
                            ApplyMappingSwap(project, map, set);
                            return Task.CompletedTask;
                        }
                    }
                }

                _pendingUpdate = project;
                if (!_updateLoopActive)
                {
                    _updateLoopActive = true;
                    _updateTask = Task.Run(UpdateLoop);
                }

                return _updateTask;
            }
        }

        /// <summary>The stream-set-unchanged edit path: swap mapping + cut schedules atomically,
        /// no decoder touched. The mix worker gets the edited project as a mixer-snapshot swap at
        /// its next chunk boundary — volume/transition edits ride this without a decoder in
        /// sight. Caller holds <see cref="_lifecycleSync"/>.</summary>
        private void ApplyMappingSwap(Project project, ProjectTimelineMap map, PipelineSet set)
        {
            // captured through the OLD warp before anything swaps: the project position is the
            // stable anchor an edit must preserve, whatever it does to the output timeline.
            long positionTicks = Position.Ticks;

            _project = project;
            _map = map;
            var warp = TimeWarp.Build(project);
            bool warpChanged = !warp.MappingEquals(_warp);
            _warp = warp;
            _frameSource.SetCutSchedules(BuildCutSchedules(map));
            if (positionTicks > map.DurationTicks)
                positionTicks = map.DurationTicks;
            if (set.MixWorker != null)
            {
                bool wasEof = set.MixWorker.EofReached;
                set.MixWorker.UpdateProject(project, warp);
                // an edit that extends the audio revives a worker idling at the old audio end;
                // without a re-base it would resume mixing THERE (its next-frame cursor and the
                // sink's timing base froze at the old end), and OnTick's re-attach would yank
                // the master clock backwards. Flush production onto the live position instead —
                // the mix thread adopts the project before the seek in the same iteration, so
                // the grown end is seen first and the seek then re-bases cursor + sink timing.
                // A changed warp mapping needs the same flush: the sink's timing describes the
                // old output domain until production re-bases on the playhead under the new one.
                if (wasEof || warpChanged)
                    set.MixWorker.PrepareSeek(new TimeSpan(positionTicks));
            }
            // each pipe's active offset intentionally kept: if the current position's segment
            // offset changed under the new map (e.g. a cut moved past the playhead), the
            // next tick's seam check sees the mismatch and hops that pipe.

            // a speed edit re-shapes the output timeline under the playhead: keep the project
            // position and re-anchor the clock's output time to it under the new warp — paused
            // included. Every edit that leaves the mapping alone leaves the clock alone.
            if (warpChanged)
                _clock.SetPosition(new TimeSpan(warp.ToOutput(positionTicks)));
        }

        /// <summary>Drains queued project updates on a background task, same discipline as
        /// <see cref="SeekLoop"/>: coalesced latest-wins, serialized under
        /// <see cref="_lifecycleSync"/>. Re-checks the stream signature per iteration — a
        /// queued rebuild superseded by an edit that restored the current stream set collapses
        /// back into the cheap mapping swap.</summary>
        private void UpdateLoop()
        {
            while (true)
            {
                Project project;
                lock (_updateSync)
                {
                    project = _pendingUpdate;
                    _pendingUpdate = null;
                    if (project == null || _disposed)
                    {
                        _updateLoopActive = false;
                        return;
                    }
                }

                try
                {
                    lock (_lifecycleSync)
                    {
                        if (_disposed)
                            continue; // next iteration clears the loop flag and exits

                        var map = ProjectTimelineMap.Build(project);
                        var set = _pipelines;
                        if (set != null && StreamSignature(project, map, CollectAudioStreams(project), MixRate(project), _options.SidecarCacheDir) == set.Signature)
                            ApplyMappingSwap(project, map, set);
                        else
                            ReopenCore(project, map); // contains its own failures (Failed state)
                    }
                }
                catch
                {
                    // a malformed project failed mapping; drop this update — a later one can
                    // still supersede it, and faulting the shared task would tear the loop down.
                }
            }
        }

        /// <summary>Full pipeline rebuild for a changed stream set, preserving position/state.
        /// Runs on the update loop's background task, under <see cref="_lifecycleSync"/>.
        /// Never throws: the old set is already gone when the rebuild starts, so a failure
        /// (source file deleted/moved, decoder init error) disposes anything partially built
        /// (see <see cref="BuildPipelines"/>), records <see cref="LastError"/> and lands the
        /// player in <see cref="PlayerState.Failed"/> — surfaced through
        /// <see cref="StateChanged"/>, never as an exception escaping into a caller's
        /// PropertyChanged handler. A later update retries the rebuild (self-heal).</summary>
        private void ReopenCore(Project project, ProjectTimelineMap map)
        {
            var pos = Position;
            bool wasPlaying = _state == PlayerState.Playing;
            if (wasPlaying)
                Pause();
            if (_state == PlayerState.Ended)
                SetState(PlayerState.Paused);

            var old = _pipelines;
            _pipelines = null;
            _frameSource.Clear();
            _clock.SetAudioSource(null);
            _audioDetached = false;
            if (old != null)
                DisposePipelineSet(old);

            _project = project;
            _map = map;
            _warp = TimeWarp.Build(project);
            _frameSource.SetCutSchedules(BuildCutSchedules(map));

            if (pos.Ticks > map.DurationTicks)
                pos = new TimeSpan(map.DurationTicks);

            PipelineSet set;
            try
            {
                set = BuildPipelines(project, map); // disposes its own partial build on failure
            }
            catch (Exception ex)
            {
                LastError = ex;
                SetState(PlayerState.Failed);
                return;
            }

            _pipelines = set;
            PrimeAndStart(set, map, pos.Ticks);
            _clock.SetPosition(new TimeSpan(ProjectToClockTicks(pos.Ticks)));
            if (_state == PlayerState.Failed)
                SetState(PlayerState.Paused); // a retried rebuild succeeded — transport is back

            // land the position with a real container seek (PrimeAndStart decodes from the file
            // head otherwise), then resume if we interrupted playback.
            var seek = SeekAsync(pos, SeekMode.Exact);
            if (wasPlaying)
                seek.ContinueWith(_ => Play(), TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        // ---------------------------------------------------------------------------- transport

        public void Play()
        {
            if (_state == PlayerState.Ended)
            {
                var t = SeekAsync(TimeSpan.Zero, SeekMode.Exact);
                t.ContinueWith(_ => PlayCore(), TaskContinuationOptions.OnlyOnRanToCompletion);
                return;
            }

            PlayCore();
        }

        private void PlayCore()
        {
            if (_state != PlayerState.Paused && _state != PlayerState.Ended)
                return;

            SetState(PlayerState.Playing);
            _pipelines?.AudioSink?.Play();
            _clock.Start();
            WakeAllPresenters();
        }

        public void Pause()
        {
            if (_state != PlayerState.Playing)
                return;

            _clock.Stop();
            _pipelines?.AudioSink?.Pause();
            SetState(PlayerState.Paused);
            WakeAllPresenters();
        }

        /// <summary>Seek in timeline time; concurrent calls coalesce (last position wins).</summary>
        public Task SeekAsync(TimeSpan position, SeekMode mode)
        {
            if (_map == null)
                throw new InvalidOperationException("No project open.");

            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;
            var dur = Duration;
            if (dur > TimeSpan.Zero && position > dur)
                position = dur;

            lock (_seekSync)
            {
                _pendingSeekPos = position;
                _pendingSeekMode = mode;
                _pendingSeamHop = false;
                _hasPendingSeek = true;
                return EnsureSeekLoop();
            }
        }

        /// <summary>Queues a hop of whichever pipes crossed a seam (see <see cref="HopSeams"/>),
        /// through the same loop so it never races a seek. It gives way to a queued seek, which
        /// lands every pipe anyway.</summary>
        private Task RequestSeamHop()
        {
            lock (_seekSync)
            {
                if (!_hasPendingSeek)
                {
                    _pendingSeamHop = true;
                    _hasPendingSeek = true;
                }

                return EnsureSeekLoop();
            }
        }

        /// <summary>Caller holds <see cref="_seekSync"/>.</summary>
        private Task EnsureSeekLoop()
        {
            if (!_seekLoopActive)
            {
                _seekLoopActive = true;
                _seekTask = Task.Run(SeekLoop);
            }

            return _seekTask;
        }

        private void SeekLoop()
        {
            while (true)
            {
                TimeSpan pos;
                SeekMode mode;
                bool seamHop;
                lock (_seekSync)
                {
                    if (!_hasPendingSeek || _disposed)
                    {
                        _seekLoopActive = false;
                        return;
                    }

                    pos = _pendingSeekPos;
                    mode = _pendingSeekMode;
                    seamHop = _pendingSeamHop;
                    _hasPendingSeek = false;
                }

                try
                {
                    lock (_lifecycleSync)
                    {
                        if (_disposed)
                            continue;
                        if (seamHop)
                            HopSeams();
                        else
                            DoSeek(pos, mode);
                    }
                }
                catch
                {
                    // a failed seek leaves the pipeline at its old position; not fatal.
                }
            }
        }

        private void DoSeek(TimeSpan target, SeekMode mode)
        {
            if (_state == PlayerState.Ended)
                SetState(PlayerState.Paused);

            var set = _pipelines;
            var map = _map;
            long tl = target.Ticks;

            // the clock lands on the target before any worker is unblocked: the post-seek immediate
            // present arrives on a decode thread and asks the clock where the playhead is (see
            // OnImmediatePresented), so a clock still holding the pre-seek position would race it.
            _lastSeekWasFast = mode == SeekMode.Fast;
            _clock.SetPosition(new TimeSpan(ProjectToClockTicks(tl)));

            // the mix worker seeks in timeline time — no source-domain mapping exists for audio.
            set?.MixWorker?.PrepareSeek(target);

            if (set != null)
                SeekPipes(set.AllVideo, map, tl, mode);

            // re-attach the audio master if it was detached at end-of-audio (also for
            // audio-only projects, which have no video pipes).
            if (_audioDetached && set?.AudioSink != null)
            {
                _audioDetached = false;
                _clock.SetAudioSource(set.AudioSink);
            }

            RaisePositionChanged();
        }

        /// <summary>
        /// The seam hop: lands only the pipes that crossed a seam on the segment at the playhead,
        /// leaving the rest decoding through it, so a pasted clip's edges do not stall the stream
        /// under it. The timeline position does not change across a video seam, so nothing else
        /// moves: the clock runs on, and the timeline-domain mix is already producing the right
        /// samples, where a flush would discard the ring's correct buffered lead (an audible dropout
        /// at every cut) and force the mix's streams off the decode-discard path that keeps preview
        /// seams sample-exact with render. Reads the playhead here rather than where the tick saw
        /// it, because a seek may have landed since.
        /// </summary>
        private void HopSeams()
        {
            var set = _pipelines;
            var map = _map;
            if (set == null)
                return;

            long tl = Position.Ticks;
            var crossed = new List<VideoPipe>();
            foreach (var pipe in set.AllVideo)
            {
                if (HasCrossedSeam(map, pipe, tl))
                    crossed.Add(pipe);
            }

            SeekPipes(crossed, map, tl, SeekMode.Exact);
        }

        private static void SeekPipes(IReadOnlyList<VideoPipe> pipes, ProjectTimelineMap map, long tl, SeekMode mode)
        {
            // 1. record per-worker post-flush behavior (source-domain targets).
            foreach (var pipe in pipes)
            {
                Volatile.Write(ref pipe.ActiveOffsetTicks, OffsetAt(map, pipe.Key, tl));
                pipe.Worker.PrepareSeek(new TimeSpan(VideoSourceTarget(map, pipe.Key, tl)), mode);
            }

            // 2/3. flush + container seek, then unblock the pipeline.
            foreach (var pipe in pipes)
            {
                int serial = pipe.Demuxer.SeekAndFlush(new TimeSpan(VideoSourceTarget(map, pipe.Key, tl)));
                pipe.Worker.OnSeeked(serial);
                Interlocked.Increment(ref pipe.SeekCount);
            }
        }

        public async Task StepFrameAsync(int direction)
        {
            var set = _pipelines;
            if (_map == null || set?.Primary == null)
                return;

            if (_state == PlayerState.Playing)
                Pause();

            long from = Position.Ticks;
            var driver = StepDriver(set, _map, from);

            if (direction < 0)
            {
                var target = Position - driver.Worker.FrameDuration;
                if (target < TimeSpan.Zero)
                    target = TimeSpan.Zero;
                await SeekAsync(target, SeekMode.Exact);
                return;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            driver.Worker.RequestStep(tcs, pts =>
            {
                if (_map.TryGetVideo(driver.Key, out var stream))
                    _clock.SetPosition(new TimeSpan(ProjectToClockTicks(stream.StepForward(pts.Ticks, Position.Ticks))));
                RaisePositionChanged();
            });

            foreach (var pipe in set.AllVideo)
            {
                if (pipe == driver)
                    continue;

                // an idle pipe waits: its next clip is what it is parked on, or a place it has yet
                // to reach, and walking on through it would hand the playhead a frame well inside
                // that clip once this pipe has to say where the playhead goes. Forgetting its offset
                // makes the entry a seam, whose hop lands the clip.
                if (OffsetAt(_map, pipe.Key, from) != long.MinValue)
                    pipe.Worker.RequestStep(null, null);
                else
                    Volatile.Write(ref pipe.ActiveOffsetTicks, long.MinValue);
            }

            try
            {
                await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }

            // stepping across a cut seam leaves a pipeline about to decode cut-out source; the
            // stepped frame's mapped position already sits in the next segment, so re-sync there.
            long tl = Position.Ticks;
            foreach (var pipe in set.AllVideo)
            {
                if (HasCrossedSeam(_map, pipe, tl))
                {
                    await SeekAsync(Position, SeekMode.Exact);
                    break;
                }
            }
        }

        /// <summary>The pipe a frame step at the playhead maps through: the first with a clip there.
        /// Only that pipe's next frame says where the playhead goes, since a lane with nothing at
        /// the playhead holds a frame from somewhere else on the timeline. <see cref="PipelineSet.AllVideo"/>
        /// holds the primary first and a matte after its parent, so this is the primary whenever it
        /// can be and never a matte. Asked per step, so it follows the mapping across a live edit
        /// that swaps what the pipes play. With no clip at the playhead, the primary walks on.</summary>
        private static VideoPipe StepDriver(PipelineSet set, ProjectTimelineMap map, long tl)
        {
            foreach (var pipe in set.AllVideo)
            {
                if (OffsetAt(map, pipe.Key, tl) != long.MinValue)
                    return pipe;
            }

            return set.Primary;
        }

        // ----------------------------------------------------------------------------- composing

        /// <summary>
        /// Snapshot for the preview's draw operation: the frame source to
        /// <see cref="PlaybackFrameSource.Pump"/>+compose from, and the timeline instant to
        /// compose at. Returns false before a project is open.
        /// </summary>
        public bool TryGetFrameSource(out PlaybackFrameSource source, out long timelineTicks)
        {
            source = _frameSource;
            timelineTicks = Position.Ticks;
            return _state is PlayerState.Paused or PlayerState.Playing or PlayerState.Ended;
        }

        // -------------------------------------------------------------------------------- ticks

        private void OnImmediatePresented(VideoDecodeWorker worker, TimeSpan pts)
        {
            // the primary track's immediately-presented post-seek frame defines the position a
            // paused UI shows, mapped into timeline time.
            var set = _pipelines;
            if (set?.Primary == null || worker != set.Primary.Worker || _state == PlayerState.Playing)
                return;

            // a Fast (scrub) seek keeps the clock on its target — see _lastSeekWasFast — and a
            // queued newer seek makes this frame stale: its pts describes a target the clock has
            // already left (or is about to leave), and adopting it would drag the composed
            // instant backwards mid-scrub.
            if (_lastSeekWasFast)
                return;
            lock (_seekSync)
            {
                if (_hasPendingSeek)
                    return;
            }

            // …but only where the clip at the playhead plays this frame (see TryMapInSegmentAt).
            // Anywhere else its pts would drag the position off: to the end of the video on a seek
            // past the footage, to the frame's other home on a seek into a repeat.
            var map = _map;
            if (map == null || !map.TryGetVideo(set.Primary.Key, out var stream)
                || !stream.TryMapInSegmentAt(Position.Ticks, pts.Ticks, out long frameTicks))
                return;

            _clock.SetPosition(new TimeSpan(ProjectToClockTicks(frameTicks)));
            RaisePositionChanged();
        }

        private void TickLoop()
        {
            while (!_tickStop.Wait(TickIntervalMs))
                OnTick();
        }

        private void OnTick()
        {
            if (_disposed || _state != PlayerState.Playing)
                return;

            try
            {
                var set = _pipelines;
                var map = _map;
                var pos = Position;
                long tl = pos.Ticks;

                // cut seam crossed: the source-timeline offset at the playhead no longer matches
                // what a pipeline is decoding — hop it with an internal exact seek. This is
                // FFmpegVideoPlayer's skip-range hop with the discontinuities read straight from
                // the project.
                if (set != null && TickSeams(set, map, tl)
                    && Interlocked.CompareExchange(ref _skipSeekBusy, 1, 0) == 0)
                {
                    RequestSeamHop().ContinueWith(_ => Interlocked.Exchange(ref _skipSeekBusy, 0));
                }

                // audio finished before video — hand the clock to the stopwatch so video plays
                // out. The reverse transition brings the audio master back when the worker
                // produces again (a seek's optimistic EofReached clear lost the race to the mix
                // thread, or an edit extended the audio). Serialized against DoSeek/ReopenCore —
                // TryEnter, never lock: the work self-retries next tick, and the timer thread
                // must not block behind a hundreds-of-ms rebuild.
                if (set?.MixWorker != null && Monitor.TryEnter(_lifecycleSync))
                {
                    try
                    {
                        // re-check under the lock: a rebuild may have swapped the set out
                        if (!_disposed && _pipelines == set)
                            TickAudioAttach(set, pos);
                    }
                    finally
                    {
                        Monitor.Exit(_lifecycleSync);
                    }
                }

                // The timeline length is what ends playback: media running out says nothing about
                // the timeline, because text/image items (which have no media pipeline at all) can
                // outlast the last video frame and must keep playing to the project's end. Running
                // out of media is only a fallback for a timeline of unknown length.
                long dur = map?.DurationTicks ?? 0;
                bool ended = dur > 0 && tl >= dur;
                if (dur <= 0 && set != null && set.AllVideo.Length > 0)
                {
                    bool videoDone = true;
                    foreach (var pipe in set.AllVideo)
                        videoDone &= pipe.Demuxer.IsEof && pipe.Worker.EofPresented;
                    bool audioDone = set.MixWorker == null
                                     || (set.MixWorker.EofReached && set.AudioRing.Available == 0);
                    ended = videoDone && audioDone;
                }

                if (ended)
                {
                    _clock.Stop();
                    set?.AudioSink?.Pause();
                    SetState(PlayerState.Ended);
                }

                RaisePositionChanged();
            }
            catch
            {
                // never let a tick take down the timer thread.
            }
        }

        /// <summary>The tick's seam watch: whether any pipe crossed a seam at the playhead. A lane
        /// above 0 forgets its offset on leaving its clips: it stopped decoding there, so the next
        /// clip enters through a hop even when it continues the same offset.</summary>
        private static bool TickSeams(PipelineSet set, ProjectTimelineMap map, long tl)
        {
            bool crossed = false;
            foreach (var pipe in set.AllVideo)
            {
                if (pipe.Key.Lane > 0 && OffsetAt(map, pipe.Key, tl) == long.MinValue)
                    Volatile.Write(ref pipe.ActiveOffsetTicks, long.MinValue);
                else
                    crossed |= HasCrossedSeam(map, pipe, tl);
            }

            return crossed;
        }

        /// <summary>Whether the segment at the playhead is not the one the pipe was last synced to.
        /// A gap crosses nothing: the pipe hops if it comes out somewhere else.</summary>
        private static bool HasCrossedSeam(ProjectTimelineMap map, VideoPipe pipe, long tl)
        {
            long offset = OffsetAt(map, pipe.Key, tl);
            return offset != long.MinValue && offset != Volatile.Read(ref pipe.ActiveOffsetTicks);
        }

        /// <summary>The tick's audio-master state machine (detach at end-of-audio, re-attach when
        /// production resumes). Caller holds <see cref="_lifecycleSync"/>.</summary>
        private void TickAudioAttach(PipelineSet set, TimeSpan pos)
        {
            bool audioEof = set.MixWorker.EofReached && set.AudioRing.Available == 0;
            if (!_audioDetached && audioEof)
            {
                _audioDetached = true;
                _clock.SetAudioSource(null);
            }
            else if (_audioDetached && !set.MixWorker.EofReached)
            {
                // only trust the sink as master when its timing describes the
                // playhead: after an EOF idle the base pts is still frozen where
                // production parked, and attaching that snaps the clock backwards.
                // The sink's timing lives in the clock's (output) domain.
                var sink = set.AudioSink;
                var posOut = new TimeSpan(ProjectToClockTicks(pos.Ticks));
                if (sink.HasTiming && (posOut - sink.PlayedTime).Duration() <= AudioReattachTolerance)
                {
                    _audioDetached = false;
                    _clock.SetAudioSource(sink);
                }
                else
                {
                    // flush + re-base production on the playhead; re-attach on a
                    // later tick once the re-based timing lands.
                    set.MixWorker.PrepareSeek(pos);
                }
            }
        }

        public PlaybackStatistics GetStatistics()
        {
            var set = _pipelines;
            var workers = set?.AllVideo ?? Array.Empty<VideoPipe>();
            var tracks = new TrackStatistics[workers.Length];
            for (int i = 0; i < workers.Length; i++)
                tracks[i] = workers[i].Worker.GetIntervalStatistics();

            return new PlaybackStatistics
            {
                Video = tracks,
                HasAudio = set?.MixWorker != null,
                AudioBufferedSeconds = set?.AudioRing != null
                    ? set.AudioRing.Available / 2.0 / set.MixRate
                    : 0,
            };
        }

        // ------------------------------------------------------------------------------ mapping

        /// <summary>Project → clock domain: the clock runs in warped output time, so every value
        /// handed to it (seek targets, adopted frame pts) maps through the warp. An identity warp
        /// keeps the domains coincident and both conversions exact pass-throughs.</summary>
        private long ProjectToClockTicks(long projectTicks)
        {
            var warp = _warp;
            return warp is { IsIdentity: false } ? warp.ToOutput(projectTicks) : projectTicks;
        }

        private long ClockToProjectTicks(long clockTicks)
        {
            var warp = _warp;
            return warp is { IsIdentity: false } ? warp.ToProject(clockTicks) : clockTicks;
        }

        /// <summary>Source pts → the clock's domain (project timeline, then through the warp):
        /// what the decode workers pace their presentation against. Asks for the timeline instant
        /// nearest the playhead, which is where a frame of a repeated span belongs.</summary>
        private long MapVideoPtsToClock(LaneKey key, long srcTicks)
        {
            var map = _map;
            long tl = map != null && map.TryGetVideo(key, out var sm)
                ? sm.SourceToTimeline(srcTicks, Position.Ticks)
                : srcTicks;
            return ProjectToClockTicks(tl);
        }

        private static long VideoSourceTarget(ProjectTimelineMap map, LaneKey key, long tlTicks)
        {
            if (map != null && map.TryGetVideo(key, out var sm))
                return sm.TimelineToSource(tlTicks);
            return tlTicks;
        }

        private static long OffsetAt(ProjectTimelineMap map, LaneKey key, long tlTicks)
        {
            if (map != null && map.TryGetVideo(key, out var sm))
                return sm.OffsetAtTimeline(tlTicks);
            return long.MinValue;
        }

        private static Dictionary<LaneKey, SkipRangeSchedule> BuildCutSchedules(ProjectTimelineMap map)
        {
            var cuts = new Dictionary<LaneKey, SkipRangeSchedule>();
            foreach (var (key, stream) in map.VideoStreams)
            {
                if (stream.SourceCuts.Ranges.Count > 0)
                    cuts[key] = stream.SourceCuts;
            }

            return cuts;
        }

        private static Source FindSource(Project project, Guid sourceId)
        {
            if (project.Sources == null)
                return null;
            foreach (var source in project.Sources)
            {
                if (source.Id == sourceId)
                    return source;
            }

            return null;
        }

        /// <summary>The mix rate for a project: its own output rate, falling back to the open
        /// options when the project does not carry one.</summary>
        private int MixRate(Project project)
            => project.Output != null && project.Output.SampleRate > 0
                ? project.Output.SampleRate
                : _options.AudioSampleRate;

        /// <summary>The distinct audio streams referenced by items on unmuted audio tracks,
        /// sorted — the audio half of the pipeline identity. Mute/unmute and stream add/remove
        /// change it (rebuild); volume/transition edits do not (cheap mixer swap).</summary>
        private static List<(Guid SourceId, int StreamIndex)> CollectAudioStreams(Project project)
        {
            var audioTracks = new HashSet<Guid>();
            foreach (var track in project.Tracks ?? new List<Track>())
            {
                if (track.Kind == TrackKind.Audio && !track.Muted)
                    audioTracks.Add(track.Id);
            }

            var keys = new List<(Guid SourceId, int StreamIndex)>();
            foreach (var item in project.Items ?? new List<Item>())
            {
                if (item.Content is MediaContent media && item.DurationTicks > 0
                    && audioTracks.Contains(item.TrackId)
                    && !keys.Contains((media.SourceId, media.StreamIndex)))
                    keys.Add((media.SourceId, media.StreamIndex));
            }

            keys.Sort((a, b) =>
            {
                int bySource = a.SourceId.CompareTo(b.SourceId);
                return bySource != 0 ? bySource : a.StreamIndex.CompareTo(b.StreamIndex);
            });
            return keys;
        }

        /// <summary>Identity of the decode-pipeline set: which streams of which files feed it
        /// (one line per lane of a video stream, so a drag that starts or stops overlapping a clip of
        /// its stream rebuilds, and one per referenced audio item stream — a video stream
        /// tagged when the project wants its person matte, an audio stream tagged when its
        /// track's denoise sidecar feeds the mix; either toggle changes what is decoded, so it
        /// must rebuild, while the blur/strength dials are per-pixel/per-sample math over the
        /// same streams and deliberately are not part of the identity), plus the mix rate.
        /// The matte tag carries the sidecar's on-disk readiness (<c>:mt1</c> valid,
        /// <c>:mt0</c> missing/stale): a sidecar that finishes generating mid-session flips the
        /// tag, so the completion push rebuilds and the matte pipe opens — otherwise the preview
        /// would stay unmatted until the next structural edit. Edits that keep this equal never
        /// rebuild pipelines.</summary>
        private static string StreamSignature(Project project, ProjectTimelineMap map,
            List<(Guid SourceId, int StreamIndex)> audioStreams, int mixRate, string sidecarCacheDir)
        {
            var keys = new List<LaneKey>(map.VideoStreams.Keys);
            keys.Sort();

            var mattes = MatteGenerator.CollectMatteStreams(project);
            var denoised = DenoisedAudioSource.CollectDenoisedStreams(project);
            var sb = new StringBuilder();
            foreach (var key in keys)
            {
                var sourcePath = FindSource(project, key.SourceId)?.Path;
                sb.Append("v:").Append(key.SourceId).Append(':').Append(key.StreamIndex)
                  .Append(':').Append(key.Lane).Append(':').Append(sourcePath);
                if (mattes.Contains((key.SourceId, key.StreamIndex)))
                {
                    var mattePath = AiSidecars.MattePath(sidecarCacheDir, key.SourceId, key.StreamIndex);
                    sb.Append(AiSidecars.IsValid(mattePath, sourcePath) ? ":mt1" : ":mt0");
                }
                sb.Append('\n');
            }
            foreach (var key in audioStreams)
            {
                sb.Append("a:").Append(key.SourceId).Append(':').Append(key.StreamIndex)
                  .Append(':').Append(FindSource(project, key.SourceId)?.Path);
                if (denoised.ContainsKey(key))
                    sb.Append(":dn");
                sb.Append('\n');
            }

            if (audioStreams.Count > 0)
                sb.Append("rate:").Append(mixRate);
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------ helpers

        private void WakeAllPresenters()
        {
            var set = _pipelines;
            if (set == null)
                return;
            foreach (var pipe in set.AllVideo)
                pipe.Worker.WakePresent();
        }

        private void SetState(PlayerState state)
        {
            lock (_stateSync)
            {
                if (_state == state)
                    return;
                _state = state;
            }

            var handler = StateChanged;
            if (handler != null)
                _dispatch(() => handler(this, state));
        }

        private void RaisePositionChanged()
        {
            var handler = PositionChanged;
            if (handler != null)
                _dispatch(() => handler(this, EventArgs.Empty));
        }

        // ------------------------------------------------------------------------------ dispose

        public void Dispose()
        {
            lock (_seekSync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _hasPendingSeek = false;
            }

            // defer teardown behind an in-flight OpenCore (native use-after-free otherwise).
            var open = _openTask;
            if (open != null && !open.IsCompleted)
            {
                open.ContinueWith(_ => DisposeCore(), TaskScheduler.Default);
                return;
            }

            DisposeCore();
        }

        private void DisposeCore()
        {
            // joined outside the lock: the tick only ever TryEnters _lifecycleSync, so it
            // cannot deadlock here, and stopping it first means teardown races nothing. The
            // event is deliberately not disposed — a tick that outlived the join would fault
            // on it.
            _tickStop.Set();
            var tick = _tickThread;
            _tickThread = null;
            tick?.Join(TimeSpan.FromSeconds(1));

            lock (_lifecycleSync)
            {
                var set = _pipelines;
                _pipelines = null;
                _frameSource.Dispose();
                if (set != null)
                    DisposePipelineSet(set);

                _state = PlayerState.Idle;
            }
        }

        private static void DisposePipelineSet(PipelineSet set)
        {
            // stop order matters (see FFmpegVideoPlayer.DisposeCore): demux threads first, then
            // wake every consumer via queue Stop, then join workers, then free queues/contexts.
            foreach (var pipe in set.AllVideo)
                pipe.Demuxer.Stop();

            foreach (var pipe in set.AllVideo)
                pipe.Queue.Stop();

            foreach (var pipe in set.AllVideo)
            {
                pipe.Worker?.Dispose(); // null only when its decoder failed to open
                pipe.Sink.Dispose();
            }

            set.MixWorker?.Dispose(); // joins the mix thread before its sink/ring go away
            set.AudioSink?.Dispose();

            foreach (var pipe in set.AllVideo)
            {
                pipe.Queue.Dispose();
                pipe.Demuxer.Dispose();
            }
        }
    }
}
