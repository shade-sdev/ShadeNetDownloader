using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ShadeNetDownloader.Services;

/// <summary>What the Appearance page needs of the theme: apply a look, and read what shows.</summary>
public interface IThemeApplier
{
    void Apply(AppTheme theme, AppearanceSettings appearance);

    /// <summary>Font, zoom, list density, corners and motion.</summary>
    void ApplyLayout(LayoutSettings layout);

    /// <summary>Whether the dark palette is the one showing.</summary>
    bool IsDark { get; }

    /// <summary>The next change of the look is a big one (a preset, an imported theme): it blends in instead of jumping.</summary>
    void Crossfade();

    /// <summary>The colour a theme brush has right now, or null when it is no plain colour.</summary>
    Color? ColorOf(string key);

    event Action? Applied;
}

/// <summary>
/// Applies the look: System / Light / Dark (in System mode the window follows Windows live), then on top of it the accent,
/// the window's material, the state colours and the palette colours set by hand (<see cref="AppearanceSettings"/>).
/// <para>
/// The theme's own colours live in WPF-UI's theme dictionary, which is replaced whenever light and dark switch. What the user
/// set is written to the application's own resources, which every lookup consults before that dictionary, so it outlives the
/// switch; it is written again after each switch because the light and dark palettes differ. Everything is looked up live
/// (DynamicResource), so a change shows at once, everywhere.
/// </para>
/// </summary>
public sealed class AppThemeService : IThemeApplier
{
    Window? _window;
    AppTheme _current = AppTheme.System;
    AppearanceSettings _appearance = new();
    readonly HashSet<string> _written = new();   // the keys this service put into the application's resources
    bool _applying;
    (AppTheme, WindowBackdropType, bool, bool)? _basis;   // what the theme was last switched with

    public AppThemeService()
    {
        // light and dark switched (by Windows in System mode, or by Apply): the theme's dictionary was replaced, and with it
        // the accent WPF-UI derived. What the user chose is put back on top.
        ApplicationThemeManager.Changed += (_, _) =>
        {
            if (!_applying) Application.Current?.Dispatcher.BeginInvoke(ApplyOnTop);
        };
    }

    /// <summary>The look in use (a copy is kept: changing the one passed in later does nothing until it is applied again).</summary>
    public AppearanceSettings Appearance => _appearance;

    /// <summary>Whether the dark palette is the one showing (in System mode, what Windows uses).</summary>
    public bool IsDark => ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Light;

    /// <summary>The picture and tint drawn behind the window (the main window binds a layer to it).</summary>
    public BackgroundLayer Layer { get; } = new();

    /// <summary>Raised just before the look changes a lot (light and dark, a preset): the window fades the old look out.</summary>
    public event Action? Switching;

    public void Crossfade() => Switching?.Invoke();

    /// <summary>Raised after anything of the look changed (the Appearance page refreshes its preview).</summary>
    public event Action? Applied;

    /// <summary>
    /// Called once by the main window. Colours are applied right away (no flash of the wrong theme); the live
    /// "follow Windows" watcher can only be attached once the window has a handle, so that happens on Loaded.
    /// </summary>
    public void Attach(Window window)
    {
        _window = window;
        window.Loaded += (_, _) => Apply(_current, _appearance);
    }

    public void Apply(AppTheme theme) => Apply(theme, _appearance);

    public void Apply(AppTheme theme, AppearanceSettings appearance)
    {
        _current = theme;
        _appearance = appearance.Clone();
        _appearance.Normalize();
        var loaded = _window is { IsLoaded: true };
        var backdrop = BackdropFor(_appearance.Backdrop);
        // with an accent of their own, WPF-UI must not put the Windows accent back on every switch
        var followAccent = _appearance.Accent == null;

        // a colour being picked changes the look many times a second: the theme itself is switched only when light / dark,
        // the material or the accent's source changed (or the window just appeared); the rest is written over it
        var basis = (theme, backdrop, followAccent, loaded);
        if (_basis.HasValue && _basis.Value.Equals(basis))
        {
            ApplyOnTop();
            return;
        }

        if (_basis.HasValue && loaded) Switching?.Invoke();   // (not the first time: there is nothing to blend from)
        _basis = basis;
        _applying = true;
        try
        {
            switch (theme)
            {
                case AppTheme.System:
                    ApplicationThemeManager.ApplySystemTheme(followAccent);
                    if (loaded)
                    {
                        SystemThemeWatcher.UnWatch(_window!);
                        SystemThemeWatcher.Watch(_window!, backdrop, followAccent);
                    }

                    break;

                case AppTheme.Light:
                    if (loaded) SystemThemeWatcher.UnWatch(_window!);
                    ApplicationThemeManager.Apply(ApplicationTheme.Light, backdrop, followAccent);
                    break;

                case AppTheme.Dark:
                    if (loaded) SystemThemeWatcher.UnWatch(_window!);
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark, backdrop, followAccent);
                    break;
            }
        }
        finally
        {
            _applying = false;
        }

        ApplyBackdrop(backdrop);
        ApplyOnTop();
    }

    static WindowBackdropType BackdropFor(BackdropKind kind) => kind switch
    {
        BackdropKind.MicaAlt => WindowBackdropType.Tabbed,
        BackdropKind.Acrylic => WindowBackdropType.Acrylic,
        BackdropKind.Solid => WindowBackdropType.None,
        _ => WindowBackdropType.Mica,
    };

    void ApplyBackdrop(WindowBackdropType backdrop)
    {
        if (_window is not FluentWindow fluent) return;
        if (fluent.WindowBackdropType != backdrop) fluent.WindowBackdropType = backdrop;
        // switching the theme puts WPF-UI's own choice of material on the main window: the chosen one goes back on top
        if (fluent.IsLoaded) WindowBackgroundManager.UpdateBackground(fluent, ApplicationThemeManager.GetAppTheme(), backdrop);

        // a material shows only through a see-through window; without one the theme's background colour is the surface
        if (backdrop == WindowBackdropType.None) fluent.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ApplicationBackgroundBrush");
        else fluent.Background = Brushes.Transparent;
    }

    /// <summary>The accent, the state colours and the palette, written over the current theme.</summary>
    void ApplyOnTop()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;
        var dark = IsDark;
        var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;

        _applying = true;
        try
        {
            var accent = _appearance.Accent != null && ThemeColor.TryParse(_appearance.Accent, out var chosen)
                ? chosen
                : ApplicationAccentColorManager.GetColorizationColor();
            ApplicationAccentColorManager.Apply(accent, theme);
        }
        finally
        {
            _applying = false;
        }

        // what was written for the last look and is not part of this one goes, so the theme's own colour shows again
        var wanted = new Dictionary<string, Brush>();
        foreach (var (key, value) in _appearance.PaletteFor(dark))
            if (ThemeColor.TryParse(value, out var colour)) wanted[key] = Frozen(colour);

        foreach (var (key, _, brush, background) in StatusColors.Entries)
        {
            if (!_appearance.Status.TryGetValue(key, out var value) || !ThemeColor.TryParse(value, out var colour)) continue;
            wanted[brush] = Frozen(colour);
            wanted[background] = Frozen(ThemeColor.WithAlpha(colour, StatusColors.BackgroundAlpha));
        }

        foreach (var key in _written.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            resources.Remove(key);
            _written.Remove(key);
        }

        foreach (var (key, brush) in wanted)
        {
            resources[key] = brush;
            _written.Add(key);
        }

        Layer.Update(_appearance);

        Applied?.Invoke();
    }

    static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    // ---- text and layout ----

    /// <summary>The key WPF-UI's controls take their font from.</summary>
    const string FontKey = "ContentControlThemeFontFamily";

    public void ApplyLayout(LayoutSettings layout)
    {
        var l = layout.Clone();
        l.Normalize();
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        // the font: one that is installed, else the Windows one
        var font = l.Font != null && IsInstalled(l.Font) ? new FontFamily(l.Font) : null;
        if (font != null) resources[FontKey] = font;
        else resources.Remove(FontKey);   // (only the one written here: WPF-UI's own lives in its dictionary)
        if (_window != null)
        {
            if (font != null) _window.FontFamily = font;
            else _window.ClearValue(Window.FontFamilyProperty);

            // the size of everything: the window's content is scaled (it lays itself out again at the new size)
            if (_window.Content is FrameworkElement root)
                root.LayoutTransform = Math.Abs(l.Zoom - 1) < 0.001 ? Transform.Identity : new ScaleTransform(l.Zoom, l.Zoom);
        }

        // the room each download gets in the list, and the height of the sidebar's items
        var (padding, gap, navHeight) = l.Density switch
        {
            ListDensity.Compact => (new Thickness(12, 8, 10, 8), new Thickness(0, 0, 0, 4), 32.0),
            ListDensity.Spacious => (new Thickness(20, 18, 16, 18), new Thickness(0, 0, 0, 12), 42.0),
            _ => (new Thickness(16, 14, 12, 14), new Thickness(0, 0, 0, 8), 38.0),
        };
        resources["DownloadCardPadding"] = padding;
        resources["DownloadCardGap"] = gap;
        resources["NavItemHeight"] = navHeight;

        // corners: ours and WPF-UI's, scaled together
        var f = l.CornerFactor;
        resources["RadiusSm"] = new CornerRadius(4 * f);
        resources["RadiusMd"] = new CornerRadius(8 * f);
        resources["RadiusLg"] = new CornerRadius(12 * f);
        resources["RadiusBadge"] = new CornerRadius(5 * f);
        resources["CardRadius"] = new CornerRadius(10 * f);
        resources["SheetRadius"] = new CornerRadius(12 * f);
        resources["ControlCornerRadius"] = new CornerRadius(4 * f);
        resources["OverlayCornerRadius"] = new CornerRadius(8 * f);

        ApplyMotion(l.Motion);
        Applied?.Invoke();
    }

    /// <summary>
    /// Whether things move. Animations already on screen keep the timing they were made with, so this is also called before
    /// the window is created (App start-up); what is made afterwards follows at once.
    /// </summary>
    public static void ApplyMotion(MotionLevel level)
    {
        Motion.Level = level;
        var resources = Application.Current?.Resources;
        if (resources == null) return;
        if (Motion.Allowed)
        {
            resources.Remove("MotionFast");   // the tokens' own durations show again
            resources.Remove("MotionBase");
            resources.Remove("MotionSlow");
        }
        else
        {
            resources["MotionFast"] = new Duration(TimeSpan.Zero);
            resources["MotionBase"] = new Duration(TimeSpan.Zero);
            resources["MotionSlow"] = new Duration(TimeSpan.Zero);
        }
    }

    static HashSet<string>? _installedFonts;

    /// <summary>The names of the font families installed, sorted (read once).</summary>
    public static IReadOnlyCollection<string> InstalledFonts()
    {
        _installedFonts ??= new HashSet<string>(
            Fonts.SystemFontFamilies.Select(f => f.Source).Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        return _installedFonts.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static bool IsInstalled(string name)
    {
        InstalledFonts();
        return _installedFonts!.Contains(name);
    }

    /// <summary>The colour a theme brush (or colour) has right now, set by hand or the theme's own; null when it is no plain colour.</summary>
    public Color? ColorOf(string key) => Application.Current?.TryFindResource(key) switch
    {
        SolidColorBrush brush => brush.Color,
        Color colour => colour,
        _ => null,
    };
}
