using System;
using System.Linq;
using System.Text.Json;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Playback;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Cursor and keys clips on a clock of their own (<see cref="SourceClockContent"/>): the clock
    /// operations on them, and how grouped overlays stay in step when a screen clip's mapping changes
    /// (<see cref="TimelineOps.RebaseOverlays"/>).
    /// </summary>
    public class OverlayClockTests
    {
        private static long Sec(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

        /// <summary>A recording's rows: a screen stream of 100 s, a shorter webcam, and cursor and
        /// keys rows above them. Grouped unless asked not to be. Nothing on the rows yet.</summary>
        private static Project Recording(out Track screen, out Track cursorRow, out Track keysRow, bool grouped = true)
        {
            var group = grouped ? Guid.NewGuid() : (Guid?)null;
            screen = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0, GroupId = group };
            keysRow = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Keys", Order = 1, GroupId = group };
            cursorRow = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Cursor", Order = 2, GroupId = group };
            return new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = Guid.NewGuid(),
                        Path = @"C:\rec\in.mp4",
                        InputCapturePath = TestPath.ExistingCapture,
                        Streams =
                        {
                            new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, DurationTicks = Sec(100) },
                            new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480, DurationTicks = Sec(40) },
                        },
                    },
                },
                Tracks = { screen, keysRow, cursorRow },
            };
        }

        /// <summary>A second recording in the project, with nothing on the rows yet.</summary>
        private static Source OtherRecording(Project project)
        {
            var other = new Source
            {
                Id = Guid.NewGuid(),
                Path = @"C:\rec\other.mp4",
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, DurationTicks = Sec(100) } },
            };
            project.Sources.Add(other);
            return other;
        }

        private static Item Add(Project project, Track row, double start, double duration, SourceClockContent clock)
        {
            clock.SourceId = clock.SourceId == Guid.Empty ? project.Sources[0].Id : clock.SourceId;
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = row.Id,
                TimelineStartTicks = Sec(start),
                DurationTicks = Sec(duration),
                Content = clock,
            };
            project.Items.Add(item);
            return item;
        }

        private static Item Screen(Project project, Track row, double start, double duration, double sourceIn,
            double speed = 1.0, int stream = 0) =>
            Add(project, row, start, duration, new MediaContent { StreamIndex = stream, SourceInTicks = Sec(sourceIn), Speed = speed });

        private static Item Cursor(Project project, Track row, double start, double duration, double sourceIn, double speed = 1.0) =>
            Add(project, row, start, duration, new CursorContent { SourceInTicks = Sec(sourceIn), Speed = speed });

        private static Item Keys(Project project, Track row, double start, double duration, double sourceIn, double speed = 1.0) =>
            Add(project, row, start, duration, new KeyboardContent { SourceInTicks = Sec(sourceIn), Speed = speed });

        private static SourceClockContent ClockOf(Item item) => (SourceClockContent)item.Content;

        /// <summary>The clip on the row covering the instant.</summary>
        private static Item At(Project project, Track row, double t) =>
            project.Items.Single(i => i.TrackId == row.Id && i.Covers(Sec(t)));

        /// <summary>What the row shows at the instant, by its own clock.</summary>
        private static long SourceAt(Project project, Track row, double t)
        {
            var item = At(project, row, t);
            return FrameComposer.SourceTimeTicks(ClockOf(item), item, Sec(t));
        }

        private static int ClipCount(Project project, Track row) => project.Items.Count(i => i.TrackId == row.Id);

        private static EditorSession SessionOf(Project project)
        {
            project.Normalize();
            return new EditorSession(project, null, save => save());
        }

        private static TimelineGap GapAt(Project project, Track row, double t) => TimelineOps.FindGap(project, row.Id, Sec(t)).Value;

        // ------------------------------------------------------------------------------- the wire

        [Fact]
        public void Every_clock_content_round_trips_with_its_discriminator_first()
        {
            var project = Recording(out var screen, out var cursorRow, out var keysRow);
            Add(project, screen, 0, 5, new MediaContent { StreamIndex = 1, SourceInTicks = Sec(1), Speed = 2.0 });
            Add(project, cursorRow, 0, 5, new CursorContent { SourceInTicks = Sec(2), Speed = 0.5, Freeze = true, Style = "native" });
            Add(project, keysRow, 0, 5, new KeyboardContent { SourceInTicks = Sec(3), Speed = 3.0, FontSize = 20 });

            var json = project.ToJson();
            var read = Project.FromJson(json);

            Assert.Equal(json, read.ToJson());

            var media = read.Items.Select(i => i.Content).OfType<MediaContent>().Single();
            Assert.Equal((project.Sources[0].Id, Sec(1), 2.0, false, 1), (media.SourceId, media.SourceInTicks, media.Speed, media.Freeze, media.StreamIndex));
            var cursor = read.Items.Select(i => i.Content).OfType<CursorContent>().Single();
            Assert.Equal((project.Sources[0].Id, Sec(2), 0.5, true, "native"), (cursor.SourceId, cursor.SourceInTicks, cursor.Speed, cursor.Freeze, cursor.Style));
            var keys = read.Items.Select(i => i.Content).OfType<KeyboardContent>().Single();
            Assert.Equal((project.Sources[0].Id, Sec(3), 3.0, false, 20.0), (keys.SourceId, keys.SourceInTicks, keys.Speed, keys.Freeze, keys.FontSize));

            using var doc = JsonDocument.Parse(json);
            var types = doc.RootElement.GetProperty("Items").EnumerateArray()
                .Select(i => i.GetProperty("Content").EnumerateObject().First())
                .Select(p => (p.Name, p.Value.GetString())).OrderBy(p => p.Item2).ToList();
            Assert.Equal(new[] { ("$type", "cursor"), ("$type", "keyboard"), ("$type", "media") }, types);
        }

        [Fact]
        public void An_overlay_saved_without_a_clock_reads_the_default_one()
        {
            var cursor = Assert.IsType<CursorContent>(JsonSerializer.Deserialize(
                """{ "$type": "cursor", "SourceId": "00000000-0000-0000-0000-000000000001" }""",
                ProjectJsonContext.Default.ItemContent));

            Assert.Equal((0L, 1.0, false), (cursor.SourceInTicks, cursor.Speed, cursor.Freeze));
        }

        [Fact]
        public void Overlay_copies_carry_the_clock()
        {
            foreach (SourceClockContent original in new SourceClockContent[]
                     {
                         new CursorContent { SourceId = Guid.NewGuid(), SourceInTicks = 5, Speed = 2.0, Freeze = true },
                         new KeyboardContent { SourceId = Guid.NewGuid(), SourceInTicks = 7, Speed = 0.5, Freeze = true },
                     })
            {
                var copy = (SourceClockContent)original.Clone();

                Assert.NotSame(original, copy);
                Assert.Equal((original.SourceId, original.SourceInTicks, original.Speed, original.Freeze),
                    (copy.SourceId, copy.SourceInTicks, copy.Speed, copy.Freeze));
            }
        }

        [Theory]
        [InlineData(-1L, 1.0)]
        [InlineData(0L, 0.0)]
        [InlineData(0L, -2.0)]
        [InlineData(0L, double.NaN)]
        [InlineData(0L, double.PositiveInfinity)]
        public void Validate_refuses_an_overlay_clock_that_cannot_run(long sourceIn, double speed)
        {
            foreach (var clock in new SourceClockContent[] { new CursorContent(), new KeyboardContent() })
            {
                var project = Recording(out _, out var cursorRow, out _);
                clock.SourceInTicks = sourceIn;
                clock.Speed = speed;
                Add(project, cursorRow, 0, 5, clock);

                Assert.NotEmpty(project.Validate());
            }
        }

        // -------------------------------------------------------------------- the clock operations

        public static TheoryData<bool, double> KindsAndSpeeds()
        {
            var data = new TheoryData<bool, double>();
            foreach (var isCursor in new[] { true, false })
            {
                foreach (var speed in new[] { 1.0, 2.0, 0.5 })
                    data.Add(isCursor, speed);
            }
            return data;
        }

        [Theory]
        [MemberData(nameof(KindsAndSpeeds))]
        public void Trim_and_split_leave_an_overlay_showing_the_same_moment(bool isCursor, double speed)
        {
            var project = Recording(out _, out var cursorRow, out _);
            var clip = isCursor ? Cursor(project, cursorRow, 10, 20, 4, speed) : Keys(project, cursorRow, 10, 20, 4, speed);
            var at18 = SourceAt(project, cursorRow, 18);
            var at22 = SourceAt(project, cursorRow, 22);

            Assert.Equal(Sec(3), TimelineOps.TrimStart(project, clip.Id, Sec(3)));
            Assert.Equal(at18, SourceAt(project, cursorRow, 18));
            Assert.Equal(Sec(4 + 3 * speed), ClockOf(clip).SourceInTicks);

            Assert.Equal(-Sec(1), TimelineOps.TrimStart(project, clip.Id, -Sec(1)));
            Assert.Equal(at18, SourceAt(project, cursorRow, 18));

            Assert.Equal(-Sec(3), TimelineOps.TrimEnd(project, clip.Id, -Sec(3)));
            Assert.Equal(at22, SourceAt(project, cursorRow, 22));

            Assert.True(TimelineOps.SplitItem(project, clip.Id, Sec(20)));
            Assert.Equal(2, ClipCount(project, cursorRow));
            Assert.Equal(at18, SourceAt(project, cursorRow, 18));
            Assert.Equal(at22, SourceAt(project, cursorRow, 22));
        }

        [Theory]
        [InlineData(1.0, 2.0)]
        [InlineData(2.0, 1.0)]
        [InlineData(0.5, 4.0)]
        public void An_overlay_cannot_trim_back_past_the_start_of_its_source(double speed, double room)
        {
            var project = Recording(out _, out var cursorRow, out _);
            var clip = Cursor(project, cursorRow, 10, 5, 2, speed);

            Assert.Equal(-Sec(room), TimelineOps.TrimStart(project, clip.Id, -Sec(9)));
            Assert.Equal(0, ClockOf(clip).SourceInTicks);
        }

        [Theory]
        [InlineData(1.0, 65.0)]
        [InlineData(2.0, 30.0)]
        public void An_overlay_cannot_trim_out_past_the_end_of_the_screen_stream(double speed, double room)
        {
            // 70 s of the 100 s screen stream remain, in timeline ticks at the speed. The webcam is
            // stream 1 and stops at 40 s, which would allow far less.
            var project = Recording(out _, out var cursorRow, out var keysRow);
            var cursor = Cursor(project, cursorRow, 0, 5, 30, speed);
            var keys = Keys(project, keysRow, 0, 5, 30, speed);

            Assert.Equal(Sec(room), TimelineOps.TrimEnd(project, cursor.Id, Sec(500)));
            Assert.Equal(Sec(room), TimelineOps.TrimEnd(project, keys.Id, Sec(500)));
        }

        [Fact]
        public void An_overlay_on_a_freeze_trims_to_any_length_and_keeps_its_moment()
        {
            var project = Recording(out _, out var cursorRow, out _);
            var clip = Add(project, cursorRow, 10, 5, new CursorContent { SourceInTicks = Sec(99), Freeze = true });

            Assert.Equal(-Sec(10), TimelineOps.TrimStart(project, clip.Id, -Sec(50)));
            Assert.Equal(Sec(500), TimelineOps.TrimEnd(project, clip.Id, Sec(500)));
            Assert.Equal(Sec(99), ClockOf(clip).SourceInTicks);
            Assert.Equal(Sec(99), SourceAt(project, cursorRow, 300));
        }

        [Fact]
        public void Setting_an_overlays_speed_retimes_it_and_a_held_one_refuses()
        {
            var project = Recording(out _, out var cursorRow, out var keysRow);
            var clip = Cursor(project, cursorRow, 10, 10, 4);
            var held = Add(project, keysRow, 10, 10, new KeyboardContent { SourceInTicks = Sec(4), Freeze = true });

            Assert.Equal(2.0, TimelineOps.SetSpeed(project, clip.Id, 2.0));
            Assert.Equal(Sec(5), clip.DurationTicks);
            Assert.Equal(Sec(6), SourceAt(project, cursorRow, 11));

            Assert.Equal(1.0, TimelineOps.SetSpeed(project, held.Id, 2.0));
            Assert.Equal(Sec(10), held.DurationTicks);
        }

        [Fact]
        public void A_cut_across_the_rows_advances_each_overlays_clock()
        {
            var project = Recording(out var screen, out var cursorRow, out var keysRow);
            Screen(project, screen, 10, 20, 4, speed: 2.0);
            Cursor(project, cursorRow, 10, 20, 4, speed: 2.0);
            Keys(project, keysRow, 10, 20, 4, speed: 2.0);

            Assert.True(TimelineOps.SplitAcross(project, Sec(20)).Cut);

            Assert.Equal(Sec(24), ClockOf(At(project, cursorRow, 20)).SourceInTicks);
            Assert.Equal(Sec(24), ClockOf(At(project, keysRow, 20)).SourceInTicks);
            foreach (var t in new[] { 15.0, 20.0, 25.0 })
            {
                Assert.Equal(SourceAt(project, screen, t), SourceAt(project, cursorRow, t));
                Assert.Equal(SourceAt(project, screen, t), SourceAt(project, keysRow, t));
            }
        }

        [Fact]
        public void Cursor_and_screen_stay_aligned_through_a_speed_item()
        {
            // the warp bends the output clock, and whichever output instant is playing, the composer
            // is asked for its project time: both clips answer the same
            var project = Recording(out var screen, out var cursorRow, out var keysRow);
            var speedRow = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Name = "Speed", Order = 3 };
            project.Tracks.Add(speedRow);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(), TrackId = speedRow.Id,
                TimelineStartTicks = Sec(5), DurationTicks = Sec(10),
                Content = new SpeedContent { Factor = 2.0 },
            });
            Screen(project, screen, 0, 30, 7);
            Cursor(project, cursorRow, 0, 30, 7);
            Keys(project, keysRow, 0, 30, 7);

            var warp = TimeWarp.Build(project);
            Assert.False(warp.IsIdentity);
            for (var output = 0.0; output < warp.ToOutput(Sec(30)) / (double)TimeSpan.TicksPerSecond; output += 0.7)
            {
                var t = warp.ToProject(Sec(output)) / (double)TimeSpan.TicksPerSecond;
                Assert.Equal(SourceAt(project, screen, t), SourceAt(project, cursorRow, t));
                Assert.Equal(SourceAt(project, screen, t), SourceAt(project, keysRow, t));
            }

            // and the same through a cut made under the speed item
            Assert.True(TimelineOps.SplitAcross(project, Sec(10)).Cut);
            Assert.Equal(SourceAt(project, screen, 12), SourceAt(project, cursorRow, 12));
            Assert.Equal(Sec(19), SourceAt(project, cursorRow, 12));
        }

        // -------------------------------------------------------------------------------- creation

        [Fact]
        public void An_added_overlay_takes_each_clips_clock_and_skips_what_is_not_its_recording()
        {
            var project = Recording(out var screen, out _, out _);
            project.Tracks.RemoveAll(t => t.Id != screen.Id);
            screen.GroupId = null;
            var other = OtherRecording(project);

            Screen(project, screen, 0, 8, 2);
            Screen(project, screen, 8, 4, 20, speed: 2.0);
            Add(project, screen, 12, 3, new MediaContent { SourceInTicks = Sec(5), Freeze = true });
            Add(project, screen, 20, 5, new MediaContent { SourceId = other.Id, SourceInTicks = Sec(1) });

            var session = SessionOf(project);
            session.AddCursorTrack();
            session.AddKeyboardTrack();

            foreach (var kind in new[] { typeof(CursorContent), typeof(KeyboardContent) })
            {
                var clips = session.Project.Items.Where(i => i.Content.GetType() == kind).ToList();
                Assert.Equal(
                    new[] { (Sec(0), Sec(8), Sec(2), 1.0, false), (Sec(8), Sec(4), Sec(20), 2.0, false), (Sec(12), Sec(3), Sec(5), 1.0, true) },
                    clips.Select(c => (c.TimelineStartTicks, c.DurationTicks, ClockOf(c).SourceInTicks, ClockOf(c).Speed, ClockOf(c).Freeze)));
            }
        }

        // ------------------------------------------------------------------------------- re-deriving

        [Fact]
        public void Rederive_touches_only_the_span_and_the_first_part_keeps_the_id()
        {
            var project = Recording(out _, out var cursorRow, out _);
            var clip = Cursor(project, cursorRow, 0, 30, 0);
            var host = new Item
            {
                Id = Guid.NewGuid(), TimelineStartTicks = Sec(10), DurationTicks = Sec(10),
                Content = new MediaContent { SourceId = project.Sources[0].Id, SourceInTicks = Sec(50), Speed = 2.0 },
            };
            long earliest = long.MaxValue, latest = long.MinValue;

            OverlayClock.Rederive(project, clip, Sec(10), Sec(20), t =>
            {
                earliest = Math.Min(earliest, t);
                latest = Math.Max(latest, t);
                return host.Covers(t) ? host : null;
            });
            project.Normalize();

            Assert.True(earliest >= Sec(10) && latest < Sec(20));
            Assert.Equal(3, ClipCount(project, cursorRow));
            Assert.Same(clip, At(project, cursorRow, 5));
            Assert.Equal(Sec(5), SourceAt(project, cursorRow, 5));
            Assert.Equal(Sec(60), SourceAt(project, cursorRow, 15));
            Assert.Equal(2.0, ClockOf(At(project, cursorRow, 15)).Speed);
            Assert.Equal(Sec(25), SourceAt(project, cursorRow, 25));
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void An_overlay_within_rounding_of_its_host_keeps_its_own_clock()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var host = Screen(project, screen, 0, 20, 0, speed: 1.37);
            var clip = Cursor(project, cursorRow, 0, 20, 0, speed: 1.37);
            ClockOf(clip).SourceInTicks = 3; // ticks, not seconds
            var before = project.ToJson();

            TimelineOps.RebaseOverlays(project, host, 0, Sec(20));

            Assert.Equal(before, project.ToJson());
        }

        // ------------------------------------------------------- grouped overlays follow the screen

        [Fact]
        public void Rejoining_where_nothing_moves_leaves_the_overlay_alone()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            Screen(project, screen, 0, 10, 0);
            Screen(project, screen, 20, 10, 20);
            var clip = Cursor(project, cursorRow, 0, 30, 0);

            Assert.True(TimelineOps.RejoinGap(project, GapAt(project, screen, 15)));

            Assert.Same(clip, Assert.Single(project.Items, i => i.TrackId == cursorRow.Id));
            Assert.Equal(Sec(15), SourceAt(project, cursorRow, 15));
        }

        [Fact]
        public void Rejoining_that_slides_the_row_brings_every_later_overlay_back_into_step()
        {
            var project = Recording(out var screen, out var cursorRow, out var keysRow);
            Screen(project, screen, 0, 10, 0);
            Screen(project, screen, 15, 10, 10);
            Screen(project, screen, 30, 10, 25);
            foreach (var row in new[] { cursorRow, keysRow })
            {
                Cursor(project, row, 0, 10, 0);
                Cursor(project, row, 15, 10, 10);
                Cursor(project, row, 30, 10, 25);
            }

            Assert.True(TimelineOps.RejoinGap(project, GapAt(project, screen, 12)));

            // the left clip now plays 20 s straight through, and the last one slid 5 s left
            Assert.Equal(Sec(20), At(project, screen, 19).DurationTicks);
            Assert.Equal(Sec(25), At(project, screen, 25).TimelineStartTicks);
            foreach (var row in new[] { cursorRow, keysRow })
            {
                Assert.Equal(SourceAt(project, screen, 17), SourceAt(project, row, 17));
                Assert.Equal(Sec(17), SourceAt(project, row, 17));
                Assert.Equal(SourceAt(project, screen, 32), SourceAt(project, row, 32));
                Assert.Equal(Sec(32), SourceAt(project, row, 32));

                // past the screen, each keeps the clock it had
                Assert.Equal(Sec(15), SourceAt(project, row, 20));
                Assert.Equal(Sec(30), SourceAt(project, row, 35));
            }
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void Rejoining_that_slides_another_recordings_clip_brings_its_overlays_along()
        {
            var project = Recording(out var screen, out var cursorRow, out var keysRow);
            var other = OtherRecording(project);
            Screen(project, screen, 0, 10, 0);
            Screen(project, screen, 15, 10, 10);
            Add(project, screen, 30, 10, new MediaContent { SourceId = other.Id, SourceInTicks = Sec(40) });
            Cursor(project, cursorRow, 0, 10, 0);
            Cursor(project, cursorRow, 15, 10, 10);
            Add(project, cursorRow, 30, 10, new CursorContent { SourceId = other.Id, SourceInTicks = Sec(40) });
            Add(project, keysRow, 30, 10, new KeyboardContent { SourceId = other.Id, SourceInTicks = Sec(40) });

            Assert.True(TimelineOps.RejoinGap(project, GapAt(project, screen, 12)));

            // the rejoined clip plays through to 20 s, and the other recording's clip slid 5 s left
            Assert.Equal(Sec(17), SourceAt(project, cursorRow, 17));
            Assert.Equal(Sec(25), At(project, screen, 30).TimelineStartTicks);
            foreach (var row in new[] { cursorRow, keysRow })
            {
                Assert.Equal(Sec(45), SourceAt(project, screen, 30));
                Assert.Equal(Sec(45), SourceAt(project, row, 30));
                Assert.Equal(Sec(49), SourceAt(project, row, 34));

                // past the slid clip the overlay keeps the clock it had
                Assert.Equal(Sec(47), SourceAt(project, row, 37));
            }
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void Rejoining_webcam_clips_that_slides_a_screen_clip_brings_its_overlays_along()
        {
            var project = Recording(out var screen, out var cursorRow, out var keysRow);
            Screen(project, screen, 0, 10, 0, stream: 1);
            Screen(project, screen, 15, 10, 10, stream: 1);
            Screen(project, screen, 30, 10, 40);
            Cursor(project, cursorRow, 30, 10, 40);
            Keys(project, keysRow, 30, 10, 40);

            Assert.True(TimelineOps.RejoinGap(project, GapAt(project, screen, 12)));

            Assert.Equal(Sec(25), At(project, screen, 30).TimelineStartTicks);
            foreach (var row in new[] { cursorRow, keysRow })
            {
                Assert.Equal(Sec(45), SourceAt(project, screen, 30));
                Assert.Equal(Sec(45), SourceAt(project, row, 30));
                Assert.Equal(Sec(49), SourceAt(project, row, 34));
            }
        }

        [Fact]
        public void Rejoining_webcam_clips_that_slides_only_webcam_leaves_the_overlays_alone()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var webcamRow = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Webcam", Order = 3, GroupId = screen.GroupId };
            project.Tracks.Add(webcamRow);
            Screen(project, screen, 0, 40, 0);
            Screen(project, webcamRow, 0, 10, 0, stream: 1);
            Screen(project, webcamRow, 15, 10, 10, stream: 1);
            Screen(project, webcamRow, 30, 5, 25, stream: 1);

            // a nudge: a rebase of any kind would put it back on the screen clip's time
            var clip = Cursor(project, cursorRow, 0, 40, 7);

            Assert.True(TimelineOps.RejoinGap(project, GapAt(project, webcamRow, 12)));

            Assert.Equal(Sec(25), At(project, webcamRow, 27).TimelineStartTicks);
            Assert.Same(clip, Assert.Single(project.Items, i => i.TrackId == cursorRow.Id));
            Assert.Equal(Sec(7), ClockOf(clip).SourceInTicks);
        }

        [Fact]
        public void Rejoining_with_nothing_later_on_the_row_stops_at_the_rejoined_clip()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var inset = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Inset", Order = 3, GroupId = screen.GroupId };
            project.Tracks.Add(inset);
            Screen(project, screen, 0, 10, 0);
            Screen(project, screen, 15, 10, 10);
            Screen(project, inset, 22, 8, 60);

            // a nudge past where the rejoined clip now ends
            var nudged = Cursor(project, cursorRow, 22, 8, 3);

            Assert.True(TimelineOps.RejoinGap(project, GapAt(project, screen, 12)));

            Assert.Equal(Sec(20), At(project, screen, 10).DurationTicks);
            Assert.Equal(Sec(3), ClockOf(nudged).SourceInTicks);
        }

        [Fact]
        public void A_freeze_fill_of_the_left_neighbour_holds_the_overlays_on_its_last_frame()
        {
            var project = Recording(out var screen, out var cursorRow, out var keysRow);
            Screen(project, screen, 0, 10, 0);
            Screen(project, screen, 20, 10, 20);
            Cursor(project, cursorRow, 0, 30, 0);
            Keys(project, keysRow, 0, 30, 0);

            Assert.NotNull(TimelineOps.FillGapWithFreeze(project, GapAt(project, screen, 15), fromLeft: true));

            var held = Sec(10) - 1;
            foreach (var row in new[] { cursorRow, keysRow })
            {
                Assert.Equal(3, ClipCount(project, row));
                Assert.Equal(Sec(5), SourceAt(project, row, 5));
                Assert.Equal(held, SourceAt(project, row, 11));
                Assert.Equal(held, SourceAt(project, row, 19));
                Assert.True(ClockOf(At(project, row, 15)).Freeze);
                Assert.False(ClockOf(At(project, row, 25)).Freeze);
                Assert.Equal(Sec(25), SourceAt(project, row, 25));
            }
            Assert.Equal(SourceAt(project, screen, 15), SourceAt(project, cursorRow, 15));
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void A_freeze_fill_of_the_right_neighbour_holds_the_overlays_on_its_first_frame()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            Screen(project, screen, 0, 10, 0);
            Screen(project, screen, 20, 10, 20);
            Cursor(project, cursorRow, 0, 30, 0);

            Assert.NotNull(TimelineOps.FillGapWithFreeze(project, GapAt(project, screen, 15), fromLeft: false));

            Assert.Equal(Sec(20), SourceAt(project, cursorRow, 11));
            Assert.Equal(Sec(20), SourceAt(project, cursorRow, 19));
            Assert.Equal(Sec(25), SourceAt(project, cursorRow, 25));
        }

        [Fact]
        public void A_freeze_fill_is_one_undo_with_its_overlay_parts()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            Screen(project, screen, 0, 10, 0);
            Screen(project, screen, 20, 10, 20);
            Cursor(project, cursorRow, 0, 30, 0);
            var session = SessionOf(project);
            var before = session.Project.ToJson();

            Assert.NotNull(session.FillGapWithFreeze(session.FindGap(screen.Id, Sec(15)).Value, fromLeft: true));
            Assert.Equal(3, ClipCount(session.Project, cursorRow));

            session.Undo();

            Assert.Equal(before, session.Project.ToJson());
            Assert.False(session.CanUndo);
        }

        [Fact]
        public void Extending_the_left_clip_over_an_overlay_already_in_step_changes_nothing()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            Screen(project, screen, 0, 10, 0);
            Screen(project, screen, 20, 10, 20);
            var clip = Cursor(project, cursorRow, 0, 30, 0);

            Assert.True(TimelineOps.ExtendIntoGap(project, GapAt(project, screen, 15), fromLeft: true));

            Assert.Same(clip, Assert.Single(project.Items, i => i.TrackId == cursorRow.Id));
            Assert.Equal(SourceAt(project, screen, 15), SourceAt(project, cursorRow, 15));
        }

        [Fact]
        public void Extending_the_right_clip_gives_the_overlay_what_the_gap_now_shows()
        {
            var project = Recording(out var screen, out var cursorRow, out var keysRow);
            Screen(project, screen, 0, 10, 0);
            Screen(project, screen, 20, 10, 40);
            Cursor(project, cursorRow, 0, 20, 0);
            Cursor(project, cursorRow, 20, 10, 40);
            Keys(project, keysRow, 0, 30, 0);

            Assert.True(TimelineOps.ExtendIntoGap(project, GapAt(project, screen, 15), fromLeft: false));

            Assert.Equal(Sec(35), SourceAt(project, screen, 15));
            Assert.Equal(Sec(35), SourceAt(project, cursorRow, 15));
            Assert.Equal(Sec(35), SourceAt(project, keysRow, 15));
            Assert.Equal(Sec(5), SourceAt(project, cursorRow, 5));

            // past the gap the keys clip keeps its own clock: that span was not part of this edit
            Assert.Equal(Sec(25), SourceAt(project, keysRow, 25));
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void A_speed_change_on_a_screen_clip_carries_its_overlay()
        {
            var project = Recording(out var screen, out var cursorRow, out var keysRow);
            var first = Screen(project, screen, 0, 20, 0);
            Screen(project, screen, 20, 20, 20);
            Cursor(project, cursorRow, 0, 40, 0);
            Keys(project, keysRow, 0, 40, 0);

            Assert.Equal(2.0, TimelineOps.SetSpeed(project, first.Id, 2.0));

            Assert.Equal(Sec(10), first.DurationTicks);
            foreach (var row in new[] { cursorRow, keysRow })
            {
                Assert.Equal(Sec(10), SourceAt(project, row, 5));
                Assert.Equal(2.0, ClockOf(At(project, row, 5)).Speed);

                // the stretch it left behind has no screen under it and keeps its clock, which is
                // in step with the next clip where that begins
                Assert.Equal(Sec(15), SourceAt(project, row, 15));
                Assert.Equal(1.0, ClockOf(At(project, row, 15)).Speed);
                Assert.Equal(SourceAt(project, screen, 30), SourceAt(project, row, 30));
            }
        }

        [Fact]
        public void A_slowed_screen_clip_stretches_its_overlay_clock_up_to_the_next_clip()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var first = Screen(project, screen, 0, 20, 0);
            Screen(project, screen, 25, 15, 25);
            Cursor(project, cursorRow, 0, 40, 0);

            Assert.Equal(0.5, TimelineOps.SetSpeed(project, first.Id, 0.5));

            // end-trimmed at the next clip, and the overlay follows both sides of that edge
            Assert.Equal(Sec(25), first.DurationTicks);
            Assert.Equal(Sec(5), SourceAt(project, cursorRow, 10));
            Assert.Equal(0.5, ClockOf(At(project, cursorRow, 10)).Speed);
            Assert.Equal(Sec(30), SourceAt(project, cursorRow, 30));
            Assert.Equal(1.0, ClockOf(At(project, cursorRow, 30)).Speed);
        }

        // ----------------------------------------------------------------- what a rebase leaves alone

        [Fact]
        public void A_nudge_outside_the_rebased_span_survives_and_one_inside_is_overwritten()
        {
            var project = Recording(out var screen, out var cursorRow, out var keysRow);
            Screen(project, screen, 0, 10, 0);
            Screen(project, screen, 20, 10, 20);
            var before = Cursor(project, cursorRow, 0, 10, 33);
            var inside = Cursor(project, cursorRow, 12, 6, 70);
            var after = Cursor(project, cursorRow, 22, 6, 50);

            Assert.NotNull(TimelineOps.FillGapWithFreeze(project, GapAt(project, screen, 15), fromLeft: true));

            Assert.Equal(Sec(33), ClockOf(before).SourceInTicks);
            Assert.False(ClockOf(before).Freeze);
            Assert.Equal(Sec(50), ClockOf(after).SourceInTicks);
            Assert.False(ClockOf(after).Freeze);

            Assert.True(ClockOf(inside).Freeze);
            Assert.Equal(Sec(10) - 1, ClockOf(inside).SourceInTicks);
        }

        [Fact]
        public void An_ungrouped_overlay_is_left_alone()
        {
            var project = Recording(out var screen, out var cursorRow, out _, grouped: false);
            var first = Screen(project, screen, 0, 20, 0);
            Screen(project, screen, 30, 5, 30);
            Screen(project, screen, 40, 10, 40);
            var clip = Cursor(project, cursorRow, 0, 50, 0);

            TimelineOps.SetSpeed(project, first.Id, 2.0);
            Assert.True(TimelineOps.ExtendIntoGap(project, GapAt(project, screen, 15), fromLeft: true));
            Assert.NotNull(TimelineOps.FillGapWithFreeze(project, GapAt(project, screen, 37), fromLeft: true));

            Assert.Same(clip, Assert.Single(project.Items, i => i.TrackId == cursorRow.Id));
            Assert.Equal((0L, 1.0, false), (ClockOf(clip).SourceInTicks, ClockOf(clip).Speed, ClockOf(clip).Freeze));
        }

        [Fact]
        public void A_webcam_clip_on_a_grouped_row_does_not_lead_the_overlays()
        {
            var project = Recording(out _, out var cursorRow, out _);
            var webcamRow = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 3, GroupId = cursorRow.GroupId };
            project.Tracks.Add(webcamRow);
            var webcam = Screen(project, webcamRow, 0, 20, 0, stream: 1);
            var screen = project.Tracks.Single(t => t.Name == "Screen");
            Screen(project, screen, 0, 20, 30);

            // a nudge: a rebase of any kind would put it back on the screen clip's 30 s
            var clip = Cursor(project, cursorRow, 0, 20, 7);

            TimelineOps.SetSpeed(project, webcam.Id, 2.0);

            Assert.Same(clip, Assert.Single(project.Items, i => i.TrackId == cursorRow.Id));
            Assert.Equal((Sec(7), 1.0), (ClockOf(clip).SourceInTicks, ClockOf(clip).Speed));
        }

        [Fact]
        public void An_overlay_of_another_recording_on_the_group_is_left_alone()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var other = OtherRecording(project);
            var first = Screen(project, screen, 0, 20, 0);
            var clip = Add(project, cursorRow, 0, 20, new CursorContent { SourceId = other.Id });

            TimelineOps.SetSpeed(project, first.Id, 2.0);

            Assert.Equal(1.0, ClockOf(clip).Speed);
            Assert.Equal(Sec(20), clip.DurationTicks);
        }

        [Fact]
        public void Two_grouped_rows_playing_the_screen_leave_the_backmost_leading()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var inset = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Inset", Order = 3, GroupId = screen.GroupId };
            project.Tracks.Add(inset);
            Screen(project, screen, 0, 20, 5);
            var front = Screen(project, inset, 0, 20, 60);
            var clip = Cursor(project, cursorRow, 0, 20, 5);

            TimelineOps.SetSpeed(project, front.Id, 2.0);

            Assert.Same(clip, Assert.Single(project.Items, i => i.TrackId == cursorRow.Id));
            Assert.Equal(Sec(5), ClockOf(clip).SourceInTicks);
            Assert.Equal(1.0, ClockOf(clip).Speed);
        }

        [Fact]
        public void What_a_sped_up_clip_vacates_goes_to_the_grouped_clip_still_playing_there()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var inset = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Inset", Order = 3, GroupId = screen.GroupId };
            project.Tracks.Add(inset);
            var back = Screen(project, screen, 0, 20, 5);
            Screen(project, inset, 0, 20, 60);
            Cursor(project, cursorRow, 0, 20, 5);

            TimelineOps.SetSpeed(project, back.Id, 2.0);

            Assert.Equal(Sec(5 + 2 * 5), SourceAt(project, cursorRow, 5));
            Assert.Equal(Sec(60 + 15), SourceAt(project, cursorRow, 15));
        }

        // -------------------------------------------------------- a trim back out over another clock

        [Fact]
        public void A_trim_out_past_a_speed_change_takes_the_overlay_with_it()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var first = Screen(project, screen, 0, 10, 0);
            Cursor(project, cursorRow, 0, 20, 0);
            var session = SessionOf(project);

            session.SetItemSpeed(first.Id, 2.0);
            Assert.Equal(Sec(5), session.Project.Items.Single(i => i.Id == first.Id).DurationTicks);

            Assert.Equal(Sec(2), session.TrimItemEnd(first.Id, Sec(2)));

            // the clip shows 12 s of source at 6 s, where the overlay's old clock said 6 s
            Assert.Equal(Sec(12), SourceAt(session.Project, screen, 6));
            Assert.Equal(Sec(12), SourceAt(session.Project, cursorRow, 6));
            Assert.Equal(Sec(8), SourceAt(session.Project, cursorRow, 8));
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_trim_back_under_a_neighbours_overhang_takes_the_overlay_there()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            Screen(project, screen, 0, 7, 0);
            var second = Screen(project, screen, 10, 10, 30);
            Cursor(project, cursorRow, 0, 10, 0);
            Cursor(project, cursorRow, 10, 10, 30);
            var session = SessionOf(project);

            Assert.Equal(-Sec(3), session.TrimItemStart(second.Id, -Sec(3)));

            Assert.Equal(Sec(5), SourceAt(session.Project, cursorRow, 5));
            Assert.Equal(Sec(28), SourceAt(session.Project, screen, 8));
            Assert.Equal(Sec(28), SourceAt(session.Project, cursorRow, 8));
            Assert.Equal(Sec(35), SourceAt(session.Project, cursorRow, 15));
        }

        [Fact]
        public void A_trim_out_over_an_overlay_in_step_changes_nothing()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var clip = Screen(project, screen, 0, 10, 0);
            var cursor = Cursor(project, cursorRow, 0, 20, 0);
            var session = SessionOf(project);

            session.TrimItemEnd(clip.Id, Sec(5));

            Assert.Equal(cursor.Id, Assert.Single(session.Project.Items, i => i.TrackId == cursorRow.Id).Id);
            Assert.Equal(Sec(12), SourceAt(session.Project, cursorRow, 12));
        }

        [Fact]
        public void A_shorter_trim_never_touches_the_overlay()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var clip = Screen(project, screen, 0, 10, 0);
            var cursor = Cursor(project, cursorRow, 0, 10, 77);
            var session = SessionOf(project);

            session.TrimItemEnd(clip.Id, -Sec(5));
            session.TrimItemStart(clip.Id, Sec(1));

            var overlay = Assert.Single(session.Project.Items, i => i.TrackId == cursorRow.Id);
            Assert.Equal((cursor.Id, Sec(77), Sec(10)), (overlay.Id, ClockOf(overlay).SourceInTicks, overlay.DurationTicks));
        }

        [Fact]
        public void A_trim_drag_takes_the_overlay_once_on_release()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var first = Screen(project, screen, 0, 10, 0);
            Cursor(project, cursorRow, 0, 20, 0);
            var session = SessionOf(project);
            session.SetItemSpeed(first.Id, 2.0);
            var parts = ClipCount(session.Project, cursorRow);
            var before = session.Project.ToJson();

            using (var gesture = session.BeginGesture("Trim"))
            {
                for (var step = 0; step < 4; step++)
                    session.TrimItemEnd(first.Id, Sec(0.5));

                // nothing is cut while the pointer is down
                Assert.Equal(parts, ClipCount(session.Project, cursorRow));
                gesture.Commit();
            }

            // one more part, from the 5 s the clip began the drag at to the 7 s it ended on
            Assert.Equal(parts + 1, ClipCount(session.Project, cursorRow));
            Assert.Equal(Sec(5), At(session.Project, cursorRow, 6).TimelineStartTicks);
            Assert.Equal(Sec(2), At(session.Project, cursorRow, 6).DurationTicks);
            Assert.Equal(Sec(12), SourceAt(session.Project, cursorRow, 6));
            Assert.Equal(Sec(8), SourceAt(session.Project, cursorRow, 8));

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void A_trim_drag_out_and_back_changes_nothing()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var first = Screen(project, screen, 0, 10, 0);
            Cursor(project, cursorRow, 0, 20, 0);
            var session = SessionOf(project);
            var original = session.Project.ToJson();
            session.SetItemSpeed(first.Id, 2.0);
            var before = session.Project.ToJson();

            using (var gesture = session.BeginGesture("Trim"))
            {
                session.TrimItemEnd(first.Id, Sec(3));
                session.TrimItemEnd(first.Id, -Sec(3));
                gesture.Commit();
            }

            Assert.Equal(before, session.Project.ToJson());

            // and it left no undo entry: the next undo takes back the speed change
            session.Undo();
            Assert.Equal(original, session.Project.ToJson());
        }

        [Fact]
        public void A_cancelled_trim_drag_changes_nothing()
        {
            var project = Recording(out var screen, out var cursorRow, out _);
            var first = Screen(project, screen, 0, 10, 0);
            Cursor(project, cursorRow, 0, 20, 0);
            var session = SessionOf(project);
            session.SetItemSpeed(first.Id, 2.0);
            var before = session.Project.ToJson();

            using (var gesture = session.BeginGesture("Trim"))
            {
                session.TrimItemEnd(first.Id, Sec(3));
                gesture.Cancel();
            }

            Assert.Equal(before, session.Project.ToJson());
        }
    }
}
