using System;
using System.Collections.Generic;
using System.Linq;

namespace Prisc.VideoSDK.Model;

/// <summary>
/// Everything the keystroke overlay draws with, fully resolved: the parametric keycap shape, the
/// typing track and key track shapes, and the six colors. The three preset styles are nothing
/// more than three sets of these numbers (see <see cref="KeystrokeStyles"/>), which is why a
/// user can rebuild any of them by hand in the <c>custom</c> style — this is also the type
/// <see cref="KeyboardContent.Custom"/> stores.
/// <para>
/// <b>Two groups.</b> Typed text never wears a keycap: each run of typed words is plain text in
/// <see cref="TypingTextColor"/> on a rounded "typing track" in <see cref="TypingTrackColor"/>.
/// Every special key and every chord member is a keycap: an <i>outer</i> key in
/// <see cref="KeyOuterColor"/> with a <i>key top</i> in <see cref="KeyTopColor"/> inset into it,
/// legended in <see cref="KeyTextColor"/>, and each contiguous run of keycaps in a line (a chord's
/// "+" included) sits on a "key track" in <see cref="KeyTrackColor"/> — transparent for none.
/// </para>
/// <para>
/// <b>Units.</b> Every shape value is a fraction, so the look scales with the font size. The
/// keycap's own five (<see cref="OuterRadius"/>, <see cref="TopRadius"/>, <see cref="PaddingX"/>,
/// <see cref="PaddingY"/>, <see cref="Drop"/>) are fractions of the keycap's outer height; the
/// track values (<see cref="KeyTrackPadding"/>, <see cref="KeyTrackRadius"/>,
/// <see cref="TypingPadding"/>, <see cref="TypingRadius"/>) are fractions of the row height. The
/// key top sits <see cref="PaddingX"/> in from both sides, <see cref="PaddingY"/> down from the
/// top and <see cref="PaddingY"/> + <see cref="Drop"/> up from the bottom — so the outer key
/// shows as an even rim plus a deeper lip underneath, the 3D of the thing. Out-of-range values
/// are clamped when drawn (<see cref="Clamped"/>), never rejected, so a hand-edited project
/// still opens.
/// </para>
/// <para>Colors are packed ARGB, alpha included.</para>
/// </summary>
public sealed record KeystrokeLook
{
    /// <summary>Upper bound of every corner radius: half the height is a full pill.</summary>
    public const double MaxRadius = 0.5;

    /// <summary>Upper bound of <see cref="PaddingX"/>/<see cref="PaddingY"/>.</summary>
    public const double MaxPadding = 0.25;

    /// <summary>Upper bound of <see cref="Drop"/>.</summary>
    public const double MaxDrop = 0.4;

    /// <summary>The smallest share of the outer key the key top keeps vertically, whatever the
    /// padding and drop ask for — the legend has to sit on something.</summary>
    public const double MinTopHeight = 0.2;

    /// <summary>Upper bound of <see cref="KeyTrackPadding"/>: the caps keep at least 40% of the
    /// row.</summary>
    public const double MaxKeyTrackPadding = 0.3;

    /// <summary>Upper bound of <see cref="TypingPadding"/>.</summary>
    public const double MaxTypingPadding = 1.0;

    // ------------------------------------------------------------------ keycap shape

    /// <summary>Corner radius of the outer key, a fraction of the keycap's outer height
    /// (0..<see cref="MaxRadius"/>).</summary>
    public double OuterRadius { get; set; }

    /// <summary>Corner radius of the key top, a fraction of the keycap's outer height
    /// (0..<see cref="MaxRadius"/>).</summary>
    public double TopRadius { get; set; }

    /// <summary>"Padding W": how far the key top sits in from the outer key's left and right
    /// edges, a fraction of the keycap's outer height (0..<see cref="MaxPadding"/>).</summary>
    public double PaddingX { get; set; }

    /// <summary>"Padding H": how far the key top sits down from the outer key's top edge — and,
    /// with <see cref="Drop"/> added, up from its bottom edge — a fraction of the keycap's outer
    /// height (0..<see cref="MaxPadding"/>).</summary>
    public double PaddingY { get; set; }

    /// <summary>"Vertical offset": the extra outer key showing under the key top, a fraction of
    /// the keycap's outer height (0..<see cref="MaxDrop"/>).</summary>
    public double Drop { get; set; }

    // ------------------------------------------------------------------- track shapes

    /// <summary>Space between a key track's edge and the keycaps on it, all four sides, a
    /// fraction of the row height (0..<see cref="MaxKeyTrackPadding"/>). Applies whether or not
    /// the track is drawn, so the keycap geometry never depends on a color.</summary>
    public double KeyTrackPadding { get; set; }

    /// <summary>Corner radius of the key track, a fraction of the row height
    /// (0..<see cref="MaxRadius"/>).</summary>
    public double KeyTrackRadius { get; set; }

    /// <summary>Horizontal padding inside the typing track, each side, a fraction of the row
    /// height (0..<see cref="MaxTypingPadding"/>).</summary>
    public double TypingPadding { get; set; }

    /// <summary>Corner radius of the typing track, a fraction of the row height
    /// (0..<see cref="MaxRadius"/>).</summary>
    public double TypingRadius { get; set; }

    // ------------------------------------------------------------------------- colors

    /// <summary>The typing track (the pill typed words sit on), packed ARGB.</summary>
    public uint TypingTrackColor { get; set; }

    /// <summary>Typed text, packed ARGB.</summary>
    public uint TypingTextColor { get; set; }

    /// <summary>The key track behind each run of keycaps, packed ARGB; transparent draws
    /// none.</summary>
    public uint KeyTrackColor { get; set; }

    /// <summary>The key top, packed ARGB.</summary>
    public uint KeyTopColor { get; set; }

    /// <summary>The outer key (rim and drop), packed ARGB.</summary>
    public uint KeyOuterColor { get; set; }

    /// <summary>Keycap legends and icons, packed ARGB.</summary>
    public uint KeyTextColor { get; set; }

    /// <summary>An independent copy (records may not declare a member named Clone).</summary>
    public KeystrokeLook Copy() => this with { };

    /// <summary>A copy with every shape value pulled into its range (NaN reads as 0), and the
    /// key top's padding/drop shrunk together, if need be, so it keeps
    /// <see cref="MinTopHeight"/> of the outer key. Colors are taken as they are.</summary>
    public KeystrokeLook Clamped()
    {
        double paddingY = Clamp(PaddingY, MaxPadding);
        double drop = Clamp(Drop, MaxDrop);
        double used = 2 * paddingY + drop;
        if (used > 1 - MinTopHeight)
        {
            double k = (1 - MinTopHeight) / used;
            paddingY *= k;
            drop *= k;
        }

        return this with
        {
            OuterRadius = Clamp(OuterRadius, MaxRadius),
            TopRadius = Clamp(TopRadius, MaxRadius),
            PaddingX = Clamp(PaddingX, MaxPadding),
            PaddingY = paddingY,
            Drop = drop,
            KeyTrackPadding = Clamp(KeyTrackPadding, MaxKeyTrackPadding),
            KeyTrackRadius = Clamp(KeyTrackRadius, MaxRadius),
            TypingPadding = Clamp(TypingPadding, MaxTypingPadding),
            TypingRadius = Clamp(TypingRadius, MaxRadius),
        };
    }

    private static double Clamp(double value, double max) =>
        double.IsNaN(value) ? 0 : Math.Clamp(value, 0, max);
}

/// <summary>
/// The keystroke overlay's styles and themes — preset geometry and palettes as data, in this one
/// place — and the resolver every drawer goes through. A style is a keycap <i>shape</i>
/// (<c>default</c>, <c>laptop</c>, <c>simple</c>) or <c>custom</c>; a theme is a <i>palette</i>
/// for both groups (typing and keys). <c>custom</c> ignores the theme and draws
/// <see cref="KeyboardContent.Custom"/> instead.
/// <para>
/// Ids are wire contract (stored in projects and settings): append, never rename. An unknown
/// style or theme resolves to the default one, so a project from a newer build still opens.
/// </para>
/// </summary>
public static class KeystrokeStyles
{
    public const string Default = "default";
    public const string Laptop = "laptop";
    public const string Simple = "simple";
    public const string Custom = "custom";

    public const string Dark = "dark";
    public const string Light = "light";

    /// <summary>The style a fresh keyboard item or overlay starts with.</summary>
    public const string DefaultStyle = Default;

    /// <summary>The theme a fresh keyboard item or overlay starts with.</summary>
    public const string DefaultTheme = Dark;

    /// <summary>Every style id in menu order — the 2x2 picker reads it row by row.</summary>
    public static readonly IReadOnlyList<string> Styles = new[] { Default, Laptop, Simple, Custom };

    /// <summary>The styles a theme applies to: every style but <c>custom</c>.</summary>
    public static readonly IReadOnlyList<string> PresetStyles = new[] { Default, Laptop, Simple };

    /// <summary>Every theme id in menu order.</summary>
    public static readonly IReadOnlyList<string> Themes = new[]
    {
        Dark, Light, "pink", "red", "blue", "green", "purple", "yellow", "teal",
    };

    // ---------------------------------------------------------------------------- data

    /// <summary>One preset keycap/track shape. The keycap values are fractions of the keycap's
    /// outer height; the track values fractions of the row height (see
    /// <see cref="KeystrokeLook"/>). <paramref name="KeyTrack"/> says whether the style draws a
    /// key track at all — the theme supplies its color.</summary>
    private sealed record Shape(string Name, double OuterRadius, double TopRadius, double PaddingX,
        double PaddingY, double Drop, double KeyTrackPadding, double KeyTrackRadius,
        double TypingPadding, double TypingRadius, bool KeyTrack);

    /// <summary>One theme's palette. <paramref name="KeyTrack"/> is the key track color used by
    /// the styles that draw one.</summary>
    private sealed record Palette(string Name, uint TypingTrack, uint TypingText, uint KeyTop,
        uint KeyOuter, uint KeyText, uint KeyTrack);

    // Typing (the pill under typed words) is the same shape for every preset: today's 0.3 row
    // radius and ~0.55 em of side padding. The key track padding of the trackless styles is the
    // breathing room a keycap always had inside its row.
    private static readonly Shape[] Shapes =
    {
        // Keystro "Default": a key top inset into a darker outer key on three sides, with a
        // deeper lip at the bottom; both rounded.
        new(Default, OuterRadius: 0.17, TopRadius: 0.12, PaddingX: 0.09, PaddingY: 0.055, Drop: 0.105,
            KeyTrackPadding: 0.03, KeyTrackRadius: 0.3, TypingPadding: 0.285, TypingRadius: 0.3, KeyTrack: false),
        // Keystro "Laptop" (measured off its 64 px key: 8 px drop, ~8 px top corners): the key top flush with the outer
        // key on three sides, the outer showing as a deep strip underneath with rounder corners than the top.
        new(Laptop, OuterRadius: 0.17, TopRadius: 0.125, PaddingX: 0, PaddingY: 0, Drop: 0.125,
            KeyTrackPadding: 0.03, KeyTrackRadius: 0.3, TypingPadding: 0.285, TypingRadius: 0.3, KeyTrack: false),
        // Keystro "Magic board": flat keys with a hairline outer rim, on a light tray.
        new(Simple, OuterRadius: 0.13, TopRadius: 0.115, PaddingX: 0.016, PaddingY: 0.016, Drop: 0,
            KeyTrackPadding: 0.1, KeyTrackRadius: 0.2, TypingPadding: 0.285, TypingRadius: 0.3, KeyTrack: true),
    };

    // Dark is the original livery: 55% black pill, white typing, 3D3D3D caps on black. The
    // accents are Keystro's own (accent = key top, outer = the accent darkened like Keystro's
    // DD5D8F -> BD507A); their legends turn dark where white would not read (yellow, teal), and
    // their typing keeps the dark pill. The key track the Simple style draws is Keystro's light
    // grey tray everywhere but Light, which keeps a near-white tray under its grey keys. Light holds
    // its three surfaces apart: white typing track, DCDCDC key top, 8C8C8C outer key.
    private const uint DarkPill = 0x8C000000, White = 0xFFFFFFFF, Ink = 0xFF1E1E1E, Tray = 0xFFC8C8C8;

    private static readonly Palette[] Palettes =
    {
        new(Dark, DarkPill, White, KeyTop: 0xFF3D3D3D, KeyOuter: 0xFF000000, KeyText: White, KeyTrack: Tray),
        new(Light, TypingTrack: 0xF2FFFFFF, TypingText: 0xFF111111, KeyTop: 0xFFDCDCDC, KeyOuter: 0xFF8C8C8C,
            KeyText: Ink, KeyTrack: 0xFFF2F2F2),
        Accent("pink", 0xFFDD5D8F, White),
        Accent("red", 0xFFD34949, White),
        Accent("blue", 0xFF1F91E6, White),
        Accent("green", 0xFF58A05A, White),
        Accent("purple", 0xFF961CF0, White),
        Accent("yellow", 0xFFE4C025, Ink),
        Accent("teal", 0xFF2DD0BD, Ink),
    };

    private static Palette Accent(string name, uint accent, uint legend) =>
        new(name, DarkPill, White, accent, Darken(accent, 0.855), legend, Tray);

    private static uint Darken(uint argb, double factor)
    {
        uint Channel(int shift) => (uint)Math.Round(((argb >> shift) & 0xFF) * factor) << shift;
        return (argb & 0xFF000000) | Channel(16) | Channel(8) | Channel(0);
    }

    // ------------------------------------------------------------------------- names

    /// <summary>The menu label for a style id; the id itself when it is unknown.</summary>
    public static string StyleDisplayName(string style) => Normalize(style, Styles, null) switch
    {
        Default => "Default",
        Laptop => "Laptop",
        Simple => "Simple",
        Custom => "Custom",
        _ => style ?? "",
    };

    /// <summary>The menu label for a theme id; the id itself when it is unknown.</summary>
    public static string ThemeDisplayName(string theme)
    {
        var id = Normalize(theme, Themes, null);
        return id == null ? theme ?? "" : char.ToUpperInvariant(id[0]) + id.Substring(1);
    }

    // ---------------------------------------------------------------------- resolving

    /// <summary>The preset look of <paramref name="style"/> in <paramref name="theme"/> — a fresh
    /// instance the caller may keep and edit. <c>custom</c> and unknown styles read as the default
    /// style, an unknown theme as the default theme.</summary>
    public static KeystrokeLook Preset(string style, string theme)
    {
        var shape = ShapeOf(style);
        var palette = PaletteOf(theme);
        return new KeystrokeLook
        {
            OuterRadius = shape.OuterRadius,
            TopRadius = shape.TopRadius,
            PaddingX = shape.PaddingX,
            PaddingY = shape.PaddingY,
            Drop = shape.Drop,
            KeyTrackPadding = shape.KeyTrackPadding,
            KeyTrackRadius = shape.KeyTrackRadius,
            TypingPadding = shape.TypingPadding,
            TypingRadius = shape.TypingRadius,
            TypingTrackColor = palette.TypingTrack,
            TypingTextColor = palette.TypingText,
            KeyTrackColor = shape.KeyTrack ? palette.KeyTrack : 0,
            KeyTopColor = palette.KeyTop,
            KeyOuterColor = palette.KeyOuter,
            KeyTextColor = palette.KeyText,
        };
    }

    /// <summary>
    /// What the overlay draws for these settings, clamped and ready: the <paramref name="custom"/>
    /// look for the <c>custom</c> style (or, while nothing was ever seeded, the look it would be
    /// seeded with), else the preset of <paramref name="style"/> in <paramref name="theme"/>.
    /// Always a fresh instance.
    /// </summary>
    public static KeystrokeLook Resolve(string style, string theme, KeystrokeLook custom)
    {
        if (IsCustom(style))
            return (custom ?? Preset(DefaultStyle, theme)).Clamped();
        return Preset(style, theme);
    }

    /// <summary><see cref="Resolve(string, string, KeystrokeLook)"/> for a keyboard item or a live
    /// overlay's settings; null resolves to the defaults.</summary>
    public static KeystrokeLook Resolve(KeyboardContent keyboard) =>
        keyboard == null
            ? Preset(DefaultStyle, DefaultTheme)
            : Resolve(keyboard.Style, keyboard.Theme, keyboard.Custom);

    public static bool IsCustom(string style) =>
        string.Equals(style, Custom, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="custom"/> holds the user's own work: false for null and for a look
    /// that is still exactly some preset in some theme (seeded, then never edited — or edited back),
    /// since re-seeding that loses nothing.
    /// </summary>
    public static bool IsCustomized(KeystrokeLook custom) =>
        custom != null && !AllPresets.Value.Contains(custom);

    /// <summary>The look <c>custom</c> starts from when it is picked while
    /// <paramref name="currentStyle"/>/<paramref name="currentTheme"/> are on screen: that preset,
    /// so the user tweaks what they were looking at (the default style when custom is itself
    /// current).</summary>
    public static KeystrokeLook SeedCustom(string currentStyle, string currentTheme) =>
        Preset(IsCustom(currentStyle) ? DefaultStyle : currentStyle, currentTheme);

    /// <summary>
    /// Switches <paramref name="keyboard"/> to <paramref name="style"/> — the one write a style
    /// picker needs. Entering <c>custom</c> from a preset always seeds
    /// <see cref="KeyboardContent.Custom"/> from that preset and theme, so custom starts from
    /// whatever the user was looking at (an earlier custom look is replaced; the editor's undo
    /// brings it back). Re-picking <c>custom</c> while it is current keeps it.
    /// </summary>
    public static void SelectStyle(KeyboardContent keyboard, string style)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        style = Normalize(style, Styles, DefaultStyle);
        if (style == Custom && (!IsCustom(keyboard.Style) || keyboard.Custom == null))
            keyboard.Custom = SeedCustom(keyboard.Style, keyboard.Theme);
        keyboard.Style = style;
    }

    /// <summary>
    /// The preset <paramref name="look"/> is closest to — the one differing from it in the fewest
    /// values, ties going to <paramref name="preferTheme"/> and then to the earlier style — as a
    /// fresh instance. A custom look seeded from a preset and edited a little stays nearest to
    /// that preset, so this is what a custom editor's per-value resets go back to, with no record
    /// of where the look was seeded from to keep. Null reads as the default style in
    /// <paramref name="preferTheme"/>.
    /// </summary>
    public static KeystrokeLook NearestPreset(KeystrokeLook look, string preferTheme)
    {
        if (look == null)
            return Preset(DefaultStyle, preferTheme);

        var themes = Themes.OrderBy(t => string.Equals(t, preferTheme, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        KeystrokeLook best = null;
        int bestDiff = int.MaxValue;
        foreach (var theme in themes)
        {
            foreach (var style in PresetStyles)
            {
                var preset = Preset(style, theme);
                int diff = Differences(look, preset);
                if (diff < bestDiff)
                {
                    best = preset;
                    bestDiff = diff;
                }
            }
        }
        return best;
    }

    private static int Differences(KeystrokeLook a, KeystrokeLook b)
    {
        int n = 0;
        void D(double x, double y) { if (x != y) n++; }
        void C(uint x, uint y) { if (x != y) n++; }
        D(a.OuterRadius, b.OuterRadius);
        D(a.TopRadius, b.TopRadius);
        D(a.PaddingX, b.PaddingX);
        D(a.PaddingY, b.PaddingY);
        D(a.Drop, b.Drop);
        D(a.KeyTrackPadding, b.KeyTrackPadding);
        D(a.KeyTrackRadius, b.KeyTrackRadius);
        D(a.TypingPadding, b.TypingPadding);
        D(a.TypingRadius, b.TypingRadius);
        C(a.TypingTrackColor, b.TypingTrackColor);
        C(a.TypingTextColor, b.TypingTextColor);
        C(a.KeyTrackColor, b.KeyTrackColor);
        C(a.KeyTopColor, b.KeyTopColor);
        C(a.KeyOuterColor, b.KeyOuterColor);
        C(a.KeyTextColor, b.KeyTextColor);
        return n;
    }

    /// <summary>A theme tile's two-tone swatch, Keystro style: the key top color and the typing
    /// track color.</summary>
    public static (uint KeyTop, uint TypingTrack) ThemeSwatch(string theme)
    {
        var palette = PaletteOf(theme);
        return (palette.KeyTop, palette.TypingTrack);
    }

    private static Shape ShapeOf(string style)
    {
        var id = Normalize(style, PresetStyles, DefaultStyle);
        return Shapes.First(s => s.Name == id);
    }

    private static Palette PaletteOf(string theme)
    {
        var id = Normalize(theme, Themes, DefaultTheme);
        return Palettes.First(p => p.Name == id);
    }

    private static string Normalize(string id, IReadOnlyList<string> ids, string fallback)
    {
        if (id != null)
        {
            foreach (var known in ids)
            {
                if (string.Equals(known, id, StringComparison.OrdinalIgnoreCase))
                    return known;
            }
        }
        return fallback;
    }

    private static readonly Lazy<HashSet<KeystrokeLook>> AllPresets = new(() =>
        PresetStyles.SelectMany(s => Themes.Select(t => Preset(s, t))).ToHashSet());
}
