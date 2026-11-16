using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Media;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Playback;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// What must hold after any edit at all, under random session operations on every kind of row.
    /// Permanent: every public edit on <see cref="EditorSession"/> belongs in <see cref="Op"/>.
    /// After each one that went through:
    /// <list type="bullet">
    /// <item><see cref="Project.Validate"/> is clean.</item>
    /// <item>The JSON round-trips exactly, with no legacy link group left in it.</item>
    /// <item>One undo restores the bytes from before the edit, and one redo the bytes after it (I5).</item>
    /// <item>Clearing every group leaves the overlay resolver's answers and the decode lanes alone (I1).</item>
    /// <item>A group never has exactly one row holding clips (I2).</item>
    /// <item>A clip on a row locked before and after keeps its placement, except through undo and
    /// redo (I7).</item>
    /// </list>
    /// An edit that changed nothing must not leave an undo entry behind, and a drag never changes a
    /// clip's row (I8). <see cref="OverlaySyncFuzzTests"/> is the other half: it holds overlays in
    /// step, which free edits like these take out of step on purpose.
    /// </summary>
    [Collection("EditorSession clipboard")] // copies and pastes share the one static clipboard
    public class GeneralEditFuzzTests
    {
        private const long Sec = TimeSpan.TicksPerSecond;
        private const int Seeds = 120;
        private const int StepsPerSeed = 40;

        /// <summary>A run stops growing here: a project is rarely this big, and the checks scale
        /// with it.</summary>
        private const int MostClips = 60;

        /// <summary>Below this a run stops taking time out of the timeline.</summary>
        private const long ShortestTimeline = 10 * Sec;

        private static readonly double[] Speeds = { 0.5, 0.75, 1.0, 1.37, 2.0, 3.0 };
        private static readonly int[] FrameRates = { 24, 30, 60 };
        private static readonly VideoEffectKind[] Effects = { VideoEffectKind.Blur, VideoEffectKind.BgBlur, VideoEffectKind.BgRemove };

        /// <summary>The allowlist. Copy and the selection operations change no bytes; they are here
        /// for what the operations after them see.</summary>
        private enum Op
        {
            MoveLinked,
            MoveAlone,
            MoveThenCancel,
            Trim,
            SplitClip,
            SplitEverything,
            Delete,
            RippleDelete,
            CloseGap,
            RippleCloseGap,
            Rejoin,
            FreezeFill,
            Extend,
            ClipSpeed,
            SpeedFactor,
            PitchCorrect,
            WarpExempt,
            Copy,
            Cut,
            PasteAt,
            PasteIntoGap,
            Select,
            SelectLinked,
            Group,
            Ungroup,
            UngroupAll,
            Hide,
            Mute,
            Denoise,
            DenoiseStrength,
            Lock,
            Rename,
            DuplicateRow,
            DeleteRow,
            ReorderRow,
            MoveRowLayer,
            AddText,
            AddImage,
            AddObscure,
            AddShape,
            AddWallpaper,
            AddSpeed,
            AddZoom,
            AddCursor,
            AddKeys,
            Import,
            RemoveImport,
            Relink,
            SourceRows,
            Placement,
            Opacity,
            Volume,
            Effect,
            OutputSize,
            FrameRate,
            VoiceTake,
            VoiceTakeCancelled,
            Undo,
            Redo,
        }

        [Fact]
        public void Every_edit_leaves_a_valid_project_that_undoes_to_the_byte()
        {
            var ops = Enum.GetValues<Op>();
            var applied = ops.ToDictionary(op => op, _ => 0);

            for (var seed = 0; seed < Seeds; seed++)
            {
                var rng = new Random(seed);
                var session = NewProject(withOverlays: seed % 2 == 0);
                var now = 0L;
                session.Clock = () => now += 3_600_000; // an hour between edits: nothing coalesces
                var history = 0;
                session.HistoryChanged += (_, _) => history++;
                var log = new List<string>();
                var lastOp = Op.Copy;

                for (var step = 0; step < StepsPerSeed; step++)
                {
                    // a new edit clears the redo stack, so a redo has to come straight after an undo
                    var op = lastOp == Op.Undo && rng.Next(2) == 0 ? Op.Redo : ops[rng.Next(ops.Length)];
                    lastOp = op;
                    if (session.Project.Items.Count == 0 || session.Project.Items.Count > MostClips)
                        break;

                    var before = session.Project.ToJson();
                    var beforeLocks = LockedPlacements(session.Project);
                    history = 0;

                    string what;
                    try
                    {
                        what = Apply(op, session, rng);
                    }
                    catch (Exception e)
                    {
                        throw new Xunit.Sdk.XunitException($"seed {seed}: {op} threw {e}, after:\n{String.Join("\n", log)}");
                    }

                    if (what == null)
                        continue;

                    var after = session.Project.ToJson();
                    if (after == before)
                    {
                        Assert.True(op is Op.Undo or Op.Redo || history == 0,
                            $"seed {seed}: {op} {what} changed nothing and touched the history, after:\n{String.Join("\n", log)}");
                        continue;
                    }

                    applied[op]++;
                    log.Add($"{op} {what}");
                    AssertSound(session, op, before, after, beforeLocks, $"seed {seed}, after:\n{String.Join("\n", log)}");
                }
            }

            // the run has to have reached everything it claims to cover
            foreach (var (op, count) in applied.Where(a => a.Key is not (Op.Copy or Op.Select or Op.SelectLinked or Op.MoveThenCancel or Op.VoiceTakeCancelled)))
                Assert.True(count >= 10, $"{op} only went through {count} times, of: {String.Join(", ", applied)}");
        }

        private static void AssertSound(EditorSession session, Op op, string before, string after,
            Dictionary<Guid, string> beforeLocks, string context)
        {
            var problems = session.Project.Validate();
            Assert.True(problems.Count == 0, $"invalid: {String.Join("; ", problems)}, {context}");

            var reloaded = Project.FromJson(after);
            Assert.True(reloaded.ToJson() == after, $"the JSON did not round-trip: {Difference(after, reloaded.ToJson())}, {context}");
            Assert.DoesNotContain("LinkGroupId", after);

            if (op is Op.Undo)
            {
                session.Redo();
                Assert.True(session.Project.ToJson() == before, $"redo after an undo did not restore the bytes: {Difference(before, session.Project.ToJson())}, {context}");
                session.Undo();
            }
            else if (op is Op.Redo)
            {
                session.Undo();
                Assert.True(session.Project.ToJson() == before, $"undo after a redo did not restore the bytes: {Difference(before, session.Project.ToJson())}, {context}");
                session.Redo();
            }
            else
            {
                session.Undo();
                Assert.True(session.Project.ToJson() == before, $"one undo did not restore the bytes: {Difference(before, session.Project.ToJson())}, {context}");
                session.Redo();
            }

            Assert.True(session.Project.ToJson() == after, $"coming back did not restore the bytes: {Difference(after, session.Project.ToJson())}, {context}");
            var live = session.Project.Items.Select(i => i.Id).ToHashSet();
            Assert.True(session.SelectedItemIds.All(live.Contains), $"a dead clip is selected, {context}");

            AssertGroupsDoNotChangeWhatComposes(session.Project, context);
            AssertNoLoneGroup(session.Project, context);

            if (op is not (Op.Undo or Op.Redo))
                AssertLockedPlacementsHeld(session.Project, beforeLocks, context);
        }

        /// <summary>Where two JSON texts first part, for a failure message anyone can read.</summary>
        private static string Difference(string expected, string actual)
        {
            var at = 0;
            while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
                at++;
            string Near(string text) => text.Substring(Math.Max(0, at - 200), Math.Min(text.Length - Math.Max(0, at - 200), 400));
            return $"at {at}, expected: {Near(expected)} ---- got: {Near(actual)} ----";
        }

        /// <summary>The project with each row's order value replaced by its place in the order.</summary>
        private static string RanksOnly(string json)
        {
            var project = Project.FromJson(json);
            var rank = 0;
            foreach (var track in project.Tracks.OrderBy(t => t.Order))
                track.Order = rank++;
            return project.ToJson();
        }

        // --------------------------------------------------------------------------- the invariants

        /// <summary>I1: what each overlay resolves to and which decoder each clip reads from, with and
        /// without groups.</summary>
        private static void AssertGroupsDoNotChangeWhatComposes(Project project, string context)
        {
            var cleared = Project.FromJson(project.ToJson());
            foreach (var track in cleared.Tracks)
                track.GroupId = null;

            Assert.True(ResolverAnswers(project) == ResolverAnswers(cleared), $"a group changed what an overlay resolves to, {context}");

            var lanes = StreamLanes.Assign(project);
            var clearedLanes = StreamLanes.Assign(cleared);
            Assert.True(lanes.Count == clearedLanes.Count && lanes.All(l => clearedLanes[l.Key] == l.Value),
                $"a group changed the decode lanes, {context}");
        }

        private static string ResolverAnswers(Project project)
        {
            var rows = OverlayGoldenResolver.OverlayRows(project);
            if (rows.Count == 0)
                return "";

            var times = OverlayGoldenResolver.SampleTimes(project);
            var samples = rows.SelectMany(row => times.Select(t =>
                OverlayGoldenResolver.Resolve(project, row, t, project.Output.WidthPx, project.Output.HeightPx)));
            return JsonSerializer.Serialize(samples);
        }

        /// <summary>I2: tracks of a group with exactly one of them holding clips are nulled.</summary>
        private static void AssertNoLoneGroup(Project project, string context)
        {
            var holding = project.Items.Select(i => i.TrackId).ToHashSet();
            foreach (var group in project.Tracks.Where(t => t.GroupId != null).GroupBy(t => t.GroupId))
                Assert.True(group.Count(t => holding.Contains(t.Id)) != 1, $"group {group.Key} has one row holding clips, {context}");
        }

        /// <summary>Everything the lock covers on each clip of a locked row. Opacity and mask live on
        /// the same transform and are deliberately left out: the lock allows them.</summary>
        private static Dictionary<Guid, string> LockedPlacements(Project project)
        {
            var locked = project.Tracks.Where(t => t.PlacementLocked).Select(t => t.Id).ToHashSet();
            return project.Items.Where(i => locked.Contains(i.TrackId)).ToDictionary(i => i.Id, PlacementOf);
        }

        private static string PlacementOf(Item item)
        {
            var t = item.Transform ?? new Transform();
            return JsonSerializer.Serialize(new
            {
                item.TrackId,
                t.X,
                t.Y,
                t.Scale,
                t.ScaleY,
                t.Rotation,
                t.Aspect,
                t.AspectStretch,
                t.Crop,
                t.CropWindow,
                TextSize = (item.Content as TextContent)?.Size,
                Shape = item.Content is ShapeContent s ? new { s.Kind, s.StartX, s.StartY, s.EndX, s.EndY, s.Bend, s.BendSkew } : null,
            });
        }

        /// <summary>I7, for the clips still on a row that was locked before the edit and still is.
        /// Clips moved out by an edit are not on a locked row any more; there is no such edit today,
        /// and <see cref="PlacementOf"/> carries the row so one would be reported here.</summary>
        private static void AssertLockedPlacementsHeld(Project project, Dictionary<Guid, string> before, string context)
        {
            foreach (var item in project.Items)
            {
                if (before.TryGetValue(item.Id, out var was) && project.TrackOf(item)?.PlacementLocked == true)
                    Assert.True(was == PlacementOf(item), $"a locked clip's placement went from {was} to {PlacementOf(item)}, {context}");
            }
        }

        // ------------------------------------------------------------------------------- the project

        /// <summary>One clip of every kind, on rows of every kind: a two-slice grouped recording with
        /// webcam and audio, cursor and keys if asked for (half the runs add them as they go), a card, a rectangle, an arrow, a picture, a wallpaper, a
        /// voice take, a speed item and a zoom.</summary>
        private static EditorSession NewProject(bool withOverlays)
        {
            var group = Guid.NewGuid();
            var screen = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0, GroupId = group };
            var webcam = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Webcam", Order = 1, GroupId = group };
            var audio = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 2, GroupId = group };
            var source = new Source
            {
                Id = Guid.NewGuid(),
                Path = TestPath.Native(@"C:\rec\in.mp4"),
                InputCapturePath = TestPath.ExistingCapture,
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = 200 * Sec },
                    new SourceStream { Index = 1, Kind = StreamKind.Video, Width = 640, Height = 480, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = 200 * Sec },
                    new SourceStream { Index = 2, Kind = StreamKind.Audio, DurationTicks = 200 * Sec },
                },
            };

            Item Clip(Track row, int stream, long start, long length) => new Item
            {
                Id = Guid.NewGuid(), TrackId = row.Id, TimelineStartTicks = start, DurationTicks = length,
                Content = new MediaContent { SourceId = source.Id, StreamIndex = stream, SourceInTicks = 20 * Sec + start },
            };

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources = { source },
                Tracks = { screen, webcam, audio },
                Items =
                {
                    Clip(screen, 0, 0, 20 * Sec), Clip(screen, 0, 20 * Sec, 40 * Sec),
                    Clip(webcam, 1, 0, 20 * Sec), Clip(webcam, 1, 20 * Sec, 40 * Sec),
                    Clip(audio, 2, 0, 60 * Sec),
                },
            };

            project.Normalize();
            var session = new EditorSession(project, null, null);
            if (withOverlays)
            {
                Assert.NotNull(session.AddKeyboardTrack());
                Assert.NotNull(session.AddCursorTrack());
            }
            Assert.NotNull(session.AddText(5 * Sec, 5 * Sec));
            Assert.NotNull(session.AddShape(5 * Sec, 5 * Sec));
            Assert.NotNull(session.AddShape(15 * Sec, 5 * Sec, ShapeKind.Arrow));
            Assert.NotNull(session.AddImage(TestPath.Native(@"C:\media\logo.png"), 30 * Sec, 5 * Sec));
            Assert.NotNull(session.AddBackground(0, 10 * Sec));
            Assert.NotNull(session.AddSpeedEffect(40 * Sec, 5 * Sec));
            Assert.NotNull(session.AddZoomEffect(10 * Sec, 5 * Sec));
            Record(session, 2 * Sec, 3 * Sec);
            return session;
        }

        private static MediaProbeResult WavProbe(long length) => new MediaProbeResult
        {
            Path = TestPath.Native(@"C:\rec\voice.wav"),
            DurationTicks = length,
            VideoStreams = Array.Empty<VideoStreamProbe>(),
            AudioStreams = new[] { new AudioStreamProbe { StreamIndex = 0, SampleRate = 48_000, Channels = 1, DurationTicks = length } },
            HasAudio = true,
        };

        private static MediaProbeResult ClipProbe() => new MediaProbeResult
        {
            Path = TestPath.Native(@"C:\media\clip.mp4"),
            DurationTicks = 8 * Sec,
            VideoStreams = new[] { new VideoStreamProbe { StreamIndex = 0, Width = 1280, Height = 720, AvgFrameRateNum = 30, AvgFrameRateDen = 1, DurationTicks = 8 * Sec } },
            AudioStreams = new[] { new AudioStreamProbe { StreamIndex = 1, SampleRate = 48_000, Channels = 2, DurationTicks = 8 * Sec } },
            HasAudio = true,
        };

        private static Item Record(EditorSession session, long start, long length)
        {
            var take = session.BeginVoiceTake(start);
            session.UpdateVoiceTake(take, length);
            return session.FinishVoiceTake(take, TestPath.Native($@"C:\rec\voice-{Guid.NewGuid():N}.wav"), WavProbe(length), length);
        }

        // ------------------------------------------------------------------------------ the operations

        private static T Pick<T>(Random rng, IReadOnlyList<T> options) => options[rng.Next(options.Count)];

        /// <summary>A time on a 1 ms grid in <c>[from, to)</c>.</summary>
        private static long TimeIn(Random rng, long from, long to) =>
            to <= from ? from : from + rng.NextInt64((to - from) / TimeSpan.TicksPerMillisecond + 1) * TimeSpan.TicksPerMillisecond;

        private static string Span(Item clip) =>
            $"{clip.Content.GetType().Name} [{clip.TimelineStartTicks}, {clip.TimelineEndTicks}) row {clip.TrackId}";

        private static List<Guid> SomeClips(Random rng, Project project, int most) =>
            project.Items.OrderBy(_ => rng.Next()).Take(rng.Next(1, most + 1)).Select(i => i.Id).ToList();

        private static List<Guid> SomeRows(Random rng, Project project, int most) =>
            project.Items.Select(i => i.TrackId).Distinct().OrderBy(_ => rng.Next()).Take(rng.Next(1, most + 1)).ToList();

        /// <summary>Every gap on any row: before its first clip and after each one.</summary>
        private static List<TimelineGap> Gaps(EditorSession session)
        {
            var gaps = new List<TimelineGap>();
            foreach (var row in session.Project.Tracks.Select(t => t.Id))
            {
                if (session.FindGap(row, 0) is { } first)
                    gaps.Add(first);
                foreach (var clip in session.Project.Items.Where(i => i.TrackId == row))
                {
                    if (session.FindGap(row, clip.TimelineEndTicks) is { } gap)
                        gaps.Add(gap);
                }
            }
            return gaps.Distinct().ToList();
        }

        /// <summary>Applies the operation at random and says what it did. Null when it had nothing to
        /// work on.</summary>
        private static string Apply(Op op, EditorSession session, Random rng)
        {
            var project = session.Project;
            var clip = Pick(rng, project.Items);
            var end = project.GetDurationTicks();

            switch (op)
            {
                case Op.MoveLinked:
                    return Move(session, TimelineOps.TimeLinked(project, new[] { clip.Id }), rng);

                case Op.MoveAlone:
                    return Move(session, SomeClips(rng, project, 3), rng);

                case Op.MoveThenCancel:
                {
                    var before = project.ToJson();
                    using (var gesture = session.BeginGesture("Move"))
                    {
                        session.MoveItems(new[] { clip.Id }, TimeIn(rng, 0, 12 * Sec) - 6 * Sec);
                        gesture.Cancel();
                    }
                    Assert.True(session.Project.ToJson() == before, "a cancelled drag changed the project: " + Difference(before, session.Project.ToJson()));
                    return null; // checked here: there is no undo entry for the checks after an edit to use
                }

                case Op.Trim:
                    return Trim(session, clip, rng);

                case Op.SplitClip:
                {
                    var t = TimeIn(rng, clip.TimelineStartTicks, clip.TimelineEndTicks);
                    session.SplitItemAt(clip.Id, t);
                    return $"{Span(clip)} at {t}";
                }

                case Op.SplitEverything:
                {
                    var t = TimeIn(rng, 0, end);
                    session.SplitAtPlayhead(t);
                    return $"at {t}";
                }

                case Op.Delete:
                {
                    var ids = SomeClips(rng, project, 3);
                    session.DeleteItems(ids);
                    return $"{ids.Count} clips";
                }

                case Op.RippleDelete:
                {
                    if (end - clip.DurationTicks < ShortestTimeline)
                        return null;
                    var what = Span(clip);
                    var before = project.ToJson();
                    var can = session.CanRippleDeleteItem(clip.Id);
                    session.RippleDeleteItem(clip.Id);
                    Assert.True(can == (session.Project.ToJson() != before), $"CanRippleDeleteItem said {can} of {what}");
                    return what;
                }

                case Op.CloseGap:
                {
                    if (Gaps(session) is not { Count: > 0 } gaps)
                        return null;
                    var gap = Pick(rng, gaps);
                    var can = session.CanCloseGap(gap);
                    Assert.True(can == session.CloseGap(gap), $"CanCloseGap said {can} of {gap}");
                    return gap.ToString();
                }

                case Op.RippleCloseGap:
                {
                    if (Gaps(session) is not { Count: > 0 } gaps)
                        return null;
                    var gap = Pick(rng, gaps);
                    if (end - gap.DurationTicks < ShortestTimeline)
                        return null;
                    session.RippleCloseGap(gap);
                    return gap.ToString();
                }

                case Op.Rejoin:
                {
                    var gaps = Gaps(session).Where(g => TimelineOps.CanRejoinGap(project, g)).ToList();
                    if (gaps.Count == 0)
                    {
                        // a cut and a drag is the usual way to a gap that heals
                        var t = TimeIn(rng, clip.TimelineStartTicks, clip.TimelineEndTicks);
                        return session.SplitItemAt(clip.Id, t) ? $"cut {Span(clip)} at {t}" : null;
                    }
                    var gap = Pick(rng, gaps);
                    session.RejoinGap(gap);
                    return gap.ToString();
                }

                case Op.FreezeFill:
                {
                    var fromLeft = rng.Next(2) == 0;
                    var gaps = Gaps(session).Where(g => TimelineOps.CanFillGapWithFreeze(project, g, fromLeft)).ToList();
                    if (gaps.Count == 0)
                        return null;
                    var gap = Pick(rng, gaps);
                    session.FillGapWithFreeze(gap, fromLeft);
                    return $"{gap} fromLeft={fromLeft}";
                }

                case Op.Extend:
                {
                    var fromLeft = rng.Next(2) == 0;
                    var gaps = Gaps(session).Where(g => TimelineOps.CanExtendIntoGap(project, g, fromLeft)).ToList();
                    if (gaps.Count == 0)
                        return null;
                    var gap = Pick(rng, gaps);
                    session.ExtendIntoGap(gap, fromLeft);
                    return $"{gap} fromLeft={fromLeft}";
                }

                case Op.ClipSpeed:
                {
                    var clocked = project.Items.Where(i => i.Content is SourceClockContent { Freeze: false }).ToList();
                    if (clocked.Count == 0)
                        return null;
                    var target = Pick(rng, clocked);
                    var speed = Pick(rng, Speeds);
                    session.SetItemSpeed(target.Id, speed);
                    return $"{Span(target)} to {speed}";
                }

                case Op.SpeedFactor:
                {
                    var speeds = project.Items.Where(i => i.Content is SpeedContent).ToList();
                    if (speeds.Count == 0)
                        return null;
                    var target = Pick(rng, speeds);
                    var factor = Pick(rng, Speeds);
                    session.SetSpeedFactor(target.Id, factor);
                    return $"{Span(target)} to {factor}";
                }

                case Op.PitchCorrect:
                {
                    var speeds = project.Items.Where(i => i.Content is SpeedContent).ToList();
                    if (speeds.Count == 0)
                        return null;
                    var target = Pick(rng, speeds);
                    session.SetSpeedPitchCorrect(target.Id, !((SpeedContent)target.Content).PitchCorrect);
                    return Span(target);
                }

                case Op.WarpExempt:
                {
                    var sounds = project.Items.Where(i => i.Content is MediaContent && project.TrackOf(i)?.Kind == TrackKind.Audio).ToList();
                    if (sounds.Count == 0)
                        return null;
                    var target = Pick(rng, sounds);
                    var value = !((MediaContent)target.Content).SpeedWarpExempt;
                    session.SetSpeedWarpExempt(target.Id, value);
                    return $"{Span(target)} to {value}";
                }

                case Op.Copy:
                    return session.CopyItems(SomeClips(rng, project, 4)) ? "copied" : null;

                case Op.Cut:
                {
                    var ids = SomeClips(rng, project, 4);
                    return session.CutItems(ids) ? $"{ids.Count} clips" : null;
                }

                case Op.PasteAt:
                {
                    if (session.ClipboardSize.Clips == 0)
                        return null;
                    var anchor = TimeIn(rng, 0, end + 5 * Sec);
                    session.PasteAt(anchor);
                    return $"at {anchor}";
                }

                case Op.PasteIntoGap:
                {
                    var gaps = Gaps(session).Where(session.CanPasteIntoGap).ToList();
                    if (gaps.Count == 0)
                        return null;
                    var gap = Pick(rng, gaps);
                    session.PasteIntoGap(gap);
                    return gap.ToString();
                }

                case Op.Select:
                    session.SelectMany(SomeClips(rng, project, 4));
                    return "selected";

                case Op.SelectLinked:
                    session.SelectLinkedClips();
                    return "linked";

                case Op.Group:
                {
                    var rows = SomeRows(rng, project, 3);
                    if (!session.CanGroupTracks(rows))
                        return null;
                    session.GroupTracks(rows);
                    return $"{rows.Count} rows";
                }

                case Op.Ungroup:
                    session.UngroupTracks(SomeRows(rng, project, 2));
                    return "ungrouped";

                case Op.UngroupAll:
                    session.UngroupAll(SomeRows(rng, project, 1));
                    return "all";

                case Op.Hide:
                {
                    var row = Pick(rng, project.Tracks);
                    session.SetTrackHidden(row.Id, !row.Hidden);
                    return row.Name;
                }

                case Op.Mute:
                {
                    var row = Pick(rng, project.Tracks);
                    session.SetTrackMuted(row.Id, !row.Muted);
                    return row.Name;
                }

                case Op.Denoise:
                {
                    var row = Pick(rng, project.Tracks);
                    session.SetTrackDenoise(row.Id, !row.Denoise);
                    return row.Name;
                }

                case Op.DenoiseStrength:
                {
                    var row = Pick(rng, project.Tracks);
                    session.SetTrackDenoiseStrength(row.Id, rng.NextDouble());
                    return row.Name;
                }

                case Op.Lock:
                {
                    var row = Pick(rng, project.Tracks);
                    session.SetTrackPlacementLocked(row.Id, !row.PlacementLocked);
                    return row.Name;
                }

                case Op.Rename:
                {
                    var row = Pick(rng, project.Tracks);
                    session.RenameTrack(row.Id, row.Name + "+");
                    return row.Name;
                }

                case Op.DuplicateRow:
                    return session.DuplicateTrack(Pick(rng, project.Tracks).Id) ? "duplicated" : null;

                case Op.DeleteRow:
                    // two thirds of the rows would soon be gone with this in every run
                    return project.Tracks.Count > 6 && session.DeleteTrack(Pick(rng, project.Tracks).Id) ? "deleted" : null;

                case Op.ReorderRow:
                {
                    var index = rng.Next(project.Tracks.Count);
                    return session.MoveTrackToIndex(Pick(rng, project.Tracks).Id, index) ? $"to {index}" : null;
                }

                case Op.MoveRowLayer:
                    return session.MoveTrackLayer(Pick(rng, project.Tracks).Id, rng.Next(2) == 0) ? "moved" : null;

                case Op.AddText:
                    return Added(session.AddText(TimeIn(rng, 0, end), 3 * Sec));

                case Op.AddImage:
                    return Added(session.AddImage(TestPath.Native(@"C:\media\logo.png"), TimeIn(rng, 0, end), 3 * Sec));

                case Op.AddObscure:
                    return Added(session.AddObscure(TimeIn(rng, 0, end), 3 * Sec));

                case Op.AddShape:
                {
                    var kinds = Enum.GetValues<ShapeKind>();
                    return Added(session.AddShape(TimeIn(rng, 0, end), 3 * Sec, Pick(rng, kinds)));
                }

                case Op.AddWallpaper:
                    return Added(session.AddBackground(TimeIn(rng, 0, end), 3 * Sec));

                case Op.AddSpeed:
                {
                    var t = TimeIn(rng, 0, end);
                    return session.CanAddSpeedEffect(t) ? Added(session.AddSpeedEffect(t, 3 * Sec)) : null;
                }

                case Op.AddZoom:
                    return Added(session.AddZoomEffect(TimeIn(rng, 0, end), 3 * Sec));

                case Op.AddCursor:
                    return Added(session.AddCursorTrack());

                case Op.AddKeys:
                    return Added(session.AddKeyboardTrack());

                case Op.Import:
                {
                    var created = session.ImportMedia(TestPath.Native(@"C:\media\clip.mp4"), ClipProbe(), TimeIn(rng, 0, end));
                    return $"{created?.Count} clips";
                }

                case Op.RemoveImport:
                {
                    var imported = project.Sources.Where(s => s.Path == TestPath.Native(@"C:\media\clip.mp4")).ToList();
                    return imported.Count > 0 && session.RemoveSource(imported[0].Id) ? "removed" : null;
                }

                case Op.Relink:
                {
                    // a file of a different shape: what it lacks keeps its old description
                    var source = Pick(rng, project.Sources);
                    var moved = TestPath.Native($@"C:\media\moved-{rng.Next(1000)}.mp4");
                    session.RelinkSource(source.Id, moved, ClipProbe());
                    return moved;
                }

                case Op.SourceRows:
                    session.SetSourceRowsEnabled(Pick(rng, project.Sources).Id, rng.Next(2) == 0);
                    return "set";

                case Op.Placement:
                    return session.EditPlacement(clip.Id, PlacementEdit(clip, rng), null, null) ? Span(clip) : null;

                case Op.Opacity:
                    session.EditFeed(clip.Id, i => (i.Transform ??= new Transform()).Opacity = 0.25 + rng.NextDouble() / 2, null, null);
                    return Span(clip);

                case Op.Volume:
                    session.EditItem(clip.Id, i => i.Volume = rng.NextDouble());
                    return Span(clip);

                case Op.Effect:
                    session.SetItemEffect(clip.Id, rng.Next(2) == 0 ? null : new VideoEffect { Kind = Pick(rng, Effects), Amount = rng.NextDouble() });
                    return Span(clip);

                case Op.FrameRate:
                    return session.SetOutputFrameRate(Pick(rng, FrameRates), 1) ? "set" : null;

                case Op.OutputSize:
                    return session.SetOutputSize(1280 + 2 * rng.Next(200), 720 + 2 * rng.Next(200)) ? "resized" : null;

                case Op.VoiceTake:
                    return Added(Record(session, TimeIn(rng, 0, end), TimeIn(rng, Sec / 2, 3 * Sec)));

                case Op.VoiceTakeCancelled:
                {
                    var before = project.ToJson();
                    var take = session.BeginVoiceTake(TimeIn(rng, 0, end));
                    session.UpdateVoiceTake(take, Sec);
                    session.CancelVoiceTake(take);
                    // order values need not be contiguous, and the row a take adds steps the speed row
                    // up for good, so this one compares where the rows sit and not the numbers
                    Assert.True(RanksOnly(session.Project.ToJson()) == RanksOnly(before),
                        "a cancelled take changed the project: " + Difference(RanksOnly(before), RanksOnly(session.Project.ToJson())));
                    return null; // as for a cancelled drag
                }

                case Op.Undo:
                    if (!session.CanUndo)
                        return null;
                    session.Undo();
                    return "undone";

                case Op.Redo:
                    if (!session.CanRedo)
                        return null;
                    session.Redo();
                    return "redone";

                default:
                    throw new ArgumentOutOfRangeException(nameof(op));
            }
        }

        private static string Added(Item item) => item == null ? null : Span(item);

        /// <summary>A write of something the lock covers, of the kinds the gizmo and the inspector make:
        /// position, size, rotation, crop, aspect, text size and line ends.</summary>
        private static Action<Item> PlacementEdit(Item clip, Random rng)
        {
            var value = rng.NextDouble();
            return rng.Next(6) switch
            {
                0 => i => i.Transform.X = value,
                1 => i => i.Transform.Scale = 0.1 + value / 2,
                2 => i => i.Transform.Rotation = value * 90,
                3 => i => i.Transform.Crop = new CropRect { Left = value / 4 },
                4 => i => i.Transform.Aspect = 0.5 + value,
                _ => clip.Content switch
                {
                    TextContent => i => ((TextContent)i.Content).Size = 12 + value * 100,
                    ShapeContent => i => ((ShapeContent)i.Content).Bend = value,
                    _ => i => i.Transform.Y = value,
                },
            };
        }

        /// <summary>A drag of these clips, in one go or a pointer step at a time, and I8.</summary>
        private static string Move(EditorSession session, IReadOnlyCollection<Guid> ids, Random rng)
        {
            var rows = session.Project.Items.ToDictionary(i => i.Id, i => i.TrackId);
            var delta = TimeIn(rng, 0, 12 * Sec) - 6 * Sec;

            if (rng.Next(2) == 0)
            {
                session.MoveItems(ids, delta);
            }
            else
            {
                using var gesture = session.BeginGesture("Move");
                for (var step = 0; step < 3; step++)
                    session.MoveItems(ids, delta / 3);
                gesture.Commit();
            }

            foreach (var item in session.Project.Items)
                Assert.True(!rows.TryGetValue(item.Id, out var row) || row == item.TrackId, "a drag changed a clip's row");
            return $"{ids.Count} clips by {delta}";
        }

        /// <summary>A trim of either edge, growing or shrinking, in one go or a pointer step at a time.</summary>
        private static string Trim(EditorSession session, Item clip, Random rng)
        {
            var delta = TimeIn(rng, 0, 10 * Sec) - 5 * Sec;
            var atStart = rng.Next(2) == 0;
            var what = $"{Span(clip)} {(atStart ? "start" : "end")} by {delta}";
            void Step(long d)
            {
                if (atStart)
                    session.TrimItemStart(clip.Id, d);
                else
                    session.TrimItemEnd(clip.Id, d);
            }

            if (rng.Next(2) == 0)
            {
                Step(delta);
            }
            else
            {
                using var gesture = session.BeginGesture("Trim");
                for (var step = 0; step < 3; step++)
                    Step(delta / 3);
                gesture.Commit();
            }
            return what;
        }
    }
}
