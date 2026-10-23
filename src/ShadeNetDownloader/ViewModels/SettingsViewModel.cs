using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShadeNet.Core;
using ShadeNet.Core.Tools;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Services.Browser;
using ShadeNetDownloader.Services.Tray;

namespace ShadeNetDownloader.ViewModels;

public enum SettingsSection
{
    General,
    Downloads,
    Categories,
    Queue,
    Network,
    Browser,
    Appearance,
    About,
}

public sealed partial class SettingsViewModel : ObservableObject
{
    readonly SettingsService _settings;
    readonly AppThemeService _theme;
    readonly BrowserIntegrationInstaller _installer;
    readonly IBrowserBridge? _bridge;

    public SettingsViewModel(SettingsService settings, AppThemeService theme,
        BrowserIntegrationInstaller? installer = null, IBrowserBridge? bridge = null,
        DownloadManager? manager = null, FinishWatcher? finish = null, StartupRegistration? startup = null,
        AppController? app = null, QueueScheduler? scheduler = null, IToolUpdates? toolManager = null, ToolUpdateService? toolUpdates = null, AppUpdateService? appUpdates = null)
    {
        _settings = settings;
        _theme = theme;
        _installer = installer ?? new BrowserIntegrationInstaller(settings.DataDir);
        _bridge = bridge;
        _selectedTheme = settings.Current.Theme;

        // Engine settings are edited in place; the manager is told so it can act (e.g. start waiting downloads).
        void ApplyEngine()
        {
            if (manager != null) manager.ApplySettings();
            else settings.Current.Engine.Normalize();
        }

        General = new GeneralSettingsViewModel(settings, startup ?? new StartupRegistration(), () => app?.ApplyTraySetting());
        Downloads = new DownloadSettingsViewModel(settings, ApplyEngine, () => CategoriesChanged?.Invoke());
        Network = new NetworkSettingsViewModel(settings, ApplyEngine);
        Queue = new QueueSettingsViewModel(settings, ApplyEngine, () =>
        {
            scheduler?.Reset();
            scheduler?.Evaluate();
        }, finish);

        var tools = toolManager ?? new ToolManager(settings.Current.Engine, Path.Combine(settings.DataDir, "tools"));
        Tools = new ToolsSettingsViewModel(settings, tools, toolUpdates ?? new ToolUpdateService(tools, settings, new ToastService()), ApplyEngine);

        if (appUpdates != null) Updates = new UpdatesSettingsViewModel(settings, appUpdates);

        RefreshIntegration();
        if (_bridge != null) _bridge.StatusChanged += () => Application.Current?.Dispatcher.BeginInvoke(RefreshIntegration);
    }

    public GeneralSettingsViewModel General { get; }
    public DownloadSettingsViewModel Downloads { get; }
    public NetworkSettingsViewModel Network { get; }
    public QueueSettingsViewModel Queue { get; }

    /// <summary>yt-dlp and ffmpeg: where they are, their versions, a different location, updates.</summary>
    public ToolsSettingsViewModel Tools { get; }

    /// <summary>The newer-version check of ShadeNet itself (Settings > About); null in a view model made without the service.</summary>
    public UpdatesSettingsViewModel? Updates { get; }

    /// <summary>Categories were added, removed or changed: the sidebar rebuilds its list.</summary>
    public event Action? CategoriesChanged;

    // ---- which section of the page is showing ----

    [ObservableProperty] SettingsSection _section = SettingsSection.General;

    partial void OnSectionChanged(SettingsSection value)
    {
        foreach (var name in new[] { nameof(IsGeneral), nameof(IsDownloads), nameof(IsCategories), nameof(IsQueue), nameof(IsNetwork), nameof(IsBrowser), nameof(IsAppearance), nameof(IsAbout) })
            OnPropertyChanged(name);
    }

    public bool IsGeneral { get => Section == SettingsSection.General; set { if (value) Section = SettingsSection.General; } }
    public bool IsDownloads { get => Section == SettingsSection.Downloads; set { if (value) Section = SettingsSection.Downloads; } }
    public bool IsCategories { get => Section == SettingsSection.Categories; set { if (value) Section = SettingsSection.Categories; } }
    public bool IsQueue { get => Section == SettingsSection.Queue; set { if (value) Section = SettingsSection.Queue; } }
    public bool IsNetwork { get => Section == SettingsSection.Network; set { if (value) Section = SettingsSection.Network; } }
    public bool IsBrowser { get => Section == SettingsSection.Browser; set { if (value) Section = SettingsSection.Browser; } }
    public bool IsAppearance { get => Section == SettingsSection.Appearance; set { if (value) Section = SettingsSection.Appearance; } }
    public bool IsAbout { get => Section == SettingsSection.About; set { if (value) Section = SettingsSection.About; } }

    public string DataFolder => _settings.DataDir;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    [ObservableProperty] AppTheme _selectedTheme;

    public bool IsSystem
    {
        get => SelectedTheme == AppTheme.System;
        set { if (value) SelectedTheme = AppTheme.System; }
    }

    public bool IsLight
    {
        get => SelectedTheme == AppTheme.Light;
        set { if (value) SelectedTheme = AppTheme.Light; }
    }

    public bool IsDark
    {
        get => SelectedTheme == AppTheme.Dark;
        set { if (value) SelectedTheme = AppTheme.Dark; }
    }

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        OnPropertyChanged(nameof(IsSystem));
        OnPropertyChanged(nameof(IsLight));
        OnPropertyChanged(nameof(IsDark));

        if (_settings.Current.Theme == value) return;
        _settings.Current.Theme = value;
        _theme.Apply(value);
        _settings.Save();
    }

    /// <summary>Quick toggle used by the title bar button: System → Light → Dark → System.</summary>
    public void CycleTheme() =>
        SelectedTheme = SelectedTheme switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.System,
        };

    [RelayCommand]
    void OpenDataFolder() => Process.Start(new ProcessStartInfo(DataFolder) { UseShellExecute = true });

    // ---- browser integration ----

    BrowserSettings Browser => _settings.Current.Browser;

    public string ExtensionFolder => _installer.ExtensionFolder;

    [ObservableProperty] string _integrationStatus = "";
    [ObservableProperty] string _integrationError = "";
    [ObservableProperty] bool _isIntegrationInstalled;
    [ObservableProperty] bool _isExtensionConnected;

    /// <summary>The steps still open, shown under the status once the host is registered but no browser has connected.</summary>
    public bool NeedsExtensionInstall => IsIntegrationInstalled && !IsExtensionConnected;

    partial void OnIsIntegrationInstalledChanged(bool value) => OnPropertyChanged(nameof(NeedsExtensionInstall));
    partial void OnIsExtensionConnectedChanged(bool value) => OnPropertyChanged(nameof(NeedsExtensionInstall));

    public bool BrowserEnabled
    {
        get => Browser.Enabled;
        set
        {
            if (Browser.Enabled == value) return;
            Browser.Enabled = value;
            OnPropertyChanged();
            BrowserSettingsChanged();
        }
    }

    public bool CaptureShowsDialog
    {
        get => Browser.Mode == CaptureMode.ShowDialog;
        set { if (value) SetMode(CaptureMode.ShowDialog); }
    }

    public bool CaptureStartsImmediately
    {
        get => Browser.Mode == CaptureMode.StartImmediately;
        set { if (value) SetMode(CaptureMode.StartImmediately); }
    }

    void SetMode(CaptureMode mode)
    {
        if (Browser.Mode == mode) return;
        Browser.Mode = mode;
        OnPropertyChanged(nameof(CaptureShowsDialog));
        OnPropertyChanged(nameof(CaptureStartsImmediately));
        BrowserSettingsChanged();
    }

    /// <summary>One host per line (commas and spaces work too); sub-domains are included automatically.</summary>
    public string ExcludedHostsText
    {
        get => string.Join(Environment.NewLine, Browser.ExcludedHosts);
        set
        {
            var hosts = (value ?? "")
                .Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(h => h.ToLowerInvariant().TrimStart('*').TrimStart('.'))
                .Where(h => h.Length > 0)
                .Distinct()
                .ToList();

            if (hosts.SequenceEqual(Browser.ExcludedHosts)) return;
            Browser.ExcludedHosts = hosts;
            OnPropertyChanged();
            BrowserSettingsChanged();
        }
    }

    void BrowserSettingsChanged()
    {
        _settings.Save();
        if (_bridge != null) _ = _bridge.PushSettingsAsync();
    }

    public void RefreshIntegration()
    {
        var status = _installer.GetStatus();
        IsIntegrationInstalled = status.IsInstalled;
        IsExtensionConnected = _bridge?.IsBrowserConnected == true;

        IntegrationStatus =
            !status.HostFilesPresent ? "The browser host is missing from this installation."
            : IsExtensionConnected ? "Connected to your browser."
            : status.IsInstalled ? $"Set up for {string.Join(", ", status.RegisteredBrowsers)}. Waiting for the extension."
            : "Not set up yet.";
    }

    [RelayCommand]
    void InstallIntegration()
    {
        try
        {
            _installer.Install();
            IntegrationError = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            IntegrationError = ex.Message;
        }

        RefreshIntegration();
    }

    [RelayCommand]
    void UninstallIntegration()
    {
        try
        {
            _installer.Uninstall();
            IntegrationError = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IntegrationError = ex.Message;
        }

        RefreshIntegration();
    }

    [RelayCommand]
    void OpenExtensionFolder()
    {
        if (Directory.Exists(ExtensionFolder))
            Process.Start(new ProcessStartInfo(ExtensionFolder) { UseShellExecute = true });
        else
            IntegrationError = "The extension folder is missing from this installation.";
    }

    /// <summary>The installed browsers, each with a button that opens its extensions page.</summary>
    public IReadOnlyList<ExtensionBrowserTarget> InstalledBrowsers { get; } = ExtensionBrowserFinder.Installed();

    public bool HasNoInstalledBrowser => InstalledBrowsers.Count == 0;

    /// <summary>What the last button did, shown under the steps (the folder is copied so that it can be pasted in the browser's folder box).</summary>
    [ObservableProperty] string _extensionNote = "";

    [RelayCommand]
    void OpenBrowserExtensionsPage(ExtensionBrowserTarget? browser)
    {
        if (browser == null) return;

        if (!ExtensionBrowserFinder.TryOpen(browser))
        {
            IntegrationError = $"Could not start {browser.Name}. Open it yourself and type {browser.ExtensionsAddress} in the address bar.";
            return;
        }

        IntegrationError = "";
        ExtensionNote = TryCopyFolder() ? "The folder is copied: press Ctrl+V in the box that opens." : "";
    }

    [RelayCommand]
    void CopyExtensionFolder() =>
        ExtensionNote = TryCopyFolder() ? "Copied. Press Ctrl+V in the box that opens." : "Could not copy. Select the folder above and press Ctrl+C.";

    /// <summary>Another program may hold the clipboard for a moment; the folder is shown in a box too, so a failed copy is no error.</summary>
    bool TryCopyFolder()
    {
        try
        {
            Clipboard.SetText(ExtensionFolder);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }
}
