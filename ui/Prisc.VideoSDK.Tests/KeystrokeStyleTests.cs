using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Prisc.VideoSDK.Composition;
using Prisc.VideoSDK.Model;
using SkiaSharp;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// The keystroke overlay's styles and themes: preset data, the resolver, custom seeding, the
    /// wire format (old projects included) and the geometry invariant the editor's gizmo leans on —
    /// whatever the look, every track and keycap draws inside the measured row height.
    /// </summary>
    public class KeystrokeStyleTests
    {
        // ------------------------------------------------------------------------ preset data

        [Fact]
        public void Styles_and_themes_are_the_published_lists()
        {
            Assert.Equal(new[] { "default", "laptop", "simple", "custom" }, KeystrokeStyles.Styles);
            Assert.Equal(new[] { "dark", "light", "pink", "red", "blue", "green", "purple", "yellow", "teal" },
                KeystrokeStyles.Themes);
            Assert.Equal(new[] { "Default", "Laptop", "Simple", "Custom" },
                KeystrokeStyles.Styles.Select(KeystrokeStyles.StyleDisplayName));
            Assert.Equal("Pink", KeystrokeStyles.ThemeDisplayName("pink"));
            Assert.Equal("Dark", KeystrokeStyles.ThemeDisplayName("DARK"));
            Assert.Equal("nope", KeystrokeStyles.ThemeDisplayName("nope"));
        }

        [Fact]
        public void A_fresh_keyboard_item_is_default_dark()
        {
            var keyboard = new KeyboardContent();
            Assert.Equal(KeystrokeStyles.Default, keyboard.Style);
            Assert.Equal(KeystrokeStyles.Dark, keyboard.Theme);
            Assert.Equal(KeystrokeStyles.Preset("default", "dark"), KeystrokeStyles.Resolve(keyboard));
        }

        /// <summary>Laptop + Dark keeps the overlay's original colors, number for number where the old
        /// renderer hard-coded them: 55% black typing track with white text, 3D3D3D key tops flush
        /// on a black outer key that shows only as a strip below. Its shape follows Keystro's Laptop.</summary>
        [Fact]
        public void Laptop_dark_is_the_original_livery()
        {
            var look = KeystrokeStyles.Preset("laptop", "dark");
            Assert.Equal(0x8C000000u, look.TypingTrackColor);
            Assert.Equal(0xFFFFFFFFu, look.TypingTextColor);
            Assert.Equal(0xFF3D3D3Du, look.KeyTopColor);
            Assert.Equal(0xFF000000u, look.KeyOuterColor);
            Assert.Equal(0xFFFFFFFFu, look.KeyTextColor);
            Assert.Equal(0u, look.KeyTrackColor);
            Assert.Equal(0, look.PaddingX);
            Assert.Equal(0, look.PaddingY);
            // Keystro's Laptop proportions: a deep drop (~1/8 of the key), and the outer key's
            // bottom corners rounder than the key top's
            Assert.True(look.Drop >= 0.1);
            Assert.True(look.OuterRadius > look.TopRadius);
            Assert.Equal(0.3, look.TypingRadius);
        }

        /// <summary>The three presets differ in shape only where their references do: Default
        /// insets its key top on every side with a deeper lip below, Laptop is flush with a drop,
        /// Simple is a flat key in a hairline rim on a key track.</summary>
        [Fact]
        public void The_three_presets_are_three_shapes_of_one_model()
        {
            var def = KeystrokeStyles.Preset("default", "dark");
            var laptop = KeystrokeStyles.Preset("laptop", "dark");
            var simple = KeystrokeStyles.Preset("simple", "dark");

            Assert.True(def.PaddingX > 0 && def.PaddingY > 0 && def.Drop > def.PaddingY);
            Assert.True(laptop.PaddingX == 0 && laptop.PaddingY == 0 && laptop.Drop > 0);
            Assert.True(simple.PaddingX > 0 && simple.PaddingX == simple.PaddingY && simple.Drop == 0);

            Assert.Equal(0u, def.KeyTrackColor);
            Assert.Equal(0u, laptop.KeyTrackColor);
            Assert.NotEqual(0u, simple.KeyTrackColor >> 24);
            Assert.True(simple.KeyTrackPadding > laptop.KeyTrackPadding);
        }

        [Fact]
        public void Themes_color_both_groups_and_accent_the_keys()
        {
            var light = KeystrokeStyles.Preset("default", "light");
            // the light theme is a real one: light typing track under dark text, light keys under
            // dark legends
            Assert.True(Luma(light.TypingTrackColor) > 0.9 && Luma(light.TypingTextColor) < 0.1);
            Assert.True(Luma(light.KeyTopColor) > 0.6 && Luma(light.KeyTextColor) < 0.2);
            // the key top, its outer key and the typing track must each read apart
            Assert.True(Luma(light.TypingTrackColor) - Luma(light.KeyTopColor) > 0.1);
            Assert.True(Luma(light.KeyTopColor) - Luma(light.KeyOuterColor) > 0.2);

            var pink = KeystrokeStyles.Preset("default", "pink");
            Assert.Equal(0xFFDD5D8Fu, pink.KeyTopColor);
            Assert.Equal(0xFFBD507Au, pink.KeyOuterColor); // Keystro's own shadow for the pink
            Assert.Equal(0xFFFFFFFFu, pink.KeyTextColor);
            Assert.Equal(0x8C000000u, pink.TypingTrackColor);

            // white would not read on the light accents
            Assert.True(Luma(KeystrokeStyles.Preset("default", "yellow").KeyTextColor) < 0.2);
            Assert.True(Luma(KeystrokeStyles.Preset("default", "teal").KeyTextColor) < 0.2);

            // every theme keeps its legend legible on its key top
            foreach (var theme in KeystrokeStyles.Themes)
            {
                var look = KeystrokeStyles.Preset("default", theme);
                Assert.True(Contrast(look.KeyTextColor, look.KeyTopColor) >= 3.0, theme);
            }
        }

        [Fact]
        public void Unknown_ids_resolve_to_the_defaults()
        {
            Assert.Equal(KeystrokeStyles.Preset("default", "dark"), KeystrokeStyles.Preset("retro", "neon"));
            Assert.Equal(KeystrokeStyles.Preset("laptop", "pink"), KeystrokeStyles.Preset("LAPTOP", "Pink"));
            Assert.Equal(KeystrokeStyles.Preset("default", "dark"), KeystrokeStyles.Resolve(null));
        }

        [Fact]
        public void Theme_swatch_is_key_top_and_typing_track()
        {
            var (keyTop, typing) = KeystrokeStyles.ThemeSwatch("blue");
            Assert.Equal(0xFF1F91E6u, keyTop);
            Assert.Equal(0x8C000000u, typing);
        }

        // ------------------------------------------------------------------------- resolving

        [Fact]
        public void Custom_resolves_to_the_custom_look_and_ignores_the_theme()
        {
            var custom = KeystrokeStyles.Preset("simple", "green") with { KeyTopColor = 0xFF123456 };
            var keyboard = new KeyboardContent { Style = "custom", Theme = "pink", Custom = custom };
            var look = KeystrokeStyles.Resolve(keyboard);
            Assert.Equal(custom, look);
            Assert.NotSame(custom, look);
        }

        [Fact]
        public void Resolve_clamps_the_custom_shape()
        {
            var custom = KeystrokeStyles.Preset("default", "dark") with
            {
                OuterRadius = 9, TopRadius = -1, PaddingX = double.NaN, PaddingY = 0.25, Drop = 0.4,
                KeyTrackPadding = 2, TypingPadding = -3,
            };
            var look = KeystrokeStyles.Resolve("custom", "dark", custom);
            Assert.Equal(KeystrokeLook.MaxRadius, look.OuterRadius);
            Assert.Equal(0, look.TopRadius);
            Assert.Equal(0, look.PaddingX);
            Assert.Equal(KeystrokeLook.MaxKeyTrackPadding, look.KeyTrackPadding);
            Assert.Equal(0, look.TypingPadding);
            // padding + drop asked for 90% of the key; the key top keeps its minimum
            Assert.Equal(1 - KeystrokeLook.MinTopHeight, 2 * look.PaddingY + look.Drop, 9);
        }

        // ---------------------------------------------------------------------- custom seeding

        [Fact]
        public void Entering_custom_seeds_from_the_look_on_screen()
        {
            var keyboard = new KeyboardContent { Style = "laptop", Theme = "yellow" };
            KeystrokeStyles.SelectStyle(keyboard, "custom");

            Assert.Equal("custom", keyboard.Style);
            Assert.Equal(KeystrokeStyles.Preset("laptop", "yellow"), keyboard.Custom);
            Assert.Equal(KeystrokeStyles.Preset("laptop", "yellow"), KeystrokeStyles.Resolve(keyboard));
        }

        [Fact]
        public void An_unedited_custom_reseeds_from_the_next_preset()
        {
            var keyboard = new KeyboardContent { Style = "laptop", Theme = "yellow" };
            KeystrokeStyles.SelectStyle(keyboard, "custom");
            Assert.False(KeystrokeStyles.IsCustomized(keyboard.Custom));

            KeystrokeStyles.SelectStyle(keyboard, "simple");
            keyboard.Theme = "blue";
            KeystrokeStyles.SelectStyle(keyboard, "custom");

            Assert.Equal(KeystrokeStyles.Preset("simple", "blue"), keyboard.Custom);
        }

        [Fact]
        public void Entering_custom_from_a_preset_starts_from_that_preset_even_after_edits()
        {
            var keyboard = new KeyboardContent { Style = "default", Theme = "dark" };
            KeystrokeStyles.SelectStyle(keyboard, "custom");
            keyboard.Custom.KeyTopColor = 0xFF102030;
            Assert.True(KeystrokeStyles.IsCustomized(keyboard.Custom));

            // re-picking custom while it is current keeps the edit
            KeystrokeStyles.SelectStyle(keyboard, "custom");
            Assert.Equal(0xFF102030u, keyboard.Custom.KeyTopColor);

            // leaving it draws the preset; coming back from laptop/blue starts from laptop/blue
            KeystrokeStyles.SelectStyle(keyboard, "laptop");
            Assert.Equal(KeystrokeStyles.Preset("laptop", "dark"), KeystrokeStyles.Resolve(keyboard));
            keyboard.Theme = "blue";
            KeystrokeStyles.SelectStyle(keyboard, "custom");
            Assert.Equal(KeystrokeStyles.Preset("laptop", "blue"), keyboard.Custom);
        }

        [Fact]
        public void Custom_with_nothing_seeded_draws_the_default_style_in_its_theme()
        {
            var keyboard = new KeyboardContent { Style = "custom", Theme = "red" };
            Assert.Equal(KeystrokeStyles.Preset("default", "red"), KeystrokeStyles.Resolve(keyboard));

            KeystrokeStyles.SelectStyle(keyboard, "custom");
            Assert.Equal(KeystrokeStyles.Preset("default", "red"), keyboard.Custom);
        }

        [Fact]
        public void SeedCustom_is_the_preset_on_screen_as_a_fresh_instance()
        {
            var seed = KeystrokeStyles.SeedCustom("laptop", "pink");
            Assert.Equal(KeystrokeStyles.Preset("laptop", "pink"), seed);
            Assert.NotSame(seed, KeystrokeStyles.SeedCustom("laptop", "pink"));
            Assert.Equal(KeystrokeStyles.Preset("default", "pink"), KeystrokeStyles.SeedCustom("custom", "pink"));
        }

        // -------------------------------------------------------------------------- wire format

        [Fact]
        public void Style_theme_and_custom_round_trip()
        {
            var project = KeyboardProject(new KeyboardContent
            {
                Style = "custom",
                Theme = "teal",
                Custom = KeystrokeStyles.Preset("simple", "light") with { PaddingX = 0.2, KeyTrackColor = 0x80FF00FF },
            }, out var source);

            var json = project.ToJson();
            Assert.Contains("\"Style\": \"custom\"", json);
            Assert.Contains("\"Theme\": \"teal\"", json);
            var restored = Project.FromJson(json);

            Assert.Equal(json, restored.ToJson());
            Assert.Empty(restored.Validate());
            var keyboard = (KeyboardContent)restored.Items.Single(i => i.Content is KeyboardContent).Content;
            Assert.Equal(((KeyboardContent)project.Items.Single(i => i.Content is KeyboardContent).Content).Custom, keyboard.Custom);
            Assert.Equal(source.Id, keyboard.SourceId);
        }

        [Fact]
        public void A_never_customised_item_writes_no_custom_block()
        {
            var json = KeyboardProject(new KeyboardContent(), out _).ToJson();
            Assert.DoesNotContain("\"Custom\"", json);
            Assert.DoesNotContain("TextColor", json);
            Assert.DoesNotContain("BackgroundColor", json);
        }

        /// <summary>Projects written before the styles existed carry the old typing colors. They
        /// still open (the reader skips members it does not map), and draw the default look.</summary>
        [Fact]
        public void A_legacy_project_with_untouched_colors_loads_as_the_old_laptop_look()
        {
            var keyboard = LoadLegacy(textColor: 0xFFFFFFFF, backgroundColor: 0x8C000000, out _);
            Assert.Equal(52, keyboard.FontSize);
            Assert.Equal(KeystrokeStyles.Laptop, keyboard.Style);
            Assert.Equal(KeystrokeStyles.Dark, keyboard.Theme);
            Assert.Null(keyboard.Custom);
            Assert.Equal(KeystrokeStyles.Preset("laptop", "dark"), KeystrokeStyles.Resolve(keyboard));
        }

        [Fact]
        public void A_legacy_project_with_changed_colors_loads_as_a_custom_laptop_look_with_them()
        {
            var keyboard = LoadLegacy(textColor: 0xFF00FF00, backgroundColor: 0x8C112233, out _);
            Assert.Equal(KeystrokeStyles.Custom, keyboard.Style);
            Assert.Equal(KeystrokeStyles.Dark, keyboard.Theme);
            var expected = KeystrokeStyles.Preset("laptop", "dark") with
            {
                TypingTextColor = 0xFF00FF00,
                TypingTrackColor = 0x8C112233,
            };
            Assert.Equal(expected, keyboard.Custom);
            Assert.True(KeystrokeStyles.IsCustomized(keyboard.Custom));
        }

        [Fact]
        public void A_migrated_legacy_project_saves_without_the_legacy_fields()
        {
            var keyboard = LoadLegacy(textColor: 0xFF00FF00, backgroundColor: 0x8C112233, out var restored);
            var json = restored.ToJson();
            Assert.DoesNotContain("\"TextColor\"", json);
            Assert.DoesNotContain("\"BackgroundColor\"", json);
            var again = (KeyboardContent)Project.FromJson(json).Items.Single(i => i.Content is KeyboardContent).Content;
            Assert.Equal(KeystrokeStyles.Custom, again.Style);
            Assert.Equal(keyboard.Custom, again.Custom);
        }

        [Fact]
        public void A_current_project_is_not_touched_by_the_legacy_migration()
        {
            var project = KeyboardProject(new KeyboardContent { Style = "simple", Theme = "pink" }, out _);
            var keyboard = (KeyboardContent)Project.FromJson(project.ToJson()).Items.Single(i => i.Content is KeyboardContent).Content;
            Assert.Equal("simple", keyboard.Style);
            Assert.Equal("pink", keyboard.Theme);
            Assert.Null(keyboard.Custom);
        }

        /// <summary>A project as a pre-styles build wrote it: the two colors, no style or theme.</summary>
        private static KeyboardContent LoadLegacy(uint textColor, uint backgroundColor, out Project restored)
        {
            var json = KeyboardProject(new KeyboardContent { FontSize = 52 }, out _).ToJson();
            int at = json.IndexOf("\"FontSize\"", StringComparison.Ordinal);
            Assert.True(at > 0);
            json = json.Insert(at, $"\"TextColor\": {textColor},\n        \"BackgroundColor\": {backgroundColor},\n        ");
            json = json.Replace("\"Style\": \"default\",", "").Replace("\"Theme\": \"dark\",", "");
            Assert.DoesNotContain("\"Style\"", json);

            restored = Project.FromJson(json);
            Assert.Empty(restored.Validate());
            return (KeyboardContent)restored.Items.Single(i => i.Content is KeyboardContent).Content;
        }

        [Fact]
        public void Unknown_style_and_theme_ids_still_validate()
        {
            var project = KeyboardProject(new KeyboardContent { Style = "from-the-future", Theme = "ultraviolet" }, out _);
            Assert.Empty(Project.FromJson(project.ToJson()).Validate());
        }

        // -------------------------------------------------------------- geometry invariants

        public static IEnumerable<object[]> AllLooks() =>
            KeystrokeStyles.PresetStyles.SelectMany(s => new[] { "dark", "light", "pink" }.Select(t => new object[] { s, t }))
                .Append(new object[] { "custom-extreme", "dark" });

        private static KeystrokeLook LookFor(string style, string theme) => style == "custom-extreme"
            ? (KeystrokeStyles.Preset("default", theme) with
            {
                KeyTrackColor = 0xFFFF00FF, KeyTrackPadding = KeystrokeLook.MaxKeyTrackPadding,
                Drop = KeystrokeLook.MaxDrop, PaddingY = KeystrokeLook.MaxPadding, PaddingX = KeystrokeLook.MaxPadding,
            }).Clamped()
            : KeystrokeStyles.Preset(style, theme);

        /// <summary>
        /// The gizmo boxes a keyboard item with <see cref="FrameComposer.MeasureKeyboardHeight"/>,
        /// which knows nothing of the look. Every look must therefore draw inside it: three rows
        /// (typing, a chord, typing with keycaps) stacked up from the anchor ink nothing above the
        /// measured block or below the anchor, and every row's vertical extent — tracks included —
        /// is the measured row.
        /// </summary>
        [Theory]
        [MemberData(nameof(AllLooks))]
        public void Every_look_draws_inside_the_measured_block(string style, string theme)
        {
            var look = LookFor(style, theme) with
            {
                TypingTrackColor = 0xFF00FF00, // opaque so the typing rows' extent is exact
                KeyTrackColor = 0xFF0000FF,      // drawn even for the trackless styles
            };
            const int w = 900, h = 500;
            const float fontPx = 40, anchorY = 420;
            var keyboard = new KeyboardContent { FontSize = fontPx };
            float measured = (float)FrameComposer.MeasureKeyboardHeight(keyboard, 3, h, h);

            using var bitmap = new SKBitmap(w, h);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                KeystrokeRenderer.Draw(canvas, SampleRows(), look, null, null, fontPx, w / 2f, anchorY,
                    w, KeystrokeAlignment.Center, KeystrokeStacking.Up, 1.0);
            }

            var ink = InkRows(bitmap);
            Assert.NotNull(ink);
            Assert.True(ink.Value.Top >= Math.Floor(anchorY - measured) - 0,
                $"{style}/{theme}: ink at y={ink.Value.Top} above the measured block top {anchorY - measured}");
            Assert.True(ink.Value.Bottom <= anchorY,
                $"{style}/{theme}: ink at y={ink.Value.Bottom} below the anchor {anchorY}");
            // with opaque tracks on every row, the block is filled edge to edge
            Assert.True(ink.Value.Top <= Math.Ceiling(anchorY - measured) + 1,
                $"{style}/{theme}: the top row does not reach the measured block top");
        }

        /// <summary>Wrapping runs the identical arithmetic as drawing: a long mixed row wrapped
        /// at a narrow width never inks outside the wrap box, whatever the track paddings.</summary>
        [Theory]
        [MemberData(nameof(AllLooks))]
        public void Wrapped_lines_stay_inside_the_wrap_box(string style, string theme)
        {
            var look = LookFor(style, theme) with { TypingTrackColor = 0xFF00FF00, KeyTrackColor = 0xFF0000FF };
            const int w = 900, h = 900;
            const float wrap = 300, anchorX = 450;
            var atoms = new List<KeyAtom>();
            foreach (var word in "the quick brown fox jumps over".Split(' '))
            {
                atoms.Add(new KeyAtom(KeyAtomKind.Word, word));
                if (word.Length == 5)
                    atoms.Add(new KeyAtom(KeyAtomKind.Cap, "Bksp"));
            }
            var rows = new List<KeyboardRow> { new KeyboardRow(atoms, false, 1, 1) };

            using var bitmap = new SKBitmap(w, h);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                KeystrokeRenderer.Draw(canvas, rows, look, null, null, 40, anchorX, 850, wrap,
                    KeystrokeAlignment.Center, KeystrokeStacking.Up, 1.0);
            }

            int left = w, right = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (bitmap.GetPixel(x, y).Alpha > 0)
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                    }

            Assert.True(right - left + 1 <= wrap + 1, $"{style}/{theme}: {right - left + 1}px wide in a {wrap}px box");
        }

        [Fact]
        public void A_key_track_draws_behind_a_run_of_keycaps_and_not_behind_typing()
        {
            var look = KeystrokeStyles.Preset("simple", "dark") with { KeyTrackColor = 0xFF0000FF, TypingTrackColor = 0 };
            var atoms = new List<KeyAtom>
            {
                new(KeyAtomKind.Cap, "Ctrl"), new(KeyAtomKind.Plus, "+"), new(KeyAtomKind.Cap, "K"),
                new(KeyAtomKind.Word, "hello"),
            };
            using var bitmap = new SKBitmap(600, 120);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                KeystrokeRenderer.DrawSingleLine(canvas, atoms, look, 40, 0, 10, 1.0);
            }

            var keys = KeystrokeRenderer.MeasureLine(atoms.Take(3).ToList(), look, 40);
            float keyWidth = keys.Width;
            // across the row's midline the key track shows round the caps and ends where the
            // keycaps' run does — the typed word has no key track behind it
            int y = 10 + (int)(keys.Height / 2);
            int blueRight = -1;
            for (int x = 0; x < 600; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.Blue > 200 && c.Red < 50 && c.Green < 50)
                    blueRight = x;
            }
            Assert.InRange(blueRight, keyWidth - 3, keyWidth + 1);
        }

        [Fact]
        public void The_chord_plus_contrasts_with_a_drawn_key_track()
        {
            // every shipped look with a key track: the "+" reaches 3:1 on the tray (white legends
            // on Simple's grey tray fall back to a darker key color or ink)
            foreach (var theme in KeystrokeStyles.Themes)
            {
                var look = KeystrokeStyles.Preset("simple", theme);
                Assert.True(KeystrokeRenderer.PlusOnOpaqueTrack(look), theme);
                double contrast = KeystrokeRenderer.Contrast(KeystrokeRenderer.PlusOnTrack(look), look.KeyTrackColor);
                Assert.True(contrast >= KeystrokeRenderer.MinPlusContrast, $"{theme}: {contrast:F2}:1");
            }
            // a light key on a darker tray: the dark legend reads, the near-white top would not
            var light = KeystrokeStyles.Preset("simple", "light");
            Assert.Equal(light.KeyTextColor, KeystrokeRenderer.PlusOnTrack(light));
            // a dark key on a light tray: the black outer key, not the white legend
            var dark = KeystrokeStyles.Preset("simple", "dark");
            Assert.Equal(dark.KeyOuterColor, KeystrokeRenderer.PlusOnTrack(dark));
        }

        [Fact]
        public void A_translucent_key_track_keeps_the_plus_halo()
        {
            var look = KeystrokeStyles.Preset("default", "dark") with { KeyTrackColor = 0x26FFFFFF };
            Assert.False(KeystrokeRenderer.PlusOnOpaqueTrack(look));
            Assert.False(KeystrokeRenderer.PlusOnOpaqueTrack(look with { KeyTrackColor = 0 }));
            Assert.True(KeystrokeRenderer.PlusOnOpaqueTrack(look with { KeyTrackColor = 0xFFFFFFFF }));
        }

        [Fact]
        public void A_hairline_rim_survives_a_small_font()
        {
            // Simple's 0.016 rim is well under a pixel at 16 px; it is floored to one so the keys
            // keep their outline instead of melting into the tray
            var look = KeystrokeStyles.Preset("simple", "yellow");
            using var bitmap = new SKBitmap(200, 60);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                KeystrokePreview.Draw(canvas, new SKRect(0, 0, 200, 60), look,
                    keys: new[] { "A" }, typed: null, chord: false, maxFontPx: 16);
            }
            int rim = CountNear(bitmap, look.KeyOuterColor, 24);
            Assert.True(rim >= 20, $"only {rim} rim pixels at 16 px");
        }

        private static int CountNear(SKBitmap bitmap, uint argb, int tolerance)
        {
            var want = new SKColor(argb);
            int count = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    if (Math.Abs(c.Red - want.Red) <= tolerance && Math.Abs(c.Green - want.Green) <= tolerance
                        && Math.Abs(c.Blue - want.Blue) <= tolerance)
                        count++;
                }
            }
            return count;
        }

        [Fact]
        public void The_preview_fits_inside_its_box()
        {
            foreach (var style in KeystrokeStyles.Styles)
            {
                using var bitmap = new SKBitmap(200, 100);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    KeystrokePreview.Draw(canvas, new SKRect(20, 20, 180, 80), style, "pink");
                }

                var ink = InkBox(bitmap);
                Assert.NotNull(ink);
                Assert.True(ink.Value.Left >= 19 && ink.Value.Right <= 181 && ink.Value.Top >= 19 && ink.Value.Bottom <= 81,
                    $"{style}: preview inked {ink} outside 20,20..180,80");
                // and fills the box on its limiting axis
                Assert.True(ink.Value.Right - ink.Value.Left >= 150 || ink.Value.Bottom - ink.Value.Top >= 55, style);
            }
        }

        // ------------------------------------------------------------------- visual renders

        /// <summary>
        /// Not an assertion: renders every preset style in a few themes (Keystro's Win/Alt/A sample,
        /// a chord with typing, a multi-row block over a mid-grey "video") to PNGs, for eyeballing
        /// against the Keystro references. Writes to <c>PRISC_KEYSTROKE_RENDERS</c> when that is
        /// set, else to a scratch folder under the temp directory, so it never fails a machine
        /// for want of a folder.
        /// </summary>
        [Fact]
        public void Render_style_sheets_for_visual_review()
        {
            string dir = Environment.GetEnvironmentVariable("PRISC_KEYSTROKE_RENDERS");
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Path.GetTempPath(), "prisc-keystroke-renders");
            Directory.CreateDirectory(dir);

            string[] themes = { "dark", "pink", "light", "yellow" };
            foreach (var style in KeystrokeStyles.PresetStyles)
            {
                // a sheet per style: one band per theme, Keystro's sample left, ours right,
                // over white (Keystro's own preview) and over mid-grey
                const int bandH = 150, sheetW = 1400;
                using var sheet = new SKBitmap(sheetW, bandH * themes.Length * 2);
                using (var canvas = new SKCanvas(sheet))
                {
                    for (int t = 0; t < themes.Length; t++)
                    {
                        var look = KeystrokeStyles.Preset(style, themes[t]);
                        for (int bg = 0; bg < 2; bg++)
                        {
                            float top = (t * 2 + bg) * bandH;
                            using (var fill = new SKPaint { Color = bg == 0 ? SKColors.White : new SKColor(0x6E, 0x78, 0x82) })
                                canvas.DrawRect(0, top, sheetW, bandH, fill);
                            KeystrokePreview.Draw(canvas, new SKRect(20, top + 10, 560, top + bandH - 10), look,
                                new[] { "Win", "Alt", "A" }, typed: null, chord: false);
                            KeystrokePreview.Draw(canvas, new SKRect(600, top + 10, sheetW - 20, top + bandH - 10), look,
                                new[] { "Ctrl", "Shift", "K" }, typed: "typing");
                        }
                    }
                }
                Save(sheet, Path.Combine(dir, $"style-{style}.png"));

                // a real multi-row block at the editor's default size, per theme, over a grey frame
                foreach (var theme in themes)
                {
                    var look = KeystrokeStyles.Preset(style, theme);
                    using var frame = new SKBitmap(960, 360);
                    using (var canvas = new SKCanvas(frame))
                    {
                        canvas.Clear(new SKColor(0x6E, 0x78, 0x82));
                        using (var stripe = new SKPaint { Color = new SKColor(0xF0, 0xF0, 0xF0) })
                            canvas.DrawRect(0, 0, 480, 360, stripe); // half light, half grey backdrop
                        KeystrokeRenderer.Draw(canvas, SampleRows(), look, null, null, 40, 480, 340, 900,
                            KeystrokeAlignment.Center, KeystrokeStacking.Up, 1.0);
                    }
                    Save(frame, Path.Combine(dir, $"frame-{style}-{theme}.png"));
                }
            }

            // the picker's tiles: 2x2 styles, then the theme row, at tile size
            using (var tiles = new SKBitmap(560, 420))
            using (var canvas = new SKCanvas(tiles))
            {
                canvas.Clear(new SKColor(0x2B, 0x2B, 0x2B));
                for (int i = 0; i < KeystrokeStyles.Styles.Count; i++)
                {
                    var r = new SKRect(10 + (i % 2) * 275, 10 + (i / 2) * 110, 275 + (i % 2) * 275, 110 + (i / 2) * 110);
                    using (var tile = new SKPaint { Color = new SKColor(0x3A, 0x3A, 0x3A) })
                        canvas.DrawRoundRect(r, 8, 8, tile);
                    r.Inflate(-12, -16);
                    KeystrokePreview.Draw(canvas, r, KeystrokeStyles.Styles[i], "dark");
                }
                for (int i = 0; i < KeystrokeStyles.Themes.Count; i++)
                {
                    var r = new SKRect(10 + i * 60, 240, 64 + i * 60, 300);
                    KeystrokePreview.Draw(canvas, r, "default", KeystrokeStyles.Themes[i], keys: new[] { "A" }, typed: "a");
                    var (key, typing) = KeystrokeStyles.ThemeSwatch(KeystrokeStyles.Themes[i]);
                    using var a = new SKPaint { Color = new SKColor(key) };
                    using var b = new SKPaint { Color = new SKColor(typing) };
                    canvas.DrawRect(14 + i * 60, 320, 24, 30, a);
                    canvas.DrawRect(38 + i * 60, 320, 24, 30, b);
                }
                Save(tiles, Path.Combine(dir, "picker-tiles.png"));
            }

            Assert.True(File.Exists(Path.Combine(dir, "style-default.png")));
        }

        // ---------------------------------------------------------------------------- helpers

        /// <summary>Three rows the way a recording produces them: typing with a Backspace in it, a
        /// chord, then typing ending in Enter.</summary>
        private static List<KeyboardRow> SampleRows() => new()
        {
            new KeyboardRow(new List<KeyAtom>
            {
                new(KeyAtomKind.Word, "hello"), new(KeyAtomKind.Word, "wrold"), new(KeyAtomKind.Cap, "Bksp"),
                new(KeyAtomKind.Cap, "Bksp"), new(KeyAtomKind.Word, "ld"),
            }, false, 1, 1),
            new KeyboardRow(new List<KeyAtom>
            {
                new(KeyAtomKind.Cap, "Ctrl"), new(KeyAtomKind.Plus, "+"), new(KeyAtomKind.Cap, "Shift"),
                new(KeyAtomKind.Plus, "+"), new(KeyAtomKind.Cap, "P"),
            }, true, 1, 1),
            new KeyboardRow(new List<KeyAtom>
            {
                new(KeyAtomKind.Cap, "Tab"), new(KeyAtomKind.Word, "git"), new(KeyAtomKind.Word, "push"),
                new(KeyAtomKind.Cap, "Up"), new(KeyAtomKind.Cap, "Enter"),
            }, false, 1, 1),
        };

        private static Project KeyboardProject(KeyboardContent keyboard, out Source source)
        {
            source = new Source
            {
                Id = Guid.NewGuid(), Path = "recording.mp4", InputCapturePath = "recording.input.jsonl",
                Streams = { new SourceStream { Index = 0, Kind = StreamKind.Video, Width = 1920, Height = 1080 } },
            };
            keyboard.SourceId = source.Id;
            var screen = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 0 };
            var keys = new Track { Id = Guid.NewGuid(), Kind = TrackKind.Video, Order = 1 };
            var group = Guid.NewGuid();
            var project = new Project
            {
                Output = new OutputSettings { WidthPx = 1920, HeightPx = 1080, FpsNum = 30, FpsDen = 1, SampleRate = 48000 },
                Sources = { source },
                Tracks = { screen, keys },
                Items =
                {
                    new Item
                    {
                        Id = Guid.NewGuid(), TrackId = screen.Id, TimelineStartTicks = 0,
                        DurationTicks = 10 * TimeSpan.TicksPerSecond, GroupId = group,
                        Content = new MediaContent { SourceId = source.Id, StreamIndex = 0 },
                    },
                    new Item
                    {
                        Id = Guid.NewGuid(), TrackId = keys.Id, TimelineStartTicks = 0,
                        DurationTicks = 10 * TimeSpan.TicksPerSecond, Content = keyboard, GroupId = group,
                        Transform = new Transform { X = 0.5, Y = 0.9, Scale = 0.5 },
                    },
                },
            };
            project.Normalize();
            return project;
        }

        private static (int Top, int Bottom)? InkRows(SKBitmap bitmap)
        {
            int top = -1, bottom = -1;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha == 0)
                        continue;
                    if (top < 0)
                        top = y;
                    bottom = y;
                    break;
                }
            }
            return top < 0 ? null : (top, bottom);
        }

        private static (int Left, int Top, int Right, int Bottom)? InkBox(SKBitmap bitmap)
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha == 0)
                        continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            return right < 0 ? null : (left, top, right, bottom);
        }

        private static void Save(SKBitmap bitmap, string path)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            data.SaveTo(stream);
        }

        private static double Luma(uint argb) =>
            (0.2126 * ((argb >> 16) & 0xFF) + 0.7152 * ((argb >> 8) & 0xFF) + 0.0722 * (argb & 0xFF)) / 255.0;

        private static double Contrast(uint a, uint b)
        {
            static double Lin(uint c)
            {
                double v = c / 255.0;
                return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            static double L(uint argb) => 0.2126 * Lin((argb >> 16) & 0xFF) + 0.7152 * Lin((argb >> 8) & 0xFF) + 0.0722 * Lin(argb & 0xFF);
            double la = L(a), lb = L(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }
    }
}
