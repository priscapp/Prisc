using System;
using System.Collections.Generic;
using System.Linq;
using Prisc.Geometry2D;
using Prisc.UI.VideoEditor;
using Prisc.UI.VideoEditor.Inspector;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Xunit;
using ModelTransform = Prisc.VideoSDK.Model.Transform;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Lock Placement: a track property that stops a clip moving, resizing, rotating or cropping on
    /// the canvas, enforced by <see cref="EditorSession.EditPlacement"/>, and nothing else. Time
    /// edits, opacity, mask, surround and undo all still apply. Framework-free: the gizmo's own
    /// outline and declined presses are Avalonia-bound and left to the E2E run.
    /// </summary>
    [Collection("EditorSession clipboard")] // the paste tests share its one static clipboard
    public class PlacementLockTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>One clip of each kind the lock speaks to, on a row of its own: a screen recording
        /// in two segments (a feed the write fans out over), a webcam, a text card, a rectangle, an
        /// arrow, cursor and keys rows and an audio clip.</summary>
        private sealed class Fixture
        {
            public EditorSession Session { get; }
            public SelectedItemViewModel Vm { get; }
            public Item Screen { get; }
            public Item ScreenB { get; }
            public Item Webcam { get; }
            public Item Audio { get; }
            public Item Text { get; }
            public Item Rectangle { get; }
            public Item Arrow { get; }
            public Item Cursor { get; }
            public Item Keys { get; }

            public Fixture()
            {
                var sourceId = Guid.NewGuid();
                var group = Guid.NewGuid();
                var screenRow = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0, GroupId = group };
                var webcamRow = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Webcam", Order = 1, GroupId = group };
                var audioRow = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 2, GroupId = group };

                Item Media(Track row, int stream, long start, long length) => new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = row.Id,
                    TimelineStartTicks = start,
                    DurationTicks = length,
                    Content = new MediaContent { SourceId = sourceId, StreamIndex = stream, SourceInTicks = start },
                };

                var screen = Media(screenRow, 0, 0, Ms(8_000));
                var screenB = Media(screenRow, 0, Ms(8_000), Ms(12_000));
                var webcam = Media(webcamRow, 1, 0, Ms(20_000));
                var audio = Media(audioRow, 2, 0, Ms(20_000));

                var project = new Project
                {
                    Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                    Sources =
                    {
                        new Source
                        {
                            Id = sourceId,
                            Path = @"C:\rec\input.mp4",
                            InputCapturePath = TestPath.ExistingCapture,
                            Streams =
                            {
                                new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                                new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = Ms(60_000) },
                                new SourceStream { Index = 2, Kind = StreamKind.Audio, DurationTicks = Ms(60_000) },
                            },
                        },
                    },
                    Tracks = { screenRow, webcamRow, audioRow },
                    Items = { screen, screenB, webcam, audio },
                };

                Session = new EditorSession(project, null, null);
                Vm = new SelectedItemViewModel { Session = Session };
                Screen = screen;
                ScreenB = screenB;
                Webcam = webcam;
                Audio = audio;
                Text = Session.AddText(0, Ms(5_000));
                Rectangle = Session.AddShape(0, Ms(5_000));
                Arrow = Session.AddShape(0, Ms(5_000), ShapeKind.Arrow);
                Cursor = Session.AddCursorTrack();
                Keys = Session.AddKeyboardTrack();
            }

            public Item Live(Item item) => Session.Project.Items.Single(i => i.Id == item.Id);

            public Track RowOf(Item item) => Session.Project.TrackOf(Live(item));

            public void Lock(Item item) => Session.SetTrackPlacementLocked(item.TrackId, true);
        }

        /// <summary>Counts everything a refused write must not raise, from here on.</summary>
        private sealed class Quiet
        {
            private int _projectChanged, _history;
            private readonly EditorSession _session;
            private readonly string _before;
            private readonly bool _canRedo;

            public Quiet(EditorSession session)
            {
                _session = session;
                _before = session.Project.ToJson();
                _canRedo = session.CanRedo;
                session.ProjectChanged += (_, _) => _projectChanged++;
                session.HistoryChanged += (_, _) => _history++;
            }

            public void AssertNothingHappened(string name)
            {
                Assert.True(_before == _session.Project.ToJson(), $"{name}: the project changed.");
                Assert.True(_projectChanged == 0, $"{name}: raised ProjectChanged.");
                Assert.True(_history == 0, $"{name}: pushed an undo entry.");
                Assert.Equal(_canRedo, _session.CanRedo);
            }
        }

        // ---------------------------------------------------------------------------- the session

        [Fact]
        public void EditPlacement_on_a_locked_row_refuses_without_running_the_edit()
        {
            var fx = new Fixture();
            fx.Lock(fx.Screen);
            var quiet = new Quiet(fx.Session);
            var ran = false;

            var accepted = fx.Session.EditPlacement(fx.Screen.Id, _ => ran = true, "x", null);

            Assert.False(accepted);
            Assert.False(ran);
            quiet.AssertNothingHappened("EditPlacement");
        }

        [Fact]
        public void EditPlacement_on_an_unlocked_row_writes_the_whole_feed_as_one_undo_entry()
        {
            var fx = new Fixture();
            fx.Lock(fx.Webcam); // another row's lock is not this row's
            var before = fx.Session.Project.ToJson();

            var accepted = fx.Session.EditPlacement(fx.Screen.Id, i => i.Transform.X = 0.25, "x", null);

            Assert.True(accepted);
            Assert.Equal(0.25, fx.Live(fx.Screen).Transform.X);
            Assert.Equal(0.25, fx.Live(fx.ScreenB).Transform.X);
            Assert.NotEqual(0.25, fx.Live(fx.Webcam).Transform.X);

            fx.Session.Undo();
            Assert.Equal(before, fx.Session.Project.ToJson());
        }

        [Fact]
        public void Unlocking_lets_placement_through_again()
        {
            var fx = new Fixture();
            fx.Lock(fx.Screen);
            Assert.False(fx.Session.EditPlacement(fx.Screen.Id, i => i.Transform.X = 0.25, "x", null));

            fx.Session.SetTrackPlacementLocked(fx.Screen.TrackId, false);

            Assert.True(fx.Session.EditPlacement(fx.Screen.Id, i => i.Transform.X = 0.25, "x", null));
            Assert.Equal(0.25, fx.Live(fx.Screen).Transform.X);
        }

        [Fact]
        public void EditPlacement_of_an_item_not_in_the_project_throws_like_any_other_edit()
        {
            var fx = new Fixture();
            Assert.ThrowsAny<Exception>(() => fx.Session.EditPlacement(Guid.NewGuid(), _ => { }, "x", null));
        }

        [Fact]
        public void A_lock_edit_is_mapping_and_one_undo_entry_and_setting_the_same_value_is_nothing()
        {
            var fx = new Fixture();
            var kinds = new List<ProjectChangeKind>();
            var origins = new List<object>();
            fx.Session.ProjectChanged += (_, e) =>
            {
                kinds.Add(e.Kind);
                origins.Add(e.Origin);
            };
            var before = fx.Session.Project.ToJson();
            var origin = new object();

            fx.Session.SetTrackPlacementLocked(fx.Screen.TrackId, true, origin);

            Assert.True(fx.RowOf(fx.Screen).PlacementLocked);
            Assert.Equal(new[] { ProjectChangeKind.Mapping }, kinds);
            Assert.Same(origin, origins.Single());

            var quiet = new Quiet(fx.Session);
            fx.Session.SetTrackPlacementLocked(fx.Screen.TrackId, true);
            quiet.AssertNothingHappened("Locking a locked row");

            fx.Session.Undo();
            Assert.Equal(before, fx.Session.Project.ToJson());
            Assert.False(fx.RowOf(fx.Screen).PlacementLocked);
        }

        [Fact]
        public void The_lock_survives_a_save_and_an_old_Locked_flag_does_not_become_it()
        {
            var fx = new Fixture();
            fx.Lock(fx.Screen);
            var json = fx.Session.Project.ToJson();

            var reloaded = Project.FromJson(json);
            Assert.True(reloaded.Tracks.Single(t => t.Id == fx.Screen.TrackId).PlacementLocked);
            Assert.False(reloaded.Tracks.Single(t => t.Id == fx.Webcam.TrackId).PlacementLocked);

            // the old flag meant no timeline edits at all, the opposite of this one
            var legacy = Project.FromJson(json.Replace("\"PlacementLocked\": true", "\"Locked\": true"));
            Assert.DoesNotContain(legacy.Tracks, t => t.PlacementLocked);
        }

        /// <summary>I7: undo restores history, so the older move comes back, and a redo puts it and
        /// the lock back on top of each other.</summary>
        [Fact]
        public void Undo_and_redo_reach_placement_a_lock_would_refuse()
        {
            var fx = new Fixture();
            var original = fx.Live(fx.Screen).Transform.X;
            Assert.True(fx.Session.EditPlacement(fx.Screen.Id, i => i.Transform.X = 0.25, "x", null));
            fx.Lock(fx.Screen);

            fx.Session.Undo();
            fx.Session.Undo();
            Assert.Equal(original, fx.Live(fx.Screen).Transform.X);
            Assert.False(fx.RowOf(fx.Screen).PlacementLocked);

            fx.Session.Redo();
            fx.Session.Redo();
            Assert.Equal(0.25, fx.Live(fx.Screen).Transform.X);
            Assert.True(fx.RowOf(fx.Screen).PlacementLocked);
        }

        /// <summary>The lock is about the canvas: every edit in time still applies, the row's order
        /// too, and none of them touches where the clip sits.</summary>
        [Fact]
        public void Time_edits_on_a_locked_row_all_apply_and_keep_the_placement()
        {
            var cases = new (string Name, Action<Fixture> Act)[]
            {
                ("Time-linked move", fx => Assert.NotEqual(0, fx.Session.MoveLinked(fx.ScreenB.Id, Ms(1_000)))),
                ("Alt move", fx => Assert.NotEqual(0, fx.Session.MoveItems(new[] { fx.ScreenB.Id }, Ms(1_000)))),
                ("Trim start", fx => fx.Session.TrimItemStart(fx.Screen.Id, Ms(500))),
                ("Trim end", fx => fx.Session.TrimItemEnd(fx.ScreenB.Id, -Ms(500))),
                ("Split", fx => Assert.True(fx.Session.SplitItemAt(fx.Screen.Id, Ms(4_000)))),
                ("Delete", fx => fx.Session.DeleteItems(new[] { fx.ScreenB.Id })),
                ("Row order", fx => Assert.True(fx.Session.MoveTrackLayer(fx.Screen.TrackId, towardsFront: true))),
            };

            foreach (var (name, act) in cases)
            {
                var fx = new Fixture();
                Assert.True(fx.Session.EditPlacement(fx.Screen.Id, i => i.Transform.X = 0.25, "x", null));
                fx.Lock(fx.Screen);
                var before = fx.Session.Project.ToJson();

                act(fx);

                Assert.True(before != fx.Session.Project.ToJson(), $"{name} did nothing on a locked row.");
                Assert.All(fx.Session.Project.Items.Where(i => i.TrackId == fx.Screen.TrackId),
                    i => Assert.Equal(0.25, i.Transform.X));
            }
        }

        [Fact]
        public void A_time_linked_drag_brings_the_clip_on_a_locked_row()
        {
            var fx = new Fixture();
            fx.Lock(fx.Webcam);
            var start = fx.Live(fx.Webcam).TimelineStartTicks;

            Assert.Equal(Ms(1_000), fx.Session.MoveLinked(fx.Screen.Id, Ms(1_000)));

            Assert.Equal(start + Ms(1_000), fx.Live(fx.Webcam).TimelineStartTicks);
        }

        // ---------------------------------------------------------------------------- copies

        /// <summary>Paste is time placement, and each clip keeps the transform it was copied with.</summary>
        [Fact]
        public void A_paste_at_a_free_time_on_a_locked_row_reuses_it_and_keeps_its_own_transform()
        {
            var fx = new Fixture();
            Assert.True(fx.Session.EditPlacement(fx.Text.Id, i => (i.Transform ??= new ModelTransform()).X = 0.25, "x", null));
            Assert.True(fx.Session.CopyItems(new[] { fx.Text.Id }));
            fx.Lock(fx.Text);

            var pasted = fx.Session.PasteAt(Ms(30_000));

            var clip = fx.Session.Project.Items.Single(i => i.Id == pasted.Single());
            Assert.Equal(fx.Text.TrackId, clip.TrackId);
            Assert.Equal(0.25, clip.Transform.X);
            Assert.True(fx.RowOf(clip).PlacementLocked);
        }

        [Fact]
        public void A_copied_row_carries_the_lock_on_paste_and_on_duplicate()
        {
            var fx = new Fixture();
            fx.Lock(fx.Text);
            Assert.True(fx.Session.CopyItems(new[] { fx.Text.Id }));

            var pasted = fx.Session.PasteAt(fx.Live(fx.Text).TimelineStartTicks); // occupied: a new row
            var pastedRow = fx.Session.Project.TrackOf(fx.Session.Project.Items.Single(i => i.Id == pasted.Single()));
            Assert.NotEqual(fx.Text.TrackId, pastedRow.Id);
            Assert.True(pastedRow.PlacementLocked);

            var rows = fx.Session.Project.Tracks.Select(t => t.Id).ToHashSet();
            Assert.True(fx.Session.DuplicateTrack(fx.Text.TrackId));
            Assert.True(fx.Session.Project.Tracks.Single(t => !rows.Contains(t.Id)).PlacementLocked);
        }

        // ------------------------------------------------------------------------- the inspector

        /// <summary>Every setter that says where a clip sits, each proving on an unlocked row that it
        /// does write, so a stale case cannot pass by doing nothing.</summary>
        private static readonly (string Name, Func<Fixture, Item> Target, Action<Fixture> Setup,
            Action<SelectedItemViewModel> Write, Func<SelectedItemViewModel, object> Read, string Property)[] PlacementWriters =
        {
            ("PositionX", fx => fx.Screen, null, vm => vm.PositionX = 0.2, vm => vm.PositionX, nameof(SelectedItemViewModel.PositionX)),
            ("PositionY", fx => fx.Screen, null, vm => vm.PositionY = 0.2, vm => vm.PositionY, nameof(SelectedItemViewModel.PositionY)),
            ("Scale", fx => fx.Screen, null, vm => vm.Scale = 0.2, vm => vm.Scale, nameof(SelectedItemViewModel.Scale)),
            ("ScaleHeight", fx => fx.Rectangle, null, vm => vm.ScaleHeight = 0.2, vm => vm.ScaleHeight, nameof(SelectedItemViewModel.ScaleHeight)),
            ("Rotation", fx => fx.Screen, null, vm => vm.Rotation = 20, vm => vm.Rotation, nameof(SelectedItemViewModel.Rotation)),
            ("FontSize", fx => fx.Text, null, vm => vm.FontSize = 200, vm => vm.FontSize, nameof(SelectedItemViewModel.FontSize)),
            ("Line center", fx => fx.Arrow, null, vm => vm.PositionX = 0.2, vm => vm.PositionX, nameof(SelectedItemViewModel.PositionX)),
            ("Keys position", fx => fx.Keys, null, vm => vm.PositionX = 0.2, vm => vm.PositionX, nameof(SelectedItemViewModel.PositionX)),
            ("Keys width", fx => fx.Keys, null, vm => vm.Scale = 0.2, vm => vm.Scale, nameof(SelectedItemViewModel.Scale)),
            ("CropLeft", fx => fx.Screen, null, vm => vm.CropLeft = 0.1, vm => vm.CropLeft, nameof(SelectedItemViewModel.CropLeft)),
            ("CropTop", fx => fx.Screen, null, vm => vm.CropTop = 0.1, vm => vm.CropTop, nameof(SelectedItemViewModel.CropTop)),
            ("CropRight", fx => fx.Screen, null, vm => vm.CropRight = 0.1, vm => vm.CropRight, nameof(SelectedItemViewModel.CropRight)),
            ("CropBottom", fx => fx.Screen, null, vm => vm.CropBottom = 0.1, vm => vm.CropBottom, nameof(SelectedItemViewModel.CropBottom)),
            ("CropTotal reset", fx => fx.Screen, fx => fx.Vm.CropLeft = 0.1, vm => vm.CropTotal = 0, vm => vm.CropLeft, nameof(SelectedItemViewModel.CropTotal)),
            ("Aspect 16:9", fx => fx.Webcam, null, vm => vm.Aspect169 = true, vm => vm.Aspect169, nameof(SelectedItemViewModel.Aspect169)),
            ("Aspect 1:1", fx => fx.Webcam, null, vm => vm.Aspect11 = true, vm => vm.Aspect11, nameof(SelectedItemViewModel.Aspect11)),
            ("Aspect 4:5", fx => fx.Webcam, null, vm => vm.Aspect45 = true, vm => vm.Aspect45, nameof(SelectedItemViewModel.Aspect45)),
            ("Aspect 3:2", fx => fx.Webcam, null, vm => vm.Aspect32 = true, vm => vm.Aspect32, nameof(SelectedItemViewModel.Aspect32)),
            ("Aspect 4:3", fx => fx.Screen, null, vm => vm.Aspect43 = true, vm => vm.Aspect43, nameof(SelectedItemViewModel.Aspect43)),
            ("Aspect custom", fx => fx.Webcam, null, vm => vm.AspectCustom = true, vm => vm.AspectCustom, nameof(SelectedItemViewModel.AspectCustom)),
            ("Aspect unlocked", fx => fx.Webcam, null, vm => vm.AspectUnlocked = true, vm => vm.AspectUnlocked, nameof(SelectedItemViewModel.AspectUnlocked)),
            ("Aspect original", fx => fx.Webcam, fx => fx.Vm.Aspect11 = true, vm => vm.AspectOriginal = true, vm => vm.AspectOriginal, nameof(SelectedItemViewModel.AspectOriginal)),
            ("Stretch", fx => fx.Webcam, fx => fx.Vm.Aspect11 = true, vm => vm.AspectStretch = true, vm => vm.AspectStretch, nameof(SelectedItemViewModel.AspectStretch)),
            ("Fill", fx => fx.Webcam, fx =>
            {
                fx.Vm.Aspect11 = true;
                fx.Vm.AspectStretch = true;
            }, vm => vm.AspectFill = true, vm => vm.AspectFill, nameof(SelectedItemViewModel.AspectFill)),
            ("Custom ratio W", fx => fx.Webcam, fx => fx.Vm.AspectCustom = true, vm => vm.CustomAspectW = 7, vm => vm.CustomAspectW, nameof(SelectedItemViewModel.CustomAspectW)),
            ("Custom ratio H", fx => fx.Webcam, fx => fx.Vm.AspectCustom = true, vm => vm.CustomAspectH = 7, vm => vm.CustomAspectH, nameof(SelectedItemViewModel.CustomAspectH)),
            ("Shape kind", fx => fx.Rectangle, null,
                vm => vm.ShapeKindChoice = SelectedItemViewModel.ShapeKindOptions.Single(o => o.Value == ShapeKind.Arrow),
                vm => vm.ShapeKindChoice.Value, nameof(SelectedItemViewModel.ShapeKindChoice)),
            ("Line kind", fx => fx.Arrow, null,
                vm => vm.ShapeKindChoice = SelectedItemViewModel.ShapeKindOptions.Single(o => o.Value == ShapeKind.Rectangle),
                vm => vm.ShapeKindChoice.Value, nameof(SelectedItemViewModel.ShapeKindChoice)),
        };

        public static IEnumerable<object[]> PlacementWriterNames() => PlacementWriters.Select(w => new object[] { w.Name });

        [Theory]
        [MemberData(nameof(PlacementWriterNames))]
        public void A_placement_setter_writes_on_an_unlocked_row(string name)
        {
            var writer = PlacementWriters.Single(w => w.Name == name);
            var fx = new Fixture();
            fx.Session.Select(writer.Target(fx).Id);
            writer.Setup?.Invoke(fx);
            var before = fx.Session.Project.ToJson();

            writer.Write(fx.Vm);

            Assert.NotEqual(before, fx.Session.Project.ToJson());
        }

        [Theory]
        [MemberData(nameof(PlacementWriterNames))]
        public void A_placement_setter_is_refused_on_a_locked_row_and_shows_the_model_again(string name)
        {
            var writer = PlacementWriters.Single(w => w.Name == name);
            var fx = new Fixture();
            var target = writer.Target(fx);
            fx.Session.Select(target.Id);
            writer.Setup?.Invoke(fx);
            fx.Lock(target);
            var shown = writer.Read(fx.Vm);
            var raised = new List<string>();
            fx.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var quiet = new Quiet(fx.Session);

            writer.Write(fx.Vm);

            quiet.AssertNothingHappened(name);
            Assert.Equal(shown, writer.Read(fx.Vm));
            Assert.True(raised.Contains(writer.Property), $"{name}: {writer.Property} was never told to move back, only: {String.Join(", ", raised)}");
        }

        /// <summary>A spinner reads the value it was handed until told otherwise, so the refusal has
        /// to raise the very property the setter changed.</summary>
        [Fact]
        public void A_refused_spinner_write_raises_its_own_property_with_the_old_value()
        {
            var fx = new Fixture();
            fx.Session.Select(fx.Screen.Id);
            fx.Lock(fx.Screen);
            var original = fx.Vm.PositionX;
            var last = Double.NaN;
            fx.Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SelectedItemViewModel.PositionX))
                    last = fx.Vm.PositionX;
            };

            fx.Vm.PositionX = original + 0.1;

            Assert.Equal(original, last);
        }

        /// <summary>The model holds only the ratio, so the two custom spinners and their tile cannot
        /// be read back from it after the fact.</summary>
        [Fact]
        public void A_refused_custom_ratio_keeps_its_tile_and_its_numbers()
        {
            var fx = new Fixture();
            fx.Session.Select(fx.Webcam.Id);
            fx.Vm.AspectCustom = true;
            fx.Lock(fx.Webcam);
            var (w, h) = (fx.Vm.CustomAspectW, fx.Vm.CustomAspectH);

            fx.Vm.CustomAspectW = 7;
            fx.Vm.CustomAspectH = 7;

            Assert.True(fx.Vm.AspectCustom);
            Assert.False(fx.Vm.Aspect169);
            Assert.Equal((w, h), (fx.Vm.CustomAspectW, fx.Vm.CustomAspectH));
        }

        /// <summary>The kind picker keeps its selection without an echo, and the value it would
        /// re-read has not changed, so only an explicit raise moves it back.</summary>
        [Fact]
        public void A_refused_kind_pick_raises_the_picker()
        {
            var fx = new Fixture();
            fx.Session.Select(fx.Rectangle.Id);
            fx.Lock(fx.Rectangle);
            var raised = new List<string>();
            fx.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            fx.Vm.ShapeKindChoice = SelectedItemViewModel.ShapeKindOptions.Single(o => o.Value == ShapeKind.Arrow);

            Assert.Contains(nameof(SelectedItemViewModel.ShapeKindChoice), raised);
            Assert.Equal(ShapeKind.Rectangle, fx.Vm.ShapeKindChoice.Value);
        }

        /// <summary>Everything that is not where the clip sits stays editable.</summary>
        private static readonly (string Name, Func<Fixture, Item> Target, Action<SelectedItemViewModel> Write)[] AllowedWriters =
        {
            ("Opacity", fx => fx.Screen, vm => vm.Opacity = 0.5),
            ("Mask", fx => fx.Screen, vm => vm.MaskCircle = true),
            ("Corner radius", fx => fx.Screen, vm =>
            {
                vm.MaskRounded = true;
                vm.CornerRadius = 0.3;
            }),
            ("Surround", fx => fx.Screen, vm => vm.SurroundShadow = true),
            ("Effect", fx => fx.Webcam, vm => vm.EffectBlur = true),
            ("Hide", fx => fx.Screen, vm => vm.TrackHidden = true),
            ("Text colour", fx => fx.Text, vm => vm.TextColorHex = "#FF102030"),
            ("Text words", fx => fx.Text, vm => vm.Text = "Other"),
            ("Keys font size", fx => fx.Keys, vm => vm.KeyboardFontSize = 91),
            ("Line stroke", fx => fx.Arrow, vm => vm.ShapeStrokeWidth = 21),
            ("Shape colour", fx => fx.Rectangle, vm => vm.ShapeStrokeColorHex = "#FF102030"),
            ("Entry transition", fx => fx.Screen, vm => vm.EntryKind = TransitionKind.Fade),
        };

        public static IEnumerable<object[]> AllowedWriterNames() => AllowedWriters.Select(w => new object[] { w.Name });

        [Theory]
        [MemberData(nameof(AllowedWriterNames))]
        public void What_is_not_placement_still_writes_on_a_locked_row(string name)
        {
            var writer = AllowedWriters.Single(w => w.Name == name);
            var fx = new Fixture();
            var target = writer.Target(fx);
            fx.Session.Select(target.Id);
            fx.Lock(target);
            var before = fx.Session.Project.ToJson();

            writer.Write(fx.Vm);

            Assert.NotEqual(before, fx.Session.Project.ToJson());
            Assert.True(fx.RowOf(target).PlacementLocked);
        }

        [Fact]
        public void Opacity_on_a_locked_row_reaches_the_whole_feed_and_leaves_the_placement_rows_disabled()
        {
            var fx = new Fixture();
            fx.Session.Select(fx.Screen.Id);
            fx.Lock(fx.Screen);

            fx.Vm.Opacity = 0.5;

            Assert.Equal(0.5, fx.Live(fx.Screen).Transform.Opacity);
            Assert.Equal(0.5, fx.Live(fx.ScreenB).Transform.Opacity);
            Assert.False(fx.Vm.PlacementEditable);
        }

        /// <summary>The stroke of a line re-fits the box around the same ink, so the endpoints do not
        /// move.</summary>
        [Fact]
        public void A_locked_lines_stroke_keeps_its_endpoints()
        {
            var fx = new Fixture();
            fx.Session.Select(fx.Arrow.Id);
            fx.Lock(fx.Arrow);
            ShapeLine Line()
            {
                var item = fx.Live(fx.Arrow);
                Assert.True(ShapeLineLayout.TryResolve(item.Transform, (ShapeContent)item.Content, 1920, 1080, out var line));
                return line;
            }

            var before = Line();

            fx.Vm.ShapeStrokeWidth = 21;

            var after = Line();
            Assert.Equal(before.Start.X, after.Start.X, 6);
            Assert.Equal(before.Start.Y, after.Start.Y, 6);
            Assert.Equal(before.End.X, after.End.X, 6);
            Assert.Equal(before.End.Y, after.End.Y, 6);
            Assert.Equal(21, ((ShapeContent)fx.Live(fx.Arrow).Content).StrokeWidth);
        }

        // ------------------------------------------------------------------------- the TRACK row

        [Fact]
        public void The_lock_button_is_offered_on_rows_with_a_placement_only()
        {
            var fx = new Fixture();
            Assert.False(fx.Vm.ShowPlacementLock); // nothing selected

            foreach (var item in new[] { fx.Screen, fx.Webcam, fx.Text, fx.Rectangle, fx.Arrow, fx.Keys })
            {
                fx.Session.Select(item.Id);
                Assert.True(fx.Vm.ShowPlacementLock);
            }

            var zoom = fx.Session.AddZoomEffect(0, Ms(1_000));
            foreach (var id in new[] { fx.Audio.Id, fx.Cursor.Id, zoom.Id })
            {
                fx.Session.Select(id);
                Assert.False(fx.Vm.ShowPlacementLock);
            }

            fx.Session.SelectMany(new[] { fx.Screen.Id, fx.Webcam.Id });
            Assert.False(fx.Vm.ShowPlacementLock);
        }

        [Fact]
        public void The_lock_button_writes_the_row_and_its_label_and_the_editable_flag_follow()
        {
            var fx = new Fixture();
            fx.Session.Select(fx.Screen.Id);
            Assert.Equal("Lock Placement", fx.Vm.PlacementLockLabel);
            Assert.True(fx.Vm.PlacementEditable);
            var raised = new List<string>();
            fx.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            fx.Vm.PlacementLocked = true;

            Assert.True(fx.RowOf(fx.Screen).PlacementLocked);
            Assert.False(fx.RowOf(fx.Webcam).PlacementLocked);
            Assert.True(fx.Vm.PlacementLocked);
            Assert.False(fx.Vm.PlacementEditable);
            Assert.Equal("Unlock Placement", fx.Vm.PlacementLockLabel);
            Assert.Contains(nameof(SelectedItemViewModel.PlacementEditable), raised);
            Assert.Contains(nameof(SelectedItemViewModel.PlacementLockLabel), raised);

            fx.Vm.PlacementLocked = false;

            Assert.False(fx.RowOf(fx.Screen).PlacementLocked);
            Assert.True(fx.Vm.PlacementEditable);
            Assert.Equal("Lock Placement", fx.Vm.PlacementLockLabel);
        }

        [Fact]
        public void The_lock_state_follows_the_selection_and_undo()
        {
            var fx = new Fixture();
            fx.Lock(fx.Screen);

            fx.Session.Select(fx.Screen.Id);
            Assert.True(fx.Vm.PlacementLocked);

            fx.Session.Select(fx.Webcam.Id);
            Assert.False(fx.Vm.PlacementLocked);
            Assert.True(fx.Vm.PlacementEditable);

            fx.Session.Select(fx.ScreenB.Id); // the same row from another clip
            Assert.True(fx.Vm.PlacementLocked);

            fx.Session.Undo();
            Assert.False(fx.Vm.PlacementLocked);
            Assert.True(fx.Vm.PlacementEditable);

            fx.Session.Redo();
            Assert.True(fx.Vm.PlacementLocked);
            Assert.False(fx.Vm.PlacementEditable);
        }

        [Fact]
        public void Locking_ends_crop_mode_and_crop_draw()
        {
            var fx = new Fixture();
            fx.Session.Select(fx.Screen.Id);
            fx.Vm.CropModeActive = true;

            fx.Vm.PlacementLocked = true;
            Assert.False(fx.Vm.CropModeActive);

            fx.Vm.PlacementLocked = false;
            fx.Vm.CropDrawActive = true;
            Assert.True(fx.Vm.CropDrawActive);

            fx.Vm.PlacementLocked = true;
            Assert.False(fx.Vm.CropDrawActive);
        }

        [Fact]
        public void An_undo_landing_on_a_locked_row_ends_crop_draw()
        {
            var fx = new Fixture();
            fx.Session.Select(fx.Screen.Id);
            fx.Lock(fx.Screen);
            fx.Session.SetTrackPlacementLocked(fx.Screen.TrackId, false);
            fx.Vm.CropDrawActive = true;

            fx.Session.Undo();

            Assert.False(fx.Vm.CropDrawActive);
        }

        [Fact]
        public void Crop_draw_will_not_start_on_a_locked_row_and_the_toggle_is_told_so()
        {
            var fx = new Fixture();
            fx.Session.Select(fx.Screen.Id);
            fx.Lock(fx.Screen);
            var raised = new List<string>();
            fx.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            fx.Vm.CropDrawActive = true;

            Assert.False(fx.Vm.CropDrawActive);
            Assert.Contains(nameof(SelectedItemViewModel.CropDrawActive), raised);
        }

        // ------------------------------------------------------------------------ the line handles

        private static Project OneLine(bool locked, out Item line)
        {
            var row = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Line", PlacementLocked = locked };
            var shape = new ShapeContent { Kind = ShapeKind.Arrow, StrokeWidth = 6 };
            line = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = row.Id,
                TimelineStartTicks = 0,
                DurationTicks = Ms(5_000),
                Content = shape,
                Transform = new ModelTransform(),
            };
            ShapeLineLayout.Write(line.Transform, shape, new ShapeLine(new Vec2(100, 100), new Vec2(800, 400), 0, 0), 960, 540, 0.5);
            return new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Tracks = { row },
                Items = { line },
            };
        }

        [Fact]
        public void A_locked_line_declines_every_press_a_free_one_takes_and_is_still_selected_by_a_click()
        {
            var points = new (Vec2 Point, ShapeHandlePart Part)[]
            {
                (new Vec2(100, 100), ShapeHandlePart.Start),
                (new Vec2(800, 400), ShapeHandlePart.End),
                (new Vec2(450, 250), ShapeHandlePart.Bend),
                (new Vec2(275, 175), ShapeHandlePart.Body),
            };

            var free = OneLine(false, out var freeLine);
            var locked = OneLine(true, out var lockedLine);
            foreach (var (point, part) in points)
            {
                Assert.Equal(part, ShapeHandleMath.ResolvePress(free, freeLine, 0, point, 960, 540));
                Assert.Equal(ShapeHandlePart.None, ShapeHandleMath.ResolvePress(locked, lockedLine, 0, point, 960, 540));
            }

            // a press the line declines reaches the preview, which selects by the same ink
            Assert.Equal(lockedLine.Id, ItemPlacement.HitTest(locked, 0, 275, 175, 960, 540)?.Id);
        }

        /// <summary><see cref="ShapeHandleDrag.Apply"/>, passed to <see cref="EditorSession.EditPlacement"/>
        /// the way <see cref="ShapeHandlesControl"/> passes it, writes until the row locks and nothing
        /// after. That the control does route through it is for the E2E run (scenario 15).</summary>
        [Fact]
        public void A_handle_drag_passed_to_EditPlacement_is_refused_once_its_row_locks()
        {
            var session = new EditorSession(OneLine(false, out var line), null, null);
            var drag = ShapeHandleDrag.Begin(line, ShapeHandlePart.End, new Vec2(800, 400), 960, 540, 0.5);
            var before = session.Project.ToJson();
            Assert.True(session.EditPlacement(line.Id, i => drag.Apply(i, new Vec2(700, 300), false), "handles", null));
            Assert.NotEqual(before, session.Project.ToJson());

            session.SetTrackPlacementLocked(line.TrackId, true);
            var locked = session.Project.ToJson();

            Assert.False(session.EditPlacement(line.Id, i => drag.Apply(i, new Vec2(100, 500), false), "handles", null));
            Assert.Equal(locked, session.Project.ToJson());
        }
    }
}
