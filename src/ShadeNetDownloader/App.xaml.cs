using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShadeNet.Core;
using ShadeNet.Core.Storage;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Services.Browser;
using ShadeNetDownloader.Services.Tray;
using ShadeNetDownloader.ViewModels;
using Wpf.Ui;

namespace ShadeNetDownloader;

public partial class App : Application
{
    IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // before any window: the taskbar button then shows the window's icon (see AppIdentity)
        AppIdentity.Set();

        // Command-line switches for scripts and the installer: register or remove the browser integration, then leave.
        if (e.Args.Contains("--install-browser-integration") || e.Args.Contains("--uninstall-browser-integration"))
        {
            var exitCode = 0;
            try
            {
                var installer = new BrowserIntegrationInstaller(
                    new SettingsService(Environment.GetEnvironmentVariable("SHADENET_DATA_DIR")).DataDir);
                if (e.Args.Contains("--install-browser-integration")) installer.Install();
                else installer.Uninstall();
            }
            catch (Exception)
            {
                exitCode = 1;
            }

            Shutdown(exitCode);
            return;
        }

        // "ShadeNetDownloader.exe --quit": ends a running instance for real (what the installer uses before it replaces files)
        if (e.Args.Contains("--quit"))
        {
            Task.Run(() => BrowserBridgeService.QuitRunningInstanceAsync()).GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        // One instance per user (and per data folder): a second launch, such as the browser host starting the app
        // while it is already running, just brings the first one forward.
        // (on a pool thread: waiting here for work that wants to resume on this UI thread would never finish)
        if (Task.Run(() => BrowserBridgeService.ActivateRunningInstanceAsync()).GetAwaiter().GetResult())
        {
            Shutdown();
            return;
        }

        // SHADENET_DATA_DIR lets tests and screenshots run against a throw-away data folder.
        var settings = new SettingsService(Environment.GetEnvironmentVariable("SHADENET_DATA_DIR"));
        settings.Load();
        AppThemeService.ApplyMotion(settings.Current.Layout.Motion);   // (before any window: animations take their timing when made)

        DispatcherUnhandledException += (_, args) => OnUnhandled(settings, args);

        var builder = Host.CreateApplicationBuilder(e.Args);
        var services = builder.Services;

        services.AddSingleton(settings);
        services.AddSingleton<AppThemeService>();
        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<IDialogService, DialogService>();

        // The engine: one manager for the whole app, persisted in SQLite next to the settings file.
        services.AddSingleton(sp =>
        {
            var manager = new DownloadManager(
                settings.Current.Engine,
                new JobStore(settings.DatabasePath),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("ShadeNet"));
            manager.Load();
            return manager;
        });

        // Browser integration: the extension reaches the app through the native host and a named pipe.
        services.AddSingleton<ICaptureUi, AppCaptureUi>();
        services.AddSingleton(sp => new CaptureProcessor(
            sp.GetRequiredService<DownloadManager>(), () => settings.Current.Browser, sp.GetRequiredService<ICaptureUi>()));
        services.AddSingleton(sp => new BrowserBridgeService(
            sp.GetRequiredService<CaptureProcessor>(), settings, sp.GetRequiredService<ICaptureUi>(),
            typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"));
        services.AddSingleton<IBrowserBridge>(sp => sp.GetRequiredService<BrowserBridgeService>());
        services.AddSingleton(new BrowserIntegrationInstaller(settings.DataDir));

        // Tray, notifications, queue schedule, "when finished", start-up entry.
        services.AddSingleton<AppController>();
        services.AddSingleton<IAppShell>(sp => sp.GetRequiredService<AppController>());
        services.AddSingleton(sp => new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")));
        services.AddSingleton(sp => new AppIconService(sp.GetRequiredService<TrayIcon>()));
        services.AddSingleton<IAppIconApplier>(sp => sp.GetRequiredService<AppIconService>());
        services.AddSingleton<ITrayNotifier>(sp => sp.GetRequiredService<TrayIcon>());
        services.AddSingleton<StartupRegistration>();
        services.AddSingleton<IPowerService>(sp => new WindowsPowerService(() => sp.GetRequiredService<AppController>().Exit()));
        services.AddSingleton<FinishWatcher>();
        services.AddSingleton<ShadeNet.Core.Tools.IToolUpdates>(sp =>
            new ShadeNet.Core.Tools.ToolManager(sp.GetRequiredService<SettingsService>().Current.Engine, Path.Combine(sp.GetRequiredService<SettingsService>().DataDir, "tools")));
        services.AddSingleton<ToolUpdateService>();

        // ShadeNet's own updates: the release workflow publishes update.json and the update package next to the installer
        services.AddSingleton<ShadeNet.Core.Updates.IAppUpdates>(sp => CreateAppUpdater(settings));
        services.AddSingleton<IAppUpdateApplier>(_ => new UpdateApplier());
        services.AddSingleton(sp => new AppUpdateService(
            sp.GetRequiredService<ShadeNet.Core.Updates.IAppUpdates>(), settings, sp.GetRequiredService<ToastService>(),
            sp.GetRequiredService<IAppUpdateApplier>(), () => sp.GetRequiredService<AppController>().Exit(),
            activeDownloads: () => sp.GetRequiredService<DownloadManager>().Jobs.Count(j => j.IsActive)));
        services.AddSingleton(sp => new QueueScheduler(sp.GetRequiredService<DownloadManager>(), () => settings.Current.Schedule));
        services.AddSingleton(sp => new NotificationService(
            sp.GetRequiredService<DownloadManager>(), () => settings.Current.General,
            sp.GetRequiredService<ITrayNotifier>(), sp.GetRequiredService<IAppShell>()));
        services.AddSingleton<TrayMenu>();

        services.AddSingleton<DownloadsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _host = builder.Build();

        // A hidden window must never end the program: only "Exit" (or closing without a tray) does.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Start listening before the window shows: from here on the extension (through its host) can reach us.
        if (!_host.Services.GetRequiredService<BrowserBridgeService>().Start())
        {
            // Lost a race with another instance that started a moment earlier: hand over to it.
            BrowserBridgeService.ActivateRunningInstanceAsync().GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;

        var sp = _host.Services;
        var controller = sp.GetRequiredService<AppController>();
        var tray = sp.GetRequiredService<TrayIcon>();
        var trayMenu = sp.GetRequiredService<TrayMenu>();
        controller.Attach(window, sp.GetRequiredService<MainViewModel>(), tray);
        tray.ContextMenuRequested += (x, y) => trayMenu.ShowAt(x, y);

        // Notifications are collected for a moment and sent together; the queue schedule is checked twice a minute.
        var notifications = sp.GetRequiredService<NotificationService>();
        var scheduler = sp.GetRequiredService<QueueScheduler>();
        _timers.Add(new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => notifications.Flush(), Dispatcher));
        _timers.Add(new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => scheduler.Evaluate(), Dispatcher));
        scheduler.Evaluate();
        sp.GetRequiredService<FinishWatcher>();   // starts listening

        // yt-dlp / ffmpeg updates: look about once a day (the first look a little after start-up), and ask before installing
        var toolUpdates = sp.GetRequiredService<ToolUpdateService>();
        var firstLook = new DispatcherTimer(TimeSpan.FromSeconds(45), DispatcherPriority.Background, async (t, _) =>
        {
            ((DispatcherTimer)t!).Stop();
            await toolUpdates.CheckDueAsync();
        }, Dispatcher);
        _timers.Add(firstLook);
        _timers.Add(new DispatcherTimer(TimeSpan.FromHours(3), DispatcherPriority.Background, async (_, _) => await toolUpdates.CheckDueAsync(), Dispatcher));

        // a newer ShadeNet: the first look a minute after start-up, then about once a day (at most); it asks before it installs
        var appUpdates = sp.GetRequiredService<AppUpdateService>();
        var firstAppLook = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, async (t, _) =>
        {
            ((DispatcherTimer)t!).Stop();
            await appUpdates.CheckDueAsync();
        }, Dispatcher);
        _timers.Add(firstAppLook);
        _timers.Add(new DispatcherTimer(TimeSpan.FromHours(3), DispatcherPriority.Background, async (_, _) => await appUpdates.CheckDueAsync(), Dispatcher));

        // Started by Windows or by the browser extension: go straight to the tray (or the taskbar without one).
        if (e.Args.Contains("--minimized"))
        {
            if (controller.TrayAvailable) return;   // never shown: the tray icon is the way in
            window.WindowState = WindowState.Minimized;
        }

        window.Show();
    }

    readonly List<DispatcherTimer> _timers = new();

    /// <summary>
    /// Where newer versions are looked for: the repository stamped into this build (the release workflow passes its own).
    /// SHADENET_UPDATE_URL points the look at an update.json on this computer, for tests; any other address is ignored.
    /// </summary>
    static ShadeNet.Core.Updates.IAppUpdates CreateAppUpdater(SettingsService settings)
    {
        var repository = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value ?? "shade-sdev/ShadeNetDownloader";
        var current = typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);
        var folder = Path.Combine(settings.DataDir, "updates");

        Uri? local = Uri.TryCreate(Environment.GetEnvironmentVariable("SHADENET_UPDATE_URL"), UriKind.Absolute, out var url) ? url : null;
        try
        {
            return new ShadeNet.Core.Updates.AppUpdater(repository, current, folder, settings.Current.Engine, local);
        }
        catch (ArgumentException) when (local != null)
        {
            return new ShadeNet.Core.Updates.AppUpdater(repository, current, folder, settings.Current.Engine);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            try
            {
                var settings = _host.Services.GetRequiredService<SettingsService>();
                settings.Save();

                // Give running downloads a moment to checkpoint and close their files.
                var manager = _host.Services.GetRequiredService<DownloadManager>();
                manager.StopAllAsync().Wait(TimeSpan.FromSeconds(5));

                _host.Services.GetRequiredService<BrowserBridgeService>().DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // never block shutdown
            }

            _host.Dispose();
        }

        base.OnExit(e);
    }

    static void OnUnhandled(SettingsService settings, DispatcherUnhandledExceptionEventArgs args)
    {
        try
        {
            File.AppendAllText(Path.Combine(settings.DataDir, "crash.log"),
                $"[{DateTime.Now:u}] {args.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // logging must not throw
        }

        MessageBox.Show(
            $"Something unexpected happened:\n\n{args.Exception.Message}\n\nDetails were saved to crash.log in {settings.DataDir}.",
            "ShadeNet Downloader", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }
}
