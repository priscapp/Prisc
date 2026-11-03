using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Prisc.VideoSDK.Model;

namespace Prisc.UI.Controls
{
    /// <summary>
    /// The keystroke style settings a <see cref="KeystrokeStyleEditor"/> shows: the three values
    /// <see cref="KeyboardContent"/> stores for them. <see cref="Custom"/> is null until the user
    /// first picks the <c>custom</c> style.
    /// </summary>
    public readonly record struct KeystrokeStyleState(string Style, string Theme, KeystrokeLook Custom);

    /// <summary>
    /// What a <see cref="KeystrokeStylePicker"/> needs from the place it edits: a way to read the
    /// style settings and a way to write them. The video editor's inspector adapts the selected
    /// keyboard row to it; Draw on Screen's settings page adapts its settings object.
    /// <para>
    /// The host also tells the editor when its values changed from elsewhere (undo, a new
    /// selection, a settings reload) by calling <see cref="KeystrokeStyleEditor.Refresh"/>; the
    /// editor re-reads by itself after its own writes.
    /// </para>
    /// </summary>
    public interface IKeystrokeStyleHost
    {
        /// <summary>The settings being edited, or null when there is nothing to edit (the picker
        /// then shows the defaults and ignores input).</summary>
        KeystrokeStyleState? Read();

        /// <summary>
        /// Applies <paramref name="edit"/> to the settings. The callback receives a
        /// <see cref="KeyboardContent"/> holding the current <c>Style</c>, <c>Theme</c> and
        /// <c>Custom</c> (other members are meaningless to it) and may change any of the three — it
        /// goes through <see cref="KeystrokeStyles.SelectStyle"/> for the style, so a host that
        /// keeps its own copy just copies the three back afterwards. Edits that share a
        /// <paramref name="coalesceKey"/> arrive in bursts (a spinner drag, a live color pick), and a
        /// host with an undo stack should fold each burst into one entry.
        /// </summary>
        void Edit(string coalesceKey, Action<KeyboardContent> edit);
    }

    /// <summary>One style or theme tile: the wire id and its menu label.</summary>
    public sealed class KeystrokeStyleOption
    {
        public KeystrokeStyleOption(string value, string label)
        {
            Value = value;
            Label = label;
        }

        public string Value { get; }

        public string Label { get; }

        public override string ToString() => Label;
    }

    /// <summary>
    /// The view model behind <see cref="KeystrokeStylePicker"/>: the style tiles, the theme tiles and
    /// the custom editor's rows, over an <see cref="IKeystrokeStyleHost"/>. Free of Avalonia types
    /// so a test can drive it with no UI framework at all.
    /// <para>
    /// The presets are data in <see cref="KeystrokeStyles"/>; this class only reads and writes the
    /// three stored values. Picking <c>custom</c> goes through <see cref="KeystrokeStyles.SelectStyle"/>,
    /// which seeds the custom look from the preset and theme on screen the first time (and keeps the
    /// user's own work after that). Every custom row writes a fresh copy of the look, never the
    /// stored instance, and is clamped to the row's range so what is stored is what the row shows.
    /// </para>
    /// </summary>
    public sealed class KeystrokeStyleEditor : INotifyPropertyChanged
    {
        /// <summary>The four style tiles, in the 2x2 picker's reading order.</summary>
        public static readonly IReadOnlyList<KeystrokeStyleOption> StyleOptions = KeystrokeStyles.Styles
            .Select(s => new KeystrokeStyleOption(s, KeystrokeStyles.StyleDisplayName(s)))
            .ToArray();

        /// <summary>The theme tiles, in menu order.</summary>
        public static readonly IReadOnlyList<KeystrokeStyleOption> ThemeOptions = KeystrokeStyles.Themes
            .Select(t => new KeystrokeStyleOption(t, KeystrokeStyles.ThemeDisplayName(t)))
            .ToArray();

        private readonly IKeystrokeStyleHost _host;

        private string _style = KeystrokeStyles.DefaultStyle;
        private string _theme = KeystrokeStyles.DefaultTheme;
        private KeystrokeLook _customTileLook;
        private bool _hasTarget;
        private bool _syncing;

        public KeystrokeStyleEditor(IKeystrokeStyleHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));

            ShapeRows = new[]
            {
                Number("outer", "Outer radius", "Corner radius of the outer key, as a share of the key's height.",
                    KeystrokeLook.MaxRadius, l => l.OuterRadius, (l, v) => l with { OuterRadius = v }),
                Number("top", "Top radius", "Corner radius of the key top, as a share of the key's height.",
                    KeystrokeLook.MaxRadius, l => l.TopRadius, (l, v) => l with { TopRadius = v }),
                Number("padw", "Padding W", "How far the key top sits in from the outer key's sides.",
                    KeystrokeLook.MaxPadding, l => l.PaddingX, (l, v) => l with { PaddingX = v }),
                Number("padh", "Padding H", "How far the key top sits down from the outer key's top edge (and up from the bottom, with the offset added).",
                    KeystrokeLook.MaxPadding, l => l.PaddingY, (l, v) => l with { PaddingY = v }),
                Number("drop", "Offset", "Vertical offset: how much of the outer key shows below the key top — the 3D drop.",
                    KeystrokeLook.MaxDrop, l => l.Drop, (l, v) => l with { Drop = v }),
            };

            KeyRows = new KeystrokeStyleRow[]
            {
                Color("keytop", "Key top", "The face of every keycap.", l => l.KeyTopColor, (l, v) => l with { KeyTopColor = v }),
                Color("keyouter", "Key outer", "The rim and drop of every keycap.", l => l.KeyOuterColor, (l, v) => l with { KeyOuterColor = v }),
                Color("keytext", "Key text", "Keycap legends and icons.", l => l.KeyTextColor, (l, v) => l with { KeyTextColor = v }),
                Color("keytrack", "Track", "The panel behind each run of keycaps. Fully transparent draws none.",
                    l => l.KeyTrackColor, (l, v) => l with { KeyTrackColor = v }),
                Number("keytrackpad", "Track padding", "Space between the key track's edge and the keycaps on it, as a share of the row's height.",
                    KeystrokeLook.MaxKeyTrackPadding, l => l.KeyTrackPadding, (l, v) => l with { KeyTrackPadding = v }),
                Number("keytrackradius", "Track radius", "Corner radius of the key track, as a share of the row's height.",
                    KeystrokeLook.MaxRadius, l => l.KeyTrackRadius, (l, v) => l with { KeyTrackRadius = v }),
            };

            TypingRows = new KeystrokeStyleRow[]
            {
                Color("typetext", "Text", "Typed text.", l => l.TypingTextColor, (l, v) => l with { TypingTextColor = v }),
                Color("typetrack", "Track", "The rounded panel typed text sits on. Fully transparent draws none.",
                    l => l.TypingTrackColor, (l, v) => l with { TypingTrackColor = v }),
                Number("typepad", "Padding", "Space either side of the typed text, as a share of the row's height.",
                    KeystrokeLook.MaxTypingPadding, l => l.TypingPadding, (l, v) => l with { TypingPadding = v }),
                Number("typeradius", "Radius", "Corner radius of the typing track, as a share of the row's height.",
                    KeystrokeLook.MaxRadius, l => l.TypingRadius, (l, v) => l with { TypingRadius = v }),
            };

            Refresh();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The custom editor's keycap shape rows.</summary>
        public IReadOnlyList<KeystrokeNumberRow> ShapeRows { get; }

        /// <summary>The custom editor's special-key rows (colors, then the key track's shape).</summary>
        public IReadOnlyList<KeystrokeStyleRow> KeyRows { get; }

        /// <summary>The custom editor's typing rows (colors, then the typing track's shape).</summary>
        public IReadOnlyList<KeystrokeStyleRow> TypingRows { get; }

        /// <summary>Every custom row, shape first.</summary>
        public IEnumerable<KeystrokeStyleRow> AllRows => ShapeRows.Cast<KeystrokeStyleRow>().Concat(KeyRows).Concat(TypingRows);

        /// <summary>Whether the host has anything to edit right now.</summary>
        public bool HasTarget => _hasTarget;

        /// <summary>The picked style's id, normalized to one of <see cref="KeystrokeStyles.Styles"/>
        /// (an unknown stored id reads as the default style, as it draws).</summary>
        public string Style => _style;

        /// <summary>The picked theme's id, normalized like <see cref="Style"/>.</summary>
        public string Theme => _theme;

        /// <summary>The style the theme tiles draw: the picked one (they are only shown for a
        /// preset).</summary>
        public string PresetStyle => IsCustom ? KeystrokeStyles.DefaultStyle : _style;

        public bool IsCustom => KeystrokeStyles.IsCustom(_style);

        /// <summary>Whether the theme row is on show: for the presets only — <c>custom</c> carries
        /// its own colors.</summary>
        public bool IsPreset => !IsCustom;

        /// <summary>What the Custom tile draws: the custom look while it is picked, else what picking
        /// it would start from (the user's own look once they made one, else the preset on screen).
        /// A fresh instance on every change, so a preview bound to it sees the change.</summary>
        public KeystrokeLook CustomTileLook => _customTileLook;

        /// <summary>The style tile that reads as picked; setting it is the user's pick.</summary>
        public KeystrokeStyleOption SelectedStyle
        {
            get => StyleOptions.First(o => o.Value == _style);
            set
            {
                if (value == null || _syncing || value.Value == _style)
                    return;
                if (!_hasTarget)
                {
                    // nothing to write to: snap the tiles back to what is drawn
                    OnPropertyChanged(nameof(SelectedStyle));
                    return;
                }

                var style = value.Value;
                Write("keystyle", k => KeystrokeStyles.SelectStyle(k, style));
            }
        }

        /// <summary>The theme tile that reads as picked; setting it is the user's pick.</summary>
        public KeystrokeStyleOption SelectedTheme
        {
            get => ThemeOptions.First(o => o.Value == _theme);
            set
            {
                if (value == null || _syncing || value.Value == _theme)
                    return;
                if (!_hasTarget)
                {
                    OnPropertyChanged(nameof(SelectedTheme));
                    return;
                }

                var theme = value.Value;
                Write("keytheme", k => k.Theme = theme);
            }
        }

        /// <summary>Re-reads everything from the host. Call it when the host's values changed from
        /// anywhere but this editor.</summary>
        public void Refresh()
        {
            _syncing = true;
            try
            {
                var state = _host.Read();
                SetField(ref _hasTarget, state.HasValue, nameof(HasTarget));

                var style = Normalize(state?.Style, KeystrokeStyles.Styles, KeystrokeStyles.DefaultStyle);
                var theme = Normalize(state?.Theme, KeystrokeStyles.Themes, KeystrokeStyles.DefaultTheme);
                var custom = state?.Custom;

                bool styleChanged = SetField(ref _style, style, nameof(Style));
                bool themeChanged = SetField(ref _theme, theme, nameof(Theme));
                if (styleChanged)
                {
                    OnPropertyChanged(nameof(SelectedStyle));
                    OnPropertyChanged(nameof(IsCustom));
                    OnPropertyChanged(nameof(IsPreset));
                    OnPropertyChanged(nameof(PresetStyle));
                }
                if (themeChanged)
                    OnPropertyChanged(nameof(SelectedTheme));

                var tile = IsCustom
                    ? KeystrokeStyles.Resolve(style, theme, custom)
                    : KeystrokeStyles.SeedCustom(style, theme);
                if (!Equals(tile, _customTileLook))
                {
                    _customTileLook = tile;
                    OnPropertyChanged(nameof(CustomTileLook));
                }

                // the rows show the look being edited as it is stored — unclamped, so a spinner held
                // past the point where padding and offset squeeze the key top is not fought by its
                // own echo — or, before the custom style was ever picked, what picking it would
                // start from. They reset to the preset the look is nearest to — the one it was
                // seeded from, for a look edited only a little — so a freshly seeded look shows no
                // reset dots and a reset never swaps in some other preset's value.
                var shown = IsCustom
                    ? custom ?? KeystrokeStyles.Preset(KeystrokeStyles.DefaultStyle, theme)
                    : KeystrokeStyles.SeedCustom(style, theme);
                var defaults = KeystrokeStyles.NearestPreset(shown, theme);
                foreach (var row in AllRows)
                    row.Sync(shown, defaults);
            }
            finally
            {
                _syncing = false;
            }
        }

        // ------------------------------------------------------------------------ writes

        /// <summary>A custom row's write: the look with one value changed, seeded first if the
        /// custom style never was (the rows are only on show while it is picked, but the host may
        /// have moved under them).</summary>
        internal void WriteCustom(string coalesceKey, Func<KeystrokeLook, KeystrokeLook> change)
        {
            if (_syncing || !_hasTarget)
                return;

            Write(coalesceKey, k =>
            {
                if (!KeystrokeStyles.IsCustom(k.Style) || k.Custom == null)
                    KeystrokeStyles.SelectStyle(k, KeystrokeStyles.Custom);
                k.Custom = change(k.Custom ?? KeystrokeStyles.SeedCustom(k.Style, k.Theme));
            });
        }

        internal bool IsSyncing => _syncing;

        private void Write(string coalesceKey, Action<KeyboardContent> edit)
        {
            _host.Edit(coalesceKey, edit);
            // the host may suppress its echo of our own write (the inspector does), so re-read
            // here: a style pick changes the tiles, rows and visibility, not only the field set
            Refresh();
        }

        // ---------------------------------------------------------------------- plumbing

        private KeystrokeNumberRow Number(string key, string label, string description, double max,
            Func<KeystrokeLook, double> get, Func<KeystrokeLook, double, KeystrokeLook> set) =>
            new(this, "keycustom:" + key, label, description, max, get, set);

        private KeystrokeColorRow Color(string key, string label, string description,
            Func<KeystrokeLook, uint> get, Func<KeystrokeLook, uint, KeystrokeLook> set) =>
            new(this, "keycustom:" + key, label, description, get, set);

        private static string Normalize(string id, IReadOnlyList<string> ids, string fallback) =>
            ids.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase)) ?? fallback;

        private bool SetField<T>(ref T field, T value, string name)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary><c>#AARRGGBB</c> for a packed color.</summary>
        public static string HexOf(uint argb) => "#" + argb.ToString("X8", CultureInfo.InvariantCulture);

        /// <summary>Parses <c>#RRGGBB</c> (opaque) or <c>#AARRGGBB</c>; false for anything
        /// else, so a half-typed value is never written.</summary>
        public static bool TryParseHex(string value, out uint argb)
        {
            argb = 0;
            var text = value?.Trim();
            if (text == null || text.Length is not (7 or 9) || text[0] != '#')
                return false;

            var digits = text.Substring(1);
            if (digits.Length == 6)
                digits = "FF" + digits;

            return uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out argb);
        }
    }

    /// <summary>One row of the custom editor: a label and the value it edits.</summary>
    public abstract class KeystrokeStyleRow : INotifyPropertyChanged
    {
        protected KeystrokeStyleRow(KeystrokeStyleEditor owner, string label, string description)
        {
            Owner = owner;
            Label = label;
            Description = description;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string Label { get; }

        /// <summary>The row's tooltip.</summary>
        public string Description { get; }

        protected KeystrokeStyleEditor Owner { get; }

        /// <summary>Takes the row's value from <paramref name="look"/> and its reset value from
        /// <paramref name="defaults"/>, raising only what changed.</summary>
        internal abstract void Sync(KeystrokeLook look, KeystrokeLook defaults);

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A shape value: a fraction of the key's (or the row's) height, 0..<see cref="Max"/>,
    /// shown as a percentage.</summary>
    public sealed class KeystrokeNumberRow : KeystrokeStyleRow
    {
        private readonly Func<KeystrokeLook, double> _get;
        private readonly Func<KeystrokeLook, double, KeystrokeLook> _set;
        private readonly string _coalesceKey;
        private double _value;
        private double _defaultValue;

        internal KeystrokeNumberRow(KeystrokeStyleEditor owner, string coalesceKey, string label,
            string description, double max, Func<KeystrokeLook, double> get,
            Func<KeystrokeLook, double, KeystrokeLook> set)
            : base(owner, label, description)
        {
            Max = max;
            _get = get;
            _set = set;
            _coalesceKey = coalesceKey;
        }

        public double Max { get; }

        /// <summary>The spinner step: one percent.</summary>
        public double Step => 0.01;

        public double Value
        {
            get => _value;
            set
            {
                // NaN collapses to 0 like the model's own clamp; the range is the row's
                value = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, Max);
                if (!SetField(ref _value, value) || Owner.IsSyncing)
                    return;

                Owner.WriteCustom(_coalesceKey, l => _set(l, value));
            }
        }

        /// <summary>What the row's reset dot writes back.</summary>
        public double DefaultValue => _defaultValue;

        internal override void Sync(KeystrokeLook look, KeystrokeLook defaults)
        {
            SetField(ref _value, _get(look), nameof(Value));
            SetField(ref _defaultValue, _get(defaults), nameof(DefaultValue));
        }
    }

    /// <summary>A color value, packed ARGB with alpha, shown and edited as <c>#AARRGGBB</c>.</summary>
    public sealed class KeystrokeColorRow : KeystrokeStyleRow
    {
        private readonly Func<KeystrokeLook, uint> _get;
        private readonly Func<KeystrokeLook, uint, KeystrokeLook> _set;
        private readonly string _coalesceKey;
        private string _hex = "#00000000";
        private uint _argb;
        private string _defaultHex = "#00000000";

        internal KeystrokeColorRow(KeystrokeStyleEditor owner, string coalesceKey, string label,
            string description, Func<KeystrokeLook, uint> get, Func<KeystrokeLook, uint, KeystrokeLook> set)
            : base(owner, label, description)
        {
            _get = get;
            _set = set;
            _coalesceKey = coalesceKey;
        }

        /// <summary>The packed color, for the swatch.</summary>
        public uint Argb => _argb;

        /// <summary>The color as <c>#AARRGGBB</c> (or <c>#RRGGBB</c> as typed). A value that does
        /// not parse stays on the row unwritten.</summary>
        public string Hex
        {
            get => _hex;
            set
            {
                if (!SetField(ref _hex, value) || Owner.IsSyncing)
                    return;

                if (!KeystrokeStyleEditor.TryParseHex(value, out var argb))
                    return;

                if (_argb != argb)
                {
                    _argb = argb;
                    Raise(nameof(Argb));
                }
                Owner.WriteCustom(_coalesceKey, l => _set(l, argb));
            }
        }

        /// <summary>What the row's reset dot writes back.</summary>
        public string DefaultHex => _defaultHex;

        internal override void Sync(KeystrokeLook look, KeystrokeLook defaults)
        {
            var argb = _get(look);
            SetField(ref _hex, KeystrokeStyleEditor.HexOf(argb), nameof(Hex));
            if (_argb != argb)
            {
                _argb = argb;
                Raise(nameof(Argb));
            }
            SetField(ref _defaultHex, KeystrokeStyleEditor.HexOf(_get(defaults)), nameof(DefaultHex));
        }
    }
}
