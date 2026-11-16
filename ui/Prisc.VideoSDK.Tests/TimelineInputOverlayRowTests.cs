using System;
using System.Collections.Generic;
using System.Linq;
using Prisc.UI.VideoEditor.Timeline;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    // The timeline's input-capture rows: how the layout classifies them, and that they are ordinary
    // rows of the video block in the reorder math, against the session the drop goes to. Pure model
    // math, no Avalonia runtime — Prisc.Ui exposes its internals to this project via
    // InternalsVisibleTo.
    public class TimelineInputOverlayRowTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        private static Track NewTrack(TrackKind kind, int order, string name) => new Track
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Order = order,
            Name = name,
        };

        private static Item NewItem(Track track, ItemContent content, long startTicks = 0,
            long durationTicks = TimeSpan.TicksPerSecond) => new Item
        {
            Id = Guid.NewGuid(),
            TrackId = track.Id,
            TimelineStartTicks = startTicks,
            DurationTicks = durationTicks,
            Content = content,
        };

        // ------------------------------------------------------------------------ classification

        [Fact]
        public void KindOf_classifies_the_input_overlay_rows_by_their_earliest_item()
        {
            var cursor = NewTrack(TrackKind.Video, 3, "Cursor");
            var keys = NewTrack(TrackKind.Video, 2, "Keys");

            Assert.Equal(TimelineRowKind.Cursor,
                TimelineRowLayout.KindOf(cursor, new[] { NewItem(cursor, new CursorContent()) }));
            Assert.Equal(TimelineRowKind.Keyboard,
                TimelineRowLayout.KindOf(keys, new[] { NewItem(keys, new KeyboardContent()) }));
        }

        [Fact]
        public void Input_overlay_rows_are_card_height()
        {
            Assert.Equal(26, TimelineRowLayout.HeightOf(TimelineRowKind.Cursor));
            Assert.Equal(26, TimelineRowLayout.HeightOf(TimelineRowKind.Keyboard));
        }

        // -------------------------------------------------------------------------------- layout

        /// <summary>An overlay row is drawn where its Order puts it, like any row: a webcam row whose
        /// Order sits between the screen row and its overlays draws between them.</summary>
        [Fact]
        public void Build_draws_the_overlay_rows_where_their_order_puts_them()
        {
            var sourceId = Guid.NewGuid();
            var screen = NewTrack(TrackKind.Video, 0, "Screen");
            var keys = NewTrack(TrackKind.Video, 1, "Keys");
            var webcam = NewTrack(TrackKind.Video, 2, "Webcam");
            var cursor = NewTrack(TrackKind.Video, 3, "Cursor");
            var audio = NewTrack(TrackKind.Audio, 4, "Audio");

            var project = new Project
            {
                Tracks = { screen, keys, webcam, cursor, audio },
                Items =
                {
                    NewItem(screen, new MediaContent { SourceId = sourceId, StreamIndex = 0 }),
                    NewItem(webcam, new MediaContent { SourceId = sourceId, StreamIndex = 1 }),
                    NewItem(keys, new KeyboardContent { SourceId = sourceId }),
                    NewItem(cursor, new CursorContent { SourceId = sourceId }),
                },
                Sources = { new Source { Id = sourceId, Path = @"C:\rec\in.mp4" } },
            };

            var rows = TimelineRowLayout.Build(project);

            Assert.Equal(new[] { cursor.Id, webcam.Id, keys.Id, screen.Id, audio.Id },
                rows.Select(r => r.TrackId).ToArray());
            Assert.Equal(new[]
            {
                TimelineRowKind.Cursor, TimelineRowKind.Video, TimelineRowKind.Keyboard,
                TimelineRowKind.Video, TimelineRowKind.Audio,
            }, rows.Select(r => r.Kind).ToArray());
            Assert.Equal(new[] { 26d, 56d, 26d, 56d, 36d }, rows.Select(r => r.Height).ToArray());
        }

        // ------------------------------------------------------------------------------- reorder

        /// <summary>The stack a recording with both overlays draws: webcam, cursor, keys, screen,
        /// then the audio row.</summary>
        private static IReadOnlyList<TimelineRow> Stack()
        {
            var rows = new List<TimelineRow>();
            double top = 0;
            foreach (var kind in new[]
            {
                TimelineRowKind.Video, TimelineRowKind.Cursor, TimelineRowKind.Keyboard,
                TimelineRowKind.Video, TimelineRowKind.Audio,
            })
            {
                var height = TimelineRowLayout.HeightOf(kind);
                rows.Add(new TimelineRow(Guid.NewGuid(), kind, top, height));
                top += height;
            }

            return rows;
        }

        [Fact]
        public void Overlay_rows_have_a_grip_and_share_the_video_block()
        {
            var rows = Stack();

            for (var row = 0; row <= 3; row++)
            {
                Assert.True(TimelineReorder.CanDrag(rows, row));
                Assert.Equal((0, 3), TimelineReorder.GroupRange(rows, row));
            }

            Assert.False(TimelineReorder.CanDrag(rows, 4)); // the only audio row
        }

        /// <summary>Every boundary of the block is a slot, the ones beside an overlay row included.</summary>
        [Fact]
        public void A_drop_can_land_on_every_boundary_of_the_block()
        {
            var rows = Stack();

            var slots = new HashSet<int>();
            for (var y = -20d; y <= TimelineRowLayout.TotalHeight(rows) + 20; y += 1)
                slots.Add(TimelineReorder.DropIndexAt(rows, 0, y));

            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, slots.OrderBy(s => s).ToArray());
        }

        [Fact]
        public void A_cursor_row_drop_flips_into_the_models_layer_order()
        {
            var rows = Stack(); // the video block is rows 0..3, so layer indexes run 3..0 down it

            Assert.Equal(3, TimelineReorder.TargetLayerIndex(rows, 1, 0)); // above the webcam
            Assert.Equal(0, TimelineReorder.TargetLayerIndex(rows, 1, 4)); // below the screen
            Assert.Equal(1, TimelineReorder.TargetLayerIndex(rows, 1, 3)); // between keys and screen
            Assert.Null(TimelineReorder.TargetLayerIndex(rows, 1, 1));     // a drag that came home
            Assert.Null(TimelineReorder.TargetLayerIndex(rows, 1, 2));
        }

        // --------------------------------------------------------- agreement with EditorSession

        /// <summary>A recording of one source over a screen and a webcam row, with both overlay
        /// rows added by the session's own factories.</summary>
        private static EditorSession OverlaySession() => OverlaySession(out _, out _);

        private static EditorSession OverlaySession(out Track screenTrack, out Track webcamTrack)
        {
            var session = BareOverlaySession(out screenTrack, out webcamTrack);
            Assert.NotNull(session.AddKeyboardTrack());
            Assert.NotNull(session.AddCursorTrack());
            return session;
        }

        /// <summary>The same recording before any overlay row exists.</summary>
        private static EditorSession BareOverlaySession(out Track screenTrack, out Track webcamTrack)
        {
            var sourceId = Guid.NewGuid();
            var screen = NewTrack(TrackKind.Video, 0, "Screen");
            var webcam = NewTrack(TrackKind.Video, 1, "Webcam");
            var audio = NewTrack(TrackKind.Audio, 2, "Audio");
            screenTrack = screen;
            webcamTrack = webcam;

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = @"C:\rec\in.mp4",
                        InputCapturePath = TestPath.ExistingCapture,
                        Streams =
                        {
                            new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080 },
                            new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480 },
                        },
                    },
                },
                Tracks = { screen, webcam, audio },
                Items =
                {
                    NewItem(screen, new MediaContent { SourceId = sourceId, StreamIndex = 0 }, 0, Ms(20_000)),
                    NewItem(webcam, new MediaContent { SourceId = sourceId, StreamIndex = 1 }, 0, Ms(20_000)),
                },
            };

            return new EditorSession(project, null, save => save());
        }

        /// <summary>The factories put the new row above the screen row even when the webcam row —
        /// which plays the same source — has been dragged behind it: measuring from the webcam
        /// row would insert the overlay beneath the screen frame, where it composes invisibly
        /// (and the default cursor overlay stands down because a cursor item exists).</summary>
        [Fact]
        public void The_factories_measure_from_the_screen_row_even_when_the_webcam_row_is_behind_it()
        {
            var session = BareOverlaySession(out var screen, out var webcam);
            Assert.True(session.MoveTrackToIndex(webcam.Id, 0)); // webcam to the backmost layer

            var keys = session.AddKeyboardTrack();
            var cursor = session.AddCursorTrack();
            Assert.NotNull(keys);
            Assert.NotNull(cursor);

            int OrderOf(Guid trackId) => session.Project.Tracks.First(t => t.Id == trackId).Order;
            Assert.True(OrderOf(webcam.Id) < OrderOf(screen.Id));
            Assert.Equal(OrderOf(screen.Id) + 1, OrderOf(keys.TrackId));
            Assert.Equal(OrderOf(screen.Id) + 2, OrderOf(cursor.TrackId));

            var rows = TimelineRowLayout.Build(session.Project);
            Assert.Equal(new[]
            {
                TimelineRowKind.Cursor, TimelineRowKind.Keyboard, TimelineRowKind.Video,
                TimelineRowKind.Video, TimelineRowKind.Audio,
            }, rows.Select(r => r.Kind).ToArray());
            Assert.Equal(screen.Id, rows[2].TrackId);
            Assert.Equal(webcam.Id, rows[3].TrackId);
            Assert.Empty(session.Project.Validate());
        }

        /// <summary>Every drop of every row, driven all the way through the session: the layer index
        /// the panel would send must land the row exactly where the indicator showed it, so the
        /// display must be the exact reverse of the model.</summary>
        [Fact]
        public void Every_drop_lands_where_the_drag_showed_it()
        {
            var shape = TimelineRowLayout.Build(OverlaySession().Project);
            Assert.Equal(new[]
            {
                TimelineRowKind.Video, TimelineRowKind.Cursor, TimelineRowKind.Keyboard,
                TimelineRowKind.Video, TimelineRowKind.Audio,
            }, shape.Select(r => r.Kind).ToArray());

            AssertEveryDropLands(OverlaySession);
        }

        /// <summary>The same sweep with a text row on top and the webcam row stepped down between the
        /// overlays: there is no arrangement a drop cannot be counted in.</summary>
        [Fact]
        public void Every_drop_lands_with_a_row_between_the_overlays()
        {
            var shape = TimelineRowLayout.Build(SteppedSession().Project);
            Assert.Equal(new[]
            {
                TimelineRowKind.Text, TimelineRowKind.Cursor, TimelineRowKind.Video,
                TimelineRowKind.Keyboard, TimelineRowKind.Video, TimelineRowKind.Audio,
            }, shape.Select(r => r.Kind).ToArray());

            AssertEveryDropLands(SteppedSession);
        }

        private static EditorSession SteppedSession()
        {
            var session = OverlaySession(out _, out var webcam);
            Assert.NotNull(session.AddText(0, Ms(2_000)));
            Assert.True(session.MoveTrackLayer(webcam.Id, towardsFront: false));
            return session;
        }

        private static void AssertEveryDropLands(Func<EditorSession> factory)
        {
            var shape = TimelineRowLayout.Build(factory().Project);
            var moves = 0;

            for (var from = 0; from < shape.Count; from++)
            {
                var (start, end) = TimelineReorder.GroupRange(shape, from);

                for (var drop = start; drop <= end + 1; drop++)
                {
                    // fresh session per drop — MoveTrackToIndex mutates the project
                    var session = factory();
                    var rows = TimelineRowLayout.Build(session.Project);

                    var layerIndex = TimelineReorder.TargetLayerIndex(rows, from, drop);
                    if (layerIndex == null)
                    {
                        Assert.True(drop == from || drop == from + 1, $"row {from} refused slot {drop}");
                        continue;
                    }

                    // what the drag meant, spelled out: the block's rows with the dragged one moved
                    var display = rows.Skip(start).Take(end - start + 1).Select(r => r.TrackId).ToList();
                    var moved = rows[from].TrackId;
                    display.Remove(moved);
                    display.Insert((drop > from ? drop - 1 : drop) - start, moved);

                    Assert.True(session.MoveTrackToIndex(moved, layerIndex.Value));
                    moves++;
                    var after = TimelineRowLayout.Build(session.Project);
                    Assert.Equal(display, after.Skip(start).Take(end - start + 1).Select(r => r.TrackId).ToList());
                    Assert.Empty(session.Project.Validate());
                }
            }

            Assert.True(moves > 0);
        }

        // --------------------------------------------------------------------------------- hosting

        private static readonly (string Name, Func<ItemContent> Make)[] VideoRowContents =
        {
            ("media", () => new MediaContent()),
            ("text", () => new TextContent { Text = "t" }),
            ("image", () => new ImageContent { Path = "i.png" }),
            ("wallpaper", () => new BackgroundContent()),
            ("obscure", () => new ObscureContent()),
            ("shape", () => new ShapeContent()),
            ("cursor", () => new CursorContent()),
            ("keys", () => new KeyboardContent()),
        };

        public static IEnumerable<object[]> VideoRowPairs() =>
            from held in VideoRowContents
            from arriving in VideoRowContents
            select new object[] { held.Name, arriving.Name };

        private static bool IsOverlay(TimelineRowKind kind) => kind is TimelineRowKind.Cursor or TimelineRowKind.Keyboard;

        /// <summary>
        /// The rule against the classification it protects, since <c>KindOf</c> is out of the model's
        /// reach: a row refuses a clip exactly when that clip, landing first on the row, would turn it
        /// into or out of a cursor or keys row, or into a background row, which is where the next
        /// wallpaper goes in front of. Other kinds may mix, as they always could, because nothing about
        /// those rows depends on what they are.
        /// </summary>
        [Theory]
        [MemberData(nameof(VideoRowPairs))]
        public void CanHost_agrees_with_KindOf_on_every_video_row(string heldName, string arrivingName)
        {
            var track = NewTrack(TrackKind.Video, 0, "Row");
            var held = NewItem(track, VideoRowContents.Single(c => c.Name == heldName).Make(), Ms(1_000));
            var project = new Project { Tracks = { track }, Items = { held } };
            var arriving = VideoRowContents.Single(c => c.Name == arrivingName).Make();

            var before = TimelineRowLayout.KindOf(track, new[] { held });
            var after = TimelineRowLayout.KindOf(track, new[] { held, NewItem(track, arriving, 0) });
            var breaks = before != after && (IsOverlay(before) || IsOverlay(after) || after == TimelineRowKind.Background);

            Assert.Equal(!breaks, TimelineOps.CanHost(track, arriving, project));
        }

        [Fact]
        public void CanHost_lets_an_empty_row_become_anything_of_its_kind()
        {
            var video = NewTrack(TrackKind.Video, 0, "Video");
            var audio = NewTrack(TrackKind.Audio, 1, "Audio");
            var effect = NewTrack(TrackKind.Effect, 2, "Effect");
            var project = new Project { Tracks = { video, audio, effect } };

            foreach (var (_, make) in VideoRowContents)
                Assert.True(TimelineOps.CanHost(video, make(), project));

            Assert.True(TimelineOps.CanHost(audio, new MediaContent(), project));
            Assert.False(TimelineOps.CanHost(audio, new CursorContent(), project));
            Assert.False(TimelineOps.CanHost(audio, new KeyboardContent(), project));
            Assert.False(TimelineOps.CanHost(audio, new TextContent(), project));
            Assert.True(TimelineOps.CanHost(effect, new ZoomContent(), project));
            Assert.True(TimelineOps.CanHost(effect, new SpeedContent(), project));
            Assert.False(TimelineOps.CanHost(effect, new CursorContent(), project));
            Assert.False(TimelineOps.CanHost(video, new ZoomContent(), project));
            Assert.False(TimelineOps.CanHost(video, new SpeedContent(), project));
        }

        /// <summary>A sound never lands on a video row nor a picture on an audio row, by the stream
        /// the clip plays. A stream this project cannot name yet says nothing either way.</summary>
        [Fact]
        public void CanHost_goes_by_the_stream_a_media_clip_plays()
        {
            var video = NewTrack(TrackKind.Video, 0, "Video");
            var audio = NewTrack(TrackKind.Audio, 1, "Audio");
            var source = new Source
            {
                Id = Guid.NewGuid(),
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video },
                    new SourceStream { Index = 1, Kind = StreamKind.Audio },
                },
            };
            var project = new Project { Sources = { source }, Tracks = { video, audio } };
            var picture = new MediaContent { SourceId = source.Id, StreamIndex = 0 };
            var sound = new MediaContent { SourceId = source.Id, StreamIndex = 1 };

            Assert.True(TimelineOps.CanHost(video, picture, project));
            Assert.False(TimelineOps.CanHost(video, sound, project));
            Assert.False(TimelineOps.CanHost(audio, picture, project));
            Assert.True(TimelineOps.CanHost(audio, sound, project));

            var unknown = new MediaContent { SourceId = Guid.NewGuid() };
            Assert.True(TimelineOps.CanHost(video, unknown, project));
            Assert.True(TimelineOps.CanHost(audio, unknown, project));
        }

        /// <summary>An effect row is one kind, and a project has one speed row: the two things
        /// <see cref="Project.Validate"/> would refuse.</summary>
        [Fact]
        public void CanHost_keeps_effect_rows_to_one_kind_and_one_speed_row()
        {
            var zooms = NewTrack(TrackKind.Effect, 0, "Zoom");
            var speeds = NewTrack(TrackKind.Effect, 1, "Speed");
            var spare = NewTrack(TrackKind.Effect, 2, "Spare");
            var project = new Project
            {
                Tracks = { zooms, speeds, spare },
                Items =
                {
                    NewItem(zooms, new ZoomContent()),
                    NewItem(speeds, new SpeedContent()),
                },
            };

            Assert.True(TimelineOps.CanHost(zooms, new ZoomContent(), project));
            Assert.False(TimelineOps.CanHost(zooms, new SpeedContent(), project));
            Assert.True(TimelineOps.CanHost(speeds, new SpeedContent(), project));
            Assert.False(TimelineOps.CanHost(speeds, new ZoomContent(), project));
            Assert.True(TimelineOps.CanHost(spare, new ZoomContent(), project));
            Assert.False(TimelineOps.CanHost(spare, new SpeedContent(), project));
        }

        [Fact]
        public void CanHost_takes_overlays_of_another_recording_on_a_row_of_their_kind()
        {
            var row = NewTrack(TrackKind.Video, 0, "Cursor");
            var project = new Project { Tracks = { row }, Items = { NewItem(row, new CursorContent { SourceId = Guid.NewGuid() }) } };

            Assert.True(TimelineOps.CanHost(row, new CursorContent { SourceId = Guid.NewGuid() }, project));
        }
    }
}
