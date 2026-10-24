using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShadeNetDownloader.Services;

namespace ShadeNetDownloader.ViewModels;

// ================================================================================================================
// Appearance: presets, accent, window material, state colours, the palette
// ================================================================================================================

/// <summary>One look of the gallery, with the four colours its card shows.</summary>
public sealed partial class ThemePresetItem : ObservableObject
{
    public ThemePresetItem(ThemePreset preset)
    {
        Preset = preset;
        var (background, card, accent, text) = preset.Swatch;
        Background = Frozen(background);
        Card = Frozen(card);
        Accent = Frozen(accent);
        Text = Frozen(text);
    }

    public ThemePreset Preset { get; }
    public string Name => Preset.Name;
    public string Description => Preset.Description;
    public Brush Background { get; }
    public Brush Card { get; }
    public Brush Accent { get; }
    public Brush Text { get; }

    [ObservableProperty] bool _isSelected;

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>
/// One colour the user may set (a state colour, or a colour of the palette). Not set, it shows the theme's own colour, so
/// the picker starts from what the app looks like; set, it is written into the look. Reset gives it back to the theme.
/// </summary>
public sealed partial class ColorSettingItem : ObservableObject
{
    readonly Func<string?> _get;
    readonly Action<string?> _set;
    readonly Func<Color?> _themeColour;

    public ColorSettingItem(string key, string group, string name, string hint, bool allowAlpha,
        Func<string?> get, Action<string?> set, Func<Color?> themeColour)
    {
        Key = key;
        Group = group;
        Name = name;
        Hint = hint;
        AllowAlpha = allowAlpha;
        _get = get;
        _set = set;
        _themeColour = themeColour;
    }

    public string Key { get; }
    public string Group { get; }
    public string Name { get; }
    public string Hint { get; }
    public bool AllowAlpha { get; }

    public bool IsSet => _get() != null;

    public string Value
    {
        get => _get() ?? (_themeColour() is { } c ? ThemeColor.Format(c) : "#00000000");
        set
        {
            if (!ThemeColor.TryParse(value, out var colour)) return;
            if (!AllowAlpha) colour = ThemeColor.WithAlpha(colour, 255);
            var text = ThemeColor.Format(colour);
            if (string.Equals(text, _get(), StringComparison.OrdinalIgnoreCase)) return;
            _set(text);
            Refresh();
        }
    }

    [RelayCommand]
    void Reset()
    {
        if (!IsSet) return;
        _set(null);
        Refresh();
    }

    /// <summary>The theme or the look changed: show what the colour is now.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsSet));
    }
}

/// <summary>The palette colours of one group ("Text", "Surfaces" ...), for the Advanced section.</summary>
public sealed record ColorGroup(string Name, IReadOnlyList<ColorSettingItem> Items);

public sealed partial class AppearanceSettingsViewModel : ObservableObject
{
    readonly SettingsService _settings;
    readonly IThemeApplier _theme;
    readonly Func<AppTheme> _baseTheme;
    readonly Action<AppTheme> _setBaseTheme;
    DispatcherTimer? _saveTimer;
    bool _refreshing;

    /// <param name="baseTheme">System / Light / Dark as chosen now (the page's theme switch owns it).</param>
    /// <param name="setBaseTheme">Chooses System / Light / Dark (a preset comes with one).</param>
    public AppearanceSettingsViewModel(SettingsService settings, IThemeApplier theme, Func<AppTheme> baseTheme, Action<AppTheme> setBaseTheme)
    {
        _settings = settings;
        _theme = theme;
        _baseTheme = baseTheme;
        _setBaseTheme = setBaseTheme;

        Presets = ThemePresets.All.Select(p => new ThemePresetItem(p)).ToList();

        StatusItems = StatusColors.Entries.Select(e => new ColorSettingItem(e.Key, "", e.Name, "", false,
            () => Model.Status.TryGetValue(e.Key, out var v) ? v : null,
            v => SetColour(Model.Status, e.Key, v),
            () => _theme.ColorOf(e.Brush))).ToList();

        PaletteGroups = ThemePalette.Entries
            .GroupBy(e => e.Group)
            .Select(g => new ColorGroup(g.Key, g.Select(e => new ColorSettingItem(e.Key, e.Group, e.Name, e.Hint, true,
                () => Palette.TryGetValue(e.Key, out var v) ? v : null,
                v => SetColour(Palette, e.Key, v),
                () => _theme.ColorOf(e.Key))).ToList()))
            .ToList();

        _theme.Applied += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    AppearanceSettings Model => _settings.Current.Appearance;

    /// <summary>The palette being edited: the dark one while the app shows dark, else the light one.</summary>
    Dictionary<string, string> Palette => Model.PaletteFor(_theme.IsDark);

    public IReadOnlyList<ThemePresetItem> Presets { get; }
    public IReadOnlyList<ColorSettingItem> StatusItems { get; }
    public IReadOnlyList<ColorGroup> PaletteGroups { get; }

    /// <summary>"Custom" when the look no longer matches a preset.</summary>
    public string PresetName => ThemePresets.Find(Model.Preset)?.Name ?? "Custom";

    public string PaletteTitle => _theme.IsDark
        ? "Colours of the dark theme (the light theme keeps its own)"
        : "Colours of the light theme (the dark theme keeps its own)";

    // ---- presets ----

    [RelayCommand]
    void ApplyPreset(ThemePresetItem? item)
    {
        if (item == null) return;
        _settings.Current.Appearance = item.Preset.Appearance.Clone();
        if (_baseTheme() != item.Preset.Theme) _setBaseTheme(item.Preset.Theme);   // (that applies the new look too)
        Apply(keepPreset: true);
    }

    [RelayCommand]
    void ResetAll() => ApplyPreset(Presets.FirstOrDefault(p => p.Preset.Id == ThemePresets.DefaultId));

    // ---- accent ----

    public bool UseWindowsAccent
    {
        get => Model.Accent == null;
        set
        {
            if (value == UseWindowsAccent) return;
            // choosing a colour of one's own starts from the one showing now
            Model.Accent = value ? null : ThemeColor.Format(_theme.ColorOf("SystemAccentColor") ?? ThemeColor.Parse("#0078D4"));
            Apply();
        }
    }

    public string AccentColor
    {
        get => Model.Accent ?? ThemeColor.Format(_theme.ColorOf("SystemAccentColor") ?? ThemeColor.Parse("#0078D4"));
        set
        {
            if (!ThemeColor.TryParse(value, out var colour)) return;
            var text = ThemeColor.Format(ThemeColor.WithAlpha(colour, 255));
            if (string.Equals(text, Model.Accent, StringComparison.OrdinalIgnoreCase)) return;
            Model.Accent = text;
            Apply();
        }
    }

    // ---- the window's material ----

    public bool IsMica { get => Model.Backdrop == BackdropKind.Mica; set { if (value) SetBackdrop(BackdropKind.Mica); } }
    public bool IsMicaAlt { get => Model.Backdrop == BackdropKind.MicaAlt; set { if (value) SetBackdrop(BackdropKind.MicaAlt); } }
    public bool IsAcrylic { get => Model.Backdrop == BackdropKind.Acrylic; set { if (value) SetBackdrop(BackdropKind.Acrylic); } }
    public bool IsSolid { get => Model.Backdrop == BackdropKind.Solid; set { if (value) SetBackdrop(BackdropKind.Solid); } }

    void SetBackdrop(BackdropKind kind)
    {
        if (Model.Backdrop == kind) return;
        Model.Backdrop = kind;
        Apply();
    }

    // ---- state colours and the palette ----

    void SetColour(Dictionary<string, string> colours, string key, string? value)
    {
        if (value == null) colours.Remove(key);
        else colours[key] = value;
        Apply();
    }

    [RelayCommand]
    void ResetStatus()
    {
        if (Model.Status.Count == 0) return;
        Model.Status.Clear();
        Apply();
    }

    [RelayCommand]
    void ResetPalette()
    {
        if (Palette.Count == 0) return;
        Palette.Clear();
        Apply();
    }

    /// <summary>Set when the main text is hard to read on the cards (a palette colour set by hand can do that).</summary>
    [ObservableProperty] string? _contrastWarning;

    /// <summary>The contrast of the main text on a card, as it shows (cards are see-through over the window).</summary>
    internal double? TextContrast()
    {
        var dark = _theme.IsDark;
        var window = Solid(_theme.ColorOf("ApplicationBackgroundBrush"), dark ? "#202020" : "#F3F3F3");
        var card = ThemeColor.Over(_theme.ColorOf("CardBackgroundFillColorDefaultBrush") ?? Colors.Transparent, window);
        var text = _theme.ColorOf("TextFillColorPrimaryBrush");
        return text is { } t ? ThemeColor.Contrast(ThemeColor.Over(t, card), card) : null;
    }

    static Color Solid(Color? colour, string fallback) =>
        colour is { } c ? ThemeColor.Over(c, ThemeColor.Parse(fallback)) : ThemeColor.Parse(fallback);

    // ---- applying and saving ----

    /// <summary>The look changed here: show it at once, remember it (the file is written once the user pauses).</summary>
    void Apply(bool keepPreset = false)
    {
        // the look is named after the preset it matches (the one it came from first), else it is "Custom"
        if (!keepPreset)
            Model.Preset = (ThemePresets.Find(Model.Preset) is { } p && Same(p.Appearance, Model) ? p : ThemePresets.All.FirstOrDefault(q => Same(q.Appearance, Model)))?.Id
                           ?? ThemePresets.CustomId;
        _theme.Apply(_baseTheme(), Model);
        Refresh();
        SaveSoon();
    }

    static bool Same(AppearanceSettings a, AppearanceSettings b) =>
        string.Equals(a.Accent, b.Accent, StringComparison.OrdinalIgnoreCase) && a.Backdrop == b.Backdrop
        && SameColours(a.Status, b.Status) && SameColours(a.Dark, b.Dark) && SameColours(a.Light, b.Light);

    static bool SameColours(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Everything the page shows, read again (after a change here, a preset, or light and dark switching).</summary>
    public void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            foreach (var item in Presets) item.IsSelected = string.Equals(item.Preset.Id, Model.Preset, StringComparison.OrdinalIgnoreCase);
            foreach (var item in StatusItems) item.Refresh();
            foreach (var item in PaletteGroups.SelectMany(g => g.Items)) item.Refresh();
            foreach (var name in new[] { nameof(PresetName), nameof(PaletteTitle), nameof(UseWindowsAccent), nameof(AccentColor),
                         nameof(IsMica), nameof(IsMicaAlt), nameof(IsAcrylic), nameof(IsSolid) })
                OnPropertyChanged(name);

            var contrast = TextContrast();
            ContrastWarning = contrast is { } c && c < 4.5
                ? string.Format(CultureInfo.InvariantCulture,
                    "The main text is hard to read on cards (contrast {0:0.0}:1; 4.5:1 is the least body text needs). Pick a lighter or darker text colour, or reset the palette.", c)
                : null;
        }
        finally
        {
            _refreshing = false;
        }
    }

    void SaveSoon()
    {
        // a slider being dragged changes the look many times a second; the settings file is written once it stops
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            _settings.Save();
            return;
        }

        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            _settings.Save();
        }, dispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
