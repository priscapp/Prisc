using System;
using System.Linq;
using Prisc.UI.VideoEditor.Timeline;
using Prisc.VideoSDK.Editing;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>What the timeline outlines as "moves with this" (design 2.6). The fixture is two grouped
    /// rows cut into two columns, so each selection has a linked clip of its own.</summary>
    public class TimelineLinkedOutlinesTests
    {
        private static long Sec(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

        private sealed class Columns
        {
            public readonly Item Screen1, Webcam1, Screen2, Webcam2;
            public readonly EditorSession Session;
            public readonly TimelineLinkedOutlines Outlines = new TimelineLinkedOutlines();

            public Columns()
            {
                var project = new Project
                {
                    Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                };

                var group = Guid.NewGuid();
                var screen = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0, GroupId = group, Name = "Screen" };
                var webcam = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 1, GroupId = group, Name = "Webcam" };
                project.Tracks.Add(screen);
                project.Tracks.Add(webcam);

                Screen1 = Clip(project, screen, 0);
                Webcam1 = Clip(project, webcam, 0);
                Screen2 = Clip(project, screen, 20);
                Webcam2 = Clip(project, webcam, 20);
                Session = new EditorSession(project, null, null);
            }

            /// <summary>Selects the way the timeline does: the session raises its event and the
            /// parent control passes it on, which only marks the outlines stale.</summary>
            public void Select(Item item)
            {
                Session.Select(item.Id);
                Outlines.Invalidate();
            }

            private static Item Clip(Project project, Track row, double start)
            {
                var item = new Item
                {
                    Id = Guid.NewGuid(),
                    TrackId = row.Id,
                    TimelineStartTicks = Sec(start),
                    DurationTicks = Sec(10),
                    Transform = new Transform { X = 0.5, Y = 0.5, Scale = 1 },
                    Content = new TextContent { Text = "x", Size = 80, Color = "#FFFFFFFF" },
                };
                project.Items.Add(item);
                return item;
            }
        }

        [Fact]
        public void The_linked_clips_are_outlined_and_the_selection_is_not()
        {
            var c = new Columns();

            c.Select(c.Screen1);

            Assert.Equal(new[] { c.Webcam1.Id }, c.Outlines.Clips(c.Session));
        }

        [Fact]
        public void A_press_that_replaces_the_selection_freezes_the_outlines_of_the_new_selection()
        {
            var c = new Columns();
            c.Select(c.Screen1);
            Assert.Equal(new[] { c.Webcam1.Id }, c.Outlines.Clips(c.Session)); // painted

            // the press: selection replaced and gesture opened, with no paint in between
            c.Select(c.Screen2);
            using var gesture = c.Outlines.BeginGesture(c.Session, "Move", null);

            Assert.True(c.Session.IsGestureActive);
            Assert.Equal(new[] { c.Webcam2.Id }, c.Outlines.Clips(c.Session));
        }

        [Fact]
        public void The_outlines_hold_still_for_the_whole_gesture_and_catch_up_when_it_ends()
        {
            var c = new Columns();
            c.Select(c.Screen1);
            var gesture = c.Outlines.BeginGesture(c.Session, "Move", null);

            // a selection change this late is not a press: it waits for the gesture to end
            c.Select(c.Screen2);
            Assert.Equal(new[] { c.Webcam1.Id }, c.Outlines.Clips(c.Session));

            gesture.Cancel();
            c.Outlines.Invalidate();
            Assert.Equal(new[] { c.Webcam2.Id }, c.Outlines.Clips(c.Session));
        }

        [Fact]
        public void Group_numbers_are_read_with_the_clips()
        {
            var c = new Columns();
            c.Select(c.Screen1);

            c.Outlines.BeginGesture(c.Session, "Move", null).Cancel();

            var group = c.Session.Project.Tracks.First().GroupId!.Value;
            Assert.Equal(0, c.Outlines.GroupNumbers[group]);
        }
    }
}
