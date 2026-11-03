using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace Prisc.Config
{
    /// <summary>
    /// Whether Draw on Screen exists as a feature. Rendered by the Draw on Screen page as the tile
    /// selector at its top (<see cref="ModeSelectorAttribute"/>). Off removes every way to open it —
    /// the tray item, the hotkey and the recording and share strips' draw buttons — and hides the
    /// rest of the page. Persisted by name.
    /// </summary>
    public enum DrawOnScreenMode
    {
        [Description("On")]
        [ModeCaption("Draw over your screen from the tray menu, a hotkey, or while recording or sharing a region.")]
        [ModeIcon("IconToolPen")]
        On,

        [Description("Off")]
        [ModeCaption("Turns Draw on Screen off entirely.")]
        [ModeIcon("IconPower")]
        Off,
    }

    /// <summary>Which part of the screen the keystroke overlay sits in. Declaration order is
    /// dropdown order: the grid read row by row.</summary>
    public enum DrawKeystrokeRegion
    {
        [Description("Top left")] TopLeft,
        [Description("Top")] Top,
        [Description("Top right")] TopRight,
        [Description("Left")] Left,
        [Description("Center")] Center,
        [Description("Right")] Right,
        [Description("Bottom left")] BottomLeft,
        [Description("Bottom")] Bottom,
        [Description("Bottom right")] BottomRight,
    }

    /// <summary>
    /// Which keystrokes the overlay shows. Mirrors the video editor's
    /// <c>Prisc.VideoSDK.Model.KeystrokeFilter</c> member for member, mapped inside Prisc.Ui: this
    /// assembly does not reference the SDK.
    /// </summary>
    public enum DrawKeystrokeFilter
    {
        [Description("None")] None,
        [Description("Special keys")] Special,
        [Description("Shortcuts")] Shortcuts,
    }

    /// <summary>How a keystroke row arrives or leaves. The visual members of the SDK's
    /// <c>TransitionKind</c>, by the same names (mapped inside Prisc.Ui).</summary>
    public enum DrawKeystrokeAnimation
    {
        [Description("None")] None,
        [Description("Fade")] Fade,
        [Description("Slide left")] SlideLeft,
        [Description("Slide right")] SlideRight,
        [Description("Slide up")] SlideUp,
        [Description("Slide down")] SlideDown,
        [Description("Wipe")] Wipe,
    }

    /// <summary>The SDK's <c>TransitionEasing</c>, by the same names (mapped inside Prisc.Ui).</summary>
    public enum DrawKeystrokeEasing
    {
        [Description("Linear")] Linear,
        [Description("Cubic in")] CubicIn,
        [Description("Cubic out")] CubicOut,
        [Description("Cubic in-out")] CubicInOut,
    }

    /// <summary>
    /// The custom keystroke look, the SDK's <c>Prisc.VideoSDK.Model.KeystrokeLook</c> property for
    /// property (same names, same meaning, same units), mapped by name inside Prisc.Ui: this
    /// assembly does not reference the SDK. Shape values are fractions — the keycap's five of its
    /// outer height, the four track values of the row height — and are clamped when drawn, never
    /// here, so a hand-edited file still loads. Colors persist as <c>#AARRGGBB</c> like every other
    /// settings color. A record, so an edit that changes nothing raises nothing.
    /// </summary>
    public sealed record DrawKeystrokeLook
    {
        public double OuterRadius { get; set; }
        public double TopRadius { get; set; }
        public double PaddingX { get; set; }
        public double PaddingY { get; set; }
        public double Drop { get; set; }
        public double KeyTrackPadding { get; set; }
        public double KeyTrackRadius { get; set; }
        public double TypingPadding { get; set; }
        public double TypingRadius { get; set; }
        public Color TypingTrackColor { get; set; }
        public Color TypingTextColor { get; set; }
        public Color KeyTrackColor { get; set; }
        public Color KeyTopColor { get; set; }
        public Color KeyOuterColor { get; set; }
        public Color KeyTextColor { get; set; }
    }

    /// <summary>
    /// Settings for Draw on Screen, and for the keystroke overlay its toolbar's keyboard button
    /// turns on. The keystroke rows are the video editor's keyboard track drawn live, so the
    /// settings are that track's KEYSTROKES, placement and ANIMATION inspector rows with the same
    /// defaults — except that a screen region stands in for the track's Center X/Center Y.
    /// <para>Every value is read live by the open session (<c>DrawOnScreenSession</c>), so an edit
    /// here shows on the next keystroke.</para>
    /// </summary>
    public class SettingsDrawOnScreen : SimpleNotifyObject
    {
        /// <summary>The mode banner at the top of the page; Off hides the rest of the page and
        /// every Draw on Screen entry point in the app (see <see cref="DrawOnScreenMode"/>).</summary>
        [ModeSelector(Feature = "Draw on Screen")]
        public DrawOnScreenMode Mode
        {
            get => _mode;
            set => Set(ref _mode, value, nameof(Mode), nameof(IsEnabled));
        }

        /// <summary>Whether Draw on Screen is offered anywhere (<see cref="Mode"/> is On).</summary>
        [Browsable(false), JsonIgnore]
        public bool IsEnabled => Mode == DrawOnScreenMode.On;

        /// <summary>The toolbar's keyboard button, remembered so the next session opens the way the
        /// last one was left.</summary>
        [Browsable(false)]
        public bool ShowKeystrokes
        {
            get => _showKeystrokes;
            set => Set(ref _showKeystrokes, value);
        }

        [Category("Instant screenshot")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Initial zoom")]
        [Description("How far Draw on Instant Screenshot zooms into the picture when it opens, centered on the mouse, in percent. Scroll the mouse wheel to zoom in or out.")]
        [Range(MinScreenshotZoom, MaxScreenshotZoom)]
        [SpinnerStep(ScreenshotZoomStep)]
        public int ScreenshotZoom
        {
            get => _screenshotZoom;
            set
            {
                // kept on the spinner's 10% grid; a typed value that had to move is re-announced so
                // the field shows where it landed
                var snapped = Math.Clamp((int)Math.Round(value / (double)ScreenshotZoomStep) * ScreenshotZoomStep,
                    MinScreenshotZoom, MaxScreenshotZoom);
                if (!Set(ref _screenshotZoom, snapped) && snapped != value)
                    OnPropertyChanged();
            }
        }

        private const int MinScreenshotZoom = 100;
        private const int MaxScreenshotZoom = 1600;
        private const int ScreenshotZoomStep = 10;

        [Category("Keystroke placement")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Screen region")]
        [Description("Where on the screen pressed keys are shown.")]
        public DrawKeystrokeRegion Region
        {
            get => _region;
            set => Set(ref _region, value);
        }

        [Category("Keystroke placement")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Margin")]
        [Description("Gap between the keys and the screen edge, in pixels.")]
        [Range(0, 1000)]
        public int Margin
        {
            get => _margin;
            set => Set(ref _margin, value);
        }

        [Category("Keystroke placement")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Width")]
        [Description("Maximum row width as a share of the screen width.")]
        [Range(0.0, 1.0)]
        public double Width
        {
            get => _width;
            set => Set(ref _width, value);
        }

        [Category("Keystroke placement")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Opacity")]
        [Range(0.0, 1.0)]
        public double Opacity
        {
            get => _opacity;
            set => Set(ref _opacity, value);
        }

        [Category("Keystrokes")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Filter")]
        [Description("Show every key, only special keys and shortcuts, or only shortcuts.")]
        public DrawKeystrokeFilter Filter
        {
            get => _filter;
            set => Set(ref _filter, value);
        }

        [Category("Keystrokes")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Size")]
        [Description("Text size of the shown keys, in pixels.")]
        [Range(8, 200)]
        public int FontSize
        {
            get => _fontSize;
            set => Set(ref _fontSize, value);
        }

        [Category("Keystrokes")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Linger")]
        [Description("How long a row stays on screen after its last key, in milliseconds.")]
        [Range(0, 10000)]
        public int LingerMs
        {
            get => _lingerMs;
            set => Set(ref _lingerMs, value);
        }

        [Category("Keystrokes")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("New row")]
        [Description("A pause in typing longer than this, in milliseconds, starts a new row.")]
        [Range(0, 10000)]
        public int PauseBreakMs
        {
            get => _pauseBreakMs;
            set => Set(ref _pauseBreakMs, value);
        }

        /// <summary>The keystroke style: one of the video editor's keyboard-track style ids
        /// (<c>default</c>, <c>laptop</c>, <c>simple</c>, <c>custom</c>; the SDK's
        /// <c>KeystrokeStyles</c>), mapped inside Prisc.Ui. A string rather than an enum because the
        /// ids are the SDK's wire contract: an id this build does not know (a newer build's style)
        /// draws the default instead of failing the whole settings file. Its row is the shared
        /// keystroke style picker, built by the settings page itself, which edits
        /// <see cref="KeystrokeTheme"/> and <see cref="KeystrokeCustom"/> with it.</summary>
        [Category("Keystroke style")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Look")]
        [Description("How pressed keys and typed text look. Pick a style and a theme, or Custom to start from the look you were on and change any part of it.")]
        public string KeystrokeStyle
        {
            get => _keystrokeStyle;
            set => Set(ref _keystrokeStyle, value);
        }

        /// <summary>The theme the preset styles are drawn in: one of the SDK's
        /// <c>KeystrokeStyles.Themes</c> ids (<c>dark</c>, <c>light</c>, <c>pink</c>...). The custom
        /// style carries its own colors and ignores it. Edited by the style picker, so not a row of
        /// its own.</summary>
        [Browsable(false)]
        public string KeystrokeTheme
        {
            get => _keystrokeTheme;
            set => Set(ref _keystrokeTheme, value);
        }

        /// <summary>The custom style's look, null until the custom style was first picked (it is
        /// then seeded from the preset and theme on screen). Kept when another style is picked, so
        /// the user's own look survives trips through the presets. Replaced whole on every edit,
        /// never changed in place, so this object raises the change.</summary>
        [Browsable(false)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DrawKeystrokeLook KeystrokeCustom
        {
            get => _keystrokeCustom;
            set => Set(ref _keystrokeCustom, value);
        }

        [Category("Keystroke animation")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Entry")]
        [Description("How each new row arrives.")]
        public DrawKeystrokeAnimation EntryKind
        {
            get => _entryKind;
            set => Set(ref _entryKind, value, nameof(EntryKind), nameof(EntryAnimates));
        }

        [Category("Keystroke animation")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Entry length")]
        [Description("In milliseconds.")]
        [Range(0, 10000)]
        [DisabledWhen(nameof(EntryAnimates), false)]
        public int EntryDurationMs
        {
            get => _entryDurationMs;
            set => Set(ref _entryDurationMs, value);
        }

        [Category("Keystroke animation")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Entry easing")]
        [DisabledWhen(nameof(EntryAnimates), false)]
        public DrawKeystrokeEasing EntryEasing
        {
            get => _entryEasing;
            set => Set(ref _entryEasing, value);
        }

        [Category("Keystroke animation")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Exit")]
        [Description("How a row leaves once its linger is up.")]
        public DrawKeystrokeAnimation ExitKind
        {
            get => _exitKind;
            set => Set(ref _exitKind, value, nameof(ExitKind), nameof(ExitAnimates));
        }

        [Category("Keystroke animation")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Exit length")]
        [Description("In milliseconds.")]
        [Range(0, 10000)]
        [DisabledWhen(nameof(ExitAnimates), false)]
        public int ExitDurationMs
        {
            get => _exitDurationMs;
            set => Set(ref _exitDurationMs, value);
        }

        [Category("Keystroke animation")]
        [VisibleWhen(nameof(Mode), DrawOnScreenMode.On)]
        [DisplayName("Exit easing")]
        [DisabledWhen(nameof(ExitAnimates), false)]
        public DrawKeystrokeEasing ExitEasing
        {
            get => _exitEasing;
            set => Set(ref _exitEasing, value);
        }

        /// <summary>Gates for the length and easing rows. [DisabledWhen] is bool-only, so these
        /// stand between it and the enums. Not [Browsable], so they are not rows of their own.</summary>
        [Browsable(false), JsonIgnore]
        public bool EntryAnimates => _entryKind != DrawKeystrokeAnimation.None;

        [Browsable(false), JsonIgnore]
        public bool ExitAnimates => _exitKind != DrawKeystrokeAnimation.None;

        // the keyboard track's defaults (KeyboardContent, and EditorSession.AddKeyboardTrack's
        // transform and row transitions), so a live overlay and a fresh track look alike
        private DrawOnScreenMode _mode = DrawOnScreenMode.On;
        private bool _showKeystrokes;
        private int _screenshotZoom = 100;
        private DrawKeystrokeRegion _region = DrawKeystrokeRegion.Bottom;
        private int _margin = 36;
        private double _width = 0.5;
        private double _opacity = 1.0;
        private DrawKeystrokeFilter _filter = DrawKeystrokeFilter.None;
        private int _fontSize = 40;
        private int _lingerMs = 1000;
        private int _pauseBreakMs = 1000;
        private string _keystrokeStyle = "default";
        private string _keystrokeTheme = "dark";
        private DrawKeystrokeLook _keystrokeCustom;
        private DrawKeystrokeAnimation _entryKind = DrawKeystrokeAnimation.SlideUp;
        private int _entryDurationMs = 300;
        private DrawKeystrokeEasing _entryEasing = DrawKeystrokeEasing.CubicInOut;
        private DrawKeystrokeAnimation _exitKind = DrawKeystrokeAnimation.Fade;
        private int _exitDurationMs = 300;
        private DrawKeystrokeEasing _exitEasing = DrawKeystrokeEasing.CubicInOut;
    }
}
