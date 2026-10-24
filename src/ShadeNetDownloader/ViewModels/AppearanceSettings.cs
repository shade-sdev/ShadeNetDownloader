using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
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
        Background = BackgroundOf(preset.Appearance, background);
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

    /// <summary>The window of the look: its background, with its tint or gradient laid over it.</summary>
    static Brush BackgroundOf(AppearanceSettings look, Color background)
    {
        if (look.Tint == null || !ThemeColor.TryParse(look.Tint, out var from)) return Frozen(background);
        var to = look.Tint2 != null && ThemeColor.TryParse(look.Tint2, out var second) ? second : from;
        var alpha = (byte)Math.Round(look.TintStrength * 255);
        var brush = new LinearGradientBrush(ThemeColor.Over(ThemeColor.WithAlpha(from, alpha), background),
            ThemeColor.Over(ThemeColor.WithAlpha(to, alpha), background), look.TintAngle);
        brush.Freeze();
        return brush;
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
    readonly Func<string?> _pickImage;
    readonly Func<string, string?> _pickExportFile;
    readonly Func<string?> _pickThemeFile;

    /// <param name="baseTheme">System / Light / Dark as chosen now (the page's theme switch owns it).</param>
    /// <param name="setBaseTheme">Chooses System / Light / Dark (a preset comes with one).</param>
    /// <param name="pickImage">Asks for a picture file (the open-file dialog); tests pass their own.</param>
    /// <param name="pickExportFile">Asks where to save a theme (given a name for it); tests pass their own.</param>
    /// <param name="pickThemeFile">Asks for a theme file to import; tests pass their own.</param>
    public AppearanceSettingsViewModel(SettingsService settings, IThemeApplier theme, Func<AppTheme> baseTheme, Action<AppTheme> setBaseTheme,
        Func<string?>? pickImage = null, Func<string, string?>? pickExportFile = null, Func<string?>? pickThemeFile = null)
    {
        _pickImage = pickImage ?? PickImageFile;
        _pickExportFile = pickExportFile ?? PickExportFile;
        _pickThemeFile = pickThemeFile ?? PickThemeFile;
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

    // ---- over the window: a tint or a gradient ----

    Color ShowingAccent => ThemeColor.TryParse(Model.Accent, out var own) ? own : _theme.ColorOf("SystemAccentColor") ?? ThemeColor.Parse("#0078D4");

    public bool HasTint
    {
        get => Model.Tint != null;
        set
        {
            if (value == HasTint) return;
            Model.Tint = value ? ThemeColor.Format(ShowingAccent) : null;   // a tint starts from the accent
            if (!value) Model.Tint2 = null;
            Apply();
        }
    }

    public string TintColor
    {
        get => Model.Tint ?? ThemeColor.Format(ShowingAccent);
        set
        {
            if (!ThemeColor.TryParse(value, out var colour)) return;
            var text = ThemeColor.Format(ThemeColor.WithAlpha(colour, 255));
            if (string.Equals(text, Model.Tint, StringComparison.OrdinalIgnoreCase)) return;
            Model.Tint = text;
            Apply();
        }
    }

    public bool UseGradient
    {
        get => Model.Tint2 != null;
        set
        {
            if (value == UseGradient) return;
            if (value)
            {
                Model.Tint ??= ThemeColor.Format(ShowingAccent);
                // the second colour starts a sixth of the colour wheel away from the first
                var (h, sat, v) = ThemeColor.ToHsv(ThemeColor.Parse(Model.Tint));
                Model.Tint2 = ThemeColor.Format(ThemeColor.FromHsv(h + 60, Math.Max(sat, 0.5), Math.Max(v, 0.6)));
            }
            else
            {
                Model.Tint2 = null;
            }

            Apply();
        }
    }

    public string Tint2Color
    {
        get => Model.Tint2 ?? TintColor;
        set
        {
            if (!ThemeColor.TryParse(value, out var colour)) return;
            var text = ThemeColor.Format(ThemeColor.WithAlpha(colour, 255));
            if (string.Equals(text, Model.Tint2, StringComparison.OrdinalIgnoreCase)) return;
            Model.Tint ??= ThemeColor.Format(ShowingAccent);
            Model.Tint2 = text;
            Apply();
        }
    }

    /// <summary>The gradient's direction, 0 to 359 degrees.</summary>
    public double TintAngle
    {
        get => Model.TintAngle;
        set => SetNumber(Model.TintAngle, Math.Clamp(value, 0, 359), v => Model.TintAngle = v);
    }

    /// <summary>The tint's strength in per cent.</summary>
    public double TintStrength
    {
        get => Math.Round(Model.TintStrength * 100);
        set => SetNumber(Model.TintStrength, Math.Clamp(value, 0, 100) / 100, v => Model.TintStrength = v);
    }

    void SetNumber(double current, double value, Action<double> set)
    {
        if (Math.Abs(current - value) < 0.0005) return;
        set(value);
        Apply();
    }

    // ---- a picture behind the app ----

    /// <summary>The largest picture accepted (a background does not need more).</summary>
    public const long MaxImageBytes = 30L * 1024 * 1024;

    public bool HasImage => Model.Image != null;
    public string ImageName => Model.Image == null ? "" : Path.GetFileName(Model.Image);

    /// <summary>Why the picture cannot be used, or null.</summary>
    [ObservableProperty] string? _imageError;

    [RelayCommand]
    void ChooseImage()
    {
        var picked = _pickImage();
        if (picked == null) return;
        var stored = KeepImage(picked);
        if (stored == null) return;
        Model.Image = stored;
        Apply();
    }

    [RelayCommand]
    void RemoveImage()
    {
        ImageError = null;
        if (Model.Image == null) return;
        Model.Image = null;
        Apply();
    }

    /// <summary>The folder ShadeNet keeps its copies of background pictures in.</summary>
    string ImageFolder => Path.Combine(_settings.DataDir, "backgrounds");

    /// <summary>
    /// A copy of the picture in ShadeNet's own folder (the look must not break when the original is moved or deleted), named
    /// after its content so the same picture is kept once. Null, with <see cref="ImageError"/> set, when it cannot be used.
    /// </summary>
    internal string? KeepImage(string source)
    {
        ImageError = null;
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists)
            {
                ImageError = "That file is gone.";
                return null;
            }

            if (info.Length > MaxImageBytes)
            {
                ImageError = $"That picture is too large ({info.Length / (1024 * 1024)} MB); pick one under {MaxImageBytes / (1024 * 1024)} MB.";
                return null;
            }

            return KeepImage(File.ReadAllBytes(source), info.Extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ImageError = "The picture could not be copied: " + ex.Message;
            return null;
        }
    }

    /// <summary>The same, for a picture's bytes (a theme file carries its picture).</summary>
    internal string? KeepImage(byte[] bytes, string extension)
    {
        ImageError = null;
        if (bytes.LongLength > MaxImageBytes)
        {
            ImageError = "That picture is too large.";
            return null;
        }

        if (!BackgroundLayer.IsPicture(bytes))
        {
            ImageError = "That file is not a picture ShadeNet can show (PNG, JPEG, BMP, GIF, TIFF or WebP with its codec).";
            return null;
        }

        try
        {
            var name = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant() + extension.ToLowerInvariant();
            Directory.CreateDirectory(ImageFolder);
            var target = Path.Combine(ImageFolder, name);
            if (!File.Exists(target)) File.WriteAllBytes(target, bytes);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ImageError = "The picture could not be copied: " + ex.Message;
            return null;
        }
    }

    /// <summary>Copies of pictures no longer in use are deleted (keep: pictures still wanted, e.g. by a look that can be undone).</summary>
    internal void PruneImages(params string?[] keep)
    {
        try
        {
            if (!Directory.Exists(ImageFolder)) return;
            foreach (var file in Directory.EnumerateFiles(ImageFolder))
                if (!keep.Any(k => string.Equals(k, file, StringComparison.OrdinalIgnoreCase))) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // one that is in use stays until the next time
        }
    }

    static string? PickImageFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a background picture",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.jxr|All files|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool IsFitFill { get => Model.ImageFit == ImageFit.Fill; set { if (value) SetFit(ImageFit.Fill); } }
    public bool IsFitFit { get => Model.ImageFit == ImageFit.Fit; set { if (value) SetFit(ImageFit.Fit); } }
    public bool IsFitCenter { get => Model.ImageFit == ImageFit.Center; set { if (value) SetFit(ImageFit.Center); } }
    public bool IsFitStretch { get => Model.ImageFit == ImageFit.Stretch; set { if (value) SetFit(ImageFit.Stretch); } }

    void SetFit(ImageFit fit)
    {
        if (Model.ImageFit == fit) return;
        Model.ImageFit = fit;
        Apply();
    }

    public double ImageBlur
    {
        get => Model.ImageBlur;
        set => SetNumber(Model.ImageBlur, Math.Clamp(value, 0, AppearanceSettings.MaxBlur), v => Model.ImageBlur = v);
    }

    /// <summary>How much the picture fades into the background, in per cent.</summary>
    public double ImageDim
    {
        get => Math.Round(Model.ImageDim * 100);
        set => SetNumber(Model.ImageDim, Math.Clamp(value, 0, AppearanceSettings.MaxDim * 100) / 100, v => Model.ImageDim = v);
    }

    // ---- sharing: a look as a theme file ----

    /// <summary>What happened to the last export or import (saved where, why a file was refused), or null.</summary>
    [ObservableProperty] string? _themeFileMessage;

    /// <summary>An imported theme is showing and can still be undone.</summary>
    [ObservableProperty] bool _isPreviewing;

    [ObservableProperty] string _previewName = "";

    (AppearanceSettings Look, AppTheme Theme)? _beforeImport;   // what Undo goes back to
    bool _importing;

    [RelayCommand]
    void ExportTheme()
    {
        var name = ThemePresets.Find(Model.Preset)?.Name ?? "My ShadeNet theme";
        var path = _pickExportFile(name);
        if (path == null) return;
        try
        {
            File.WriteAllText(path, ThemeFile.Write(Path.GetFileNameWithoutExtension(path), _baseTheme(), Model, MaxImageBytes));
            ThemeFileMessage = $"Saved \"{Path.GetFileName(path)}\". Anyone with ShadeNet can import it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ThemeFileMessage = "The theme could not be saved: " + ex.Message;
        }
    }

    /// <summary>A theme file is checked, then shown at once; the page offers to keep it or to go back.</summary>
    [RelayCommand]
    void ImportTheme()
    {
        var path = _pickThemeFile();
        if (path == null) return;

        ThemeFileContent content;
        try
        {
            content = ThemeFile.Read(path);
        }
        catch (ThemeFileException ex)
        {
            ThemeFileMessage = ex.Message;
            return;
        }

        var look = content.Appearance;
        if (content.Image != null) look.Image = KeepImage(content.Image, content.ImageExtension ?? ".png");   // (a bad one says why)

        _beforeImport ??= (Model.Clone(), _baseTheme());   // previewing one after another: Undo goes back to before the first
        _importing = true;
        try
        {
            _settings.Current.Appearance = look;
            if (_baseTheme() != content.Theme) _setBaseTheme(content.Theme);
            Apply();   // (named after a preset it matches, else Custom)
        }
        finally
        {
            _importing = false;
        }

        PreviewName = content.Name;
        IsPreviewing = true;
        ThemeFileMessage = content.Dropped > 0
            ? $"{content.Dropped} of its settings could not be used and were left out."
            : null;
    }

    [RelayCommand]
    void KeepImported()
    {
        EndPreview();
        ThemeFileMessage = null;
        SaveSoon();
    }

    [RelayCommand]
    void UndoImport()
    {
        if (_beforeImport is not { } before) return;
        _importing = true;
        try
        {
            _settings.Current.Appearance = before.Look;
            if (_baseTheme() != before.Theme) _setBaseTheme(before.Theme);
            Apply(keepPreset: true);
        }
        finally
        {
            _importing = false;
        }

        EndPreview();
        ThemeFileMessage = null;
        ImageError = null;
    }

    void EndPreview()
    {
        _beforeImport = null;
        IsPreviewing = false;
    }

    static string? PickExportFile(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the look as a theme",
            FileName = safe + ThemeFile.Extension,
            DefaultExt = ThemeFile.Extension,
            Filter = $"ShadeNet themes|*{ThemeFile.Extension}|All files|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    static string? PickThemeFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a theme",
            Filter = $"ShadeNet themes|*{ThemeFile.Extension}|All files|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    // ---- text and layout (kept apart from the look: a preset never changes them) ----

    LayoutSettings Layout => _settings.Current.Layout;

    public const string WindowsFont = "Windows font (Segoe UI)";
    IReadOnlyList<string>? _fonts;

    /// <summary>The Windows font first, then every font installed (read when the list is first opened).</summary>
    public IReadOnlyList<string> FontChoices => _fonts ??= new[] { WindowsFont }.Concat(AppThemeService.InstalledFonts()).ToList();

    public string SelectedFont
    {
        get => Layout.Font ?? WindowsFont;
        set
        {
            var font = string.IsNullOrWhiteSpace(value) || value == WindowsFont ? null : value.Trim();
            if (string.Equals(font, Layout.Font, StringComparison.OrdinalIgnoreCase)) return;
            Layout.Font = font;
            ApplyLayout();
        }
    }

    /// <summary>The size of everything in per cent, in steps of 5.</summary>
    public double ZoomPercent
    {
        get => Math.Round(Layout.Zoom * 100);
        set
        {
            var zoom = Math.Round(Math.Clamp(value, LayoutSettings.MinZoom * 100, LayoutSettings.MaxZoom * 100) / 5) * 5 / 100;
            if (Math.Abs(zoom - Layout.Zoom) < 0.001) return;
            Layout.Zoom = zoom;
            ApplyLayout();
        }
    }

    [RelayCommand]
    void ResetZoom() => ZoomPercent = 100;

    public bool IsCompact { get => Layout.Density == ListDensity.Compact; set { if (value) SetDensity(ListDensity.Compact); } }
    public bool IsComfortable { get => Layout.Density == ListDensity.Comfortable; set { if (value) SetDensity(ListDensity.Comfortable); } }
    public bool IsSpacious { get => Layout.Density == ListDensity.Spacious; set { if (value) SetDensity(ListDensity.Spacious); } }

    void SetDensity(ListDensity density)
    {
        if (Layout.Density == density) return;
        Layout.Density = density;
        ApplyLayout();
    }

    public bool IsSquare { get => Layout.Corners == Roundness.Square; set { if (value) SetCorners(Roundness.Square); } }
    public bool IsSubtle { get => Layout.Corners == Roundness.Subtle; set { if (value) SetCorners(Roundness.Subtle); } }
    public bool IsDefaultCorners { get => Layout.Corners == Roundness.Default; set { if (value) SetCorners(Roundness.Default); } }
    public bool IsRound { get => Layout.Corners == Roundness.Round; set { if (value) SetCorners(Roundness.Round); } }

    void SetCorners(Roundness corners)
    {
        if (Layout.Corners == corners) return;
        Layout.Corners = corners;
        ApplyLayout();
    }

    public bool IsMotionSystem { get => Layout.Motion == MotionLevel.System; set { if (value) SetMotion(MotionLevel.System); } }
    public bool IsMotionOn { get => Layout.Motion == MotionLevel.On; set { if (value) SetMotion(MotionLevel.On); } }
    public bool IsMotionOff { get => Layout.Motion == MotionLevel.Off; set { if (value) SetMotion(MotionLevel.Off); } }

    void SetMotion(MotionLevel motion)
    {
        if (Layout.Motion == motion) return;
        Layout.Motion = motion;
        ApplyLayout();
    }

    [RelayCommand]
    void ResetLayout()
    {
        _settings.Current.Layout = new LayoutSettings();
        ApplyLayout();
    }

    void ApplyLayout()
    {
        _theme.ApplyLayout(Layout);
        Refresh();
        SaveSoon();
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
        if (IsPreviewing && !_importing) EndPreview();   // changing anything else keeps the imported theme

        // the look is named after the preset it matches (the one it came from first), else it is "Custom"
        if (!keepPreset)
            Model.Preset = (ThemePresets.Find(Model.Preset) is { } p && Same(p.Appearance, Model) ? p : ThemePresets.All.FirstOrDefault(q => Same(q.Appearance, Model)))?.Id
                           ?? ThemePresets.CustomId;
        _theme.Apply(_baseTheme(), Model);
        Refresh();
        SaveSoon();
    }

    static bool Same(AppearanceSettings a, AppearanceSettings b) => a.SameLook(b);

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
            foreach (var name in new[]
                     {
                         nameof(PresetName), nameof(PaletteTitle), nameof(UseWindowsAccent), nameof(AccentColor),
                         nameof(IsMica), nameof(IsMicaAlt), nameof(IsAcrylic), nameof(IsSolid),
                         nameof(HasTint), nameof(TintColor), nameof(UseGradient), nameof(Tint2Color), nameof(TintAngle), nameof(TintStrength),
                         nameof(HasImage), nameof(ImageName), nameof(IsFitFill), nameof(IsFitFit), nameof(IsFitCenter), nameof(IsFitStretch),
                         nameof(ImageBlur), nameof(ImageDim),
                         nameof(SelectedFont), nameof(ZoomPercent), nameof(IsCompact), nameof(IsComfortable), nameof(IsSpacious),
                         nameof(IsSquare), nameof(IsSubtle), nameof(IsDefaultCorners), nameof(IsRound),
                         nameof(IsMotionSystem), nameof(IsMotionOn), nameof(IsMotionOff),
                     })
                OnPropertyChanged(name);

            if (Model.Image != null && !File.Exists(Model.Image)) ImageError ??= "The background picture is gone. Choose another one.";

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

    void Save()
    {
        _settings.Save();
        PruneImages(Model.Image, _beforeImport?.Look.Image);   // (the picture of the look Undo would bring back stays)
    }

    void SaveSoon()
    {
        // a slider being dragged changes the look many times a second; the settings file is written once it stops
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            Save();
            return;
        }

        _saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _saveTimer!.Stop();
            Save();
        }, dispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }
}
