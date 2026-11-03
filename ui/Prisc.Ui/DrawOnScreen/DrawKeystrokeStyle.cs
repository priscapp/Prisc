using System;
using Prisc.Config;
using Prisc.UI.Controls;
using Prisc.VideoSDK.Model;

namespace Prisc.UI.DrawOnScreen
{
    /// <summary>
    /// Draw on Screen's keystroke style settings (<see cref="SettingsDrawOnScreen.KeystrokeStyle"/>,
    /// <see cref="SettingsDrawOnScreen.KeystrokeTheme"/>, <see cref="SettingsDrawOnScreen.KeystrokeCustom"/>)
    /// as the SDK's keyboard-track values. Prisc.Shared mirrors <see cref="KeystrokeLook"/> as
    /// <see cref="DrawKeystrokeLook"/> because it cannot reference the SDK; the two carry the same
    /// members under the same names, and this is the one place that copies between them.
    /// </summary>
    public static class DrawKeystrokeStyle
    {
        /// <summary>The settings' custom look as the SDK's; null stays null (never seeded).</summary>
        public static KeystrokeLook ToLook(DrawKeystrokeLook look) => look == null ? null : new KeystrokeLook
        {
            OuterRadius = look.OuterRadius,
            TopRadius = look.TopRadius,
            PaddingX = look.PaddingX,
            PaddingY = look.PaddingY,
            Drop = look.Drop,
            KeyTrackPadding = look.KeyTrackPadding,
            KeyTrackRadius = look.KeyTrackRadius,
            TypingPadding = look.TypingPadding,
            TypingRadius = look.TypingRadius,
            TypingTrackColor = look.TypingTrackColor.ToUInt32(),
            TypingTextColor = look.TypingTextColor.ToUInt32(),
            KeyTrackColor = look.KeyTrackColor.ToUInt32(),
            KeyTopColor = look.KeyTopColor.ToUInt32(),
            KeyOuterColor = look.KeyOuterColor.ToUInt32(),
            KeyTextColor = look.KeyTextColor.ToUInt32(),
        };

        /// <summary>The SDK's look as the settings'; null stays null.</summary>
        public static DrawKeystrokeLook FromLook(KeystrokeLook look) => look == null ? null : new DrawKeystrokeLook
        {
            OuterRadius = look.OuterRadius,
            TopRadius = look.TopRadius,
            PaddingX = look.PaddingX,
            PaddingY = look.PaddingY,
            Drop = look.Drop,
            KeyTrackPadding = look.KeyTrackPadding,
            KeyTrackRadius = look.KeyTrackRadius,
            TypingPadding = look.TypingPadding,
            TypingRadius = look.TypingRadius,
            TypingTrackColor = Avalonia.Media.Color.FromUInt32(look.TypingTrackColor),
            TypingTextColor = Avalonia.Media.Color.FromUInt32(look.TypingTextColor),
            KeyTrackColor = Avalonia.Media.Color.FromUInt32(look.KeyTrackColor),
            KeyTopColor = Avalonia.Media.Color.FromUInt32(look.KeyTopColor),
            KeyOuterColor = Avalonia.Media.Color.FromUInt32(look.KeyOuterColor),
            KeyTextColor = Avalonia.Media.Color.FromUInt32(look.KeyTextColor),
        };

        /// <summary>Copies the style, theme and custom look of <paramref name="settings"/> onto
        /// <paramref name="keyboard"/>, which is what the shared renderer reads. The ids pass through
        /// as stored: the SDK resolves an unknown one to its default.</summary>
        public static void ApplyTo(SettingsDrawOnScreen settings, KeyboardContent keyboard)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(keyboard);
            keyboard.Style = settings.KeystrokeStyle;
            keyboard.Theme = settings.KeystrokeTheme;
            keyboard.Custom = ToLook(settings.KeystrokeCustom);
        }

        /// <summary>Whether the style and theme are the ones a fresh install starts with (the
        /// custom look, kept either way, does not count).</summary>
        public static bool IsDefault(SettingsDrawOnScreen settings) =>
            settings == null
            || (string.Equals(settings.KeystrokeStyle, KeystrokeStyles.DefaultStyle, StringComparison.OrdinalIgnoreCase)
                && string.Equals(settings.KeystrokeTheme, KeystrokeStyles.DefaultTheme, StringComparison.OrdinalIgnoreCase));

        /// <summary>Back to the default style and theme. The custom look is kept, so picking
        /// Custom again brings the user's own work back.</summary>
        public static void Reset(SettingsDrawOnScreen settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            settings.KeystrokeTheme = KeystrokeStyles.DefaultTheme;
            settings.KeystrokeStyle = KeystrokeStyles.DefaultStyle;
        }
    }

    /// <summary>
    /// The keystroke style picker's host over Draw on Screen's settings: reads the three values
    /// out of <see cref="SettingsDrawOnScreen"/> and writes the edited ones back. The settings
    /// raise their own change for each value written, which is what saves them (the settings
    /// window's auto-save) and restyles an open session's keys (<c>DrawOnScreenSession</c>).
    /// There is no undo stack on the settings page, so edit bursts are not coalesced.
    /// </summary>
    public sealed class DrawKeystrokeStyleHost : IKeystrokeStyleHost
    {
        private readonly SettingsDrawOnScreen _settings;

        public DrawKeystrokeStyleHost(SettingsDrawOnScreen settings)
        {
            _settings = settings;
        }

        /// <summary>True while <see cref="Edit"/> writes the settings, so a page refreshing the
        /// picker on every settings change can skip the echo of the picker's own write (the
        /// picker re-reads once the write is done).</summary>
        public bool IsWriting { get; private set; }

        public KeystrokeStyleState? Read() => _settings == null
            ? null
            : new KeystrokeStyleState(_settings.KeystrokeStyle, _settings.KeystrokeTheme, DrawKeystrokeStyle.ToLook(_settings.KeystrokeCustom));

        public void Edit(string coalesceKey, Action<KeyboardContent> edit)
        {
            if (_settings == null || edit == null)
                return;

            var keyboard = new KeyboardContent();
            DrawKeystrokeStyle.ApplyTo(_settings, keyboard);
            edit(keyboard);

            IsWriting = true;
            try
            {
                // the custom look first and the style last: entering custom seeds the look, and a
                // listener that restyles on each change then never sees the custom style with the
                // previous look
                _settings.KeystrokeCustom = DrawKeystrokeStyle.FromLook(keyboard.Custom);
                _settings.KeystrokeTheme = keyboard.Theme;
                _settings.KeystrokeStyle = keyboard.Style;
            }
            finally
            {
                IsWriting = false;
            }
        }
    }
}
