using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Media;
using Prisc.Config;
using Prisc.UI.Controls;
using Prisc.UI.DrawOnScreen;
using Prisc.VideoSDK.Model;
using Xunit;

namespace Prisc.VideoSDK.Tests
{
    /// <summary>
    /// Draw on Screen's keystroke style settings: the Prisc.Shared mirror of
    /// <see cref="KeystrokeLook"/>, its mapping to the SDK, and the picker's host over the settings.
    /// The settings page block itself is Avalonia and is not built here (no headless platform in
    /// this assembly); it adds nothing to the host but the refresh on outside changes.
    /// </summary>
    public class DrawKeystrokeStyleSettingsTests
    {
        private static PropertyInfo[] Props(Type t) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).OrderBy(p => p.Name).ToArray();

        [Fact]
        public void TheMirror_HasEveryMemberOfTheLook_ByNameAndKind()
        {
            var sdk = Props(typeof(KeystrokeLook));
            var mirror = Props(typeof(DrawKeystrokeLook));

            Assert.Equal(sdk.Select(p => p.Name), mirror.Select(p => p.Name));
            foreach (var (s, m) in sdk.Zip(mirror))
            {
                // fractions stay doubles; packed colors become settings colors ("#AARRGGBB")
                var expected = s.PropertyType == typeof(uint) ? typeof(Color) : s.PropertyType;
                Assert.Equal(expected, m.PropertyType);
            }
        }

        /// <summary>A look whose every member differs from every other, so a mapping that crossed
        /// two members over would show.</summary>
        private static KeystrokeLook DistinctLook()
        {
            var look = new KeystrokeLook();
            var i = 1;
            foreach (var p in Props(typeof(KeystrokeLook)))
            {
                if (p.PropertyType == typeof(double))
                    p.SetValue(look, i * 0.0137);
                else
                    p.SetValue(look, 0x01020304u * (uint)i | ((uint)(0x10 * i) << 24));
                i++;
            }
            return look;
        }

        [Fact]
        public void Mapping_RoundTripsEveryMember()
        {
            var look = DistinctLook();

            var mirrored = DrawKeystrokeStyle.FromLook(look);
            Assert.Equal(look, DrawKeystrokeStyle.ToLook(mirrored));
            Assert.Equal(mirrored, DrawKeystrokeStyle.FromLook(DrawKeystrokeStyle.ToLook(mirrored)));

            // member for member by name, not merely round-tripping through a swap
            foreach (var p in Props(typeof(KeystrokeLook)))
            {
                var m = typeof(DrawKeystrokeLook).GetProperty(p.Name);
                var value = m.GetValue(mirrored);
                var asSdk = value is Color c ? c.ToUInt32() : value;
                Assert.Equal(p.GetValue(look), asSdk);
            }

            Assert.Null(DrawKeystrokeStyle.ToLook(null));
            Assert.Null(DrawKeystrokeStyle.FromLook(null));
        }

        [Fact]
        public void FreshSettings_DrawLikeAFreshKeyboardTrack()
        {
            var settings = new SettingsDrawOnScreen();
            var keyboard = new KeyboardContent { Style = "x", Theme = "y", Custom = new KeystrokeLook() };

            DrawKeystrokeStyle.ApplyTo(settings, keyboard);

            var fresh = new KeyboardContent();
            Assert.Equal(KeystrokeStyles.DefaultStyle, keyboard.Style);
            Assert.Equal(KeystrokeStyles.DefaultTheme, keyboard.Theme);
            Assert.Equal(fresh.Style, keyboard.Style);
            Assert.Equal(fresh.Theme, keyboard.Theme);
            Assert.Null(keyboard.Custom);
            Assert.Equal(KeystrokeStyles.Resolve(fresh), KeystrokeStyles.Resolve(keyboard));
            Assert.True(DrawKeystrokeStyle.IsDefault(settings));
        }

        [Fact]
        public void ApplyTo_CarriesStyleThemeAndCustomLook()
        {
            var custom = KeystrokeStyles.Preset(KeystrokeStyles.Simple, "teal") with { Drop = 0.2 };
            var settings = new SettingsDrawOnScreen
            {
                KeystrokeStyle = KeystrokeStyles.Custom,
                KeystrokeTheme = KeystrokeStyles.Light,
                KeystrokeCustom = DrawKeystrokeStyle.FromLook(custom),
            };
            var keyboard = new KeyboardContent();

            DrawKeystrokeStyle.ApplyTo(settings, keyboard);

            Assert.Equal(KeystrokeStyles.Custom, keyboard.Style);
            Assert.Equal(KeystrokeStyles.Light, keyboard.Theme);
            Assert.Equal(custom, keyboard.Custom);
            Assert.Equal(custom.Clamped(), KeystrokeStyles.Resolve(keyboard));

            // the overlay draws a preset in the stored theme once the style moves off custom
            settings.KeystrokeStyle = KeystrokeStyles.Laptop;
            DrawKeystrokeStyle.ApplyTo(settings, keyboard);
            Assert.Equal(KeystrokeStyles.Preset(KeystrokeStyles.Laptop, KeystrokeStyles.Light), KeystrokeStyles.Resolve(keyboard));
        }

        private static KeystrokeStyleOption StyleOption(string id) =>
            KeystrokeStyleEditor.StyleOptions.First(o => o.Value == id);

        private static KeystrokeStyleOption ThemeOption(string id) =>
            KeystrokeStyleEditor.ThemeOptions.First(o => o.Value == id);

        [Fact]
        public void ThePicker_OverTheSettings_SeedsCustomFromTheLookOnScreen_EveryTimeItIsPickedFromAPreset()
        {
            var settings = new SettingsDrawOnScreen();
            var editor = new KeystrokeStyleEditor(new DrawKeystrokeStyleHost(settings));

            editor.SelectedStyle = StyleOption(KeystrokeStyles.Laptop);
            editor.SelectedTheme = ThemeOption("pink");
            Assert.Equal(KeystrokeStyles.Laptop, settings.KeystrokeStyle);
            Assert.Equal("pink", settings.KeystrokeTheme);
            Assert.Null(settings.KeystrokeCustom);

            // custom starts from Laptop/Pink, the look that was on screen
            editor.SelectedStyle = StyleOption(KeystrokeStyles.Custom);
            Assert.Equal(KeystrokeStyles.Custom, settings.KeystrokeStyle);
            Assert.Equal(KeystrokeStyles.Preset(KeystrokeStyles.Laptop, "pink"), DrawKeystrokeStyle.ToLook(settings.KeystrokeCustom));

            // an edit lands in the settings...
            editor.ShapeRows.First(r => r.Label == "Offset").Value = 0.3;
            var top = editor.KeyRows.OfType<KeystrokeColorRow>().First(r => r.Label == "Key top");
            top.Hex = "#80123456";
            Assert.Equal(0.3, settings.KeystrokeCustom.Drop);
            Assert.Equal(Color.FromUInt32(0x80123456), settings.KeystrokeCustom.KeyTopColor);
            var edited = settings.KeystrokeCustom;

            // ...is kept while a preset is merely on show...
            editor.SelectedStyle = StyleOption(KeystrokeStyles.Simple);
            editor.SelectedTheme = ThemeOption(KeystrokeStyles.Light);
            Assert.Equal(edited, settings.KeystrokeCustom);

            // ...and picking custom again starts over from that preset, Simple/Light
            editor.SelectedStyle = StyleOption(KeystrokeStyles.Custom);
            Assert.Equal(KeystrokeStyles.Preset(KeystrokeStyles.Simple, KeystrokeStyles.Light), DrawKeystrokeStyle.ToLook(settings.KeystrokeCustom));
            Assert.Equal(0, editor.ShapeRows.First(r => r.Label == "Offset").Value);
        }

        [Fact]
        public void HostWrites_RaiseTheStyleLast_AndOnlyWhatChanged()
        {
            var settings = new SettingsDrawOnScreen { KeystrokeTheme = "blue" };
            var host = new DrawKeystrokeStyleHost(settings);
            var raised = new List<(string Name, bool Writing)>();
            settings.PropertyChanged += (_, e) => raised.Add((e.PropertyName, host.IsWriting));

            host.Edit("k", k => KeystrokeStyles.SelectStyle(k, KeystrokeStyles.Custom));

            Assert.Equal(new[]
            {
                (nameof(SettingsDrawOnScreen.KeystrokeCustom), true),
                (nameof(SettingsDrawOnScreen.KeystrokeStyle), true),
            }, raised);
            Assert.False(host.IsWriting);

            // an edit that changes nothing raises nothing (the mirror is a record)
            raised.Clear();
            host.Edit("k", _ => { });
            Assert.Empty(raised);
        }

        [Fact]
        public void Reset_GoesBackToDefaultDark_AndKeepsTheCustomLook()
        {
            var custom = DrawKeystrokeStyle.FromLook(KeystrokeStyles.Preset(KeystrokeStyles.Simple, "red") with { Drop = 0.25 });
            var settings = new SettingsDrawOnScreen
            {
                KeystrokeStyle = KeystrokeStyles.Custom,
                KeystrokeTheme = "red",
                KeystrokeCustom = custom,
            };
            Assert.False(DrawKeystrokeStyle.IsDefault(settings));

            DrawKeystrokeStyle.Reset(settings);

            Assert.True(DrawKeystrokeStyle.IsDefault(settings));
            Assert.Equal(KeystrokeStyles.DefaultStyle, settings.KeystrokeStyle);
            Assert.Equal(KeystrokeStyles.DefaultTheme, settings.KeystrokeTheme);
            Assert.Same(custom, settings.KeystrokeCustom);
        }
    }
}
