using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Services.Tray;

/// <summary>
/// Everything about the main window's life: showing it, hiding it to the tray when it is closed, bringing it forward when
/// something needs the user, and really exiting. Also the <see cref="IAppShell"/> the notification logic talks to.
/// </summary>
public sealed class AppController : IAppShell, IDisposable
{
    readonly SettingsService _settings;
    TrayIcon? _tray;
    Window? _window;
    MainViewModel? _main;

    public AppController(SettingsService settings) => _settings = settings;

    /// <summary>True once the user (or the system) really wants the program to end: the close button then closes.</summary>
    public bool Exiting { get; private set; }

    public TrayIcon? Tray => _tray;

    /// <summary>The tray icon is shown and the close button may use it.</summary>
    public bool TrayAvailable => _tray is { IsVisible: true };

    public bool ShouldHideOnClose => !Exiting && _settings.Current.General.CloseToTray && TrayAvailable;

    public void Attach(Window window, MainViewModel main, TrayIcon? tray)
    {
        _window = window;
        _main = main;
        _tray = tray;

        if (tray != null)
        {
            tray.Clicked += () => Dispatch(BringToFront);
            main.PropertyChanged += OnMainChanged;
            ApplyTraySetting();
        }

        // Windows is ending the session (log off / shutdown): let the window close for real
        Application.Current.SessionEnding += (_, _) => Exiting = true;
    }

    void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TrayTooltip) && _tray != null && _main != null) _tray.Tooltip = _main.TrayTooltip;
    }

    /// <summary>Shows or removes the tray icon according to the setting.</summary>
    public void ApplyTraySetting()
    {
        if (_tray == null) return;
        if (_settings.Current.General.ShowTrayIcon)
        {
            _tray.Show();
            if (_main != null) _tray.Tooltip = _main.TrayTooltip;
        }
        else
        {
            _tray.Hide();
        }
    }

    // ---- window ----

    public bool IsForeground => _window is { IsVisible: true, IsActive: true, WindowState: not WindowState.Minimized };

    public void BringToFront() => Dispatch(() =>
    {
        if (_window == null) return;
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
        // Windows rarely lets a background app take the foreground; flipping Topmost is the polite way to try.
        _window.Topmost = true;
        _window.Topmost = false;
    });

    /// <summary>The close button was pressed. <c>true</c> when the window was hidden instead of closing.</summary>
    public bool HideToTrayIfWanted()
    {
        if (!ShouldHideOnClose || _window == null) return false;

        _window.Hide();
        if (!_settings.Current.General.TrayHintShown)
        {
            _settings.Current.General.TrayHintShown = true;
            _settings.Save();
            _tray?.ShowBalloon("ShadeNet is still running",
                "Your downloads continue in the background. Click the tray icon to open the window, or right-click it to quit.",
                silent: true, tag: null);
        }

        return true;
    }

    /// <summary>Really quit: downloads are paused and saved, then the program ends.</summary>
    public void Exit()
    {
        Exiting = true;
        Dispatch(() => Application.Current.Shutdown());
    }

    public void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Explorer could not be started; there is nothing more useful to do
        }
    }

    static void Dispatch(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }

    public void Dispose() => _tray?.Dispose();
}
