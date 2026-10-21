using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ShadeNetDownloader.Services;

/// <summary>Switches between System / Light / Dark at runtime. In System mode the window follows Windows live.</summary>
public sealed class AppThemeService
{
    Window? _window;
    AppTheme _current = AppTheme.System;

    /// <summary>
    /// Called once by the main window. Colours are applied right away (no flash of the wrong theme); the live
    /// "follow Windows" watcher can only be attached once the window has a handle, so that happens on Loaded.
    /// </summary>
    public void Attach(Window window)
    {
        _window = window;
        window.Loaded += (_, _) => Apply(_current);
    }

    public void Apply(AppTheme theme)
    {
        _current = theme;
        var loaded = _window is { IsLoaded: true };

        switch (theme)
        {
            case AppTheme.System:
                ApplicationThemeManager.ApplySystemTheme();
                if (loaded) SystemThemeWatcher.Watch(_window!, WindowBackdropType.Mica, true);
                break;

            case AppTheme.Light:
                if (loaded) SystemThemeWatcher.UnWatch(_window!);
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true);
                break;

            case AppTheme.Dark:
                if (loaded) SystemThemeWatcher.UnWatch(_window!);
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);
                break;
        }
    }
}
