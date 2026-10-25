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

/// <summary>How a background picture covers the window.</summary>
public enum ImageFit
{
    /// <summary>Covers the window, cut to fit (keeps its shape).</summary>
    Fill,
    /// <summary>Shows whole, with bands where the shapes differ.</summary>
    Fit,
    /// <summary>At its own size, in the middle.</summary>
    Center,
    /// <summary>Stretched to the window's shape.</summary>
    Stretch,
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

    // ---- over the window: a tint or a gradient, and a picture ----

    /// <summary>A colour laid over the window's material; null = none.</summary>
    public string? Tint { get; set; }

    /// <summary>A second colour: the tint becomes a gradient from <see cref="Tint"/> to this one.</summary>
    public string? Tint2 { get; set; }

    /// <summary>The gradient's direction in degrees (0 = left to right, 90 = top to bottom).</summary>
    public double TintAngle { get; set; } = 135;

    /// <summary>How strong the tint is, 0 to 1.</summary>
    public double TintStrength { get; set; } = 0.25;

    /// <summary>A picture behind the app: a copy kept in ShadeNet's data folder; null = none.</summary>
    public string? Image { get; set; }

    public ImageFit ImageFit { get; set; } = ImageFit.Fill;

    /// <summary>How much the picture is blurred, 0 to <see cref="MaxBlur"/> pixels.</summary>
    public double ImageBlur { get; set; }

    /// <summary>How much the picture is faded into the theme's background, so text stays readable: 0 to <see cref="MaxDim"/>.</summary>
    public double ImageDim { get; set; } = 0.4;

    public const double MaxBlur = 40;
    public const double MaxDim = 0.9;

    public AppearanceSettings Clone() => new()
    {
        Preset = Preset,
        Accent = Accent,
        Backdrop = Backdrop,
        Status = new Dictionary<string, string>(Status, StringComparer.OrdinalIgnoreCase),
        Dark = new Dictionary<string, string>(Dark, StringComparer.OrdinalIgnoreCase),
        Light = new Dictionary<string, string>(Light, StringComparer.OrdinalIgnoreCase),
        Tint = Tint,
        Tint2 = Tint2,
        TintAngle = TintAngle,
        TintStrength = TintStrength,
        Image = Image,
        ImageFit = ImageFit,
        ImageBlur = ImageBlur,
        ImageDim = ImageDim,
    };

    /// <summary>Whether two looks are the same (the preset a look matches is named after it).</summary>
    public bool SameLook(AppearanceSettings other) =>
        Same(Accent, other.Accent) && Backdrop == other.Backdrop
        && SameColours(Status, other.Status) && SameColours(Dark, other.Dark) && SameColours(Light, other.Light)
        && Same(Tint, other.Tint) && Same(Tint2, other.Tint2)
        && (Tint == null || (Close(TintAngle, other.TintAngle) && Close(TintStrength, other.TintStrength)))
        && Same(Image, other.Image)
        && (Image == null || (ImageFit == other.ImageFit && Close(ImageBlur, other.ImageBlur) && Close(ImageDim, other.ImageDim)));

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static bool Close(double a, double b) => Math.Abs(a - b) < 0.001;

    static bool SameColours(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && Same(v, kv.Value));

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

        if (Tint != null && !ThemeColor.TryParse(Tint, out _))
        {
            Tint = null;
            dropped++;
        }

        if (Tint2 != null && (Tint == null || !ThemeColor.TryParse(Tint2, out _)))
        {
            Tint2 = null;   // (a gradient needs its first colour)
            dropped++;
        }

        TintAngle = Finite(TintAngle, 135) % 360;
        if (TintAngle < 0) TintAngle += 360;
        TintStrength = Math.Clamp(Finite(TintStrength, 0.25), 0, 1);
        if (!Enum.IsDefined(ImageFit))
        {
            ImageFit = ImageFit.Fill;
            dropped++;
        }

        if (string.IsNullOrWhiteSpace(Image)) Image = null;
        ImageBlur = Math.Clamp(Finite(ImageBlur, 0), 0, MaxBlur);
        ImageDim = Math.Clamp(Finite(ImageDim, 0.4), 0, MaxDim);

        Status = Clean(Status, StatusColors.Keys, ref dropped);
        Dark = Clean(Dark, ThemePalette.Keys, ref dropped);
        Light = Clean(Light, ThemePalette.Keys, ref dropped);
        if (string.IsNullOrWhiteSpace(Preset)) Preset = ThemePresets.CustomId;
        return dropped;
    }

    static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

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
    /// <summary>A gradient of two colours lies over the whole window (it shows in the swatch grid, not the gallery).</summary>
    public bool IsGradient => Appearance.Tint != null && Appearance.Tint2 != null;

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

    static AppearanceSettings Tinted(AppearanceSettings look, string from, string to, double strength, double angle = 135)
    {
        look.Tint = from;
        look.Tint2 = to;
        look.TintStrength = strength;
        look.TintAngle = angle;
        return look;
    }

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

        Preset("aurora", "Aurora", "A teal to violet glow over Mica.", AppTheme.Dark,
            Tinted(Look("#22D3EE", BackdropKind.Mica), "#06B6D4", "#A855F7", 0.28)),

        Preset("peach", "Peach", "Light, a warm peach to pink glow.", AppTheme.Light,
            Tinted(Look("#F97316", BackdropKind.Mica), "#FDBA74", "#F9A8D4", 0.35)),

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

        // ---- gradient themes: a gradient of two colours over the whole window, with an accent that goes with it ----

        Gradient("mint", "Mint Leaf", AppTheme.Light, "#10B981", "#A8E6CF", "#DCEDC1"),
        Gradient("sherbet", "Sherbet", AppTheme.Light, "#F97316", "#FFE29F", "#FFA99F"),
        Gradient("lavender", "Lavender Haze", AppTheme.Light, "#8B5CF6", "#C3CFE2", "#E0C3FC"),
        Gradient("cottoncandy", "Cotton Candy", AppTheme.Light, "#EC4899", "#FBC2EB", "#A6C1EE"),
        Gradient("skyline", "Skyline", AppTheme.Light, "#3B82F6", "#A1C4FD", "#C2E9FB", angle: 90),
        Gradient("sage", "Sage Garden", AppTheme.Light, "#16A34A", "#D4FC79", "#96E6A1"),
        Gradient("vanilla", "Vanilla Cream", AppTheme.Light, "#D97706", "#FFF1D6", "#F5D6C6"),
        Gradient("blossom", "Blossom", AppTheme.Light, "#DB2777", "#FDCBF1", "#E6DEE9"),
        Gradient("chroma", "Chroma Glow", AppTheme.Dark, "#C084FC", "#7C3AED", "#0891B2"),
        Gradient("crimson", "Crimson Night", AppTheme.Dark, "#EF4444", "#991B1B", "#0B0B0F", angle: 160),
        Gradient("deepspace", "Deep Space", AppTheme.Dark, "#818CF8", "#312E81", "#0F0A1F", angle: 160),
        Gradient("ember", "Ember", AppTheme.Dark, "#FB923C", "#9A3412", "#3F1D1D"),
        Gradient("dusk", "Dusk", AppTheme.Dark, "#A5B4FC", "#475569", "#94A3B8", angle: 90),
        Gradient("lagoon", "Lagoon", AppTheme.Dark, "#2DD4BF", "#115E59", "#164E63"),
        Gradient("twilight", "Twilight", AppTheme.Dark, "#22D3EE", "#3730A3", "#0E7490"),
        Gradient("neon", "Neon Nights", AppTheme.Dark, "#E879F9", "#0E7490", "#86198F"),
        Gradient("desertsun", "Desert Sun", AppTheme.Dark, "#F59E0B", "#9D174D", "#D97706"),
        Gradient("storm", "Retro Storm", AppTheme.Dark, "#38BDF8", "#0369A1", "#4C1D95"),
        Gradient("khaki", "Khaki", AppTheme.Dark, "#D6B37A", "#6B5B3E", "#2A2418"),
        Gradient("royal", "Royal Blue", AppTheme.Dark, "#60A5FA", "#1E3A8A", "#312E81"),
        Gradient("evergreen", "Evergreen", AppTheme.Dark, "#4ADE80", "#14532D", "#3F6212"),
    };

    /// <summary>A gradient theme: light or dark, an accent, and two colours laid strongly over the window, from corner to corner.</summary>
    static ThemePreset Gradient(string id, string name, AppTheme theme, string accent, string from, string to, double angle = 135) =>
        Preset(id, name, $"{(theme == AppTheme.Light ? "Light" : "Dark")}: a gradient from {from} to {to}.", theme,
            Tinted(Look(accent, BackdropKind.Mica), from, to, theme == AppTheme.Light ? 0.6 : 0.55, angle));

    /// <summary>The looks of the gallery's cards (no gradient over the window).</summary>
    public static IEnumerable<ThemePreset> Looks => All.Where(p => !p.IsGradient);

    /// <summary>The gradient themes (the swatch grid).</summary>
    public static IEnumerable<ThemePreset> Gradients => All.Where(p => p.IsGradient);

    public static ThemePreset? Find(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>How much room the download list gives each download.</summary>
public enum ListDensity
{
    Compact,
    Comfortable,
    Spacious,
}

/// <summary>How round corners are, from square to very round.</summary>
public enum Roundness
{
    Square,
    Subtle,
    Default,
    Round,
}

/// <summary>Whether things move: like Windows ("Animation effects"), always, or never.</summary>
public enum MotionLevel
{
    System,
    On,
    Off,
}

/// <summary>
/// Text and layout: the font, the size of everything, the room the list gives each download, how round corners are and
/// whether things move. Kept apart from the look's colours, so choosing a preset never changes them.
/// </summary>
public sealed class LayoutSettings
{
    /// <summary>The font family's name; null = the Windows font (Segoe UI).</summary>
    public string? Font { get; set; }

    /// <summary>The size of everything, from <see cref="MinZoom"/> to <see cref="MaxZoom"/> (1 = 100 %).</summary>
    public double Zoom { get; set; } = 1;

    public ListDensity Density { get; set; } = ListDensity.Comfortable;
    public Roundness Corners { get; set; } = Roundness.Default;
    public MotionLevel Motion { get; set; } = MotionLevel.System;

    public const double MinZoom = 0.8;
    public const double MaxZoom = 1.5;

    public LayoutSettings Clone() => (LayoutSettings)MemberwiseClone();

    /// <summary>Puts every value in range (a hand-edited or shared file never breaks the app).</summary>
    public void Normalize()
    {
        Font = string.IsNullOrWhiteSpace(Font) ? null : Font.Trim();
        if (Font is { Length: > 100 }) Font = null;
        Zoom = double.IsFinite(Zoom) ? Math.Round(Math.Clamp(Zoom, MinZoom, MaxZoom) * 20) / 20 : 1;   // steps of 5 %
        if (!Enum.IsDefined(Density)) Density = ListDensity.Comfortable;
        if (!Enum.IsDefined(Corners)) Corners = Roundness.Default;
        if (!Enum.IsDefined(Motion)) Motion = MotionLevel.System;
    }

    /// <summary>How much corners are scaled from the theme's own.</summary>
    public double CornerFactor => Corners switch
    {
        Roundness.Square => 0,
        Roundness.Subtle => 0.5,
        Roundness.Round => 1.6,
        _ => 1,
    };
}

/// <summary>Whether animations play, as the user chose (in the System choice, as Windows says).</summary>
public static class Motion
{
    public static MotionLevel Level { get; set; } = MotionLevel.System;

    public static bool Allowed => Level switch
    {
        MotionLevel.On => true,
        MotionLevel.Off => false,
        _ => System.Windows.SystemParameters.ClientAreaAnimation,
    };
}
