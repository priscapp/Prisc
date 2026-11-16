using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Model;
using SkiaSharp;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Project files saved by the editor before track groups existed (Fixtures/legacy-v2), the
    /// sidecar they share, and the overlay goldens recorded from them. Each was made by running the
    /// editor's own session operations on a 60 s recording (1280x720 screen, webcam, two audio rows,
    /// Keys and Cursor rows, native cursor style) at the commit before track groups, with ids
    /// renumbered. The files are frozen: the code that made them is going away by design, so a
    /// fixture is never regenerated, and a golden changes only if an overlay's behaviour is meant to.
    ///
    /// <para>The sidecar is built to be read back from pixels: the pointer stands somewhere unique
    /// for every 100 ms of source time, and a key goes down every 5 s.</para>
    /// </summary>
    internal static class LegacyFixtures
    {
        /// <summary>Every fixture, by file name without the extension.</summary>
        public static readonly string[] Names =
        {
            // Ctrl+K at 10 s and 25 s, then the last slice dragged 2 s right. The first slice is
            // placed and cropped, the second slides in.
            "ctrl-k-recording",
            // Split at 10 s and 20 s on the screen row, delete the middle, Close Gap: one cursor clip
            // spans screen clips that jump in source time, and hangs 10 s past them.
            "overlay-discontinuous",
            // Ctrl+K at 40 s, then a screen hole at 10..20 s filled with a freeze of its left
            // neighbour, which never joins a group.
            "freeze",
            // Ctrl+K at 10, 20 and 30 s. The 10 s slice is 2x, so its overlays outlive it by 5 s; the
            // 20 s slice is 0.5x.
            "sped-segment",
            // Duplicate Track on the screen row: an ungrouped small copy, reordered behind the
            // original and dragged 3 s later, so the grouped original is not the backmost clip and
            // the two never show the same source instant. A hole cut in the original at 20..30 s
            // leaves the copy to host.
            "duplicate-row",
            // Duplicate Track on the screen row, left where it lands: a small copy in front of the
            // original, timed the same, so both show the same source instant. A hole cut in the
            // original at 20..30 s leaves the copy to host.
            "duplicate-in-front",
            // Split at 10 s and 20 s on the screen row, delete the middle, leave the hole open.
            "deleted-screen-span",
            // keep-slice-v1.v1.json (trim 2..50 s, cuts at 10..15 s and 30..33 s) through the v1
            // migration, overlays added after: every keep slice shares one group on every row.
            "keep-slice-v1",
            // A 2x speed row over 10..20 s and a 6 s voice take from 12 s, exempt from the warp.
            "exempt-voice-clip",
        };

        public const string CaptureFileName = "input-capture.jsonl";

        /// <summary>The v1 <c>videoedit.json</c> that <c>keep-slice-v1</c> was migrated from.</summary>
        public const string V1InputFileName = "keep-slice-v1.v1.json";

        public static string Directory { get; } =
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-v2");

        public static string ProjectPath(string name) => Path.Combine(Directory, name + ".json");

        public static string GoldenPath(string name) => Path.Combine(Directory, name + ".golden.json");

        /// <summary>A private, normalized copy of the fixture, with its recording's capture sidecar
        /// pointed at the shared file wherever the tests happen to be running.</summary>
        public static Project Load(string name)
        {
            var project = Project.FromJson(File.ReadAllText(ProjectPath(name)));
            project.Normalize();
            foreach (var source in project.Sources)
            {
                if (!String.IsNullOrEmpty(source.InputCapturePath))
                    source.InputCapturePath = Path.Combine(Directory, CaptureFileName);
            }
            return project;
        }

        public static IReadOnlyList<OverlaySample> ReadGolden(string name) =>
            JsonSerializer.Deserialize<List<OverlaySample>>(File.ReadAllText(GoldenPath(name)));
    }

    /// <summary>
    /// What one overlay row resolves to at one instant. <see cref="Host"/> is the screen clip's
    /// track; <see cref="Src"/> and <see cref="Dest"/> are the mapping a cursor draws through
    /// (left, top, right, bottom, in capture pixels and canvas pixels). A keyboard has no mapping.
    /// Host-less rows differ by kind: a cursor has no <see cref="SourceTicks"/> because it draws
    /// nothing, while a keyboard still has one.
    /// </summary>
    internal sealed class OverlaySample
    {
        public Guid Track { get; set; }

        public string Kind { get; set; }

        public long T { get; set; }

        /// <summary>Whether a clip on the row covers <see cref="T"/>. Nothing else is set without one.</summary>
        public bool Active { get; set; }

        public Guid? Host { get; set; }

        public long? SourceTicks { get; set; }

        public double? Speed { get; set; }

        public float[] Src { get; set; }

        public float[] Dest { get; set; }
    }

    /// <summary>
    /// The overlay resolver under test: the same steps <c>FrameComposer.DrawCursorItem</c> and
    /// <c>DrawKeyboard</c> take to find the host, the source time and the picture mapping, with the
    /// answers returned instead of drawn. It runs on the project migration left, so the goldens the
    /// old host-derived code recorded pin both the migration and the clips' own clocks.
    /// <c>LegacyFixtureGoldenTests</c> holds these answers to the real composer's pixels, so this
    /// cannot quietly become a second opinion. A keyboard no longer has a host; its
    /// <see cref="OverlaySample.Host"/> is the clip the same rule would pick, which says the clock
    /// it runs on still lines up with the picture.
    /// </summary>
    internal static class OverlayGoldenResolver
    {
        public const string CursorKind = "cursor";
        public const string KeyboardKind = "keyboard";

        /// <summary>The rows holding overlays, backmost first.</summary>
        public static IReadOnlyList<Track> OverlayRows(Project project) =>
            project.Tracks
                .Where(t => project.Items.Any(i => i.TrackId == t.Id && i.Content is CursorContent or KeyboardContent))
                .ToList();

        /// <summary>The instants worth pinning: each whole second past the end of the project, both
        /// sides of every clip edge on a video row, and a tick before each of them.</summary>
        public static IReadOnlyList<long> SampleTimes(Project project)
        {
            var video = project.Tracks.Where(t => t.Kind == TrackKind.Video).Select(t => t.Id).ToHashSet();
            var clips = project.Items.Where(i => video.Contains(i.TrackId)).ToList();

            var times = new SortedSet<long>();
            var end = clips.Max(i => i.TimelineEndTicks);
            for (long t = 0; t <= end + TimeSpan.TicksPerSecond; t += TimeSpan.TicksPerSecond)
                times.Add(t);

            foreach (var clip in clips)
            {
                times.Add(clip.TimelineStartTicks);
                times.Add(clip.TimelineEndTicks);
                if (clip.TimelineStartTicks > 0)
                    times.Add(clip.TimelineStartTicks - 1);
                times.Add(clip.TimelineEndTicks - 1);
            }

            return times.ToList();
        }

        public static OverlaySample Resolve(Project project, Track row, long t, int canvasWidth, int canvasHeight)
        {
            var kind = project.Items.Any(i => i.TrackId == row.Id && i.Content is CursorContent)
                ? CursorKind
                : KeyboardKind;
            var sample = new OverlaySample { Track = row.Id, Kind = kind, T = t };

            var item = project.Items.FirstOrDefault(i =>
                i.TrackId == row.Id && t >= i.TimelineStartTicks && t < i.TimelineEndTicks);
            if (item == null)
                return sample;

            sample.Active = true;
            return item.Content switch
            {
                CursorContent cursor => ResolveCursor(project, item, cursor, t, canvasWidth, canvasHeight, sample),
                KeyboardContent keyboard => ResolveKeyboard(project, item, keyboard, t, sample),
                _ => throw new InvalidOperationException($"Row {row.Id} mixes an overlay with other content."),
            };
        }

        private static OverlaySample ResolveCursor(Project project, Item item, CursorContent cursor, long t,
            int canvasWidth, int canvasHeight, OverlaySample sample)
        {
            var source = FrameComposer.FindSource(project, cursor.SourceId);
            var screen = FrameComposer.ResolveCursorHost(project, source, item, t);
            if (screen?.Content is not MediaContent media)
                return sample;

            sample.Host = screen.TrackId;
            sample.SourceTicks = FrameComposer.SourceTimeTicks(cursor, item, t);
            sample.Speed = TimelineOps.SpeedOf(cursor);

            var capture = InputCapture.Get(source.InputCapturePath);
            var (imgW, imgH) = FrameComposer.CaptureDims(capture.Header, source, media.StreamIndex);
            var transform = WindowCropMath.Effective(project, media, screen.Transform ?? new Transform(),
                FrameComposer.SourceTimeTicks(media, screen, t));
            if (PictureMapping.TryMap(transform, TransitionMath.Evaluate(screen, t), imgW, imgH,
                    canvasWidth, canvasHeight, out var map))
            {
                sample.Src = ToArray(map.Source);
                sample.Dest = ToArray(map.Dest);
            }
            return sample;
        }

        private static OverlaySample ResolveKeyboard(Project project, Item item, KeyboardContent keyboard, long t,
            OverlaySample sample)
        {
            var source = FrameComposer.FindSource(project, keyboard.SourceId);
            sample.Host = FrameComposer.ResolveCursorHost(project, source, item, t)?.TrackId;
            sample.SourceTicks = FrameComposer.SourceTimeTicks(keyboard, item, t);
            sample.Speed = TimelineOps.SpeedOf(keyboard);
            return sample;
        }

        private static float[] ToArray(SKRect rect) => new[] { rect.Left, rect.Top, rect.Right, rect.Bottom };
    }
}
