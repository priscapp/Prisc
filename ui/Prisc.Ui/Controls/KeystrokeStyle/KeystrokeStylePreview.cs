using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Model;
using SkiaSharp;

namespace Prisc.UI.Controls
{
    /// <summary>
    /// One keystroke style or theme tile's picture: a sample row drawn by the overlay's own pipeline
    /// (<see cref="KeystrokePreview"/>) in the look the tile stands for, so a tile is the pixels the
    /// overlay will produce rather than a stand-in. What it draws is <see cref="KeystrokeStyleTile"/>'s
    /// business; this control resolves the look on the UI thread and hands it to Avalonia's Skia
    /// lease, exactly as the inspector's <c>ShapeKindPreview</c> does. On a backend that is not
    /// Skia the tile shows nothing.
    /// </summary>
    public sealed class KeystrokeStylePreview : Control
    {
        /// <summary>The style id to draw (one of <see cref="KeystrokeStyles.Styles"/>).</summary>
        public static readonly StyledProperty<string> StyleNameProperty =
            AvaloniaProperty.Register<KeystrokeStylePreview, string>(nameof(StyleName));

        /// <summary>The theme id to draw the style in.</summary>
        public static readonly StyledProperty<string> ThemeNameProperty =
            AvaloniaProperty.Register<KeystrokeStylePreview, string>(nameof(ThemeName));

        /// <summary>The look the <c>custom</c> style draws; unused by the presets.</summary>
        public static readonly StyledProperty<KeystrokeLook> CustomLookProperty =
            AvaloniaProperty.Register<KeystrokeStylePreview, KeystrokeLook>(nameof(CustomLook));

        /// <summary>Which sample the tile draws: the full chord-plus-typing sample of a style tile,
        /// or the single key and word of a theme tile.</summary>
        public static readonly StyledProperty<KeystrokeTileSample> SampleProperty =
            AvaloniaProperty.Register<KeystrokeStylePreview, KeystrokeTileSample>(nameof(Sample));

        static KeystrokeStylePreview()
        {
            AffectsRender<KeystrokeStylePreview>(StyleNameProperty, ThemeNameProperty, CustomLookProperty,
                SampleProperty);
        }

        public KeystrokeStylePreview()
        {
            IsHitTestVisible = false;
        }

        public string StyleName
        {
            get => GetValue(StyleNameProperty);
            set => SetValue(StyleNameProperty, value);
        }

        public string ThemeName
        {
            get => GetValue(ThemeNameProperty);
            set => SetValue(ThemeNameProperty, value);
        }

        public KeystrokeLook CustomLook
        {
            get => GetValue(CustomLookProperty);
            set => SetValue(CustomLookProperty, value);
        }

        public KeystrokeTileSample Sample
        {
            get => GetValue(SampleProperty);
            set => SetValue(SampleProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            // a fresh instance for this pass alone, so nothing can change under the render thread
            var look = KeystrokeStyles.Resolve(StyleName, ThemeName, CustomLook);
            context.Custom(new TileDrawOperation(bounds, look, Sample));
        }

        private sealed class TileDrawOperation : ICustomDrawOperation
        {
            private readonly KeystrokeLook _look;
            private readonly KeystrokeTileSample _sample;

            public TileDrawOperation(Rect bounds, KeystrokeLook look, KeystrokeTileSample sample)
            {
                Bounds = bounds;
                _look = look;
                _sample = sample;
            }

            public Rect Bounds { get; }

            public bool HitTest(Point p) => false;

            /// <summary>Never equal, so Avalonia never keeps a previous pass's operation in place
            /// of this one.</summary>
            public bool Equals(ICustomDrawOperation other) => false;

            public void Dispose()
            {
            }

            public void Render(ImmediateDrawingContext context)
            {
                var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (feature == null)
                    return;

                using var lease = feature.Lease();
                var canvas = lease.SkCanvas;
                if (canvas == null)
                    return;

                KeystrokeStyleTile.Draw(canvas,
                    SKRect.Create(0, 0, (float)Bounds.Width, (float)Bounds.Height), _look, _sample);
            }
        }
    }

    /// <summary>The sample a <see cref="KeystrokeStylePreview"/> draws.</summary>
    public enum KeystrokeTileSample
    {
        /// <summary>A style tile: a two-key chord and a typed word — both groups a look styles.</summary>
        Style,

        /// <summary>A theme tile: one keycap and a typed word, small enough for a three-wide row.</summary>
        Theme,
    }

    /// <summary>
    /// What a keystroke picker tile draws, free of Avalonia so a test can render the very tiles the
    /// picker shows: a mid-grey plate (a look can be white or black, and the hosts are near-black in
    /// one chrome and near-white in the other — the shape picker's plate solves the same problem)
    /// and the sample, fitted inside it with a margin.
    /// </summary>
    public static class KeystrokeStyleTile
    {
        /// <summary>The plate's corner radius, in tile pixels.</summary>
        public const float PlateRadius = 4;

        /// <summary>The plate: the shape picker's mid grey.</summary>
        public static readonly SKColor PlateColor = new(0x74, 0x74, 0x74);

        /// <summary>The style tile's sample: one keycap beside the typed word, so the sample
        /// fits the tile at a size where each style's shape still reads.</summary>
        public static readonly IReadOnlyList<string> StyleKeys = new[] { "Ctrl" };

        /// <summary>The theme tile's sample keycap.</summary>
        public static readonly IReadOnlyList<string> ThemeKeys = new[] { "A" };

        /// <summary>The theme tile's typed word.</summary>
        public const string ThemeTyped = "a";

        /// <summary>The largest font the sample is fitted at, in tile pixels: a wide tile must not
        /// blow it up past the sizes the overlay is ever read at in a sidebar.</summary>
        public const float MaxFontPx = 24;

        /// <summary>Draws the plate filling <paramref name="bounds"/> and the sample inside it.</summary>
        public static void Draw(SKCanvas canvas, SKRect bounds, KeystrokeLook look, KeystrokeTileSample sample)
        {
            if (canvas == null || bounds.Width <= 0 || bounds.Height <= 0)
                return;

            using (var plate = new SKPaint { Color = PlateColor, IsAntialias = true })
                canvas.DrawRoundRect(bounds, PlateRadius, PlateRadius, plate);

            var box = SampleBox(bounds);
            if (box.Width <= 0 || box.Height <= 0)
                return;

            if (sample == KeystrokeTileSample.Theme)
                KeystrokePreview.Draw(canvas, box, look, ThemeKeys, ThemeTyped, chord: false, maxFontPx: MaxFontPx);
            else
                KeystrokePreview.Draw(canvas, box, look, StyleKeys, KeystrokePreview.DefaultTyped, chord: false, maxFontPx: MaxFontPx);
        }

        /// <summary>Where on the plate the sample is fitted: inset by a share of the plate's height,
        /// so the keys never touch its edge.</summary>
        public static SKRect SampleBox(SKRect plate)
        {
            float inset = Math.Max(3, plate.Height * 0.12f);
            var box = SKRect.Inflate(plate, -inset, -inset);
            return box.Width > 0 && box.Height > 0 ? box : SKRect.Empty;
        }
    }
}
