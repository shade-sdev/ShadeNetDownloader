using System.Globalization;
using System.Windows.Media;

namespace ShadeNetDownloader.Services;

/// <summary>Where the window's surface comes from. Mica and Mica Alt are drawn by Windows 11 from the desktop wallpaper.</summary>
public enum BackdropKind
{
    Mica,
    /// <summary>A stronger, more tinted Mica (what Windows calls "tabbed").</summary>
    MicaAlt,
    Acrylic,
    /// <summary>No see-through material: the theme's own background colour.</summary>
    Solid,
}

/// <summary>
/// How the app looks, beyond light and dark (<see cref="AppSettings.Theme"/>): the accent, the window's material, the colours
/// of a download's state, and any colour of the palette the user chose to set. Plain data (colours as "#RRGGBB" or
/// "#AARRGGBB" text) so a theme can be saved, shared and checked before it is used.
/// </summary>
public sealed class AppearanceSettings
{
    /// <summary>The preset this look started from (shown as selected in the gallery); "custom" once anything was changed.</summary>
    public string Preset { get; set; } = ThemePresets.DefaultId;

    /// <summary>null = the Windows accent colour, followed live.</summary>
    public string? Accent { get; set; }

    public BackdropKind Backdrop { get; set; } = BackdropKind.Mica;

    /// <summary>The colours of a download's state: <see cref="StatusColors"/> names the keys. Missing = the theme's own.</summary>
    public Dictionary<string, string> Status { get; set; } = new();

    /// <summary>Colours of the palette set by hand for the dark theme: <see cref="ThemePalette"/> names the keys.</summary>
    public Dictionary<string, string> Dark { get; set; } = new();

    /// <summary>The same, for the light theme (a colour that reads well on dark rarely does on light).</summary>
    public Dictionary<string, string> Light { get; set; } = new();

    public AppearanceSettings Clone() => new()
    {
        Preset = Preset,
        Accent = Accent,
        Backdrop = Backdrop,
        Status = new Dictionary<string, string>(Status, StringComparer.OrdinalIgnoreCase),
        Dark = new Dictionary<string, string>(Dark, StringComparer.OrdinalIgnoreCase),
        Light = new Dictionary<string, string>(Light, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>
    /// Drops what cannot be used (an unknown key, a colour that is not one, an unknown material), so a hand-edited settings
    /// file or a shared theme never breaks the app. Returns how many entries were dropped.
    /// </summary>
    public int Normalize()
    {
        var dropped = 0;
        if (Accent != null && !ThemeColor.TryParse(Accent, out _))
        {
            Accent = null;
            dropped++;
        }

        if (!Enum.IsDefined(Backdrop))
        {
            Backdrop = BackdropKind.Mica;
            dropped++;
        }

        Status = Clean(Status, StatusColors.Keys, ref dropped);
        Dark = Clean(Dark, ThemePalette.Keys, ref dropped);
        Light = Clean(Light, ThemePalette.Keys, ref dropped);
        if (string.IsNullOrWhiteSpace(Preset)) Preset = ThemePresets.CustomId;
        return dropped;
    }

    static Dictionary<string, string> Clean(Dictionary<string, string>? colours, IReadOnlyCollection<string> allowed, ref int dropped)
    {
        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in colours ?? new Dictionary<string, string>())
        {
            var known = allowed.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (known != null && ThemeColor.TryParse(value, out var colour)) clean[known] = ThemeColor.Format(colour);
            else dropped++;
        }

        return clean;
    }

    /// <summary>The palette colours set for a base theme.</summary>
    public Dictionary<string, string> PaletteFor(bool dark) => dark ? Dark : Light;
}

/// <summary>Colours as text: "#RGB", "#RRGGBB" or "#AARRGGBB" in, "#RRGGBB" (or "#AARRGGBB" when see-through) out.</summary>
public static class ThemeColor
{
    public static bool TryParse(string? text, out Color colour)
    {
        colour = default;
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return false;
        if (t[0] == '#') t = t[1..];
        if (t.Length == 3) t = string.Concat(t.Select(c => new string(c, 2)));
        if (t.Length is not (6 or 8) || !uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;

        colour = t.Length == 6
            ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static Color Parse(string text) => TryParse(text, out var c) ? c : throw new FormatException($"\"{text}\" is not a colour.");

    public static string Format(Color c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>A see-through colour as it shows on a solid background.</summary>
    public static Color Over(Color top, Color bottom)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    /// <summary>Relative luminance (WCAG 2), 0 for black to 1 for white.</summary>
    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>The contrast ratio of two solid colours (WCAG 2): 1 (none) to 21 (black on white). Body text wants 4.5 or more.</summary>
    public static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Hue (0-360), saturation and value (0-1) of a colour.</summary>
    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v, byte alpha = 255)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        byte B(double f) => (byte)Math.Round((f + m) * 255);
        return Color.FromArgb(alpha, B(r), B(g), B(b));
    }
}

/// <summary>One colour of the palette the user may set: the resource key the app and its controls draw with.</summary>
public sealed record PaletteEntry(string Key, string Group, string Name, string Hint);

/// <summary>
/// The colours of the palette that can be set by hand. They are the theme brushes every page and control is drawn with,
/// so setting one changes it everywhere at once; anything not set keeps the theme's own colour.
/// </summary>
public static class ThemePalette
{
    public static readonly IReadOnlyList<PaletteEntry> Entries = new PaletteEntry[]
    {
        new("TextFillColorPrimaryBrush", "Text", "Main text", "Titles, names, values"),
        new("TextFillColorSecondaryBrush", "Text", "Secondary text", "Descriptions, sizes, hints"),
        new("TextFillColorTertiaryBrush", "Text", "Faint text", "Times, placeholders"),
        new("ApplicationBackgroundBrush", "Surfaces", "Window background", "Shown with the Solid material"),
        new("CardBackgroundFillColorDefaultBrush", "Surfaces", "Cards", "Download rows, settings cards"),
        new("CardBackgroundFillColorSecondaryBrush", "Surfaces", "Raised cards", "Cards on cards"),
        new("LayerFillColorDefaultBrush", "Surfaces", "Panels", "The details drawer and side panels"),
        new("SolidBackgroundFillColorSecondaryBrush", "Surfaces", "Pop-ups", "Notifications and menus"),
        new("ControlFillColorDefaultBrush", "Controls", "Controls", "Text boxes and buttons"),
        new("ControlFillColorSecondaryBrush", "Controls", "Controls (hover)", "A control under the pointer, segmented switches"),
        new("SubtleFillColorSecondaryBrush", "Controls", "Highlight", "The row or item under the pointer"),
        new("SubtleFillColorTertiaryBrush", "Controls", "Progress track", "The empty part of a progress bar"),
        new("CardStrokeColorDefaultBrush", "Lines", "Card borders", ""),
        new("ControlStrokeColorDefaultBrush", "Lines", "Control borders", ""),
        new("DividerStrokeColorDefaultBrush", "Lines", "Dividers", ""),
    };

    public static readonly IReadOnlyCollection<string> Keys = Entries.Select(e => e.Key).ToList();
}

/// <summary>The colours of a download's state, and the theme brushes they replace (a solid one, and a faint one behind labels).</summary>
public static class StatusColors
{
    public const string Active = "active";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Waiting = "waiting";

    public static readonly IReadOnlyList<(string Key, string Name, string Brush, string Background)> Entries = new[]
    {
        (Active, "Downloading", "SystemFillColorAttentionBrush", "SystemFillColorAttentionBackgroundBrush"),
        (Done, "Finished", "SystemFillColorSuccessBrush", "SystemFillColorSuccessBackgroundBrush"),
        (Failed, "Failed", "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush"),
        (Waiting, "Paused and waiting", "SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush"),
    };

    public static readonly IReadOnlyCollection<string> Keys = Entries.Select(e => e.Key).ToList();

    /// <summary>How strong the faint version behind a label is (of 255).</summary>
    public const byte BackgroundAlpha = 0x33;
}

/// <summary>A ready-made look: a base theme and the appearance that goes with it.</summary>
public sealed record ThemePreset(string Id, string Name, string Description, AppTheme Theme, AppearanceSettings Appearance)
{
    /// <summary>The colours its card in the gallery shows: background, card, accent, text.</summary>
    public (Color Background, Color Card, Color Accent, Color Text) Swatch
    {
        get
        {
            var dark = Theme != AppTheme.Light;
            var palette = Appearance.PaletteFor(dark);
            Color Pick(string key, string fallback) => ThemeColor.Parse(palette.TryGetValue(key, out var v) ? v : fallback);
            var background = Pick("ApplicationBackgroundBrush", dark ? "#202020" : "#F3F3F3");
            return (background,
                ThemeColor.Over(Pick("CardBackgroundFillColorDefaultBrush", dark ? "#0DFFFFFF" : "#B3FFFFFF"), background),
                ThemeColor.Parse(Appearance.Accent ?? "#0078D4"),
                Pick("TextFillColorPrimaryBrush", dark ? "#FFFFFF" : "#1B1B1B"));
        }
    }
}

/// <summary>The gallery of ready-made looks. Every one is free and can be changed afterwards.</summary>
public static class ThemePresets
{
    public const string DefaultId = "windows";
    public const string CustomId = "custom";

    static AppearanceSettings Look(string? accent, BackdropKind backdrop, Dictionary<string, string>? dark = null,
        Dictionary<string, string>? light = null, Dictionary<string, string>? status = null) => new()
    {
        Accent = accent,
        Backdrop = backdrop,
        Dark = new(dark ?? new(), StringComparer.OrdinalIgnoreCase),
        Light = new(light ?? new(), StringComparer.OrdinalIgnoreCase),
        Status = new(status ?? new(), StringComparer.OrdinalIgnoreCase),
    };

    static ThemePreset Preset(string id, string name, string description, AppTheme theme, AppearanceSettings look)
    {
        look.Preset = id;
        return new ThemePreset(id, name, description, theme, look);
    }

    public static readonly IReadOnlyList<ThemePreset> All = new[]
    {
        Preset(DefaultId, "Windows", "Follows your Windows theme and accent colour, on Mica.", AppTheme.System,
            Look(null, BackdropKind.Mica)),

        Preset("midnight", "Midnight", "Deep violet on Mica Alt.", AppTheme.Dark,
            Look("#7C5CFF", BackdropKind.MicaAlt)),

        Preset("ocean", "Ocean", "Sky blue with blue-grey cards.", AppTheme.Dark,
            Look("#0EA5E9", BackdropKind.Mica, dark: new()
            {
                ["CardBackgroundFillColorDefaultBrush"] = "#140EA5E9",
                ["CardStrokeColorDefaultBrush"] = "#260EA5E9",
            })),

        Preset("forest", "Forest", "Calm green.", AppTheme.Dark,
            Look("#22C55E", BackdropKind.Mica, dark: new()
            {
                ["CardBackgroundFillColorDefaultBrush"] = "#1222C55E",
                ["CardStrokeColorDefaultBrush"] = "#2222C55E",
            })),

        Preset("sunset", "Sunset", "Warm orange, pink for finished downloads.", AppTheme.Dark,
            Look("#F97316", BackdropKind.MicaAlt, status: new() { [StatusColors.Done] = "#EC4899" })),

        Preset("rose", "Rose", "Light, with a rose accent.", AppTheme.Light,
            Look("#E11D48", BackdropKind.Mica)),

        Preset("paper", "Paper", "Light and solid, no see-through material.", AppTheme.Light,
            Look("#2563EB", BackdropKind.Solid, light: new()
            {
                ["ApplicationBackgroundBrush"] = "#FAFAF7",
                ["CardBackgroundFillColorDefaultBrush"] = "#FFFFFF",
            })),

        Preset("graphite", "Graphite", "Dark and solid, quiet grey accent.", AppTheme.Dark,
            Look("#94A3B8", BackdropKind.Solid, dark: new()
            {
                ["ApplicationBackgroundBrush"] = "#161616",
                ["CardBackgroundFillColorDefaultBrush"] = "#1F1F1F",
                ["LayerFillColorDefaultBrush"] = "#1A1A1A",
            })),

        Preset("nord", "Nord", "The Nord palette: arctic blues on slate.", AppTheme.Dark,
            Look("#88C0D0", BackdropKind.Solid, dark: new()
            {
                ["ApplicationBackgroundBrush"] = "#2E3440",
                ["CardBackgroundFillColorDefaultBrush"] = "#3B4252",
                ["CardBackgroundFillColorSecondaryBrush"] = "#434C5E",
                ["LayerFillColorDefaultBrush"] = "#323846",
                ["SolidBackgroundFillColorSecondaryBrush"] = "#3B4252",
                ["TextFillColorPrimaryBrush"] = "#ECEFF4",
                ["TextFillColorSecondaryBrush"] = "#D8DEE9",
                ["TextFillColorTertiaryBrush"] = "#A3ABB9",
                ["CardStrokeColorDefaultBrush"] = "#4C566A",
            }, status: new()
            {
                [StatusColors.Done] = "#A3BE8C",
                [StatusColors.Failed] = "#BF616A",
                [StatusColors.Waiting] = "#EBCB8B",
                [StatusColors.Active] = "#88C0D0",
            })),

        Preset("dracula", "Dracula", "The Dracula palette: purple and pink on charcoal.", AppTheme.Dark,
            Look("#BD93F9", BackdropKind.Solid, dark: new()
            {
                ["ApplicationBackgroundBrush"] = "#282A36",
                ["CardBackgroundFillColorDefaultBrush"] = "#343746",
                ["CardBackgroundFillColorSecondaryBrush"] = "#3E4152",
                ["LayerFillColorDefaultBrush"] = "#2D2F3D",
                ["SolidBackgroundFillColorSecondaryBrush"] = "#343746",
                ["TextFillColorPrimaryBrush"] = "#F8F8F2",
                ["TextFillColorSecondaryBrush"] = "#D6D6E0",
                ["TextFillColorTertiaryBrush"] = "#9EA1B7",
                ["CardStrokeColorDefaultBrush"] = "#44475A",
            }, status: new()
            {
                [StatusColors.Done] = "#50FA7B",
                [StatusColors.Failed] = "#FF5555",
                [StatusColors.Waiting] = "#F1FA8C",
                [StatusColors.Active] = "#8BE9FD",
            })),
    };

    public static ThemePreset? Find(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
