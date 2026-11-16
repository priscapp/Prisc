using System;
using System.Collections.Generic;
using Prisc.VideoSDK.Playback;
using LaneKey = (System.Guid SourceId, int StreamIndex, int Lane);

namespace Prisc.VideoSDK.Composition
{
    /// <summary>
    /// The preview path's <see cref="IFrameSource"/>: the latest committed frame per
    /// (sourceId, streamIndex, lane), fed by <see cref="CompositionPlayer"/>'s decode workers through
    /// one <see cref="PooledFrameSink"/> per lane. The counterpart of
    /// <see cref="SequentialFrameSource"/> — same lookup contract, but frames arrive from paced
    /// playback pipelines instead of being decoded on demand.
    ///
    /// Threading: decode/present threads only *complete* frames into the sinks (thread-safe,
    /// latest-wins). Texture upload is context-affine, so it happens in <see cref="Pump"/>, which
    /// the composing side — the Avalonia render thread inside its draw operation, or a test — calls
    /// once per composed frame with its own <see cref="FrameTextureCache"/> before
    /// <c>FrameComposer.Compose</c>. <see cref="TryGetFrame"/> then reads the cache without
    /// touching any GPU state of its own. Pump and TryGetFrame must be called on that one
    /// composing thread; nothing here ever touches a GRContext from a decode thread.
    ///
    /// Lookup semantics: hold-last. The pipelines pace frames against the shared clock, so the
    /// newest uploaded frame of a lane *is* the frame covering the clock position for the clips in
    /// that lane (<see cref="StreamLanes"/> keeps clips that want different source times apart); the
    /// requested time is not re-checked against it (during a seek the previous frame intentionally
    /// holds until the new one lands, and <see cref="IFrameSource"/> permits returning a frame ahead
    /// of the requested time). The one filter applied: frames whose source pts lies inside a cut of the current
    /// project (see <see cref="SkipRangeSchedule"/>) are dropped at the pump rather than cached,
    /// so cut-out material never reaches the canvas even in the interval between playback entering
    /// the cut and the player's internal hop over it.
    /// </summary>
    public sealed class PlaybackFrameSource : IFrameSource, IDisposable
    {
        private readonly object _sync = new object();
        private volatile Dictionary<LaneKey, PooledFrameSink> _sinks
            = new Dictionary<LaneKey, PooledFrameSink>();
        private volatile Dictionary<LaneKey, PooledFrameSink> _maskSinks
            = new Dictionary<LaneKey, PooledFrameSink>();
        private volatile Dictionary<LaneKey, SkipRangeSchedule> _cuts
            = new Dictionary<LaneKey, SkipRangeSchedule>();
        private FrameTextureCache _cache; // composing thread only
        private bool _disposed;

        /// <summary>Raised (on a decode/present thread) whenever any stream completes a new frame.
        /// Typical use: schedule an invalidate so the preview composes again. Must not block.</summary>
        public event Action FrameArrived;

        /// <summary>Registers a lane's sink. Copy-on-write so concurrent pumps stay lock-free.</summary>
        internal void RegisterStream(LaneKey key, PooledFrameSink sink)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                var next = new Dictionary<LaneKey, PooledFrameSink>(_sinks) { [key] = sink };
                _sinks = next;
            }

            sink.FrameCompleted += OnFrameCompleted;
        }

        /// <summary>Registers the sink feeding a lane's person-matte frames (the sidecar
        /// pipeline — see <c>CompositionPlayer</c>), keyed by the lane it masks. Same
        /// copy-on-write discipline as <see cref="RegisterStream"/>.</summary>
        internal void RegisterMaskStream(LaneKey key, PooledFrameSink sink)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                var next = new Dictionary<LaneKey, PooledFrameSink>(_maskSinks) { [key] = sink };
                _maskSinks = next;
            }

            sink.FrameCompleted += OnFrameCompleted;
        }

        /// <summary>Detaches every stream (pipeline teardown/reopen). Does not dispose the sinks —
        /// the player's pipelines own them.</summary>
        internal void Clear()
        {
            Dictionary<LaneKey, PooledFrameSink> old;
            Dictionary<LaneKey, PooledFrameSink> oldMasks;
            lock (_sync)
            {
                old = _sinks;
                oldMasks = _maskSinks;
                _sinks = new Dictionary<LaneKey, PooledFrameSink>();
                _maskSinks = new Dictionary<LaneKey, PooledFrameSink>();
            }

            foreach (var sink in old.Values)
                sink.FrameCompleted -= OnFrameCompleted;
            foreach (var sink in oldMasks.Values)
                sink.FrameCompleted -= OnFrameCompleted;
        }

        /// <summary>Swaps the per-lane source-domain cut schedules (project edit).</summary>
        internal void SetCutSchedules(Dictionary<LaneKey, SkipRangeSchedule> cuts)
        {
            _cuts = cuts ?? new Dictionary<LaneKey, SkipRangeSchedule>();
        }

        /// <summary>Whether any video stream is registered — false when the open project has no
        /// visible video item (every video track hidden, or none at all), in which case
        /// <see cref="FrameArrived"/> will never fire.</summary>
        public bool HasVideoStreams => _sinks.Count > 0;

        private void OnFrameCompleted() => FrameArrived?.Invoke();

        /// <summary>
        /// Uploads any newly completed frames into <paramref name="cache"/> (recycling their
        /// pooled buffers) and remembers the cache for subsequent <see cref="TryGetFrame"/> calls.
        /// Call once per composed frame, on the composing thread that owns the cache's context,
        /// before <c>FrameComposer.Compose</c>.
        /// </summary>
        public void Pump(FrameTextureCache cache)
        {
            ArgumentNullException.ThrowIfNull(cache);
            ObjectDisposedException.ThrowIf(_disposed, this);

            _cache = cache;
            var sinks = _sinks;
            var maskSinks = _maskSinks;
            var cuts = _cuts;
            foreach (var (key, sink) in sinks)
            {
                if (!sink.TryAcquireLatest(out var frame))
                    continue;

                if (cuts.TryGetValue(key, out var schedule) && schedule.TryGetSkipEnd(frame.Pts, out _))
                {
                    // cut-out material presented in the window before the player hops the cut —
                    // recycle it and keep the previous frame on screen.
                    frame.Return();
                    continue;
                }

                cache.Upload(key.SourceId, key.StreamIndex, frame.Pts.Ticks,
                    frame.Buffer, frame.Width, frame.Height, frame.RowBytes, key.Lane);
            }

            foreach (var (key, sink) in maskSinks)
            {
                if (!sink.TryAcquireLatest(out var frame))
                    continue;

                // the matte shares its stream's PTS timeline, so its lane's cut schedule
                // applies verbatim — a held frame keeps its held matte.
                if (cuts.TryGetValue(key, out var schedule) && schedule.TryGetSkipEnd(frame.Pts, out _))
                {
                    frame.Return();
                    continue;
                }

                cache.UploadMask(key.SourceId, key.StreamIndex, frame.Pts.Ticks,
                    frame.Buffer, frame.Width, frame.Height, frame.RowBytes, key.Lane);
            }
        }

        /// <inheritdoc/>
        public bool TryGetFrame(Guid sourceId, int streamIndex, long sourceTimeTicks, out FrameRef frame)
            => TryGetFrame(sourceId, streamIndex, sourceTimeTicks, 0, out frame);

        /// <inheritdoc/>
        public bool TryGetFrame(Guid sourceId, int streamIndex, long sourceTimeTicks, int lane, out FrameRef frame)
        {
            frame = default;
            var cache = _cache;
            if (cache == null)
                return false;

            if (!cache.TryGet(sourceId, streamIndex, out var image, out long ptsTicks, lane))
                return false;

            // hold-last for the matte too: its pipeline paces against the same clock as the
            // frame's, so the newest uploaded matte is the one covering the frame — the same
            // reasoning the class doc applies to the frames themselves.
            cache.TryGetMask(sourceId, streamIndex, out var mask, out _, lane);
            frame = new FrameRef(image, ptsTicks, mask);
            return true;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            Clear();
            _cache = null;
        }
    }
}
