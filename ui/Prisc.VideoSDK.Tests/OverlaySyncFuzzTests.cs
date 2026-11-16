using System;
using System.Collections.Generic;
using System.Linq;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Grouped overlays stay in step (invariant I9), under random edits to a grouped screen, cursor
    /// and keys recording: after each edit that went through, wherever an overlay and a screen clip
    /// of its recording are both active they show the same moment. Half the runs have a second
    /// recording later on the same screen row, with its own overlays on the group, and half have
    /// another recording in a group of its own over the same stretch of timeline, since a ripple on
    /// one group slides the other. Permanent: every operation that can edit a screen clip's mapping
    /// belongs in <see cref="Op"/>.
    ///
    /// <para>Left out on purpose, because they drift by design: Alt move, ungroup, and direct edits
    /// of an overlay. A paste here is of one screen clip, which is all that goes into a gap on the
    /// screen row.</para>
    ///
    /// <para>One outcome is undone as well (see <see cref="UndoDrop"/>): a time-linked drag that
    /// drops its clips onto clips of the group it did not bring, or a ripple whose clamp holds an
    /// overlay back while a later screen clip of its recording slides under it. That pair was never
    /// in step and no rebase happens on either edit, so it shows out of step until the next edit
    /// there.</para>
    /// </summary>
    [Collection("EditorSession clipboard")] // its pastes share the one static clipboard
    public class OverlaySyncFuzzTests
    {
        private const long Sec = TimeSpan.TicksPerSecond;
        private const int Seeds = 400;
        private const int StepsPerSeed = 40;

        /// <summary>The allowlist.</summary>
        private enum Op
        {
            MoveTimeLinked,
            CutEverything,
            SplitScreenClip,
            DeleteScreenClip,
            TrimScreenClip,
            RippleDelete,
            CloseGap,
            RippleCloseGap,
            Rejoin,
            CutDragRejoin,
            FreezeFill,
            Extend,
            ScreenSpeed,
            PasteIntoGap,
            TrimOtherGroup,
            RippleDeleteOtherGroup,
        }

        /// <summary>Everything, with the operations that most often find nothing to do or get
        /// refused tried more often, so that each is reached a useful number of times.</summary>
        private static readonly Op[] Ops = Enum.GetValues<Op>()
            .Concat(new[] { Op.MoveTimeLinked, Op.MoveTimeLinked, Op.CloseGap, Op.CloseGap, Op.Rejoin, Op.Rejoin, Op.Rejoin })
            .ToArray();

        /// <summary>Below this a run stops taking time out of the timeline, or it would soon have
        /// nothing left to edit.</summary>
        private const long ShortestTimeline = 30 * Sec;
        private static readonly double[] Speeds = { 0.5, 0.75, 1.0, 1.37, 1.5, 2.0, 3.0 };

        [Fact]
        public void Grouped_overlays_stay_in_step_with_the_screen_under_any_edit()
        {
            var applied = Enum.GetValues<Op>().ToDictionary(op => op, _ => 0);
            var skippedDrops = 0;

            for (var seed = 0; seed < Seeds; seed++)
            {
                var rng = new Random(seed);
                var session = NewRecording(out var screenRow);
                if (seed % 2 == 1)
                    session = WithSecondRecording(session, screenRow);
                if (seed % 4 >= 2)
                    session = WithOtherGroup(session);
                var log = new List<string>();

                for (var step = 0; step < StepsPerSeed; step++)
                {
                    if (ScreenClips(session, screenRow).Count == 0 || Overlays(session).Count == 0)
                        break;

                    var op = Ops[rng.Next(Ops.Length)];
                    var before = session.Project.ToJson();
                    var what = Apply(op, session, screenRow, rng, ref skippedDrops);
                    if (what == null || session.Project.ToJson() == before)
                        continue;

                    applied[op]++;
                    log.Add($"{op} {what}");

                    var problems = session.Project.Validate();
                    Assert.True(problems.Count == 0, $"seed {seed}: invalid after:\n{String.Join("\n", log)}\n{String.Join("\n", problems)}");
                    AssertInStep(session, seed, log);
                }
            }

            // the run has to have reached everything it claims to cover
            foreach (var (op, count) in applied)
                Assert.True(count >= 50, $"{op} only went through {count} times, of: {String.Join(", ", applied)}");
            Assert.True(skippedDrops < applied[Op.MoveTimeLinked] / 2, $"{skippedDrops} drops skipped, of: {String.Join(", ", applied)}");
        }

        private static EditorSession NewRecording(out Guid screenRow)
        {
            var group = Guid.NewGuid();
            var screen = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0, GroupId = group };
            var audio = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 1, GroupId = group };
            screenRow = screen.Id;

            var source = new Source
            {
                Id = Guid.NewGuid(),
                Path = @"C:\rec\in.mp4",
                InputCapturePath = TestPath.ExistingCapture,
                Streams =
                {
                    new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, DurationTicks = 200 * Sec },
                    new SourceStream { Index = 1, Kind = StreamKind.Audio, DurationTicks = 200 * Sec },
                },
            };

            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources = { source },
                Tracks = { screen, audio },
                Items =
                {
                    new Item
                    {
                        Id = Guid.NewGuid(), TrackId = screen.Id, TimelineStartTicks = 0, DurationTicks = 60 * Sec,
                        Content = new MediaContent { SourceId = source.Id, StreamIndex = 0, SourceInTicks = 20 * Sec },
                    },
                    new Item
                    {
                        Id = Guid.NewGuid(), TrackId = audio.Id, TimelineStartTicks = 0, DurationTicks = 60 * Sec,
                        Content = new MediaContent { SourceId = source.Id, StreamIndex = 1, SourceInTicks = 20 * Sec },
                    },
                },
            };

            project.Normalize();
            var session = new EditorSession(project, null, save => save());
            Assert.NotNull(session.AddKeyboardTrack());
            Assert.NotNull(session.AddCursorTrack());
            return session;
        }

        /// <summary>Another recording after a short gap on the same screen row, as a paste into a gap
        /// leaves it, with cursor and keys rows of its own in the group.</summary>
        private static EditorSession WithSecondRecording(EditorSession session, Guid screenRow)
        {
            var project = session.Project;
            var other = new Source
            {
                Id = Guid.NewGuid(),
                Path = @"C:\rec\other.mp4",
                InputCapturePath = TestPath.ExistingCapture,
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, DurationTicks = 200 * Sec } },
            };
            project.Sources.Add(other);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(), TrackId = screenRow, TimelineStartTicks = 70 * Sec, DurationTicks = 30 * Sec,
                Content = new MediaContent { SourceId = other.Id, StreamIndex = 0, SourceInTicks = 5 * Sec },
            });

            project.Normalize();
            Assert.Empty(project.Validate());
            var result = new EditorSession(project, null, save => save());
            Assert.NotNull(result.AddKeyboardTrack());
            Assert.NotNull(result.AddCursorTrack());
            return result;
        }

        /// <summary>Another recording in a group of its own, on its own row, over the same time as the
        /// first: its screen clips can start inside a span cut out of the first group.</summary>
        private static EditorSession WithOtherGroup(EditorSession session)
        {
            var project = session.Project;
            var other = new Source
            {
                Id = Guid.NewGuid(),
                Path = @"C:\rec\group.mp4",
                InputCapturePath = TestPath.ExistingCapture,
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080, DurationTicks = 200 * Sec } },
            };
            var row = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Other screen", Order = project.Tracks.Max(t => t.Order) + 1 };
            project.Sources.Add(other);
            project.Tracks.Add(row);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(), TrackId = row.Id, TimelineStartTicks = 15 * Sec, DurationTicks = 30 * Sec,
                Content = new MediaContent { SourceId = other.Id, StreamIndex = 0, SourceInTicks = 40 * Sec },
            });

            project.Normalize();
            Assert.Empty(project.Validate());
            var result = new EditorSession(project, null, save => save());
            Assert.NotNull(result.AddKeyboardTrack());
            Assert.NotNull(result.AddCursorTrack());
            return result;
        }

        /// <summary>Whether the clip is a screen clip a cursor of its recording draws through, on any
        /// row.</summary>
        private static bool IsScreen(Project project, Item item) =>
            item.Content is MediaContent media
            && FrameComposer.IsCursorHost(project, project.Sources.Single(s => s.Id == media.SourceId), item);

        private static List<Item> OtherGroupScreens(EditorSession session, Guid screenRow) =>
            session.Project.Items.Where(i => i.TrackId != screenRow && IsScreen(session.Project, i)).ToList();

        private static List<Item> ScreenClips(EditorSession session, Guid screenRow) =>
            session.Project.Items.Where(i => i.TrackId == screenRow).ToList();

        private static List<Item> Overlays(EditorSession session) =>
            session.Project.Items.Where(i => i.Content is InputOverlayContent).ToList();

        private static T Pick<T>(Random rng, IReadOnlyList<T> options) => options[rng.Next(options.Count)];

        /// <summary>A time on a 1 ms grid in <c>[from, to)</c>.</summary>
        private static long TimeIn(Random rng, long from, long to) =>
            to <= from ? from : from + rng.NextInt64((to - from) / TimeSpan.TicksPerMillisecond + 1) * TimeSpan.TicksPerMillisecond;

        /// <summary>Every gap on a row of the group, for the operations that work on one.</summary>
        private static List<TimelineGap> Gaps(EditorSession session, IEnumerable<Guid> rows)
        {
            var gaps = new List<TimelineGap>();
            foreach (var row in rows)
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

        /// <summary>Applies the operation at random, and says what it did. Null when it had nothing to
        /// work on.</summary>
        private static string Apply(Op op, EditorSession session, Guid screenRow, Random rng, ref int skippedDrops)
        {
            var clips = ScreenClips(session, screenRow);
            var clip = Pick(rng, clips);
            var end = session.Project.GetDurationTicks();
            var groupRows = session.Project.Tracks.Where(t => t.GroupId != null).Select(t => t.Id).ToList();

            switch (op)
            {
                case Op.MoveTimeLinked:
                    return MoveTimeLinked(session, screenRow, rng, ref skippedDrops);

                case Op.CutEverything:
                {
                    var t = TimeIn(rng, 0, end);
                    session.SplitAtPlayhead(t);
                    return $"at {t}";
                }

                case Op.SplitScreenClip:
                {
                    var t = TimeIn(rng, clip.TimelineStartTicks, clip.TimelineEndTicks);
                    session.SplitItemAt(clip.Id, t);
                    return $"{Span(clip)} at {t}";
                }

                case Op.DeleteScreenClip:
                    // the last one would leave the rest of the run nothing to edit
                    if (clips.Count == 1)
                        return null;
                    session.DeleteItems(new[] { clip.Id });
                    return Span(clip);

                case Op.TrimScreenClip:
                    return Trim(session, clip, rng);

                case Op.RippleDelete:
                    return end - clip.DurationTicks < ShortestTimeline ? null : RippleDelete(session, clip, ref skippedDrops);

                case Op.CloseGap:
                {
                    var gaps = Gaps(session, groupRows);
                    if (gaps.Count == 0)
                        return null;
                    var gap = Pick(rng, gaps);
                    session.CloseGap(gap);
                    return gap.ToString();
                }

                case Op.RippleCloseGap:
                {
                    var gaps = Gaps(session, groupRows);
                    if (gaps.Count == 0)
                        return null;
                    var gap = Pick(rng, gaps);
                    if (end - gap.DurationTicks < ShortestTimeline)
                        return null;
                    session.RippleCloseGap(gap);
                    return gap.ToString();
                }

                case Op.Rejoin:
                {
                    var gaps = Gaps(session, new[] { screenRow }).Where(g => TimelineOps.CanRejoinGap(session.Project, g)).ToList();
                    if (gaps.Count == 0)
                        return null;
                    var gap = Pick(rng, gaps);
                    session.RejoinGap(gap);
                    return gap.ToString();
                }

                case Op.CutDragRejoin:
                    return CutDragRejoin(session, screenRow, clips, rng, ref skippedDrops);

                case Op.FreezeFill:
                {
                    var gaps = Gaps(session, new[] { screenRow });
                    if (gaps.Count == 0)
                        return null;
                    var gap = Pick(rng, gaps);
                    var fromLeft = rng.Next(2) == 0;
                    if (!TimelineOps.CanFillGapWithFreeze(session.Project, gap, fromLeft))
                        return null;
                    session.FillGapWithFreeze(gap, fromLeft);
                    return $"{gap} fromLeft={fromLeft}";
                }

                case Op.Extend:
                {
                    var gaps = Gaps(session, new[] { screenRow });
                    if (gaps.Count == 0)
                        return null;
                    var gap = Pick(rng, gaps);
                    var fromLeft = rng.Next(2) == 0;
                    if (!TimelineOps.CanExtendIntoGap(session.Project, gap, fromLeft))
                        return null;
                    session.ExtendIntoGap(gap, fromLeft);
                    return $"{gap} fromLeft={fromLeft}";
                }

                case Op.ScreenSpeed:
                {
                    var speed = Pick(rng, Speeds);
                    session.SetItemSpeed(clip.Id, speed);
                    return $"{Span(clip)} to {speed}";
                }

                case Op.PasteIntoGap:
                {
                    var gaps = Gaps(session, new[] { screenRow });
                    if (gaps.Count == 0)
                        return null;
                    var gap = Pick(rng, gaps);
                    Assert.True(session.CopyItems(new[] { clip.Id }));
                    session.PasteIntoGap(gap);
                    return $"{Span(clip)} into {gap}";
                }

                case Op.TrimOtherGroup:
                {
                    var others = OtherGroupScreens(session, screenRow);
                    return others.Count == 0 ? null : Trim(session, Pick(rng, others), rng);
                }

                case Op.RippleDeleteOtherGroup:
                {
                    var others = OtherGroupScreens(session, screenRow);
                    if (others.Count == 0)
                        return null;
                    var other = Pick(rng, others);
                    return end - other.DurationTicks < ShortestTimeline ? null : RippleDelete(session, other, ref skippedDrops);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(op));
            }
        }

        private static string Span(Item clip) => $"[{clip.TimelineStartTicks}, {clip.TimelineEndTicks})";

        private static Project Snapshot(EditorSession session) => Project.FromJson(session.Project.ToJson());

        private static string RippleDelete(EditorSession session, Item clip, ref int skippedDrops)
        {
            var before = Snapshot(session);
            session.RippleDeleteItem(clip.Id);
            return UndoDrop(session, before, ref skippedDrops) ? null : Span(clip);
        }

        /// <summary>A drag of a clip with what overlaps it on the group, either all in one go or a
        /// pointer step at a time. Undone when it drops a screen clip onto an overlay of its recording
        /// it did not bring, or an overlay onto such a screen clip.</summary>
        private static string MoveTimeLinked(EditorSession session, Guid screenRow, Random rng, ref int skippedDrops)
        {
            var seed = Pick(rng, session.Project.Items);
            var before = Snapshot(session);
            var ids = TimelineOps.TimeLinked(session.Project, new[] { seed.Id });
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

            return UndoDrop(session, before, ref skippedDrops) ? null : $"{ids.Count} clips by {delta}";
        }

        /// <summary>Undoes the edit just made when it brought together an overlay and a screen clip of
        /// its recording that did not overlap in <paramref name="before"/>: see the class remarks.
        /// Returns whether it did.</summary>
        private static bool UndoDrop(EditorSession session, Project before, ref int skippedDrops)
        {
            static bool Overlap(Item a, Item b) => a.TimelineStartTicks < b.TimelineEndTicks && b.TimelineStartTicks < a.TimelineEndTicks;

            var project = session.Project;
            var was = before.Items.ToDictionary(i => i.Id);
            var screens = project.Items.Where(i => IsScreen(project, i) && was.ContainsKey(i.Id)).ToList();
            var dropped = project.Items.Any(i => i.Content is InputOverlayContent overlay && was.TryGetValue(i.Id, out var wasOverlay)
                && screens.Any(s => ((MediaContent)s.Content).SourceId == overlay.SourceId && Overlap(i, s) && !Overlap(wasOverlay, was[s.Id])));
            if (!dropped)
                return false;

            skippedDrops++;
            session.Undo();
            return true;
        }

        /// <summary>Cuts a clip with room after it, drags the right half into that room and rejoins
        /// the two: the way to a rejoin that slides the rest of the row. The gaps other edits leave
        /// are mostly what was cut out, and healing those moves nothing.</summary>
        private static string CutDragRejoin(EditorSession session, Guid screenRow, List<Item> clips, Random rng, ref int skippedDrops)
        {
            long RoomAfter(Item c) => clips.Where(n => n.TimelineStartTicks >= c.TimelineEndTicks)
                .Select(n => n.TimelineStartTicks - c.TimelineEndTicks).DefaultIfEmpty(0).Min();

            var roomy = clips.Where(c => RoomAfter(c) >= Sec && c.DurationTicks > 2 * Sec).ToList();
            if (roomy.Count == 0)
                return null;

            var clip = Pick(rng, roomy);
            var room = RoomAfter(clip);
            var cutAt = TimeIn(rng, clip.TimelineStartTicks + Sec, clip.TimelineEndTicks - Sec);
            session.SplitAtPlayhead(cutAt);
            var right = ScreenClips(session, screenRow).FirstOrDefault(i => i.TimelineStartTicks == cutAt);
            if (right == null)
                return null;

            var before = Snapshot(session);
            var ids = TimelineOps.TimeLinked(session.Project, new[] { right.Id });
            var delta = TimeIn(rng, Sec / 2, room);
            session.MoveItems(ids, delta);
            if (UndoDrop(session, before, ref skippedDrops))
                return $"cut at {cutAt}, drag by {delta} undone";

            if (session.FindGap(screenRow, cutAt) is not { } gap || !TimelineOps.CanRejoinGap(session.Project, gap))
                return $"cut at {cutAt}, drag by {delta}";

            session.RejoinGap(gap);
            return $"cut at {cutAt}, drag by {delta}, rejoined";
        }

        /// <summary>A trim of either edge, growing or shrinking, either all in one go or a pointer step
        /// at a time.</summary>
        private static string Trim(EditorSession session, Item clip, Random rng)
        {
            var delta = TimeIn(rng, 0, 10 * Sec) - 5 * Sec;
            var atStart = rng.Next(2) == 0;
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

            return $"{Span(clip)} {(atStart ? "start" : "end")} by {delta}";
        }

        private static void AssertInStep(EditorSession session, int seed, List<string> log)
        {
            var project = session.Project;

            foreach (var overlay in Overlays(session))
            {
                var clock = (SourceClockContent)overlay.Content;
                var source = project.Sources.Single(s => s.Id == clock.SourceId);
                var screens = project.Items.Where(s => FrameComposer.IsCursorHost(project, source, s)).ToList();
                foreach (var t in Instants(overlay, screens))
                {
                    var host = FrameComposer.ResolveCursorHost(project, source, overlay, t);
                    if (host == null)
                    {
                        Assert.DoesNotContain(screens, s => s.Covers(t));
                        continue;
                    }

                    var hostTicks = FrameComposer.SourceTimeTicks((MediaContent)host.Content, host, t);
                    var overlayTicks = FrameComposer.SourceTimeTicks(clock, overlay, t);
                    Assert.True(OverlayClock.SameInstant(hostTicks, overlayTicks),
                        $"seed {seed}: {overlay.Content.GetType().Name} {Span(overlay)} shows {overlayTicks} at {t} " +
                        $"over a screen clip {Span(host)} showing {hostTicks}, after:\n{String.Join("\n", log)}");
                }
            }
        }

        /// <summary>Where a mismatch would show: both ends and the middle of the overlay, and both
        /// sides of each screen clip's edges inside it.</summary>
        private static IEnumerable<long> Instants(Item overlay, List<Item> screens)
        {
            var times = new SortedSet<long>
            {
                overlay.TimelineStartTicks,
                overlay.TimelineStartTicks + overlay.DurationTicks / 2,
                overlay.TimelineEndTicks - 1,
            };
            foreach (var screen in screens)
            {
                foreach (var t in new[]
                         {
                             screen.TimelineStartTicks, screen.TimelineEndTicks - 1,
                             screen.TimelineStartTicks + screen.DurationTicks / 2,
                         })
                {
                    if (overlay.Covers(t))
                        times.Add(t);
                }
            }
            return times;
        }
    }
}
