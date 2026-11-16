using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Prisc.UI.VideoEditor;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Playback;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>A file saved with a link group on each item, read into a group on each row.</summary>
    public class ProjectMigrationTests
    {
        private static readonly Guid L1 = new Guid("00000000-0000-0000-0000-000000000001");
        private static readonly Guid L2 = new Guid("00000000-0000-0000-0000-000000000002");
        private static readonly Guid L3 = new Guid("00000000-0000-0000-0000-000000000003");
        private static readonly Guid L4 = new Guid("00000000-0000-0000-0000-000000000004");

        /// <summary>Builds a legacy file: a row per name, an item per (row, legacy id).</summary>
        private static string LegacyJson(out Dictionary<string, Guid> rows, params (string Row, Guid? Link)[] items)
        {
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 64, HeightPx = 64, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            };

            rows = new Dictionary<string, Guid>();
            long start = 0;
            foreach (var (row, link) in items)
            {
                if (!rows.TryGetValue(row, out var id))
                {
                    rows[row] = id = Guid.NewGuid();
                    project.Tracks.Add(new Track { Id = id, Kind = TrackKind.Video, Name = row, Order = project.Tracks.Count });
                }

                project.Items.Add(new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = id,
                    TimelineStartTicks = start++,
                    DurationTicks = 1,
                    Content = new SolidContent { Color = "#FF000000" },
                    LegacyLinkGroupId = link,
                });
            }

            return project.ToJson();
        }

        private static Guid? GroupOf(Project project, Guid trackId) =>
            project.Tracks.Single(t => t.Id == trackId).GroupId;

        // ------------------------------------------------------------------------- overlay clocks

        private const long Sec = TimeSpan.TicksPerSecond;

        /// <summary>A legacy recording under construction: a screen row and cursor and keys rows, all
        /// on one source, none of it on the rows yet.</summary>
        private sealed class Legacy
        {
            public readonly Project Project = new Project
            {
                Output = new OutputSettings { WidthPx = 64, HeightPx = 64, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
            };

            public readonly Source Source = new Source
            {
                Id = Guid.NewGuid(),
                Path = "recording.mp4",
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 64, Height = 64 },
                    new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 32, Height = 32 },
                },
            };

            public readonly Track Screen = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            public readonly Track Keys = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Keys", Order = 5 };
            public readonly Track Cursor = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Cursor", Order = 6 };

            public Legacy()
            {
                Project.Sources.Add(Source);
                Project.Tracks.AddRange(new[] { Screen, Keys, Cursor });
            }

            public Item Add(Track row, long start, long duration, ItemContent content, Guid? link = null)
            {
                var item = new Item
                {
                    Id = Guid.NewGuid(), TrackId = row.Id, TimelineStartTicks = start, DurationTicks = duration,
                    Content = content, LegacyLinkGroupId = link,
                };
                Project.Items.Add(item);
                return item;
            }

            public Item Clip(long start, long duration, long sourceIn, double speed = 1.0, bool freeze = false, Guid? link = null, Track row = null, int stream = 0) =>
                Add(row ?? Screen, start, duration, new MediaContent
                {
                    SourceId = Source.Id, StreamIndex = stream, SourceInTicks = sourceIn, Speed = speed, Freeze = freeze,
                }, link);

            public Item CursorClip(long start, long duration, Guid? link = null) =>
                Add(Cursor, start, duration, new CursorContent { SourceId = Source.Id }, link);

            public Item KeysClip(long start, long duration, Guid? link = null) =>
                Add(Keys, start, duration, new KeyboardContent { SourceId = Source.Id }, link);

            public Project Load() => Project.FromJson(Project.ToJson());
        }

        /// <summary>
        /// The accepted exception to I4 (design 3.2): a cursor fade that spans a discontinuity. Entry
        /// stays on the first piece and exit on the last, and a transition is clamped to its own
        /// clip, so the ramp finishes with the piece: the old build, with one clip, read 0.25 at
        /// 0.5 s and 0.75 at 1.5 s. A piece's transition has no way to say it started earlier.
        /// </summary>
        [Fact]
        public void A_cursor_fade_spanning_a_discontinuity_finishes_with_its_piece()
        {
            var legacy = new Legacy();
            var link = Guid.NewGuid();
            legacy.Clip(0, Sec, 0, link: link);
            legacy.Clip(Sec, 9 * Sec, 5 * Sec, link: link);
            var cursor = legacy.CursorClip(0, 10 * Sec, link);
            cursor.Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };
            cursor.Exit = new Transition { Kind = TransitionKind.Fade, DurationTicks = 2 * Sec, Easing = TransitionEasing.Linear };

            var parts = Parts(legacy.Load(), legacy.Cursor);

            Assert.Equal(2, parts.Count);
            Assert.NotNull(parts[0].Entry);
            Assert.Null(parts[0].Exit);
            Assert.Null(parts[1].Entry);
            Assert.NotNull(parts[1].Exit);
            Assert.Equal(0.5, Composition.TransitionMath.EntryProgress(parts[0], Sec / 2), 3);
            Assert.Equal(1.0, Composition.TransitionMath.EntryProgress(parts[1], Sec + Sec / 2), 3);
        }

        private static List<Item> Parts(Project project, Track row) =>
            project.Items.Where(i => i.TrackId == row.Id).OrderBy(i => i.TimelineStartTicks).ToList();

        private static (long Start, long Duration, long SourceIn, double Speed, bool Freeze) Shape(Item item)
        {
            var clock = (SourceClockContent)item.Content;
            return (item.TimelineStartTicks, item.DurationTicks, clock.SourceInTicks, clock.Speed, clock.Freeze);
        }

        [Fact]
        public void An_overlay_across_two_hosts_is_cut_where_they_jump()
        {
            var file = new Legacy();
            file.Clip(0, 10 * Sec, 0, link: L1);
            file.Clip(10 * Sec, 10 * Sec, 25 * Sec, link: L1);
            var cursor = file.CursorClip(0, 20 * Sec, L1);
            var keys = file.KeysClip(0, 20 * Sec, L1);

            var project = file.Load();

            foreach (var (row, id) in new[] { (file.Cursor, cursor.Id), (file.Keys, keys.Id) })
            {
                var parts = Parts(project, row);
                Assert.Equal(new[] { (0L, 10 * Sec, 0L, 1.0, false), (10 * Sec, 10 * Sec, 25 * Sec, 1.0, false) }, parts.Select(Shape));
                Assert.Equal(id, parts[0].Id);
                Assert.NotEqual(parts[0].Id, parts[1].Id);
            }
            Assert.Empty(project.Validate());
        }

        [Fact]
        public void Hosts_that_continue_each_other_leave_one_overlay()
        {
            var file = new Legacy();
            file.Clip(0, 10 * Sec, 3 * Sec, link: L1);
            file.Clip(10 * Sec, 10 * Sec, 13 * Sec, link: L1);
            file.CursorClip(0, 20 * Sec, L1);

            var part = Assert.Single(Parts(file.Load(), file.Cursor));

            Assert.Equal((0L, 20 * Sec, 3 * Sec, 1.0, false), Shape(part));
        }

        [Fact]
        public void A_sped_host_gives_the_overlay_its_speed_and_the_moment_it_starts_on()
        {
            var file = new Legacy();
            file.Clip(8 * Sec, 10 * Sec, 1 * Sec, speed: 2.0, link: L1);
            file.CursorClip(10 * Sec, 5 * Sec, L1);

            var part = Assert.Single(Parts(file.Load(), file.Cursor));

            // 2 s into a 2x clip that began at 1 s of source
            Assert.Equal((10 * Sec, 5 * Sec, 5 * Sec, 2.0, false), Shape(part));
        }

        [Fact]
        public void A_freeze_host_gives_the_overlay_the_held_moment()
        {
            var file = new Legacy();
            file.Clip(0, 10 * Sec, 0, link: L1);
            file.Clip(10 * Sec, 10 * Sec, 10 * Sec - 1, freeze: true);
            file.Clip(20 * Sec, 10 * Sec, 20 * Sec, link: L1);
            file.KeysClip(0, 30 * Sec, L1);

            var parts = Parts(file.Load(), file.Keys);

            Assert.Equal(new[]
            {
                (0L, 10 * Sec, 0L, 1.0, false),
                (10 * Sec, 10 * Sec, 10 * Sec - 1, 1.0, true),
                (20 * Sec, 10 * Sec, 20 * Sec, 1.0, false),
            }, parts.Select(Shape));
        }

        [Fact]
        public void Stretches_with_no_host_keep_the_clips_own_time()
        {
            // the old keys ran on the clip's own time where there was no screen, and the old cursor
            // drew nothing; a kept part draws nothing either, and its existence keeps the
            // project-wide default overlay stood down
            var file = new Legacy();
            file.Clip(10 * Sec, 10 * Sec, 40 * Sec, link: L1);
            file.CursorClip(5 * Sec, 25 * Sec, L1);
            file.KeysClip(5 * Sec, 25 * Sec, L1);

            var project = file.Load();

            var expected = new[]
            {
                (5 * Sec, 5 * Sec, 0L, 1.0, false),
                (10 * Sec, 10 * Sec, 40 * Sec, 1.0, false),
                (20 * Sec, 10 * Sec, 15 * Sec, 1.0, false),
            };
            Assert.Equal(expected, Parts(project, file.Cursor).Select(Shape));
            Assert.Equal(expected, Parts(project, file.Keys).Select(Shape));
        }

        [Fact]
        public void An_overlay_with_no_host_anywhere_is_left_as_it_was()
        {
            var file = new Legacy();
            file.Clip(100 * Sec, 5 * Sec, 0, link: L1);
            var cursor = file.CursorClip(0, 10 * Sec, L1);

            var part = Assert.Single(Parts(file.Load(), file.Cursor));

            Assert.Equal(cursor.Id, part.Id);
            Assert.Equal((0L, 10 * Sec, 0L, 1.0, false), Shape(part));
        }

        [Fact]
        public void A_cut_cursor_keeps_entry_on_the_first_part_and_exit_on_the_last()
        {
            var file = new Legacy();
            file.Clip(0, 10 * Sec, 0, link: L1);
            file.Clip(10 * Sec, 10 * Sec, 30 * Sec, link: L1);
            file.Clip(20 * Sec, 10 * Sec, 50 * Sec, link: L1);
            var cursor = file.CursorClip(0, 30 * Sec, L1);
            cursor.Entry = new Transition { Kind = TransitionKind.Fade, DurationTicks = Sec };
            cursor.Exit = new Transition { Kind = TransitionKind.SlideUp, DurationTicks = Sec };

            var parts = Parts(file.Load(), file.Cursor);

            Assert.Equal(3, parts.Count);
            Assert.Equal(TransitionKind.Fade, parts[0].Entry.Kind);
            Assert.Null(parts[0].Exit);
            Assert.Null(parts[1].Entry);
            Assert.Null(parts[1].Exit);
            Assert.Null(parts[2].Entry);
            Assert.Equal(TransitionKind.SlideUp, parts[2].Exit.Kind);
        }

        [Fact]
        public void Every_part_of_a_cut_keys_clip_keeps_both_transitions_they_animate_its_rows()
        {
            var file = new Legacy();
            file.Clip(0, 10 * Sec, 0, link: L1);
            file.Clip(10 * Sec, 10 * Sec, 30 * Sec, link: L1);
            var keys = file.KeysClip(0, 20 * Sec, L1);
            keys.Entry = new Transition { Kind = TransitionKind.SlideUp, DurationTicks = Sec };
            keys.Exit = new Transition { Kind = TransitionKind.Fade, DurationTicks = Sec };

            var parts = Parts(file.Load(), file.Keys);

            Assert.Equal(2, parts.Count);
            Assert.All(parts, part =>
            {
                Assert.Equal(TransitionKind.SlideUp, part.Entry.Kind);
                Assert.Equal(TransitionKind.Fade, part.Exit.Kind);
            });
        }

        [Fact]
        public void A_part_keeps_the_look_of_the_clip_it_came_from()
        {
            var file = new Legacy();
            file.Clip(0, 10 * Sec, 0, link: L1);
            file.Clip(10 * Sec, 10 * Sec, 30 * Sec, link: L1);
            var keys = file.KeysClip(0, 20 * Sec, L1);
            keys.Transform = new Transform { X = 0.5, Y = 0.85, Scale = 0.5 };
            ((KeyboardContent)keys.Content).FontSize = 33;

            var parts = Parts(file.Load(), file.Keys);

            Assert.All(parts, part =>
            {
                Assert.Equal(33, ((KeyboardContent)part.Content).FontSize);
                Assert.Equal(0.85, part.Transform.Y);
            });
            Assert.NotSame(parts[0].Transform, parts[1].Transform);
        }

        [Fact]
        public void A_host_carrying_the_overlays_legacy_id_wins_over_the_backmost()
        {
            var file = new Legacy();
            var back = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Back", Order = -1 };
            file.Project.Tracks.Add(back);
            file.Clip(0, 10 * Sec, 70 * Sec, row: back);
            file.Clip(0, 10 * Sec, 5 * Sec, link: L1);
            file.CursorClip(0, 10 * Sec, L1);

            Assert.Equal(5 * Sec, ((SourceClockContent)Assert.Single(Parts(file.Load(), file.Cursor)).Content).SourceInTicks);
        }

        [Fact]
        public void With_no_host_carrying_its_id_an_overlay_takes_the_backmost_whatever_the_file_order()
        {
            var file = new Legacy();
            var back = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Back", Order = -1 };
            file.Project.Tracks.Add(back);
            file.Clip(0, 10 * Sec, 5 * Sec, link: L2);
            file.Clip(0, 10 * Sec, 70 * Sec, row: back);
            file.CursorClip(0, 10 * Sec, L1);

            Assert.Equal(70 * Sec, ((SourceClockContent)Assert.Single(Parts(file.Load(), file.Cursor)).Content).SourceInTicks);
        }

        [Fact]
        public void A_webcam_clip_is_no_host()
        {
            var file = new Legacy();
            file.Clip(0, 10 * Sec, 9 * Sec, link: L1, stream: 1);
            file.CursorClip(0, 10 * Sec, L1);

            Assert.Equal(0, ((SourceClockContent)Assert.Single(Parts(file.Load(), file.Cursor)).Content).SourceInTicks);
        }

        [Fact]
        public void A_clip_on_an_audio_row_is_no_host()
        {
            var file = new Legacy();
            var audio = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 1 };
            file.Project.Tracks.Add(audio);
            file.Clip(0, 10 * Sec, 9 * Sec, link: L1, row: audio);
            file.CursorClip(0, 10 * Sec, L1);

            Assert.Equal(0, ((SourceClockContent)Assert.Single(Parts(file.Load(), file.Cursor)).Content).SourceInTicks);
        }

        [Fact]
        public void Overlays_are_clocked_before_their_legacy_ids_go_and_a_second_load_changes_nothing()
        {
            var file = new Legacy();
            file.Clip(0, 10 * Sec, 0, link: L1);
            file.Clip(10 * Sec, 10 * Sec, 30 * Sec, link: L1);
            file.CursorClip(0, 20 * Sec, L1);
            file.KeysClip(0, 25 * Sec, L1);

            var project = file.Load();
            project.Normalize();
            var json = project.ToJson();

            Assert.DoesNotContain("LinkGroupId", json);
            var again = Project.FromJson(json, out var upgraded);
            Assert.False(upgraded);
            Assert.Equal(json, again.ToJson());
        }

        public static TheoryData<string> MalformedFiles()
        {
            const string id = "00000000-0000-0000-0000-00000000000a";
            const string unknown = "00000000-0000-0000-0000-00000000000b";
            var overlay = $$"""{ "Id": "{{id}}", "TrackId": "{{id}}", "DurationTicks": 100, "LinkGroupId": "{{id}}", "Content": { "$type": "cursor", "SourceId": "{{id}}" } }""";
            var host = $$"""{ "Id": "{{unknown}}", "TrackId": "{{id}}", "DurationTicks": 100, "LinkGroupId": "{{id}}", "Content": { "$type": "media", "SourceId": "{{id}}" } }""";
            var noContent = $$"""{ "Id": "{{unknown}}", "TrackId": "{{unknown}}", "DurationTicks": 100, "LinkGroupId": "{{id}}" }""";
            var negative = overlay.Replace("100", "-5");
            var unsped = host.Replace("\"SourceId\"", "\"Speed\": 0, \"SourceId\"");
            var track = $$"""{ "Id": "{{id}}", "Kind": "Video" }""";
            var source = $$"""{ "Id": "{{id}}", "Path": "x" }""";
            var sourceNullStream = $$"""{ "Id": "{{id}}", "Path": "x", "Streams": [ null ] }""";

            return new TheoryData<string>(
                $$"""{ "Version": 2, "Items": [ {{overlay}} ] }""",
                $$"""{ "Version": 2, "Items": [ null, {{overlay}} ], "Tracks": null, "Sources": null }""",
                $$"""{ "Version": 2, "Items": [ {{overlay}}, {{noContent}} ], "Tracks": [ null, {{track}} ], "Sources": [ null ] }""",
                $$"""{ "Version": 2, "Items": [ {{overlay}}, {{host}} ], "Tracks": [ {{track}} ], "Sources": [ {{source}} ] }""",
                $$"""{ "Version": 2, "Items": [ {{overlay}}, {{host}} ], "Tracks": [ {{track}} ], "Sources": [ {{sourceNullStream}} ] }""",
                $$"""{ "Version": 2, "Items": [ {{overlay}}, {{host}} ], "Sources": [ {{source}} ] }""",
                $$"""{ "Version": 2, "Items": [ {{negative}}, {{host}} ], "Tracks": [ {{track}} ], "Sources": [ {{source}} ] }""",
                $$"""{ "Version": 2, "Items": [ {{overlay}}, {{unsped}} ], "Tracks": [ {{track}} ], "Sources": [ {{source}} ] }""");
        }

        [Theory]
        [MemberData(nameof(MalformedFiles))]
        public void Overlay_clocks_get_through_a_malformed_file(string json)
        {
            var project = Project.FromJson(json, out var upgraded);

            Assert.True(upgraded);
            Assert.All(project.Items.Where(i => i != null), i => Assert.Null(i.LegacyLinkGroupId));
        }

        [Fact]
        public void Rows_linked_directly_or_through_a_row_share_the_components_smallest_id()
        {
            var json = LegacyJson(out var rows,
                ("A", L2), ("B", L2), ("B", L1), ("C", L1), // A-B by L2, B-C by L1
                ("D", L3), ("E", L3),
                ("F", L4), ("F", L4),                       // one row: nothing to keep in step
                ("G", null));

            var project = Project.FromJson(json, out var upgraded);

            Assert.True(upgraded);
            Assert.Equal(L1, GroupOf(project, rows["A"]));
            Assert.Equal(L1, GroupOf(project, rows["B"]));
            Assert.Equal(L1, GroupOf(project, rows["C"]));
            Assert.Equal(L3, GroupOf(project, rows["D"]));
            Assert.Equal(L3, GroupOf(project, rows["E"]));
            Assert.Null(GroupOf(project, rows["F"]));
            Assert.Null(GroupOf(project, rows["G"]));
        }

        [Fact]
        public void The_ids_do_not_depend_on_the_order_of_the_items()
        {
            var forward = Project.FromJson(LegacyJson(out var a, ("X", L3), ("Y", L3), ("Y", L1), ("Z", L1)));
            var backward = Project.FromJson(LegacyJson(out var b, ("Z", L1), ("Y", L1), ("Y", L3), ("X", L3)));

            foreach (var row in new[] { "X", "Y", "Z" })
            {
                Assert.Equal(L1, GroupOf(forward, a[row]));
                Assert.Equal(L1, GroupOf(backward, b[row]));
            }
        }

        [Fact]
        public void A_migrated_project_carries_no_legacy_id_and_migrates_no_further()
        {
            var once = Project.FromJson(LegacyJson(out _, ("A", L1), ("B", L1), ("C", L2)), out var first);
            var json = once.ToJson();

            Assert.True(first);
            Assert.All(once.Items, i => Assert.Null(i.LegacyLinkGroupId));
            Assert.DoesNotContain("LinkGroupId", json);

            var twice = Project.FromJson(json, out var second);

            Assert.False(second);
            Assert.Equal(json, twice.ToJson());
        }

        /// <summary>A hand-built or current file is untouched: this is the path every undo restore
        /// and every snapshot for the player goes through.</summary>
        [Fact]
        public void A_current_file_is_left_alone()
        {
            var group = Guid.NewGuid();
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 64, HeightPx = 64, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Tracks =
                {
                    new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, GroupId = group },
                    new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, GroupId = group },
                },
            };

            var read = Project.FromJson(project.ToJson(), out var upgraded);

            Assert.False(upgraded);
            Assert.Equal(project.ToJson(), read.ToJson());
        }

        /// <summary>An exception out of a load costs the user's edit, and the autosave would then
        /// overwrite the file, so nothing a damaged file can hold may throw.</summary>
        [Theory]
        [InlineData("{\"Version\":2}", false)]
        [InlineData("{\"Version\":2,\"Items\":null}", false)]
        [InlineData("{\"Version\":2,\"Items\":[null]}", false)]
        [InlineData("{\"Version\":2,\"Tracks\":null,\"Items\":[{\"LinkGroupId\":\"00000000-0000-0000-0000-000000000001\"}]}", true)]
        [InlineData("{\"Version\":2,\"Tracks\":[null],\"Items\":[null,{\"LinkGroupId\":\"00000000-0000-0000-0000-000000000001\"}]}", true)]
        // two items on rows the project does not have
        [InlineData("{\"Version\":2,\"Items\":[{\"TrackId\":\"00000000-0000-0000-0000-00000000000a\",\"LinkGroupId\":\"00000000-0000-0000-0000-000000000001\"},{\"TrackId\":\"00000000-0000-0000-0000-00000000000b\",\"LinkGroupId\":\"00000000-0000-0000-0000-000000000001\"}]}", true)]
        // a row with no items, a link naming no row, and a duplicated row
        [InlineData("{\"Version\":2,\"Tracks\":[{\"Id\":\"00000000-0000-0000-0000-00000000000a\"},{\"Id\":\"00000000-0000-0000-0000-00000000000a\"},{\"Id\":\"00000000-0000-0000-0000-00000000000b\"}],\"Items\":[{\"TrackId\":\"00000000-0000-0000-0000-00000000000a\",\"LinkGroupId\":\"00000000-0000-0000-0000-000000000001\"},{\"TrackId\":\"00000000-0000-0000-0000-00000000000c\",\"LinkGroupId\":\"00000000-0000-0000-0000-000000000001\"}]}", true)]
        public void Malformed_files_migrate_without_throwing(string json, bool expectUpgraded)
        {
            var project = Project.FromJson(json, out var upgraded);

            Assert.Equal(expectUpgraded, upgraded);
            Assert.All(project.Items ?? new List<Item>(), i => Assert.Null(i?.LegacyLinkGroupId));
        }

        [Fact]
        public void A_null_document_stays_null_and_is_no_upgrade()
        {
            Assert.Null(Project.FromJson("null", out var upgraded));
            Assert.False(upgraded);
        }

        /// <summary>A row only sharing its id with a row the project does not have is alone.</summary>
        [Fact]
        public void A_link_to_a_missing_row_groups_nothing()
        {
            var json = "{\"Version\":2,\"Tracks\":[{\"Id\":\"00000000-0000-0000-0000-00000000000a\"}]," +
                       "\"Items\":[{\"TrackId\":\"00000000-0000-0000-0000-00000000000a\",\"LinkGroupId\":\"00000000-0000-0000-0000-000000000001\"}," +
                       "{\"TrackId\":\"00000000-0000-0000-0000-00000000000c\",\"LinkGroupId\":\"00000000-0000-0000-0000-000000000001\"}]}";

            var project = Project.FromJson(json);

            Assert.Null(Assert.Single(project.Tracks).GroupId);
        }

        /// <summary>Real old files. Which rows must share a group is read from the raw file, with
        /// no help from the code under test: rows that shared an id, directly or through another
        /// row. Every file still validates, because every later edit would otherwise roll back.</summary>
        [Theory]
        [MemberData(nameof(Fixtures))]
        public void A_saved_fixture_comes_out_grouped_by_the_links_in_its_file(string name)
        {
            var raw = File.ReadAllText(LegacyFixtures.ProjectPath(name));
            var links = new List<(Guid Track, Guid Link)>();
            using (var doc = JsonDocument.Parse(raw))
            {
                foreach (var item in doc.RootElement.GetProperty("Items").EnumerateArray())
                {
                    if (item.TryGetProperty("LinkGroupId", out var link))
                        links.Add((item.GetProperty("TrackId").GetGuid(), link.GetGuid()));
                }
            }

            var project = Project.FromJson(raw, out var upgraded);

            Assert.True(upgraded);
            Assert.Empty(project.Validate());
            Assert.DoesNotContain("LinkGroupId", project.ToJson());

            // rows sharing an id share a group, and it is one of the ids they held
            foreach (var shared in links.GroupBy(l => l.Link).Where(g => g.Select(l => l.Track).Distinct().Count() > 1))
            {
                var groups = shared.Select(l => GroupOf(project, l.Track)).Distinct().ToList();
                Assert.NotNull(Assert.Single(groups));
                Assert.Contains(links, l => l.Link == groups[0]);
            }

            // a row that never held an id is in no group
            foreach (var track in project.Tracks.Where(t => links.All(l => l.Track != t.Id)))
                Assert.Null(track.GroupId);
        }

        public static TheoryData<string> Fixtures()
        {
            var data = new TheoryData<string>();
            foreach (var name in LegacyFixtures.Names)
                data.Add(name);
            return data;
        }

        /// <summary>The one recording that has an ungrouped copy beside it: the copy stays out, so
        /// a drag of the recording leaves it where it is.</summary>
        [Fact]
        public void A_duplicate_row_stays_out_of_the_recordings_group()
        {
            var project = LegacyFixtures.Load("duplicate-row");

            var grouped = project.Tracks.Where(t => t.GroupId != null).ToList();

            Assert.True(grouped.Count >= 2);
            Assert.Single(grouped.Select(t => t.GroupId).Distinct());
            Assert.Contains(project.Tracks, t => t.GroupId == null
                && project.Items.Any(i => i.TrackId == t.Id && i.Content is MediaContent));
        }

        // ------------------------------------------------------------------------ the safety net

        private static readonly MediaProbeResult AnyProbe = new MediaProbeResult
        {
            VideoStreams = new[] { new VideoStreamProbe { StreamIndex = 0, Width = 1280, Height = 720, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = 60 * TimeSpan.TicksPerSecond } },
            DurationTicks = 60 * TimeSpan.TicksPerSecond,
        };

        /// <summary>Runs the body in an empty directory holding a copy of a legacy file under the
        /// name the editor saves it by.</summary>
        private static void WithLegacyFile(Action<string> body)
        {
            var dir = Path.Combine(Path.GetTempPath(), "prisc-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, VideoEditPersistence.FileName);
                File.Copy(LegacyFixtures.ProjectPath("ctrl-k-recording"), path);
                body(path);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        private static string BackupOf(string path) =>
            Path.Combine(Path.GetDirectoryName(path), VideoEditPersistence.PreTrackGroupsFileName);

        [Fact]
        public void Opening_an_old_file_keeps_a_copy_of_it_beside_it()
        {
            WithLegacyFile(path =>
            {
                var original = File.ReadAllBytes(path);

                var project = VideoEditPersistence.LoadOrCreate(path, TestPath.Native(@"C:\rec\input.mp4"), AnyProbe);

                Assert.Contains(project.Tracks, t => t.GroupId != null);
                Assert.Equal(original, File.ReadAllBytes(BackupOf(path)));
                Assert.Equal(original, File.ReadAllBytes(path)); // the load itself writes nothing over it
            });
        }

        [Fact]
        public void A_blank_project_keeps_its_copy_too()
        {
            WithLegacyFile(path =>
            {
                var original = File.ReadAllBytes(path);

                VideoEditPersistence.LoadOrCreateBlank(path);

                Assert.Equal(original, File.ReadAllBytes(BackupOf(path)));
            });
        }

        /// <summary>The copy is of the first open: reopening a file nothing has saved over yet must
        /// not replace it, and a file already upgraded never touches it.</summary>
        [Fact]
        public void The_copy_is_written_once()
        {
            WithLegacyFile(path =>
            {
                File.WriteAllText(BackupOf(path), "the first copy");

                VideoEditPersistence.LoadOrCreate(path, TestPath.Native(@"C:\rec\input.mp4"), AnyProbe);

                Assert.Equal("the first copy", File.ReadAllText(BackupOf(path)));
            });
        }

        [Fact]
        public void A_current_file_gets_no_copy()
        {
            WithLegacyFile(path =>
            {
                File.WriteAllText(path, Project.FromJson(File.ReadAllText(path)).ToJson());

                VideoEditPersistence.LoadOrCreate(path, TestPath.Native(@"C:\rec\input.mp4"), AnyProbe);

                Assert.False(File.Exists(BackupOf(path)));
            });
        }

        /// <summary>A failed copy must not cost the edit: here a directory is in the copy's way
        /// (<c>File.Exists</c> says no, and the copy throws).</summary>
        [Fact]
        public void A_failed_copy_still_opens_the_project()
        {
            WithLegacyFile(path =>
            {
                Directory.CreateDirectory(BackupOf(path));

                var project = VideoEditPersistence.LoadOrCreate(path, TestPath.Native(@"C:\rec\input.mp4"), AnyProbe);

                Assert.Contains(project.Tracks, t => t.GroupId != null);
            });
        }
    }
}
