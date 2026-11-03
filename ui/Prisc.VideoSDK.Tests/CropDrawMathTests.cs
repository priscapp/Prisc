using System;
using System.Linq;
using Prisc.UI.VideoEditor;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>The preview's "Draw crop" geometry: the drag rectangle (clamped to the picture,
    /// optionally held to a ratio), the insets it turns into, and the isolated project the picture
    /// is composed from while the mode is on.</summary>
    public class CropDrawMathTests
    {
        [Fact]
        public void DragRect_IsNormalizedWhicheverWayTheDragRuns()
        {
            var rect = CropDrawMath.DragRect(80, 60, 20, 10, 0, 0, 100, 100, null);
            Assert.Equal((20.0, 10.0, 60.0, 50.0), rect);
        }

        [Fact]
        public void DragRect_ClampsAnchorAndPointerToThePicture()
        {
            var rect = CropDrawMath.DragRect(-30, 20, 500, 400, 10, 10, 100, 50, null);
            Assert.Equal((10.0, 20.0, 100.0, 40.0), rect);
        }

        [Fact]
        public void DragRect_HoldsTheRatioAndShrinksToFit()
        {
            // 16:9 asked for, pulled mostly sideways: the width wins and the height follows
            var (_, _, w, h) = CropDrawMath.DragRect(0, 0, 160, 10, 0, 0, 1000, 1000, 16.0 / 9.0);
            Assert.Equal(160, w, 6);
            Assert.Equal(90, h, 6);

            // pulled past the bottom edge: the whole rect shrinks about the anchor, ratio intact
            (_, _, w, h) = CropDrawMath.DragRect(0, 0, 800, 800, 0, 0, 1000, 300, 16.0 / 9.0);
            Assert.Equal(300, h, 6);
            Assert.Equal(300 * 16.0 / 9.0, w, 6);
        }

        [Fact]
        public void Insets_AreFractionsOfThePicture()
        {
            var insets = CropDrawMath.Insets(30, 20, 50, 60, 10, 10, 100, 80);
            Assert.Equal((0.2, 0.125, 0.3, 0.125), insets);
        }

        [Fact]
        public void IsolatedProject_ShowsTheItemsRowAloneUnzoomedAndWhole()
        {
            var sourceId = Guid.NewGuid();
            var screenTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            var camTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 1 };
            var fxTrack = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Effect, Order = 2 };
            var cam = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = camTrack.Id,
                DurationTicks = TimeSpan.TicksPerSecond,
                Content = new MediaContent { SourceId = sourceId, StreamIndex = 1 },
                Transform = new Transform
                {
                    X = 0.8, Y = 0.7, Scale = 0.25, ScaleY = 0.3, Rotation = 12, Opacity = 0.5,
                    Aspect = 1, Crop = new CropRect { Left = 0.2 }, Mask = new Mask { Shape = MaskShape.Circle },
                },
            };
            var screen = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = screenTrack.Id,
                DurationTicks = TimeSpan.TicksPerSecond,
                Content = new MediaContent { SourceId = sourceId },
            };
            var zoom = new Item
            {
                Id = Guid.NewGuid(),
                TrackId = fxTrack.Id,
                DurationTicks = TimeSpan.TicksPerSecond,
                Content = new ZoomContent(),
            };
            var project = new Project { Tracks = { screenTrack, camTrack, fxTrack }, Items = { screen, cam, zoom } };
            var before = project.ToJson();

            var isolated = CropDrawMath.BuildIsolatedProject(project, cam.Id);

            // the input is the player's snapshot: never touched
            Assert.Equal(before, project.ToJson());

            Assert.True(isolated.Tracks.Single(t => t.Id == screenTrack.Id).Hidden);
            Assert.False(isolated.Tracks.Single(t => t.Id == camTrack.Id).Hidden);
            Assert.DoesNotContain(isolated.Items, i => i.Content is ZoomContent);

            var transform = isolated.Items.Single(i => i.Id == cam.Id).Transform;
            Assert.Null(transform.Crop);
            Assert.Null(transform.Aspect);
            Assert.Null(transform.ScaleY);
            Assert.Null(transform.Mask);
            Assert.Equal(0, transform.Rotation);
            Assert.Equal(1, transform.Opacity);
            // where the user put it stays put
            Assert.Equal((0.8, 0.7, 0.25), (transform.X, transform.Y, transform.Scale));

            Assert.Null(CropDrawMath.BuildIsolatedProject(project, Guid.NewGuid()));
        }
    }
}
