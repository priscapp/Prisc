using System;
using System.Linq;
using Prisc.VideoSDK.Audio;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Prisc.VideoSDK.Playback;
using Prisc.VideoSDK.Render;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// <see cref="MediaContent.SpeedWarpExempt"/>: a clip on the output clock. The mixing half
    /// runs against a fake <see cref="IAudioSource"/> whose samples encode their source position
    /// (no FFmpeg): an exempt clip under a 2x speed item must read its source at one source
    /// second per OUTPUT second, stop where its output span ends, and change nothing at all
    /// under an identity warp. The editing half drives <see cref="EditorSession"/>: the re-fit
    /// of an exempt clip's project span when a speed item arrives, leaves, or is dragged, when
    /// the flag flips, and the in-point math of a trim under a warp.
    /// </summary>
    public class SpeedWarpExemptTests
    {
        private const int Rate = 48000;
        private const long Second = 10_000_000;

        private static long Ms(long ms) => ms * TimeSpan.TicksPerMillisecond;

        // ------------------------------------------------------------------------------ fixture

        /// <summary>Sample value is a pure function of the source position (ch0 = f(pos),
        /// ch1 = -f(pos)), so what a mixed sample should be is computable without a mixer.</summary>
        private sealed class PositionAudioSource : IAudioSource
        {
            public bool ReadSamples(Guid sourceId, int streamIndex, long sourcePosFrames,
                float[] dst, int frames, out int framesRead)
            {
                for (int i = 0; i < frames; i++)
                {
                    float v = HashValue(sourcePosFrames + i);
                    dst[i * 2] = v;
                    dst[i * 2 + 1] = -v;
                }
                framesRead = frames;
                return true;
            }
        }

        private static float HashValue(long pos) => ((pos * 31) % 997) / 1000f;

        private static Project NewProject() => new Project
        {
            Output = new OutputSettings { WidthPx = 64, HeightPx = 64, FpsNum = 30, FpsDen = 1, SampleRate = Rate },
        };

        private static Item AddAudioItem(Project project, long startTicks, long durationTicks, bool exempt)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Order = project.Tracks.Count };
            project.Tracks.Add(track);
            var item = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new MediaContent { SourceId = Guid.NewGuid(), StreamIndex = 0, SourceInTicks = 0, SpeedWarpExempt = exempt },
            };
            project.Items.Add(item);
            return item;
        }

        private static void AddSpeedItem(Project project, long startTicks, long durationTicks, double factor)
        {
            var track = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Order = project.Tracks.Count, Name = "Speed" };
            project.Tracks.Add(track);
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = startTicks,
                DurationTicks = durationTicks,
                Content = new SpeedContent { Factor = factor, PitchCorrect = false },
            });
        }

        /// <summary>Reads [0, totalFrames) through the render's warp stage in forward chunks.</summary>
        private static float[] RenderMix(Project project, long totalFrames, int chunkFrames = 1601)
        {
            var warp = TimeWarp.Build(project);
            var resampler = new WarpAudioResampler(new AudioMixer(project, new PositionAudioSource(), warp), warp, Rate);
            var result = new float[totalFrames * AudioMixer.Channels];
            var chunk = new float[chunkFrames * AudioMixer.Channels];
            long pos = 0;
            while (pos < totalFrames)
            {
                int n = (int)Math.Min(chunkFrames, totalFrames - pos);
                resampler.ReadChunk(pos, n, chunk);
                Array.Copy(chunk, 0, result, pos * AudioMixer.Channels, n * AudioMixer.Channels);
                pos += n;
            }
            return result;
        }

        // ------------------------------------------------------------------------------- mixing

        [Fact]
        public void Exempt_clip_reads_one_source_second_per_output_second_under_2x()
        {
            // 2s of project at 2x is 1s of output; the exempt clip covers that second and reads
            // source frame o at output frame o, verbatim: no resampling touches it.
            var project = NewProject();
            AddAudioItem(project, 0, 2 * Second, exempt: true);
            AddSpeedItem(project, 0, 2 * Second, 2.0);

            var warp = TimeWarp.Build(project);
            Assert.Equal(Second, warp.ToOutput(2 * Second));

            var dst = RenderMix(project, Rate);
            for (long o = 0; o < Rate; o++)
            {
                Assert.Equal(HashValue(o), dst[o * 2]);
                Assert.Equal(-HashValue(o), dst[o * 2 + 1]);
            }
        }

        [Fact]
        public void Exempt_clip_falls_silent_where_its_output_span_ends()
        {
            var project = NewProject();
            AddAudioItem(project, 0, 2 * Second, exempt: true);
            AddSpeedItem(project, 0, 2 * Second, 2.0);

            // past the 2x span the warp continues at speed 1, but the clip's output span was
            // [0, 1s): nothing of it plays after that
            var dst = RenderMix(project, 2 * Rate);
            Assert.NotEqual(0f, dst[(Rate - 1) * 2]);
            for (long o = Rate; o < 2 * Rate; o++)
            {
                Assert.Equal(0f, dst[o * 2]);
                Assert.Equal(0f, dst[o * 2 + 1]);
            }
        }

        [Fact]
        public void Mixer_keeps_exempt_clips_off_the_project_clock()
        {
            var project = NewProject();
            AddAudioItem(project, 0, 2 * Second, exempt: true);
            AddSpeedItem(project, 0, 2 * Second, 2.0);

            var mixer = new AudioMixer(project, new PositionAudioSource(), TimeWarp.Build(project));
            Assert.True(mixer.HasOutputItems);
            Assert.Equal(1, mixer.OutputItemCount);
            Assert.Equal(1, mixer.AudibleItemCount);

            // the project-clock mix (what the warp stages bend) has nothing in it
            var chunk = new float[100 * AudioMixer.Channels];
            mixer.MixChunk(0, 100, chunk);
            Assert.All(chunk, v => Assert.Equal(0f, v));

            // the output-clock mix carries the clip at output positions
            mixer.MixOutputChunk(1000, 100, chunk);
            for (int i = 0; i < 100; i++)
                Assert.Equal(HashValue(1000 + i), chunk[i * 2]);
        }

        [Fact]
        public void Flag_changes_nothing_under_an_identity_warp()
        {
            var flagged = NewProject();
            AddAudioItem(flagged, Second / 2, 2 * Second, exempt: true);
            var plain = NewProject();
            AddAudioItem(plain, Second / 2, 2 * Second, exempt: false);

            var mixer = new AudioMixer(flagged, new PositionAudioSource(), TimeWarp.Build(flagged));
            Assert.False(mixer.HasOutputItems);
            Assert.Equal(1, mixer.AudibleItemCount);

            // element-wise exact: the flag is invisible until a speed item bends time
            Assert.Equal(RenderMix(plain, 3 * Rate), RenderMix(flagged, 3 * Rate));
        }

        [Fact]
        public void Non_exempt_clip_beside_an_exempt_one_is_still_warped()
        {
            // both clips cover 2s of project under 2x. The exempt one plays 1s of source over
            // the output second; the plain one is resampled and plays all 2s of its source over
            // the same second. Both are audible in the sum, so the mix is neither alone.
            var project = NewProject();
            AddAudioItem(project, 0, 2 * Second, exempt: true);
            AddAudioItem(project, 0, 2 * Second, exempt: false);
            AddSpeedItem(project, 0, 2 * Second, 2.0);

            var dst = RenderMix(project, Rate);
            var exemptOnly = NewProject();
            AddAudioItem(exemptOnly, 0, 2 * Second, exempt: true);
            AddSpeedItem(exemptOnly, 0, 2 * Second, 2.0);
            var expectedExempt = RenderMix(exemptOnly, Rate);

            // subtracting the exempt part leaves the plain clip's resampled samples, which
            // read source ~2o (tick quantization keeps it within a few frames)
            int checkedFrames = 0;
            for (long o = 100; o < Rate; o += 997)
            {
                float plain = dst[o * 2] - expectedExempt[o * 2];
                bool near = false;
                for (long s = 2 * o - 3; s <= 2 * o + 3 && !near; s++)
                    near = Math.Abs(plain - HashValue(s)) < 1e-3f || Math.Abs(plain) < 1e-6f;
                Assert.True(near, $"frame {o}: plain part {plain} is not near source ~{2 * o}");
                checkedFrames++;
            }
            Assert.True(checkedFrames > 10);
        }

        [Fact]
        public void Clone_carries_the_flag()
        {
            var media = new MediaContent { SourceId = Guid.NewGuid(), SpeedWarpExempt = true };
            Assert.True(((MediaContent)media.Clone()).SpeedWarpExempt);
        }

        [Fact]
        public void Flag_round_trips_through_json()
        {
            var project = NewProject();
            AddAudioItem(project, 0, Second, exempt: true);

            var loaded = Project.FromJson(project.ToJson());
            Assert.True(((MediaContent)loaded.Items.Single().Content).SpeedWarpExempt);
        }

        // ------------------------------------------------------------------------------ editing

        /// <summary>A 10s solid on a video row plus one audio row playing a 60s source; the audio
        /// clip's span and flag are the test's to set.</summary>
        private static EditorSession NewSession(long clipStart, long clipDuration, bool exempt,
            out Item clip, out Track audioTrack, long streamDurationTicks = 0)
        {
            var sourceId = Guid.NewGuid();
            var video = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Screen", Order = 0 };
            audioTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Audio, Name = "Audio", Order = 1 };
            clip = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioTrack.Id,
                TimelineStartTicks = clipStart,
                DurationTicks = clipDuration,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 0, SourceInTicks = 0, SpeedWarpExempt = exempt },
            };
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = Rate },
                Sources =
                {
                    new Source
                    {
                        Id = sourceId,
                        Path = TestPath.Native(@"C:\rec\voice.wav"),
                        Streams = { new SourceStream { Index = 0, Kind = StreamKind.Audio, DurationTicks = streamDurationTicks > 0 ? streamDurationTicks : Ms(60_000) } },
                    },
                },
                Tracks = { video, audioTrack },
                Items =
                {
                    new Item
                    {
                        Id = Guid.NewGuid(),
                        TrackId = video.Id,
                        TimelineStartTicks = 0,
                        DurationTicks = Ms(10_000),
                        Content = new SolidContent { Color = "#FF000000" },
                    },
                    clip,
                },
            };
            return new EditorSession(project, null, save => save());
        }

        private static Item Live(EditorSession session, Item item) => session.Project.Items.Single(i => i.Id == item.Id);

        [Fact]
        public void Adding_and_removing_a_speed_item_refits_an_exempt_clip()
        {
            // clip [2s, 4s) = 2s of real time. Under 2x over the whole project its start lands at
            // output 1s and 2s of real time later is output 3s = project 6s: the clip now spans
            // [2s, 6s) of project time and plays exactly the same 2s of source.
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _);

            var speed = session.AddSpeedEffect(0, Ms(10_000));
            Assert.NotNull(speed);
            Assert.Equal(2.0, ((SpeedContent)speed.Content).Factor);
            Assert.Equal(Ms(2_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());

            session.DeleteItems(new[] { speed.Id });
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);

            // the snapshots restore the same states in reverse
            session.Undo();
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);
            session.Undo();
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
        }

        [Fact]
        public void Changing_the_speed_factor_refits_an_exempt_clip()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _);
            var speed = session.AddSpeedEffect(0, Ms(10_000));
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);

            // 4x: output 0.5s at the start, 2s of real time later is output 2.5s = project 10s
            session.SetSpeedFactor(speed.Id, 4.0);
            Assert.Equal(Ms(8_000), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_plain_clip_is_not_refit()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: false, out var clip, out _);
            session.AddSpeedEffect(0, Ms(10_000));
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
        }

        [Fact]
        public void Flipping_the_flag_refits_in_both_directions()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: false, out var clip, out _);
            session.AddSpeedEffect(0, Ms(10_000));
            ProjectChangeKind? kind = null;
            session.ProjectChanged += (_, e) => kind = e.Kind;

            // on: the 2s of project it covered become 2s of real time, which is 4s of project
            session.SetSpeedWarpExempt(clip.Id, true);
            Assert.True(((MediaContent)Live(session, clip).Content).SpeedWarpExempt);
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);
            Assert.Equal(ProjectChangeKind.Mapping, kind);

            // off: back to the 2s of project its 2s of real time span
            session.SetSpeedWarpExempt(clip.Id, false);
            Assert.False(((MediaContent)Live(session, clip).Content).SpeedWarpExempt);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);

            session.Undo();
            Assert.True(((MediaContent)Live(session, clip).Content).SpeedWarpExempt);
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);
        }

        [Fact]
        public void Flipping_the_flag_without_a_warp_keeps_the_span()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: false, out var clip, out _);

            session.SetSpeedWarpExempt(clip.Id, true);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            Assert.True(session.CanUndo);
        }

        [Fact]
        public void Refit_stops_short_of_the_next_clip_on_the_row()
        {
            var seed = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out var audioTrack);
            // a second clip at 5s on the same row, in the model the session then opens
            var project = seed.Project;
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioTrack.Id,
                TimelineStartTicks = Ms(5_000),
                DurationTicks = Ms(1_000),
                Content = new MediaContent { SourceId = project.Sources[0].Id, StreamIndex = 0 },
            });
            var session = new EditorSession(project, null, save => save());

            // the re-fit would run the clip out to 6s, over the neighbour at 5s: it stops there
            session.AddSpeedEffect(0, Ms(10_000));
            Assert.Equal(Ms(3_000), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_drag_that_comes_back_restores_the_original_span()
        {
            // the speed item starts past the clip, so the clip is unwarped. Dragged over it the
            // clip re-fits; dragged back the original 2s comes back exactly and the gesture is a
            // no-op (no undo entry) rather than a one-tick rounding of the clip.
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _);
            var speed = session.AddSpeedEffect(Ms(5_000), Ms(5_000));
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            var before = session.Project.ToJson();

            using (var gesture = session.BeginGesture("Move"))
            {
                // 2x over [0, 5s): the clip's start is output 1s, and its 2s of real time reach
                // output 3s, half a second past the span's output end of 2.5s (= project 5s)
                session.MoveLinked(speed.Id, -Ms(5_000));
                Assert.Equal(Ms(3_500), Live(session, clip).DurationTicks);

                session.MoveLinked(speed.Id, Ms(5_000));
                Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
                gesture.Commit();
            }

            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void Moving_an_exempt_clip_across_a_speed_boundary_keeps_its_real_time_span()
        {
            // 2x over [0, 5s): the clip at 2s starts at output 1s and its 2s of real time reach
            // output 3s, half a second past the span's output end of 2.5s (= project 5s), so it
            // spans 3.5s of project. Moved out past the speed item it is 2s of project again;
            // moved back, 3.5s. The mapping never changed, so this is the move's own re-fit.
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _);
            session.AddSpeedEffect(0, Ms(5_000));
            Assert.Equal(Ms(3_500), Live(session, clip).DurationTicks);

            Assert.Equal(Ms(4_000), session.MoveLinked(clip.Id, Ms(4_000)));
            Assert.Equal(Ms(6_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());

            Assert.Equal(-Ms(4_000), session.MoveLinked(clip.Id, -Ms(4_000)));
            Assert.Equal(Ms(2_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(3_500), Live(session, clip).DurationTicks);

            // the same inside a drag, step by step
            using (var gesture = session.BeginGesture("Move"))
            {
                session.MoveLinked(clip.Id, Ms(2_000)); // start 4s: output 2s + 2s = 4s = project 6.5s
                Assert.Equal(Ms(2_500), Live(session, clip).DurationTicks);
                session.MoveLinked(clip.Id, Ms(2_000));
                Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
                gesture.Commit();
            }
            Assert.Equal(Ms(6_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);

            // a plain clip's project span is untouched by the same move
            session.SetSpeedWarpExempt(clip.Id, false);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            session.MoveLinked(clip.Id, -Ms(4_000));
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
        }

        [Fact]
        public void A_moved_exempt_clip_stops_short_of_the_next_clip_on_the_row()
        {
            var seed = NewSession(Ms(6_000), Ms(2_000), exempt: true, out var clip, out var audioTrack);
            var project = seed.Project;
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioTrack.Id,
                TimelineStartTicks = Ms(3_500),
                DurationTicks = Ms(1_000),
                Content = new MediaContent { SourceId = project.Sources[0].Id, StreamIndex = 0 },
            });
            var session = new EditorSession(project, null, save => save());
            session.AddSpeedEffect(0, Ms(3_000)); // 2x over [0, 3s); the clip at 6s is unwarped

            // moved to 1s its 2s of real time reach output 2.5s = project 4s, over the
            // neighbour at 3.5s: it is end-trimmed to 2.5s of project instead
            Assert.Equal(-Ms(5_000), session.MoveLinked(clip.Id, -Ms(5_000)));
            Assert.Equal(Ms(1_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_500), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_drag_out_past_a_neighbour_and_back_keeps_the_original_length(bool exempt)
        {
            // 2x over [5s, 10s), neighbour at 6s. At 4s an exempt clip's 2s of real time reach
            // project 7s, so it is end-trimmed at the neighbour to [4s, 6s), which is only 1.5s of
            // real time. The trim is that step's, not the clip's: coming back, the 2s the drag
            // began with are measured again and the gesture has changed nothing.
            var session = NewSessionWithNeighbour(Ms(1_000), exempt, Ms(6_000), speedSpan: 0, out var clip);
            session.AddSpeedEffect(Ms(5_000), Ms(5_000));
            var before = session.Project.ToJson();

            using (var gesture = session.BeginGesture("Move"))
            {
                Assert.Equal(Ms(3_000), session.MoveLinked(clip.Id, Ms(3_000)));
                Assert.Equal(Ms(4_000), Live(session, clip).TimelineStartTicks);
                Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);

                // part of the way back there is room again, so the clip is whole straight away
                Assert.Equal(-Ms(2_000), session.MoveLinked(clip.Id, -Ms(2_000)));
                Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);

                // and a second approach is trimmed the same as the first
                Assert.Equal(Ms(2_000), session.MoveLinked(clip.Id, Ms(2_000)));
                Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);

                Assert.Equal(-Ms(3_000), session.MoveLinked(clip.Id, -Ms(3_000)));
                Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
                gesture.Commit();
            }

            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void A_drag_that_ends_against_a_neighbour_keeps_the_end_trim()
        {
            // the same drag let go at the neighbour: this is where the clip stopped, so the trim
            // is real, and a later drag starts from what is left of it
            var session = NewSessionWithNeighbour(Ms(1_000), exempt: true, Ms(6_000), speedSpan: 0, out var clip);
            session.AddSpeedEffect(Ms(5_000), Ms(5_000));

            using (var gesture = session.BeginGesture("Move"))
            {
                session.MoveLinked(clip.Id, Ms(3_000));
                gesture.Commit();
            }
            Assert.Equal(Ms(4_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);

            using (var gesture = session.BeginGesture("Move"))
            {
                session.MoveLinked(clip.Id, -Ms(3_000));
                gesture.Commit();
            }
            Assert.Equal(Ms(1_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(1_500), Live(session, clip).DurationTicks);

            session.Undo();
            session.Undo();
            Assert.Equal(Ms(1_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
        }

        /// <summary>A session whose audio row holds the clip and one more item, with the speed
        /// item (if any) added over both.</summary>
        private static EditorSession NewSessionWithNeighbour(long clipStart, bool exempt, long neighbourStart,
            long speedSpan, out Item clip)
        {
            var seed = NewSession(clipStart, Ms(2_000), exempt, out clip, out var audioTrack);
            var project = seed.Project;
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioTrack.Id,
                TimelineStartTicks = neighbourStart,
                DurationTicks = Ms(1_000),
                Content = new MediaContent { SourceId = project.Sources[0].Id, StreamIndex = 0 },
            });
            var session = new EditorSession(project, null, save => save());
            if (speedSpan > 0)
                session.AddSpeedEffect(0, speedSpan);
            return session;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_move_onto_the_next_clip_is_refused_with_no_speed_item_around(bool exempt)
        {
            // [3.5s, 5.5s) runs over the neighbour at 4s. Nothing re-fits under an identity warp,
            // so this is a plain collision for either clock, not a reason to end-trim the clip.
            var session = NewSessionWithNeighbour(Ms(1_000), exempt, Ms(4_000), speedSpan: 0, out var clip);
            var before = session.Project.ToJson();

            Assert.Equal(0, session.MoveLinked(clip.Id, Ms(2_500)));
            Assert.Equal(before, session.Project.ToJson());
        }

        [Theory]
        [InlineData(1)] // start rounds down on the output clock: the re-derived span is a tick short
        [InlineData(3)] // start rounds up: the re-derived span is a tick long
        public void A_collision_is_still_refused_when_rounding_nudges_the_exempt_clip(long oddTicks)
        {
            // 2x everywhere, so the clip at 6s spans 4s of project, and from an odd start the warp
            // gives that back a tick off. That tick is rounding, not a re-fit, so it must not
            // license end-trimming the clip to fit in front of the neighbour at 5s.
            var session = NewSessionWithNeighbour(Ms(6_000), exempt: true, Ms(5_000), speedSpan: Ms(20_000), out var clip);
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);
            var before = session.Project.ToJson();

            Assert.Equal(0, session.MoveLinked(clip.Id, -Ms(2_000) + oddTicks));
            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void A_tick_of_rounding_does_not_run_an_exempt_clip_into_a_neighbour_it_only_touches()
        {
            // the clip lands at 4s + 3 ticks, its 4s of project ending exactly on the neighbour.
            // The warp gives the span back a tick long, and the row clamp takes that tick off.
            var session = NewSessionWithNeighbour(Ms(2_000), exempt: true, Ms(8_000) + 3, speedSpan: Ms(20_000), out var clip);
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);

            Assert.Equal(Ms(2_000) + 3, session.MoveLinked(clip.Id, Ms(2_000) + 3));
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        // ----------------------------------------------------------------- the other time shifts

        [Fact]
        public void Close_gap_refits_an_exempt_clip_sliding_under_a_speed_item()
        {
            // 2x over [0, 3s); the clip at 6s is unwarped. Slid to 0 its 2s of real time reach
            // output 2s, half a second past the span's output end of 1.5s (= project 3s), so it
            // spans 3.5s of project. The mapping never changed: only the shift can see this.
            var session = NewSession(Ms(6_000), Ms(2_000), exempt: true, out var clip, out var audioTrack);
            session.AddSpeedEffect(0, Ms(3_000));
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            var before = session.Project.ToJson();

            var gap = session.FindGap(audioTrack.Id, Ms(1_000)).Value;
            Assert.True(session.CanCloseGap(gap));
            Assert.True(session.CloseGap(gap));

            Assert.Equal(0, Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(3_500), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void Ripple_delete_refits_an_exempt_clip_sliding_under_a_speed_item()
        {
            // 2x over [0, 5s), which starts before the cut and so stays put. The clip slides from
            // 6s to 4s: output 2s + 2s of real time = output 4s, 1.5s past the span's output end
            // of 2.5s (= project 5s), so it spans 2.5s of project. The mapping never changed.
            var session = NewSession(Ms(6_000), Ms(2_000), exempt: true, out var clip, out _);
            session.AddSpeedEffect(0, Ms(5_000));
            var card = session.AddText(Ms(4_000), Ms(2_000));
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);

            session.RippleDeleteItem(card.Id);

            Assert.Equal(Ms(4_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_500), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Ripple_delete_that_would_collide_is_refused_for_either_clock(bool exempt, bool speedItemLater)
        {
            // cutting the card's [4s, 6s) lands the clip on the cut's start, [4s, 6s), and pulls
            // the neighbour from 7s back to 5s, inside it. Rolled back, not end-trimmed to fit. A
            // speed item past the cut slides too, which changes the warp's mapping and so re-fits
            // the clip a second time, in the session: that one must not end-trim it either.
            var session = NewSessionWithNeighbour(Ms(5_000), exempt, Ms(7_000), speedSpan: 0, out var clip);
            if (speedItemLater)
                session.AddSpeedEffect(Ms(10_000), Ms(2_000));
            var card = session.AddText(Ms(4_000), Ms(2_000));
            var before = session.Project.ToJson();

            Assert.False(session.CanRippleDeleteItem(card.Id));
            session.RippleDeleteItem(card.Id);

            Assert.Equal(before, session.Project.ToJson());
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
        }

        /// <summary>A session with a clip at [3s, 6s) on the audio row, and a cut row
        /// [0, 5s) + [8s, 14s) whose gap at [5s, 8s) runs through the end of that clip.</summary>
        private static EditorSession NewSessionWithGapOverClip(bool exempt, out Item clip, out Track cutRow)
        {
            var seed = NewSession(Ms(3_000), Ms(3_000), exempt, out clip, out _);
            var project = seed.Project;
            cutRow = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Name = "Cut", Order = project.Tracks.Count };
            project.Tracks.Add(cutRow);
            AddSolid(project, cutRow, 0, Ms(5_000));
            AddSolid(project, cutRow, Ms(8_000), Ms(6_000));
            return new EditorSession(project, null, save => save());
        }

        private static Item AddSolid(Project project, Track track, long start, long duration)
        {
            var solid = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = new SolidContent { Color = "#FF000000" },
            };
            project.Items.Add(solid);
            return solid;
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Ripple_close_gap_keeps_the_cut_on_a_clip_it_runs_through(bool exempt, bool speedItemLater)
        {
            // the gap takes the last second of the clip. A speed item past it slides too, which
            // changes the warp's mapping: the clip must not get its cut second back from the
            // re-fit, which would play it over the closed span.
            var session = NewSessionWithGapOverClip(exempt, out var clip, out var cutRow);
            var speed = speedItemLater ? session.AddSpeedEffect(Ms(10_000), Ms(2_000)) : null;
            var before = session.Project.ToJson();

            Assert.True(session.RippleCloseGap(session.FindGap(cutRow.Id, Ms(6_000)).Value));

            Assert.Equal(Ms(3_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            if (speed != null)
                Assert.Equal(Ms(7_000), Live(session, speed).TimelineStartTicks);
            Assert.Empty(session.Project.Validate());

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Ripple_delete_keeps_the_cut_on_a_grouped_clip_it_runs_through(bool exempt)
        {
            // the same cut through RippleDelete: the clip is grouped with the [5s, 8s) card, so it
            // loses what it plays under the card, and a speed item past the cut slides
            var session = NewSessionWithGapOverClip(exempt, out var clip, out var cutRow);
            var project = session.Project;
            var card = AddSolid(project, cutRow, Ms(5_000), Ms(3_000));
            project.TrackOf(card).GroupId = project.TrackOf(Live(session, clip)).GroupId = Guid.NewGuid();
            session = new EditorSession(project, null, save => save());
            var speed = session.AddSpeedEffect(Ms(10_000), Ms(2_000));

            session.RippleDeleteItem(card.Id);

            Assert.Equal(Ms(3_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            Assert.Equal(Ms(7_000), Live(session, speed).TimelineStartTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Ripple_close_gap_measures_a_cut_clip_in_the_time_it_kept()
        {
            // 2x over [4s, 9s) puts the clip at [3s, 8s), 3s of real time, and the gap cuts the
            // speed item as well as the clip. What the clip keeps is [3s, 5s): 1s plain and 1s at
            // 2x, so 1.5s of real time, and the speed item left over [4s, 6s) still gives that
            // back as [3s, 5s).
            var session = NewSessionWithGapOverClip(exempt: true, out var clip, out var cutRow);
            var speed = session.AddSpeedEffect(Ms(4_000), Ms(5_000));
            Assert.Equal(Ms(5_000), Live(session, clip).DurationTicks);

            Assert.True(session.RippleCloseGap(session.FindGap(cutRow.Id, Ms(6_000)).Value));

            Assert.Equal(Ms(3_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            var warp = session.CurrentWarp;
            Assert.Equal(Ms(1_500), warp.ToOutput(Ms(5_000)) - warp.ToOutput(Ms(3_000)));
            Assert.Equal(Ms(4_000), Live(session, speed).TimelineStartTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void A_clip_cut_in_two_by_a_ripple_keeps_both_parts_when_a_later_speed_item_slides()
        {
            // the gap runs through the middle of [3s, 10s): the left part ends at the cut, the right
            // part closes up against it, and neither is stretched
            var session = NewSessionWithGapOverClip(exempt: true, out var clip, out var cutRow);
            var project = session.Project;
            Live(session, clip).DurationTicks = Ms(7_000);
            session = new EditorSession(project, null, save => save());
            session.AddSpeedEffect(Ms(10_000), Ms(2_000));

            Assert.True(session.RippleCloseGap(session.FindGap(cutRow.Id, Ms(6_000)).Value));

            var parts = session.Project.Items.Where(i => i.Content is MediaContent).OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(2, parts.Count);
            Assert.Equal((Ms(3_000), Ms(5_000)), (parts[0].TimelineStartTicks, parts[0].TimelineEndTicks));
            Assert.Equal((Ms(5_000), Ms(7_000)), (parts[1].TimelineStartTicks, parts[1].TimelineEndTicks));
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Rejoin_refits_a_later_exempt_clip_sliding_under_a_speed_item()
        {
            // the row holds a cut at [0, 2s) + [4s, 6s), source [0, 2s) + [2s, 4s), and the exempt
            // clip at 8s, just past 2x over [0, 8s). Rejoined the cut ends at 4s, so the clip goes
            // to 6s: output 3s, and its 2s of real time reach output 5s, 1s past the span's output
            // end of 4s (= project 8s), so it spans 3s of project. The mapping never changed.
            var seed = NewSession(Ms(8_000), Ms(2_000), exempt: true, out var clip, out var audioTrack);
            var project = seed.Project;
            var left = AddCutHalf(project, audioTrack, 0, Ms(2_000), sourceIn: 0);
            AddCutHalf(project, audioTrack, Ms(4_000), Ms(2_000), sourceIn: Ms(2_000));
            var session = new EditorSession(project, null, save => save());
            session.AddSpeedEffect(0, Ms(8_000));
            var before = session.Project.ToJson();

            Assert.True(session.RejoinGap(session.FindGap(audioTrack.Id, Ms(3_000)).Value));

            Assert.Equal(Ms(4_000), Live(session, left).DurationTicks);
            Assert.Equal(Ms(6_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(3_000), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());

            session.Undo();
            Assert.Equal(before, session.Project.ToJson());
        }

        [Fact]
        public void Rejoin_does_not_end_trim_a_later_exempt_clip_against_the_clip_it_absorbs()
        {
            // the row holds a cut at [0, 2s) + [5s, 6s), source [0, 2s) + [2s, 3s), and the exempt
            // clip at 6s, past 2x over [0, 5s). Rejoined the cut ends at 3s, so the clip goes to
            // 3s: output 1.5s, and its 1.5s of real time reach output 3s, 0.5s past the span's
            // output end of 2.5s (= project 5s), so it spans 2.5s of project. The right half that
            // used to start at 5s is gone by then, so nothing on the row stops it short.
            var seed = NewSession(Ms(6_000), Ms(1_500), exempt: true, out var clip, out var audioTrack);
            var project = seed.Project;
            var left = AddCutHalf(project, audioTrack, 0, Ms(2_000), sourceIn: 0);
            AddCutHalf(project, audioTrack, Ms(5_000), Ms(1_000), sourceIn: Ms(2_000));
            var session = new EditorSession(project, null, save => save());
            session.AddSpeedEffect(0, Ms(5_000));

            Assert.True(session.RejoinGap(session.FindGap(audioTrack.Id, Ms(3_000)).Value));

            Assert.Equal(Ms(3_000), Live(session, left).DurationTicks);
            Assert.Equal(Ms(3_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_500), Live(session, clip).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        private static Item AddCutHalf(Project project, Track track, long start, long duration, long sourceIn)
        {
            var half = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = track.Id,
                TimelineStartTicks = start,
                DurationTicks = duration,
                Content = new MediaContent { SourceId = project.Sources[0].Id, StreamIndex = 0, SourceInTicks = sourceIn },
            };
            project.Items.Add(half);
            return half;
        }

        [Fact]
        public void Ripple_close_gap_keeps_an_exempt_clip_past_the_speed_item_at_its_span()
        {
            // a clip at [2s, 3s) puts the gap past the speed item, so the slide crosses no boundary:
            // what the clip plays keeps its length, and the speed item ahead of the cut stays put
            var seed = NewSession(Ms(8_000), Ms(2_000), exempt: true, out var clip, out var audioTrack);
            var project = seed.Project;
            project.Items.Add(new Item
            {
                Id = Guid.NewGuid(),
                TrackId = audioTrack.Id,
                TimelineStartTicks = Ms(2_000),
                DurationTicks = Ms(1_000),
                Content = new MediaContent { SourceId = project.Sources[0].Id, StreamIndex = 0 },
            });
            var session = new EditorSession(project, null, save => save());
            var speed = session.AddSpeedEffect(0, Ms(2_000));
            var gap = session.FindGap(audioTrack.Id, Ms(5_000)).Value;

            Assert.True(session.RippleCloseGap(gap));

            Assert.Equal(Ms(3_000), Live(session, clip).TimelineStartTicks);
            Assert.Equal(Ms(2_000), Live(session, clip).DurationTicks);
            Assert.Equal(0, Live(session, speed).TimelineStartTicks);
            Assert.Equal(Ms(2_000), Live(session, speed).DurationTicks);
            Assert.Empty(session.Project.Validate());
        }

        [Fact]
        public void Shifting_a_speed_item_with_an_exempt_clip_fits_the_clip_to_the_moved_warp()
        {
            // 2x over [4s, 9s), clip at 5s: output 4.5s, and its 4s of project reach output 6.5s,
            // so it is 2s of real time. Both slide 4s left: the speed item now covers [0, 5s) and
            // the clip starts at output 0.5s, reaching output 2.5s = project 5s, still 4s of
            // project. Fitted to the warp from before the move it would come out as 2s.
            var project = NewProject();
            var clip = AddAudioItem(project, Ms(5_000), Ms(4_000), exempt: true);
            AddSpeedItem(project, Ms(4_000), Ms(5_000), 2.0);
            var speed = project.Items.Single(i => i.Content is SpeedContent);

            TimelineOps.ShiftItems(project, new[] { speed, clip }, _ => -Ms(4_000));

            Assert.Equal(0, speed.TimelineStartTicks);
            Assert.Equal(Ms(1_000), clip.TimelineStartTicks);
            Assert.Equal(Ms(4_000), clip.DurationTicks);
        }

        [Fact]
        public void Shifting_leaves_an_item_with_no_delta_alone()
        {
            // 2x over [0, 5s), so these exempt clips' 4s of project are 2s of real time. The speed
            // item goes 10s right. The clip that stays is the mapping re-fit's to deal with
            // (EditorSession.RefitExemptItems); fitted here it would shrink to 2s.
            var project = NewProject();
            var still = AddAudioItem(project, Ms(1_000), Ms(4_000), exempt: true);
            var moved = AddAudioItem(project, Ms(1_000), Ms(4_000), exempt: true);
            var plain = AddAudioItem(project, Ms(1_000), Ms(4_000), exempt: false);
            AddSpeedItem(project, 0, Ms(5_000), 2.0);

            TimelineOps.ShiftItems(project, project.Items,
                i => i == still ? 0 : i == moved ? Ms(20_000) : Ms(10_000));

            Assert.Equal(Ms(1_000), still.TimelineStartTicks);
            Assert.Equal(Ms(4_000), still.DurationTicks);

            // past the speed item, now at [10s, 15s), the moved clip's 2s of real time are 2s of
            // project; the plain clip keeps its project span wherever it lands
            Assert.Equal(Ms(21_000), moved.TimelineStartTicks);
            Assert.Equal(Ms(2_000), moved.DurationTicks);
            Assert.Equal(Ms(11_000), plain.TimelineStartTicks);
            Assert.Equal(Ms(4_000), plain.DurationTicks);
        }

        [Fact]
        public void Trimming_the_start_of_an_exempt_clip_moves_its_in_point_by_output_time()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _);
            session.AddSpeedEffect(0, Ms(10_000)); // clip is [2s, 6s) now, 2s of real time

            // 2s of project off the front is 1s of output: the in-point advances one second
            var applied = session.TrimItemStart(clip.Id, Ms(2_000));
            Assert.Equal(Ms(2_000), applied);
            var live = Live(session, clip);
            Assert.Equal(Ms(4_000), live.TimelineStartTicks);
            Assert.Equal(Ms(2_000), live.DurationTicks);
            Assert.Equal(Ms(1_000), ((MediaContent)live.Content).SourceInTicks);
        }

        [Fact]
        public void Trimming_the_end_of_an_exempt_clip_is_bounded_by_its_source_in_output_time()
        {
            // 3s of source; the clip plays 2s of it over project [2s, 6s) under 2x. The
            // remaining second of source is one output second, which under 2x is 2s of project.
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _, streamDurationTicks: Ms(3_000));
            session.AddSpeedEffect(0, Ms(10_000));
            Assert.Equal(Ms(4_000), Live(session, clip).DurationTicks);

            var applied = session.TrimItemEnd(clip.Id, Ms(10_000));
            Assert.Equal(Ms(2_000), applied);
            Assert.Equal(Ms(6_000), Live(session, clip).DurationTicks);
        }

        [Fact]
        public void Splitting_an_exempt_clip_advances_the_right_half_by_output_time()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: true, out var clip, out _);
            session.AddSpeedEffect(0, Ms(10_000)); // [2s, 6s)

            Assert.True(session.SplitItemAt(clip.Id, Ms(4_000)));
            var halves = session.Project.Items.Where(i => i.Content is MediaContent).OrderBy(i => i.TimelineStartTicks).ToList();
            Assert.Equal(2, halves.Count);
            Assert.Equal(Ms(4_000), halves[1].TimelineStartTicks);
            // 2s of project under 2x is 1s of output, so the right half starts 1s into the source
            Assert.Equal(Ms(1_000), ((MediaContent)halves[1].Content).SourceInTicks);
            Assert.True(((MediaContent)halves[1].Content).SpeedWarpExempt);
        }

        [Fact]
        public void CurrentWarp_follows_the_project()
        {
            var session = NewSession(Ms(2_000), Ms(2_000), exempt: false, out _, out _);
            Assert.True(session.CurrentWarp.IsIdentity);
            Assert.Same(session.CurrentWarp, session.CurrentWarp);

            session.AddSpeedEffect(0, Ms(10_000));
            Assert.False(session.CurrentWarp.IsIdentity);
            Assert.Equal(Ms(5_000), session.CurrentWarp.ToOutput(Ms(10_000)));
        }
    }
}
