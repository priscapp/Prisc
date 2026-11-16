using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Prisc.Config;
using Prisc.UI;
using Prisc.UI.Services;
using Prisc.UI.VideoEditor;
using Prisc.UI.VideoEditor.Timeline;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>The video editor's hidden harness flags, and (debug builds only) the state file the
    /// E2E driver aims its clicks with.</summary>
    public class EditorHarnessTests
    {
        // ---------------------------------------------------------------------------------- args

        [Theory]
        [InlineData("--video-edit", "file.mp4", "file.mp4")]
        [InlineData("--VIDEO-EDIT", "file.mp4", "file.mp4")]
        [InlineData("--video-edit-session", "file.mp4", null)]
        [InlineData("--video-edit-extra", "file.mp4", null)]
        public void ValueOf_matches_the_whole_flag(string flag, string value, string expected) =>
            Assert.Equal(expected, EditorHarnessArgs.ValueOf(new[] { flag, value }, "--video-edit"));

        [Fact]
        public void ValueOf_needs_a_value_after_the_flag()
        {
            Assert.Null(EditorHarnessArgs.ValueOf(new[] { "--video-edit" }, "--video-edit"));
            Assert.Null(EditorHarnessArgs.ValueOf(Array.Empty<string>(), "--video-edit"));
            Assert.Null(EditorHarnessArgs.ValueOf(null, "--video-edit"));
        }

        [Fact]
        public void ValueOf_finds_the_flag_among_others() =>
            Assert.Equal("out.json", EditorHarnessArgs.ValueOf(
                new[] { "--video-edit-session", "session.json", "--e2e-state", "out.json" }, "--e2e-state"));

        [Theory]
        [InlineData(VideoEditorWindow.ArgName)]
        [InlineData(VideoEditorWindow.SessionArgName)]
        public void Both_editor_harnesses_count_as_harness_launches(string flag)
        {
            // settings load ahead of the mutex only on this answer; a false one kills the harness's
            // window constructor on a null SettingsRoot.Current
            Assert.True(App.IsHarnessLaunch(new[] { flag, "x" }));
        }

        [Fact]
        public void An_ordinary_launch_is_not_a_harness_launch()
        {
            Assert.False(App.IsHarnessLaunch(new[] { "--autostarted" }));
            Assert.False(App.IsHarnessLaunch(null));
        }

        // ------------------------------------------------------------------------ opening a session

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"prisc-harness-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteSession(string dir, string json)
        {
            var path = Path.Combine(dir, "session.json");
            File.WriteAllText(path, json);
            return path;
        }

        private static string JsonString(string value) => JsonSerializer.Serialize(value);

        [Fact]
        public void A_session_file_that_is_not_there_is_refused_and_not_created()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "sesion.json");

                Assert.Null(EditorHarnessArgs.OpenSession(path, out var problem));

                Assert.Contains(path, problem);
                Assert.False(File.Exists(path));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_session_flag_with_no_path_is_refused(string path)
        {
            Assert.Null(EditorHarnessArgs.OpenSession(path, out var problem));
            Assert.False(String.IsNullOrEmpty(problem));
        }

        /// <summary>A harness that fails to load must exit, not sit on a status line a driver cannot
        /// see. The window cannot be built here, so this reads its source: every message saying the
        /// open failed has to go through ShowOpenFailure. A guard on the routing only; whether the
        /// process really exits 1 is for the E2E run.</summary>
        [Fact]
        public void Every_way_the_window_fails_to_open_goes_through_ShowOpenFailure()
        {
            var ui = new DirectoryInfo(AppContext.BaseDirectory);
            while (!Directory.Exists(Path.Combine(ui.FullName, "Prisc.Ui")))
                ui = ui.Parent;
            var source = File.ReadAllText(Path.Combine(ui.FullName, "Prisc.Ui", "VideoEditor", "VideoEditorWindow.axaml.cs"));

            const string messages = "\"(Could not open|This file has no|Video playback unavailable)";
            Assert.Empty(Regex.Matches(source, @"ShowStatus\(\s*" + messages));
            Assert.NotEmpty(Regex.Matches(source, @"ShowOpenFailure\(\s*" + messages)); // still the words used
        }

        [Theory]
        [InlineData("--video-edit-session")]
        [InlineData("--VIDEO-EDIT-SESSION")]
        public void A_flag_counts_with_or_without_a_value(string flag)
        {
            Assert.True(EditorHarnessArgs.Has(new[] { flag }, "--video-edit-session"));
            Assert.True(EditorHarnessArgs.Has(new[] { "--x", flag, "y" }, "--video-edit-session"));
            Assert.Null(EditorHarnessArgs.ValueOf(new[] { flag }, "--video-edit-session"));
        }

        [Fact]
        public void Another_flag_is_not_this_one()
        {
            Assert.False(EditorHarnessArgs.Has(new[] { "--video-edit-session" }, "--video-edit"));
            Assert.False(EditorHarnessArgs.Has(new[] { "--video-edit" }, "--video-edit-session"));
            Assert.False(EditorHarnessArgs.Has(null, "--video-edit"));
        }

        [Theory]
        [InlineData("{ not json")]
        [InlineData("[]")]
        [InlineData("""{"LastModifiedUtc":"not a date"}""")]
        public void A_session_file_that_will_not_parse_is_refused_rather_than_thrown(string json)
        {
            var dir = TempDir();
            try
            {
                var path = WriteSession(dir, json);

                Assert.Null(EditorHarnessArgs.OpenSession(path, out var problem));
                Assert.Contains(path, problem);

                // refused cleanly: the file can be fixed and opened in this same process
                File.WriteAllText(path, "{\"IsVideoProject\":true}");
                using var session = EditorHarnessArgs.OpenSession(path, out problem);
                Assert.NotNull(session);
                Assert.Null(problem);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Theory]
        [InlineData("""{"VideoFile":"recording.mp4"}""")] // a misnamed field: VideoPath stays empty
        [InlineData("""{"VideoPath":"C:\\nowhere\\recording.mp4"}""")]
        [InlineData("""{"IsVideoProject":false}""")]
        public void A_session_with_no_recording_is_refused_rather_than_opened_blank(string json)
        {
            var dir = TempDir();
            try
            {
                Assert.Null(EditorHarnessArgs.OpenSession(WriteSession(dir, json), out var problem));
                Assert.False(String.IsNullOrEmpty(problem));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void A_session_opens_on_its_recording()
        {
            var dir = TempDir();
            try
            {
                var video = Path.Combine(dir, "recording.mp4");
                File.WriteAllText(video, "");

                using var session = EditorHarnessArgs.OpenSession(
                    WriteSession(dir, $$"""{"VideoPath":{{JsonString(video)}}}"""), out var problem);

                Assert.Null(problem);
                Assert.Equal(video, session.VideoPath);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void A_project_session_opens_with_no_recording()
        {
            var dir = TempDir();
            try
            {
                using var session = EditorHarnessArgs.OpenSession(
                    WriteSession(dir, """{"IsVideoProject":true}"""), out var problem);

                Assert.Null(problem);
                Assert.True(session.IsVideoProject);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ---------------------------------------------------------------------- where a render goes

        [Fact]
        public void A_harness_render_of_a_project_lands_in_its_session_directory()
        {
            var dir = TempDir();
            try
            {
                // what --video-edit-session has on a project session: no video path to sit beside
                var output = VideoRenderManager.GetOutputPathBeside(null, Path.Combine(dir, "session.json"), VideoContainer.Mp4);
                Assert.Equal(Path.Combine(dir, "project-edited.mp4"), output);

                File.WriteAllText(output, "");
                Assert.Equal(Path.Combine(dir, "project-edited-2.mp4"),
                    VideoRenderManager.GetOutputPathBeside(null, Path.Combine(dir, "session.json"), VideoContainer.Mp4));
                Assert.Equal(Path.Combine(dir, "project-edited.mkv"),
                    VideoRenderManager.GetOutputPathBeside(null, Path.Combine(dir, "session.json"), VideoContainer.Mkv));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Theory]
        [InlineData(null)] // --video-edit has no session at all
        [InlineData(@"C:\sessions\one\session.json")]
        public void A_harness_render_of_a_recording_lands_beside_it(string sessionFile)
        {
            var video = Path.Combine(Path.GetTempPath(), $"prisc-{Guid.NewGuid():N}.mp4");

            Assert.Equal(Path.ChangeExtension(video, null) + "-edited.mp4",
                VideoRenderManager.GetOutputPathBeside(video, sessionFile, VideoContainer.Mp4));
        }

        // ------------------------------------------------------------------------- a clip's rect

        private const long Sec = TimeSpan.TicksPerSecond;

        private static readonly Guid ScreenRow = Guid.NewGuid();
        private static readonly Guid AudioRow = Guid.NewGuid();
        private static readonly Guid ClipA = Guid.NewGuid();
        private static readonly Guid ClipB = Guid.NewGuid();
        private static readonly Guid ClipC = Guid.NewGuid();

        private static Project TwoRowProject() => new Project
        {
            Output = new OutputSettings { WidthPx = 1280, HeightPx = 720, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            Tracks =
            {
                new Track { Id = ScreenRow, Kind = TrackKind.Video, Name = "Screen", Order = 0 },
                new Track { Id = AudioRow, Kind = TrackKind.Audio, Name = "Audio", Order = 1, Muted = true },
            },
            Items =
            {
                new Item { Id = ClipA, TrackId = ScreenRow, TimelineStartTicks = 0, DurationTicks = 10 * Sec, Content = new MediaContent() },
                new Item { Id = ClipB, TrackId = ScreenRow, TimelineStartTicks = 12 * Sec, DurationTicks = 4 * Sec, Content = new TextContent() },
                new Item { Id = ClipC, TrackId = AudioRow, TimelineStartTicks = 2 * Sec, DurationTicks = 8 * Sec, Content = new MediaContent() },
            },
        };

        [Fact]
        public void A_clips_rect_is_its_time_span_through_the_viewport_across_its_row()
        {
            var project = TwoRowProject();
            var row = TimelineRowLayout.Build(project).Single(r => r.TrackId == ScreenRow);
            var clip = project.Items.Single(i => i.Id == ClipB);

            // 12 s in with 2 s scrolled away, for 4 s, at 10 px/s
            Assert.Equal(new TimelineItemRect(ClipB, 3, 100, 40, row.Top, row.Height),
                TimelineHitTester.ItemRect(clip, 3, row, 2 * Sec, Sec / 10.0));

            // a sliver stays a pixel wide, so it can still be drawn and found
            clip.DurationTicks = 1;
            Assert.Equal(1, TimelineHitTester.ItemRect(clip, 3, row, 2 * Sec, Sec / 10.0).Width);
        }

#if DEBUG
        // ---------------------------------------------------------------------------- state body

        /// <summary>10 px per second at 2 s of scroll, on a surface whose origin sits at (500, 300)
        /// on a 150% display with 40 px of vertical scroll.</summary>
        private static EditorDumpView View(long playhead = 0) => new EditorDumpView(
            TicksPerPixel: Sec / 10.0,
            ScrollTicks: 2 * Sec,
            PlayheadTicks: playhead,
            Scaling: 1.5,
            Window: new DumpRect(100, 50, 1600, 900),
            Ruler: new DumpRect(500, 260, 1000, 40),
            RowHeaders: new DumpRect(305, 300, 195, 400),
            RowsViewport: new DumpRect(305, 300, 1195, 200),
            Surface: new DumpRect(500, 240, 1000, 400),
            SurfaceToScreen: (x, y) => new PixelPoint(500 + (int)Math.Round(x * 1.5), 300 + (int)Math.Round((y - 40) * 1.5)));

        private static JsonElement BuildState(IReadOnlyList<Guid> selection = null) =>
            JsonDocument.Parse(EditorStateFormat.Build(TwoRowProject(), selection ?? Array.Empty<Guid>(), View(7 * Sec))).RootElement;

        private static JsonElement Clip(JsonElement state, Guid id) =>
            state.GetProperty("rows").EnumerateArray()
                 .SelectMany(r => r.GetProperty("clips").EnumerateArray())
                 .Single(c => c.GetProperty("id").GetGuid() == id);

        private static (int X, int Y, int W, int H) Rect(JsonElement rect) => (
            rect.GetProperty("x").GetInt32(), rect.GetProperty("y").GetInt32(),
            rect.GetProperty("w").GetInt32(), rect.GetProperty("h").GetInt32());

        [Fact]
        public void State_rows_come_in_the_timelines_order_with_their_screen_rects()
        {
            var state = BuildState();
            var layout = TimelineRowLayout.Build(TwoRowProject());
            var rows = state.GetProperty("rows").EnumerateArray().ToList();

            Assert.Equal(layout.Select(r => r.TrackId), rows.Select(r => r.GetProperty("trackId").GetGuid()));

            for (var i = 0; i < rows.Count; i++)
            {
                var top = 300 + (int)Math.Round((layout[i].Top - 40) * 1.5);
                var bottom = 300 + (int)Math.Round((layout[i].Bottom - 40) * 1.5);
                Assert.Equal((305, top, 195, bottom - top), Rect(rows[i].GetProperty("header")));
                Assert.Equal((500, top, 1000, bottom - top), Rect(rows[i].GetProperty("lane")));
            }

            var audio = rows.Single(r => r.GetProperty("trackId").GetGuid() == AudioRow);
            Assert.Equal("Audio", audio.GetProperty("name").GetString());
            Assert.Equal(nameof(TimelineRowKind.Audio), audio.GetProperty("kind").GetString());
            Assert.True(audio.GetProperty("muted").GetBoolean());
        }

        [Fact]
        public void State_clip_rects_land_where_the_timeline_draws_them()
        {
            var state = BuildState();
            var row = TimelineRowLayout.Build(TwoRowProject()).Single(r => r.TrackId == ScreenRow);
            var top = 300 + (int)Math.Round((row.Top - 40) * 1.5);
            var height = 300 + (int)Math.Round((row.Bottom - 40) * 1.5) - top;

            // starts 12 s in, 2 s scrolled away: 100 px of surface at 10 px/s, 150 px at 150%
            Assert.Equal((650, top, 60, height), Rect(Clip(state, ClipB).GetProperty("rect")));

            // a clip scrolled partly off stays listed, its rect running off the surface's left
            Assert.Equal((470, top, 150, height), Rect(Clip(state, ClipA).GetProperty("rect")));

            // and the middle of each one, carried back to the surface, is what the surface's own hit
            // test answers with, over the rects its layout pass makes
            var project = TwoRowProject();
            var rects = TimelineRowLayout.Build(project).SelectMany((r, i) =>
                    project.Items.Where(it => it.TrackId == r.TrackId)
                           .Select(it => TimelineHitTester.ItemRect(it, i, r, 2 * Sec, Sec / 10.0)))
                .ToList();
            foreach (var id in new[] { ClipA, ClipB, ClipC })
            {
                var (x, y, w, h) = Rect(Clip(state, id).GetProperty("rect"));
                var hit = TimelineHitTester.HitTest((x + w / 2.0 - 500) / 1.5, (y + h / 2.0 - 300) / 1.5 + 40, 0, rects);
                Assert.Equal(new TimelineHit(TimelineHitKind.ItemBody, id, rects.Single(r => r.ItemId == id).RowIndex), hit);
            }
        }

        [Fact]
        public void State_says_how_to_turn_a_time_into_a_position()
        {
            var state = BuildState();

            Assert.Equal(Sec / 10.0, state.GetProperty("ticksPerPixel").GetDouble());
            Assert.Equal(2 * Sec, state.GetProperty("scrollTicks").GetInt64());
            Assert.Equal(1.5, state.GetProperty("scaling").GetDouble());
            Assert.Equal(7 * Sec, state.GetProperty("playheadTicks").GetInt64());
            Assert.Equal(16 * Sec, state.GetProperty("durationTicks").GetInt64());
            Assert.Equal((500, 260, 1000, 40), Rect(state.GetProperty("ruler")));
            Assert.Equal((100, 50, 1600, 900), Rect(state.GetProperty("window")));
        }

        [Fact]
        public void State_reports_the_selection_primary_first()
        {
            var state = BuildState(new[] { ClipC, ClipA });

            Assert.Equal(new[] { ClipC, ClipA },
                state.GetProperty("selection").EnumerateArray().Select(e => e.GetGuid()));
            Assert.True(Clip(state, ClipA).GetProperty("selected").GetBoolean());
            Assert.False(Clip(state, ClipB).GetProperty("selected").GetBoolean());
            Assert.True(Clip(state, ClipC).GetProperty("selected").GetBoolean());
        }

        // ---------------------------------------------------------------------------- state file

        private static string TempPath() => Path.Combine(Path.GetTempPath(), $"prisc-state-{Guid.NewGuid():N}.json");

        private static void DeleteQuietly(string path)
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }

        private static (long Seq, string Marker) ReadFile(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return (doc.RootElement.GetProperty("seq").GetInt64(),
                doc.RootElement.GetProperty("state").GetProperty("marker").GetString());
        }

        [Fact]
        public void Publish_wraps_the_state_with_a_rising_sequence_and_leaves_no_temp_file()
        {
            var path = TempPath();
            try
            {
                var file = new EditorStateFile(path);

                Assert.True(file.Publish("""{"marker":"a"}"""));
                Assert.Equal((1L, "a"), ReadFile(path));

                Assert.True(file.Publish("""{"marker":"b"}"""));
                Assert.Equal((2L, "b"), ReadFile(path));

                Assert.False(File.Exists(path + ".tmp"));
            }
            finally
            {
                DeleteQuietly(path);
            }
        }

        [Fact]
        public void Publishing_the_same_state_does_not_raise_the_sequence()
        {
            var path = TempPath();
            try
            {
                var file = new EditorStateFile(path);

                file.Publish("""{"marker":"a"}""");
                var written = File.GetLastWriteTimeUtc(path);
                File.SetLastWriteTimeUtc(path, written.AddHours(-1));

                Assert.True(file.Publish("""{"marker":"a"}"""));

                Assert.Equal(1, file.Sequence);
                Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(path));
            }
            finally
            {
                DeleteQuietly(path);
            }
        }

        [Fact]
        public void An_edit_the_state_does_not_show_still_raises_the_sequence()
        {
            var path = TempPath();
            try
            {
                var file = new EditorStateFile(path);
                file.Publish("""{"marker":"a"}""");

                // an opacity change, say: a driver waiting on its gesture must see it land
                file.MarkEdited();
                Assert.True(file.Publish("""{"marker":"a"}"""));
                Assert.Equal((2L, "a"), ReadFile(path));

                // and the layout pass that follows it, with nothing new to say, does not
                Assert.True(file.Publish("""{"marker":"a"}"""));
                Assert.Equal(2, file.Sequence);
            }
            finally
            {
                DeleteQuietly(path);
            }
        }

        [Fact]
        public void An_edit_outlasts_a_refused_write()
        {
            if (!OperatingSystem.IsWindows())
                return; // the refusal is a Windows sharing violation

            var path = TempPath();
            try
            {
                var file = new EditorStateFile(path);
                file.Publish("""{"marker":"a"}""");
                file.MarkEdited();

                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert.False(file.Publish("""{"marker":"a"}"""));

                Assert.True(file.Publish("""{"marker":"a"}"""));
                Assert.Equal((2L, "a"), ReadFile(path));
            }
            finally
            {
                DeleteQuietly(path);
            }
        }

        [Fact]
        public void The_state_files_directory_is_created_for_it()
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "not", "yet", "state.json");

                Assert.True(new EditorStateFile(path).Publish("""{"marker":"a"}"""));

                Assert.Equal((1L, "a"), ReadFile(path));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void A_failure_no_reader_caused_is_thrown_rather_than_retried()
        {
            var path = TempPath();
            try
            {
                // the temp file cannot be written, so no amount of asking again would help
                Directory.CreateDirectory(path + ".tmp");

                Assert.ThrowsAny<SystemException>(() => new EditorStateFile(path).Publish("""{"marker":"a"}"""));
                Assert.False(File.Exists(path));
            }
            finally
            {
                Directory.Delete(path + ".tmp");
            }
        }

        [Fact]
        public void A_refused_write_keeps_the_sequence_and_the_state_can_be_offered_again()
        {
            if (!OperatingSystem.IsWindows())
                return; // the refusal is a Windows sharing violation

            var path = TempPath();
            try
            {
                var file = new EditorStateFile(path);
                file.Publish("""{"marker":"a"}""");

                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.False(file.Publish("""{"marker":"b"}"""));
                    Assert.Equal(1, file.Sequence);
                }

                Assert.True(file.Publish("""{"marker":"b"}"""));
                Assert.Equal((2L, "b"), ReadFile(path));
            }
            finally
            {
                DeleteQuietly(path);
            }
        }
#endif
    }
}
