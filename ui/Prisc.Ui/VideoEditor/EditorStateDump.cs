#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Prisc.UI.VideoEditor.Timeline;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;

namespace Prisc.UI.VideoEditor
{
    /// <summary>A screen rectangle, in the physical pixels a synthesized click is aimed with.</summary>
    internal readonly record struct DumpRect(int X, int Y, int Width, int Height);

    /// <summary>
    /// Everything <see cref="EditorStateFormat.Build"/> reads off the live window. The timeline's
    /// rows and clips are laid out in its drawing surface's coordinates, so
    /// <see cref="SurfaceToScreen"/> carries them to the screen, which covers the surface's vertical
    /// scroll and the display scaling.
    /// </summary>
    internal sealed record EditorDumpView(
        double TicksPerPixel,
        long ScrollTicks,
        long PlayheadTicks,
        double Scaling,
        DumpRect Window,
        DumpRect Ruler,
        DumpRect RowHeaders,
        DumpRect RowsViewport,
        DumpRect Surface,
        Func<double, double, PixelPoint> SurfaceToScreen);

    /// <summary>The state document's body. Pure, so the test suite can hold it to a known layout.</summary>
    internal static class EditorStateFormat
    {
        /// <summary>The state as JSON: where everything is on screen, and what is selected. Clips
        /// scrolled out of view are still listed, outside <see cref="EditorDumpView.RowsViewport"/>
        /// or the surface's width. Laid out by what the surface itself draws and hit-tests with:
        /// <see cref="TimelineRowLayout.Build"/> for rows, <see cref="TimelineHitTester.ItemRect"/> for
        /// clips.</summary>
        public static string Build(Project project, IReadOnlyList<Guid> selection, EditorDumpView view)
        {
            var selected = new HashSet<Guid>(selection);
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                WriteRect(json, "window", view.Window);
                json.WriteNumber("scaling", view.Scaling);
                json.WriteNumber("ticksPerPixel", view.TicksPerPixel);
                json.WriteNumber("scrollTicks", view.ScrollTicks);
                json.WriteNumber("playheadTicks", view.PlayheadTicks);
                json.WriteNumber("durationTicks", project.GetDurationTicks());
                WriteRect(json, "ruler", view.Ruler);
                WriteRect(json, "rowsViewport", view.RowsViewport);
                WriteRect(json, "surface", view.Surface);

                json.WriteStartArray("selection");
                foreach (var id in selection)
                    json.WriteStringValue(id);
                json.WriteEndArray();

                json.WriteStartArray("rows");
                var rows = TimelineRowLayout.Build(project);
                for (var i = 0; i < rows.Count; i++)
                    WriteRow(json, project, rows[i], i, selected, view);
                json.WriteEndArray();

                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteRow(Utf8JsonWriter json, Project project, TimelineRow row, int rowIndex,
            HashSet<Guid> selected, EditorDumpView view)
        {
            var track = project.Tracks.First(t => t.Id == row.TrackId);
            var top = view.SurfaceToScreen(0, row.Top).Y;
            var height = view.SurfaceToScreen(0, row.Bottom).Y - top;

            json.WriteStartObject();
            json.WriteString("trackId", track.Id);
            json.WriteString("name", track.Name);
            json.WriteString("kind", row.Kind.ToString());
            json.WriteNumber("order", track.Order);
            json.WriteBoolean("hidden", track.Hidden);
            json.WriteBoolean("muted", track.Muted);
            WriteRect(json, "header", new DumpRect(view.RowHeaders.X, top, view.RowHeaders.Width, height));
            WriteRect(json, "lane", new DumpRect(view.Surface.X, top, view.Surface.Width, height));

            json.WriteStartArray("clips");
            foreach (var item in project.Items.Where(i => i.TrackId == row.TrackId))
            {
                var rect = TimelineHitTester.ItemRect(item, rowIndex, row, view.ScrollTicks, view.TicksPerPixel);
                var topLeft = view.SurfaceToScreen(rect.X, rect.Top);
                var bottomRight = view.SurfaceToScreen(rect.Right, rect.Bottom);

                json.WriteStartObject();
                json.WriteString("id", item.Id);
                json.WriteString("content", item.Content?.GetType().Name);
                json.WriteNumber("startTicks", item.TimelineStartTicks);
                json.WriteNumber("durationTicks", item.DurationTicks);
                json.WriteBoolean("selected", selected.Contains(item.Id));
                WriteRect(json, "rect", new DumpRect(topLeft.X, topLeft.Y,
                    bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y));
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteEndObject();
        }

        private static void WriteRect(Utf8JsonWriter json, string name, DumpRect rect)
        {
            json.WriteStartObject(name);
            json.WriteNumber("x", rect.X);
            json.WriteNumber("y", rect.Y);
            json.WriteNumber("w", rect.Width);
            json.WriteNumber("h", rect.Height);
            json.WriteEndObject();
        }
    }

    /// <summary>
    /// The file an E2E driver polls: <c>{"seq":N,"state":{...}}</c>, replaced atomically so it is
    /// never seen half written. <c>seq</c> rises for every project or selection change announced
    /// through <see cref="MarkEdited"/>, whether or not the state shows it (an opacity change does
    /// not), and for any other change the state does show, such as a scroll. So a rise is a signal to
    /// read again and not evidence that a gesture succeeded: something else may have raised it, and a
    /// refused gesture can still move the selection. What the edit did is for <c>videoedit.json</c>
    /// to show, which is what assertions read.
    /// </summary>
    internal sealed class EditorStateFile
    {
        private readonly string _path;
        private string _lastState;
        private bool _editPending;

        /// <summary>Creates the directory the file goes in, so a driver can name a fresh one.</summary>
        public EditorStateFile(string path)
        {
            _path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
        }

        public long Sequence { get; private set; }

        /// <summary>The project or the selection changed: the next successful
        /// <see cref="Publish"/> raises the sequence even if its state reads the same.</summary>
        public void MarkEdited() => _editPending = true;

        /// <summary>Publishes <paramref name="state"/>, doing nothing when it is what the file
        /// already says and no edit is pending. False when a reader holding the file without delete
        /// sharing stopped the replace, with the sequence unmoved and the edit still pending so the
        /// state can be offered again. Only the replace can meet a reader, as nothing reads the temp
        /// file, so anything else that goes wrong will not clear up by trying again and is thrown.</summary>
        public bool Publish(string state)
        {
            if (state == _lastState && !_editPending)
                return true;

            var sequence = Sequence + 1;
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, $"{{\"seq\":{sequence},\"state\":{state}}}\n");
            try
            {
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            Sequence = sequence;
            _lastState = state;
            _editPending = false;
            return true;
        }
    }

    /// <summary>
    /// Debug-only E2E aid, enabled by <c>--e2e-state &lt;path&gt;</c>: keeps a file saying where this
    /// editor's clips and rows are on screen, rewritten when the project, the selection, the layout
    /// or the playhead changes. It exists to say where to click. It is not evidence: assertions read
    /// <c>videoedit.json</c> and screenshots, so a test cannot pass on something only this file says.
    /// </summary>
    internal sealed class EditorStateDump : IDisposable
    {
        public const string ArgName = "--e2e-state";

        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

        private readonly EditorStateFile _file;
        private readonly Window _window;
        private readonly TimelineControl _timeline;
        private readonly TimelineControl.DumpSources _sources;
        private readonly EditorSession _session;
        private bool _pending;
        private bool _disposed;

        public EditorStateDump(string path, Window window, TimelineControl timeline, EditorSession session)
        {
            _file = new EditorStateFile(path);
            _window = window;
            _timeline = timeline;
            _sources = timeline.GetDumpSources();
            _session = session;

            _session.ProjectChanged += OnEdited;
            _session.SelectionChanged += OnEdited;
            _sources.Viewport.Changed += OnChanged;
            _timeline.LayoutUpdated += OnChanged;
            _timeline.PropertyChanged += OnTimelinePropertyChanged;
            _window.PositionChanged += OnWindowMoved;

            Schedule();
        }

        public void Dispose()
        {
            _disposed = true;
            _session.ProjectChanged -= OnEdited;
            _session.SelectionChanged -= OnEdited;
            _sources.Viewport.Changed -= OnChanged;
            _timeline.LayoutUpdated -= OnChanged;
            _timeline.PropertyChanged -= OnTimelinePropertyChanged;
            _window.PositionChanged -= OnWindowMoved;
        }

        private void OnEdited(object sender, EventArgs e)
        {
            _file.MarkEdited();
            Schedule();
        }

        private void OnChanged(object sender, EventArgs e) => Schedule();

        private void OnWindowMoved(object sender, PixelPointEventArgs e) => Schedule();

        private void OnTimelinePropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TimelineControl.PositionProperty)
                Schedule();
        }

        /// <summary>Runs once the current burst of events and the layout pass they cause are over,
        /// so the file only ever describes a settled window.</summary>
        private void Schedule()
        {
            if (_pending || _disposed)
                return;

            _pending = true;
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }

        private void Flush()
        {
            _pending = false;
            if (_disposed)
                return;

            var surface = _sources.Surface;
            var view = new EditorDumpView(
                _sources.Viewport.TicksPerPixel,
                _sources.Viewport.ScrollTicks,
                _timeline.Position.Ticks,
                _window.RenderScaling,
                ScreenRect(_window),
                ScreenRect(_sources.Ruler),
                ScreenRect(_sources.RowHeaders),
                ScreenRect(_sources.RowsViewport),
                ScreenRect(surface),
                (x, y) => surface.PointToScreen(new Point(x, y)));

            var state = EditorStateFormat.Build(_session.Project, _session.SelectedItemIds, view);

            // nothing else may come along to retry a refused write, and a driver waits on this file
            if (!_file.Publish(state))
                DispatcherTimer.RunOnce(Schedule, RetryDelay);
        }

        private static DumpRect ScreenRect(Visual visual)
        {
            var topLeft = visual.PointToScreen(default);
            var bottomRight = visual.PointToScreen(new Point(visual.Bounds.Width, visual.Bounds.Height));
            return new DumpRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
        }
    }
}
#endif
