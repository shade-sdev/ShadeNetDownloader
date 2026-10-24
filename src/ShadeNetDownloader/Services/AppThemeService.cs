using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ShadeNetDownloader.Services;

/// <summary>What the Appearance page needs of the theme: apply a look, and read what shows.</summary>
public interface IThemeApplier
{
    void Apply(AppTheme theme, AppearanceSettings appearance);

    /// <summary>Whether the dark palette is the one showing.</summary>
    bool IsDark { get; }

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

    /// <summary>The colour a theme brush (or colour) has right now, set by hand or the theme's own; null when it is no plain colour.</summary>
    public Color? ColorOf(string key) => Application.Current?.TryFindResource(key) switch
    {
        SolidColorBrush brush => brush.Color,
        Color colour => colour,
        _ => null,
    };
}
