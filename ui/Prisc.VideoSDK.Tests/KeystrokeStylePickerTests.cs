using System;
using System.IO;
using System.Linq;
using Prisc.UI.Controls;
using Prisc.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The shared keystroke style picker's view model over a bare host (what Draw on Screen's
    /// settings page adapts to), and the tiles it draws — framework-free: the tiles go through
    /// <see cref="KeystrokeStyleTile"/>, the very drawing the picker's preview control hands
    /// Avalonia's Skia lease.
    /// </summary>
    public class KeystrokeStylePickerTests
    {
        /// <summary>A host that keeps its own copy of the three values, like a settings object.</summary>
        private sealed class SettingsHost : IKeystrokeStyleHost
        {
            public string Style = KeystrokeStyles.DefaultStyle;
            public string Theme = KeystrokeStyles.DefaultTheme;
            public KeystrokeLook Custom;
            public bool Present = true;
            public int Edits;

            public KeystrokeStyleState? Read() =>
                Present ? new KeystrokeStyleState(Style, Theme, Custom) : null;

            public void Edit(string coalesceKey, Action<KeyboardContent> edit)
            {
                Edits++;
                var k = new KeyboardContent { Style = Style, Theme = Theme, Custom = Custom?.Copy() };
                edit(k);
                Style = k.Style;
                Theme = k.Theme;
                Custom = k.Custom;
            }
        }

        private static KeystrokeStyleOption StyleOption(string id) =>
            KeystrokeStyleEditor.StyleOptions.First(o => o.Value == id);

        private static KeystrokeStyleOption ThemeOption(string id) =>
            KeystrokeStyleEditor.ThemeOptions.First(o => o.Value == id);

        [Fact]
        public void ASettingsHost_RoundTripsStyleThemeAndCustom()
        {
            var host = new SettingsHost();
            var editor = new KeystrokeStyleEditor(host);

            editor.SelectedTheme = ThemeOption("yellow");
            editor.SelectedStyle = StyleOption(KeystrokeStyles.Custom);
            Assert.Equal(KeystrokeStyles.Custom, host.Style);
            Assert.Equal(KeystrokeStyles.Preset(KeystrokeStyles.Default, "yellow"), host.Custom);

            var drop = editor.ShapeRows.First(r => r.Label == "Offset");
            drop.Value = 0.3;
            Assert.Equal(0.3, host.Custom.Drop);
            Assert.True(KeystrokeStyles.IsCustomized(host.Custom));
        }

        [Fact]
        public void Refresh_ReReadsAChangeTheHostMadeItself()
        {
            var host = new SettingsHost();
            var editor = new KeystrokeStyleEditor(host);
            var raised = new System.Collections.Generic.List<string>();
            editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            host.Style = KeystrokeStyles.Simple;
            host.Theme = KeystrokeStyles.Light;
            editor.Refresh();

            Assert.Equal(KeystrokeStyles.Simple, editor.SelectedStyle.Value);
            Assert.Equal(KeystrokeStyles.Light, editor.SelectedTheme.Value);
            Assert.Contains(nameof(KeystrokeStyleEditor.SelectedStyle), raised);
            Assert.Contains(nameof(KeystrokeStyleEditor.SelectedTheme), raised);
            Assert.Contains(nameof(KeystrokeStyleEditor.CustomTileLook), raised);
            // a refresh never writes back
            Assert.Equal(0, host.Edits);
        }

        [Fact]
        public void UnknownStoredIds_ReadAsTheDefaults()
        {
            var host = new SettingsHost { Style = "from-a-newer-build", Theme = "chartreuse" };
            var editor = new KeystrokeStyleEditor(host);

            Assert.Equal(KeystrokeStyles.DefaultStyle, editor.SelectedStyle.Value);
            Assert.Equal(KeystrokeStyles.DefaultTheme, editor.SelectedTheme.Value);
        }

        [Fact]
        public void ARowHeldPastTheKeyTopFloor_IsNotFoughtByItsOwnEcho()
        {
            // padding H and offset together can ask for more than the key has; the drawn look
            // squeezes them (KeystrokeLook.Clamped), but the row keeps the value it was given
            var host = new SettingsHost();
            var editor = new KeystrokeStyleEditor(host);
            editor.SelectedStyle = StyleOption(KeystrokeStyles.Custom);

            var padH = editor.ShapeRows.First(r => r.Label == "Padding H");
            var drop = editor.ShapeRows.First(r => r.Label == "Offset");
            padH.Value = KeystrokeLook.MaxPadding;
            drop.Value = KeystrokeLook.MaxDrop;

            Assert.Equal(KeystrokeLook.MaxPadding, padH.Value);
            Assert.Equal(KeystrokeLook.MaxDrop, drop.Value);
            Assert.Equal(KeystrokeLook.MaxDrop, host.Custom.Drop);
            Assert.True(editor.CustomTileLook.Drop < KeystrokeLook.MaxDrop);
        }

        [Fact]
        public void NoTarget_DisablesWrites()
        {
            var host = new SettingsHost { Present = false };
            var editor = new KeystrokeStyleEditor(host);

            editor.SelectedStyle = StyleOption(KeystrokeStyles.Laptop);
            editor.SelectedTheme = ThemeOption("red");
            Assert.Equal(0, host.Edits);
            Assert.False(editor.HasTarget);
        }

        [Theory]
        [InlineData("#FF102030", 0xFF102030u)]
        [InlineData("#102030", 0xFF102030u)]
        [InlineData(" #80ffffff ", 0x80FFFFFFu)]
        public void TryParseHex_Accepts(string text, uint expected)
        {
            Assert.True(KeystrokeStyleEditor.TryParseHex(text, out var argb));
            Assert.Equal(expected, argb);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("#12345")]
        [InlineData("FF102030")]
        [InlineData("#GG102030")]
        public void TryParseHex_Rejects(string text) =>
            Assert.False(KeystrokeStyleEditor.TryParseHex(text, out _));

        [Fact]
        public void Tiles_DrawInkInsideThePlate()
        {
            foreach (var sample in new[] { KeystrokeTileSample.Style, KeystrokeTileSample.Theme })
            {
                using var bitmap = new SKBitmap(124, 34);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    KeystrokeStyleTile.Draw(canvas, SKRect.Create(124, 34),
                        KeystrokeStyles.Preset(KeystrokeStyles.Default, KeystrokeStyles.Light), sample);
                }

                // the plate's corners are rounded off, its middle is covered, and something other
                // than plate grey is drawn on it
                Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
                Assert.Equal(255, bitmap.GetPixel(62, 2).Alpha);
                bool ink = false;
                for (int x = 0; x < bitmap.Width && !ink; x++)
                for (int y = 0; y < bitmap.Height && !ink; y++)
                {
                    var c = bitmap.GetPixel(x, y);
                    ink = c.Alpha == 255 && Math.Abs(c.Red - KeystrokeStyleTile.PlateColor.Red) > 40;
                }
                Assert.True(ink, sample.ToString());
            }
        }

        /// <summary>
        /// Not an assertion: renders the picker's tiles at the inspector's narrowest sizes (a 300 px
        /// sidebar: 124x48 style previews, 78x28 theme previews), at 1x and 2x, into
        /// <c>PRISC_KEYSTROKE_RENDERS/tiles</c> (else a temp folder) for eyeballing.
        /// </summary>
        [Fact]
        public void Render_tiles_for_visual_review()
        {
            string dir = Environment.GetEnvironmentVariable("PRISC_KEYSTROKE_RENDERS");
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Path.GetTempPath(), "prisc-keystroke-renders");
            dir = Path.Combine(dir, "tiles");
            Directory.CreateDirectory(dir);

            foreach (var scale in new[] { 1, 2 })
            {
                foreach (var theme in new[] { "dark", "light", "pink", "yellow" })
                {
                    // the 2x2 style grid over the compact sidebar's dark ground, then the 3x3 theme
                    // grid for the default style
                    const float styleW = 124, styleH = 48, themeW = 78, themeH = 28, gap = 22;
                    float width = 2 * styleW + gap * 3, height = 2 * (styleH + gap) + 3 * (themeH + gap) + gap;
                    using var bitmap = new SKBitmap((int)(width * scale), (int)(height * scale));
                    using (var canvas = new SKCanvas(bitmap))
                    {
                        canvas.Clear(new SKColor(0x2B, 0x2B, 0x2B));
                        canvas.Scale(scale);
                        var custom = KeystrokeStyles.Preset(KeystrokeStyles.Simple, "blue") with { Drop = 0.15, OuterRadius = 0.4, TopRadius = 0.35 };
                        for (int i = 0; i < KeystrokeStyles.Styles.Count; i++)
                        {
                            var style = KeystrokeStyles.Styles[i];
                            var rect = SKRect.Create(gap + (i % 2) * (styleW + gap), gap + (i / 2) * (styleH + gap), styleW, styleH);
                            KeystrokeStyleTile.Draw(canvas, rect, KeystrokeStyles.Resolve(style, theme, custom),
                                KeystrokeTileSample.Style);
                        }

                        float top = gap + 2 * (styleH + gap);
                        for (int i = 0; i < KeystrokeStyles.Themes.Count; i++)
                        {
                            var rect = SKRect.Create(gap + (i % 3) * (themeW + gap / 2), top + (i / 3) * (themeH + gap), themeW, themeH);
                            KeystrokeStyleTile.Draw(canvas, rect,
                                KeystrokeStyles.Preset(KeystrokeStyles.Default, KeystrokeStyles.Themes[i]),
                                KeystrokeTileSample.Theme);
                        }
                    }

                    using var image = SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    using var file = File.Create(Path.Combine(dir, $"tiles-{theme}@{scale}x.png"));
                    data.SaveTo(file);
                }
            }
        }
    }
}
