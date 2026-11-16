using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    public class ProjectModelTests
    {
        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        /// <summary>A fully-populated project exercising every content type, both transitions,
        /// crop, mask and a track group — the JSON round-trip must preserve all of it. Ids are
        /// fixed so two builds of this project serialize identically.</summary>
        private static Project FullProject()
        {
            var sourceId = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
            var videoTrack = new Guid("bbbbbbbb-0000-0000-0000-000000000001");
            var overlayTrack = new Guid("bbbbbbbb-0000-0000-0000-000000000002");
            var audioTrack = new Guid("bbbbbbbb-0000-0000-0000-000000000003");
            var linkGroup = new Guid("cccccccc-0000-0000-0000-000000000001");

            return new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30000, FpsDen = 1001, SampleRate = 48000 },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = @"C:\rec\input.mp4",
                        Streams =
                        {
                            new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, StartTimeTicks = 0, DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480, AvgFrameRateNum = 30000, AvgFrameRateDen = 1001, IsVariableFrameRate = true, StartTimeTicks = Ms(4), DurationTicks = Ms(60_000) },
                            new SourceStream { Index = 2, Kind = StreamKind.Audio, DurationTicks = Ms(60_000) },
                        },
                    },
                },
                Tracks =
                {
                    new Track { Id = videoTrack, Kind = TrackKind.Video, Name = "Screen", Order = 0, GroupId = linkGroup },
                    new Track { Id = overlayTrack, Kind = TrackKind.Video, Name = "Webcam", Order = 1, Hidden = false, GroupId = linkGroup },
                    new Track { Id = audioTrack, Kind = TrackKind.Audio, Name = "Audio", Order = 2, Muted = true },
                },
                Items =
                {
                    new Item
                    {
                        Id = new Guid("dddddddd-0000-0000-0000-000000000001"),
                        TrackId = videoTrack,
                        TimelineStartTicks = 0,
                        DurationTicks = Ms(10_000),
                        Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = Ms(2_500) },
                        Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = Ms(300), Easing = TransitionEasing.CubicOut },
                        Exit = new Transition { Kind = TransitionKind.SlideLeft, DurationTicks = Ms(500), Easing = TransitionEasing.CubicInOut },
                    },
                    new Item
                    {
                        Id = new Guid("dddddddd-0000-0000-0000-000000000002"),
                        TrackId = overlayTrack,
                        TimelineStartTicks = 0,
                        DurationTicks = Ms(10_000),
                        Content = new MediaContent { SourceId = sourceId, StreamIndex = 1, SourceInTicks = Ms(2_500) },
                        // the v1 webcam PiP, expressed exactly: center 0.82/0.78, width 0.2,
                        // rounded-rect mask with 0.25-height corners, plus a crop for good measure.
                        Transform = new Transform
                        {
                            X = 0.82,
                            Y = 0.78,
                            Scale = 0.2,
                            Rotation = 15,
                            Opacity = 0.9,
                            Crop = new CropRect { Left = 0.1, Top = 0.05, Right = 0.1, Bottom = 0.05 },
                            Mask = new Mask { Shape = MaskShape.RoundedRect, CornerRadius = 0.25 },
                        },
                    },
                    new Item
                    {
                        Id = new Guid("dddddddd-0000-0000-0000-000000000003"),
                        TrackId = audioTrack,
                        TimelineStartTicks = 0,
                        DurationTicks = Ms(10_000),
                        Content = new MediaContent { SourceId = sourceId, StreamIndex = 2, SourceInTicks = Ms(2_500) },
                        Volume = 0.75,
                    },
                    new Item
                    {
                        Id = new Guid("dddddddd-0000-0000-0000-000000000004"),
                        TrackId = overlayTrack,
                        TimelineStartTicks = Ms(10_000),
                        DurationTicks = Ms(3_000),
                        Content = new TextContent { Text = "Hello", Font = "Segoe UI", Size = 64, Color = "#FFFFFFFF", Align = TextAlign.Center },
                    },
                    new Item
                    {
                        Id = new Guid("dddddddd-0000-0000-0000-000000000005"),
                        TrackId = overlayTrack,
                        TimelineStartTicks = Ms(13_000),
                        DurationTicks = Ms(3_000),
                        Content = new ImageContent { Path = @"C:\rec\logo.png" },
                    },
                    new Item
                    {
                        Id = new Guid("dddddddd-0000-0000-0000-000000000006"),
                        TrackId = videoTrack,
                        TimelineStartTicks = Ms(10_000),
                        DurationTicks = Ms(6_000),
                        Content = new SolidContent { Color = "#FF102030" },
                    },
                },
            };
        }

        [Fact]
        public void Full_project_round_trips_losslessly()
        {
            var project = FullProject();

            var json = project.ToJson();
            var restored = Project.FromJson(json);

            // serialize → deserialize → serialize is the lossless-ness proof: any dropped or
            // defaulted field shows up as a diff in the second pass.
            Assert.Equal(json, restored.ToJson());
            Assert.Empty(restored.Validate());
        }

        [Fact]
        public void Round_trip_preserves_every_content_type()
        {
            var restored = Project.FromJson(FullProject().ToJson());

            var contents = restored.Items.Select(i => i.Content).ToList();
            Assert.Equal(3, contents.OfType<MediaContent>().Count());
            var media = (MediaContent)restored.Items[0].Content;
            Assert.Equal(0, media.StreamIndex);
            Assert.Equal(Ms(2_500), media.SourceInTicks);

            var text = Assert.Single(contents.OfType<TextContent>());
            Assert.Equal("Hello", text.Text);
            Assert.Equal(TextAlign.Center, text.Align);

            var image = Assert.Single(contents.OfType<ImageContent>());
            Assert.Equal(@"C:\rec\logo.png", image.Path);

            var solid = Assert.Single(contents.OfType<SolidContent>());
            Assert.Equal("#FF102030", solid.Color);
        }

        [Fact]
        public void Round_trip_preserves_transform_mask_and_transitions()
        {
            var restored = Project.FromJson(FullProject().ToJson());

            var webcam = restored.Items[1];
            Assert.Equal(0.82, webcam.Transform.X);
            Assert.Equal(0.78, webcam.Transform.Y);
            Assert.Equal(0.2, webcam.Transform.Scale);
            Assert.Equal(15, webcam.Transform.Rotation);
            Assert.Equal(0.9, webcam.Transform.Opacity);
            Assert.Equal(0.1, webcam.Transform.Crop.Left);
            Assert.Equal(MaskShape.RoundedRect, webcam.Transform.Mask.Shape);
            Assert.Equal(0.25, webcam.Transform.Mask.CornerRadius);

            var screen = restored.Items[0];
            Assert.Equal(TransitionKind.Fade, screen.Entry.Kind);
            Assert.Equal(Ms(300), screen.Entry.DurationTicks);
            Assert.Equal(TransitionEasing.CubicOut, screen.Entry.Easing);
            Assert.Equal(TransitionKind.SlideLeft, screen.Exit.Kind);

            Assert.NotNull(restored.TrackOf(screen).GroupId);
            Assert.Equal(restored.TrackOf(screen).GroupId, restored.TrackOf(webcam).GroupId);
            Assert.Null(restored.Tracks.Single(t => t.Name == "Audio").GroupId);
        }

        /// <summary>Track.Locked is gone, but every project saved while it existed carries the key,
        /// true or false. It must load, mean nothing, and not come back.</summary>
        [Fact]
        public void A_saved_Locked_flag_is_ignored_and_not_written_back()
        {
            var json = FullProject().ToJson();
            Assert.DoesNotContain("\"Locked\"", json);

            var withFlag = json.Replace("\"Muted\": true", "\"Muted\": true, \"Locked\": true");
            Assert.Contains("\"Locked\"", withFlag);

            Assert.Equal(json, Project.FromJson(withFlag).ToJson());
        }

        // ---------------------------------------------------------------------------------- items

        /// <summary>An item with every writable property away from its default. The clone test
        /// below refuses an item that is not, so a property added to <see cref="Item"/> later has
        /// to be added here too, and then fails until <see cref="Item.Clone"/> carries it.</summary>
        private static Item FullItem() => new Item
        {
            Id = Guid.NewGuid(),
            TrackId = Guid.NewGuid(),
            TimelineStartTicks = Ms(2_000),
            DurationTicks = Ms(3_000),
            Content = new MediaContent { SourceId = Guid.NewGuid(), StreamIndex = 2, SourceInTicks = Ms(500), Speed = 1.5, SpeedWarpExempt = true },
            Transform = new Transform { X = 0.25, Y = 0.75, Scale = 0.5 },
            Surround = Surround.Create(SurroundKind.Glow, cursor: false),
            Effect = new VideoEffect { Kind = VideoEffectKind.BgBlur, Amount = 0.4 },
            Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = Ms(300), Easing = TransitionEasing.CubicOut },
            Exit = new Transition { Kind = TransitionKind.SlideLeft, DurationTicks = Ms(500), Easing = TransitionEasing.CubicInOut },
            Volume = 0.5,
        };

        [Fact]
        public void Clone_carries_every_property_under_a_new_id_and_shares_no_object()
        {
            var item = FullItem();
            var defaults = new Item();
            // the legacy id only exists between a read and its migration, and no copy is made there
            var writable = typeof(Item).GetProperties()
                .Where(p => p.CanWrite && p.Name != nameof(Item.LegacyLinkGroupId)).ToList();
            Assert.All(writable, p => Assert.NotEqual(p.GetValue(defaults), p.GetValue(item)));

            var clone = item.Clone();

            Assert.NotEqual(item.Id, clone.Id);
            Assert.NotEqual(Guid.Empty, clone.Id);
            Assert.All(writable.Where(p => !p.PropertyType.IsValueType),
                p => Assert.NotSame(p.GetValue(item), p.GetValue(clone)));

            // apart from the id, the clone reads back exactly as the original does
            clone.Id = item.Id;
            Assert.Equal(new Project { Items = { item } }.ToJson(), new Project { Items = { clone } }.ToJson());
        }

        [Fact]
        public void Clone_of_a_bare_item_stays_bare_with_the_default_placement()
        {
            var clone = new Item { Transform = null }.Clone();

            Assert.Null(clone.Content);
            Assert.Null(clone.Surround);
            Assert.Null(clone.Effect);
            Assert.Null(clone.Entry);
            Assert.Null(clone.Exit);
            Assert.NotNull(clone.Transform);
        }

        [Fact]
        public void Covers_is_half_open()
        {
            var item = new Item { TimelineStartTicks = 100, DurationTicks = 50 };

            Assert.False(item.Covers(99));
            Assert.True(item.Covers(100));
            Assert.True(item.Covers(149));
            Assert.False(item.Covers(150));
        }

        [Fact]
        public void TrackOf_finds_the_items_row_and_is_null_when_there_is_none()
        {
            var project = FullProject();

            foreach (var item in project.Items)
                Assert.Equal(item.TrackId, project.TrackOf(item).Id);

            Assert.Null(project.TrackOf(null));
            Assert.Null(project.TrackOf(new Item { TrackId = Guid.NewGuid() }));
        }

        [Fact]
        public void Content_discriminators_are_stable_wire_contract()
        {
            var json = FullProject().ToJson();

            // these strings are the file format — a rename here breaks every saved project.
            Assert.Contains("\"$type\": \"media\"", json);
            Assert.Contains("\"$type\": \"text\"", json);
            Assert.Contains("\"$type\": \"image\"", json);
            Assert.Contains("\"$type\": \"solid\"", json);
        }

        [Fact]
        public void Null_optionals_are_omitted_from_the_json()
        {
            var project = FullProject();
            var json = project.ToJson();

            // the solid item has no transitions, no crop/mask — none of those keys should appear
            // for it, and the audio row has no group. Cheap proxy: count occurrences across the file.
            Assert.Equal(1, CountOf(json, "\"Entry\":"));
            Assert.Equal(1, CountOf(json, "\"Exit\":"));
            Assert.Equal(1, CountOf(json, "\"Crop\":"));
            Assert.Equal(1, CountOf(json, "\"Mask\":"));
            Assert.Equal(2, CountOf(json, "\"GroupId\":"));
            Assert.Equal(0, CountOf(json, "\"LinkGroupId\":")); // read from old files, never written
        }

        private static int CountOf(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
                count++;
            return count;
        }

        [Fact]
        public void Unknown_content_discriminator_throws()
        {
            var json = FullProject().ToJson().Replace("\"$type\": \"solid\"", "\"$type\": \"hologram\"");
            Assert.Throws<JsonException>(() => Project.FromJson(json));
        }

        // ------------------------------------------------------------------------- the window crop

        [Fact]
        public void A_window_crop_round_trips_through_the_project_file()
        {
            var project = FullProject();
            project.Sources[0].WindowCapturePath = @"C:\rec\window-capture.jsonl";
            project.Items[0].Transform.CropWindow = new WindowCrop { WindowId = 7, Title = "README.md", App = "Code.exe", Pid = 4212 };
            Assert.Empty(project.Validate());

            var json = project.ToJson();

            // these strings are the file format — a rename here breaks every saved project.
            Assert.Contains("\"WindowCapturePath\":", json);
            Assert.Contains("\"CropWindow\":", json);
            Assert.Contains("\"WindowId\": 7", json);
            Assert.Contains("\"Title\": \"README.md\"", json);
            Assert.Contains("\"App\": \"Code.exe\"", json);
            Assert.Contains("\"Pid\": 4212", json);

            var restored = Project.FromJson(json);
            Assert.Equal(json, restored.ToJson());
            Assert.Equal(@"C:\rec\window-capture.jsonl", restored.Sources[0].WindowCapturePath);
            var follow = restored.Items[0].Transform.CropWindow;
            Assert.NotNull(follow);
            Assert.Equal(7, follow.WindowId);
            Assert.Equal("README.md", follow.Title);
            Assert.Equal("Code.exe", follow.App);
            Assert.Equal(4212, follow.Pid);
            // the follow and the hand crop are alternatives, never combined: setting one leaves
            // the other exactly as it was, here the webcam's own crop untouched by the screen's follow.
            Assert.Null(restored.Items[0].Transform.Crop);
            Assert.Equal(0.1, restored.Items[1].Transform.Crop.Left);
            Assert.Null(restored.Items[1].Transform.CropWindow);
        }

        [Fact]
        public void A_project_without_a_window_crop_writes_no_key()
        {
            // a project saved before the feature existed has neither key. It must load with both
            // fields null and re-save byte-identical — the whole reason there is no version bump.
            var json = FullProject().ToJson();
            Assert.DoesNotContain("CropWindow", json);
            Assert.DoesNotContain("WindowCapturePath", json);

            var restored = Project.FromJson(json);

            Assert.All(restored.Sources, s => Assert.Null(s.WindowCapturePath));
            Assert.All(restored.Items, i => Assert.Null(i.Transform.CropWindow));
            Assert.Equal(json, restored.ToJson());
        }

        [Fact]
        public void Cloning_a_transform_copies_the_follow_by_value()
        {
            var original = new Transform
            {
                Crop = new CropRect { Left = 0.1 },
                CropWindow = new WindowCrop { WindowId = 7, Title = "README.md", App = "Code.exe", Pid = 4212 },
            };

            var copy = original.Clone();

            // a shared instance would let an edit on one linked segment leak into another.
            Assert.NotSame(original.CropWindow, copy.CropWindow);
            Assert.Equal(7, copy.CropWindow.WindowId);
            Assert.Equal("README.md", copy.CropWindow.Title);
            Assert.Equal("Code.exe", copy.CropWindow.App);
            Assert.Equal(4212, copy.CropWindow.Pid);

            copy.CropWindow.WindowId = 9;
            Assert.Equal(7, original.CropWindow.WindowId);

            // and no follow clones as no follow, not as an empty one — null is "manual mode".
            Assert.Null(new Transform().Clone().CropWindow);
        }

        [Fact]
        public void Min_segment_ticks_mirrors_the_v1_constant()
        {
            Assert.Equal(VideoEditDocument.MinSegmentMs * TimeSpan.TicksPerMillisecond, TimelineOps.MinSegmentTicks);
        }

        [Fact]
        public void Normalize_sorts_tracks_by_order_and_items_by_track_then_start()
        {
            var trackA = new Guid("bbbbbbbb-0000-0000-0000-00000000000a");
            var trackB = new Guid("bbbbbbbb-0000-0000-0000-00000000000b");
            var project = new Project
            {
                Tracks =
                {
                    new Track { Id = trackB, Order = 1, Name = "second" },
                    new Track { Id = trackA, Order = 0, Name = "first" },
                },
                Items =
                {
                    new Item { Id = Guid.NewGuid(), TrackId = trackB, TimelineStartTicks = Ms(500), DurationTicks = Ms(500), Content = new SolidContent() },
                    new Item { Id = Guid.NewGuid(), TrackId = trackA, TimelineStartTicks = Ms(1000), DurationTicks = Ms(500), Content = new SolidContent() },
                    new Item { Id = Guid.NewGuid(), TrackId = trackA, TimelineStartTicks = 0, DurationTicks = Ms(500), Content = new SolidContent() },
                },
            };

            project.Normalize();

            Assert.Equal(new[] { "first", "second" }, project.Tracks.Select(t => t.Name));
            Assert.Equal(new[] { trackA, trackA, trackB }, project.Items.Select(i => i.TrackId));
            Assert.Equal(new[] { 0L, Ms(1000), Ms(500) }, project.Items.Select(i => i.TimelineStartTicks));
        }

        [Fact]
        public void Normalize_replaces_null_collections()
        {
            var project = new Project { Output = null, Sources = null, Tracks = null, Items = null };
            project.Normalize();

            Assert.NotNull(project.Output);
            Assert.Empty(project.Sources);
            Assert.Empty(project.Tracks);
            Assert.Empty(project.Items);
        }

        [Fact]
        public void Validate_accepts_a_well_formed_project()
        {
            Assert.Empty(FullProject().Validate());
        }

        [Fact]
        public void Validate_rejects_bad_version_and_output()
        {
            var project = new Project
            {
                Version = 1,
                Output = new OutputSettings { WidthPx = 0, HeightPx = 1080, FpsNum = 30, FpsDen = 0, SampleRate = 0 },
            };

            var errors = project.Validate();

            Assert.Contains(errors, e => e.Contains("version 1"));
            Assert.Contains(errors, e => e.Contains("not a positive size"));
            Assert.Contains(errors, e => e.Contains("not a positive rational"));
            Assert.Contains(errors, e => e.Contains("sample rate"));
        }

        [Fact]
        public void Validate_rejects_dangling_references()
        {
            var project = FullProject();
            project.Items[0].TrackId = Guid.NewGuid();                        // unknown track
            ((MediaContent)project.Items[2].Content).SourceId = Guid.NewGuid(); // unknown source
            ((MediaContent)project.Items[1].Content).StreamIndex = 9;          // missing stream

            var errors = project.Validate();

            Assert.Contains(errors, e => e.Contains("unknown track"));
            Assert.Contains(errors, e => e.Contains("unknown source"));
            Assert.Contains(errors, e => e.Contains("stream 9"));
        }

        [Fact]
        public void Validate_rejects_duplicate_ids()
        {
            var project = FullProject();
            project.Sources.Add(new Source { Id = project.Sources[0].Id });
            project.Tracks.Add(new Track { Id = project.Tracks[0].Id });
            project.Items[5].Id = project.Items[4].Id;

            var errors = project.Validate();

            Assert.Contains(errors, e => e.Contains("Duplicate source id"));
            Assert.Contains(errors, e => e.Contains("Duplicate track id"));
            Assert.Contains(errors, e => e.Contains("Duplicate item id"));
        }

        [Fact]
        public void Validate_rejects_bad_item_geometry_and_missing_content()
        {
            var project = FullProject();
            project.Items[0].TimelineStartTicks = -1;
            project.Items[3].DurationTicks = 0;
            project.Items[4].Content = null;
            ((MediaContent)project.Items[2].Content).SourceInTicks = -5;

            var errors = project.Validate();

            Assert.Contains(errors, e => e.Contains("before the timeline origin"));
            Assert.Contains(errors, e => e.Contains("non-positive duration"));
            Assert.Contains(errors, e => e.Contains("no content"));
            Assert.Contains(errors, e => e.Contains("negative source in-point"));
        }

        [Fact]
        public void Validate_rejects_overlap_but_allows_touching_items()
        {
            var project = FullProject();

            // items 3 and 4 sit back-to-back on the overlay track at 10s|13s — touching is fine.
            Assert.Empty(project.Validate());

            // pull the second one back 1ms so they overlap.
            project.Items[4].TimelineStartTicks -= Ms(1);
            Assert.Contains(project.Validate(), e => e.Contains("overlap"));
        }

        [Fact]
        public void Validate_rejects_picture_content_on_an_audio_track()
        {
            var project = FullProject();
            var audioTrack = project.Tracks.Single(t => t.Kind == TrackKind.Audio);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioTrack.Id,
                TimelineStartTicks = Ms(20_000),
                DurationTicks = Ms(1_000),
                Content = new TextContent { Text = "nope" },
            });

            Assert.Contains(project.Validate(), e => e.Contains("audio track"));
        }
    }
}
