using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShadeNet.Core.Engine;
using ShadeNetDownloader.Services;

namespace ShadeNetDownloader.ViewModels;

// ================================================================================================================
// General: tray, start-up, notifications
// ================================================================================================================

public sealed partial class GeneralSettingsViewModel : ObservableObject
{
    readonly SettingsService _settings;
    readonly StartupRegistration _startup;
    readonly Action _trayChanged;

    public GeneralSettingsViewModel(SettingsService settings, StartupRegistration startup, Action trayChanged)
    {
        _settings = settings;
        _startup = startup;
        _trayChanged = trayChanged;
        _startWithWindows = SafeIsEnabled();
    }

    GeneralSettings Model => _settings.Current.General;

    bool SafeIsEnabled()
    {
        try
        {
            return _startup.IsEnabled;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public bool ShowTrayIcon
    {
        get => Model.ShowTrayIcon;
        set
        {
            if (Model.ShowTrayIcon == value) return;
            Model.ShowTrayIcon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanCloseToTray));
            Save();
            _trayChanged();
        }
    }

    /// <summary>Closing to the tray needs a tray icon to come back from.</summary>
    public bool CanCloseToTray => ShowTrayIcon;

    public bool CloseToTray
    {
        get => Model.CloseToTray;
        set => Set(Model.CloseToTray, value, v => Model.CloseToTray = v);
    }

    [ObservableProperty] string _startupError = "";
    bool _startWithWindows;

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            try
            {
                _startup.SetEnabled(value);
                _startWithWindows = value;
                Model.StartWithWindows = value;
                StartupError = "";
                Save();
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                StartupError = "Windows would not let ShadeNet change its start-up entry: " + ex.Message;
            }

            OnPropertyChanged();
        }
    }

    public bool NotifyOnComplete
    {
        get => Model.NotifyOnComplete;
        set => Set(Model.NotifyOnComplete, value, v => Model.NotifyOnComplete = v);
    }

    public bool NotifyOnFailure
    {
        get => Model.NotifyOnFailure;
        set => Set(Model.NotifyOnFailure, value, v => Model.NotifyOnFailure = v);
    }

    public bool NotifyOnlyInBackground
    {
        get => Model.NotifyOnlyInBackground;
        set => Set(Model.NotifyOnlyInBackground, value, v => Model.NotifyOnlyInBackground = v);
    }

    public bool NotificationSound
    {
        get => Model.NotificationSound;
        set => Set(Model.NotificationSound, value, v => Model.NotificationSound = v);
    }

    void Set(bool current, bool value, Action<bool> assign, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (current == value) return;
        assign(value);
        OnPropertyChanged(name);
        Save();
    }

    void Save() => _settings.Save();
}

// ================================================================================================================
// Downloads: folders, connections, categories
// ================================================================================================================

/// <summary>One row of the category editor: a built-in or custom category, its file types and its folder.</summary>
public sealed partial class CategoryRowViewModel : ObservableObject
{
    readonly DownloadSettingsViewModel _owner;
    readonly EngineSettings _engine;

    public CategoryRowViewModel(DownloadSettingsViewModel owner, EngineSettings engine, string name, bool isCustom)
    {
        _owner = owner;
        _engine = engine;
        Name = name;
        IsCustom = isCustom;
        Reload();
    }

    public string Name { get; private set; }
    public bool IsCustom { get; }
    public bool IsGeneral => Name == ShadeNet.Core.Util.CategoryResolver.General;

    /// <summary>"General" takes whatever no other category claims, so it has no file types to edit.</summary>
    public bool IsEditable => !IsGeneral;

    [ObservableProperty] string _extensionsText = "";
    [ObservableProperty] string _folderText = "";
    [ObservableProperty] string _folderHint = "";
    [ObservableProperty] string _error = "";

    bool _loading;

    public void Reload()
    {
        _loading = true;
        ExtensionsText = IsGeneral ? "everything else" : string.Join(" ", _engine.ExtensionsOf(Name));
        FolderText = _engine.CategoryFolders.TryGetValue(Name, out var folder) ? folder : "";
        FolderHint = $"Default: {_engine.ResolveCategoryFolderDefault(Name)}";
        Error = "";
        _loading = false;
    }

    // The text boxes update these when they lose focus (or on Enter), so a change is a finished edit.
    partial void OnExtensionsTextChanged(string value)
    {
        if (!_loading) CommitExtensions();
    }

    partial void OnFolderTextChanged(string value)
    {
        if (!_loading) CommitFolder();
    }

    /// <summary>The user finished editing the extension list.</summary>
    void CommitExtensions()
    {
        if (IsGeneral) return;
        var list = EngineSettings.NormalizeExtensions((ExtensionsText ?? "").Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));

        if (IsCustom)
        {
            var custom = _engine.CustomCategories.First(c => string.Equals(c.Name, Name, StringComparison.OrdinalIgnoreCase));
            custom.Extensions = list;
        }
        else
        {
            _engine.CategoryExtensions[Name] = list;
        }

        _owner.EnginePropertiesChanged(categories: true);
        Reload();
    }

    /// <summary>The user finished editing the folder.</summary>
    [RelayCommand]
    void CommitFolder()
    {
        var text = (FolderText ?? "").Trim();
        if (text.Length == 0)
        {
            _engine.CategoryFolders.Remove(Name);
            _owner.EnginePropertiesChanged();
            Reload();
            return;
        }

        if (!Path.IsPathFullyQualified(text) || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            Error = "Use a full folder path, such as D:\\Downloads\\Video.";
            return;
        }

        _engine.CategoryFolders[Name] = text;
        _owner.EnginePropertiesChanged();
        Error = "";
    }

    [RelayCommand]
    void Browse()
    {
        var dialog = new OpenFolderDialog { Title = $"Folder for {Name}", Multiselect = false };
        if (_engine.CategoryFolders.TryGetValue(Name, out var current) && Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog() != true) return;

        FolderText = dialog.FolderName;   // the change commits itself
    }

    /// <summary>Back to the defaults: the built-in file types and the folder under the download folder.</summary>
    [RelayCommand]
    void Reset()
    {
        _engine.CategoryFolders.Remove(Name);
        if (!IsCustom) _engine.CategoryExtensions.Remove(Name);
        _owner.EnginePropertiesChanged(categories: true);
        Reload();
    }

    [RelayCommand]
    void Remove()
    {
        if (!IsCustom) return;
        _owner.RemoveCustomCategory(Name);
    }
}

public sealed partial class DownloadSettingsViewModel : ObservableObject
{
    readonly SettingsService _settings;
    readonly Action _applyEngine;
    readonly Action _categoriesChanged;

    readonly TorrentAssociation _associations;
    readonly ShadeNet.Core.DownloadManager? _manager;
    readonly Func<IReadOnlyList<ShadeNet.Core.Torrents.NetworkAdapterInfo>> _adapters;

    /// <param name="associations">Opening magnet links and .torrent files with ShadeNet; tests pass one on a throw-away registry key.</param>
    /// <param name="manager">For how torrents reach the network and the banned peers (none in some tests).</param>
    /// <param name="adapters">The network adapters to choose from (tests pass their own).</param>
    public DownloadSettingsViewModel(SettingsService settings, Action applyEngine, Action categoriesChanged, TorrentAssociation? associations = null,
        ShadeNet.Core.DownloadManager? manager = null, Func<IReadOnlyList<ShadeNet.Core.Torrents.NetworkAdapterInfo>>? adapters = null)
    {
        _settings = settings;
        _applyEngine = applyEngine;
        _categoriesChanged = categoriesChanged;
        _associations = associations ?? new TorrentAssociation();
        _manager = manager;
        _adapters = adapters ?? ShadeNet.Core.Torrents.NetworkAdapters.List;
        _downloadRootText = Engine.DownloadRoot;
        LoadCategories();
        RefreshAssociations();
        RefreshAdapters();
    }

    // ---- torrents: encryption, the adapter, the proxy, banned peers ----

    public sealed record EncryptionChoice(TorrentEncryption Value, string Label);

    /// <summary>A network adapter torrents can be bound to; <see cref="Id"/> null for "any".</summary>
    public sealed record AdapterChoice(string? Id, string Name, string Label);

    static readonly IReadOnlyList<EncryptionChoice> Encryptions = new[]
    {
        new EncryptionChoice(TorrentEncryption.Prefer, "Prefer encrypted"),
        new EncryptionChoice(TorrentEncryption.Require, "Encrypted only"),
        new EncryptionChoice(TorrentEncryption.Off, "Off (plain only)"),
    };

    public IReadOnlyList<EncryptionChoice> EncryptionChoices => Encryptions;

    public EncryptionChoice SelectedEncryption
    {
        get => Encryptions.FirstOrDefault(c => c.Value == Engine.TorrentEncryption) ?? Encryptions[0];
        set
        {
            if (value == null || value.Value == Engine.TorrentEncryption) return;
            Engine.TorrentEncryption = value.Value;
            OnPropertyChanged();
            EnginePropertiesChanged();
        }
    }

    public ObservableCollection<AdapterChoice> AdapterChoices { get; } = new();

    AdapterChoice? _selectedAdapter;
    bool _listingAdapters;

    public AdapterChoice? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (value == null || _listingAdapters || value == _selectedAdapter) return;
            _selectedAdapter = value;
            Engine.TorrentNetworkAdapter = value.Id;
            Engine.TorrentNetworkAdapterName = value.Id == null ? null : value.Name;
            OnPropertyChanged();
            EnginePropertiesChanged();
            RefreshTorrentNetwork();
        }
    }

    /// <summary>Lists the adapters again (one may have come or gone); the chosen one stays listed while it is not there.</summary>
    public void RefreshAdapters()
    {
        _listingAdapters = true;
        try
        {
            AdapterChoices.Clear();
            AdapterChoices.Add(new AdapterChoice(null, "", "Any network (usual)"));
            foreach (var a in _adapters())
            {
                var address = a.Addresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? a.Addresses.FirstOrDefault();
                AdapterChoices.Add(new AdapterChoice(a.Id, a.Name, a.Connected ? $"{a.Name}  ·  {address}" : $"{a.Name} (not connected)"));
            }

            var chosen = Engine.TorrentNetworkAdapter;
            if (chosen != null && AdapterChoices.All(c => !string.Equals(c.Id, chosen, StringComparison.OrdinalIgnoreCase)))
            {
                var name = Engine.TorrentNetworkAdapterName ?? "The chosen adapter";
                AdapterChoices.Add(new AdapterChoice(chosen, name, $"{name} (not there now)"));
            }

            _selectedAdapter = AdapterChoices.FirstOrDefault(c => string.Equals(c.Id, chosen, StringComparison.OrdinalIgnoreCase)) ?? AdapterChoices[0];
        }
        finally
        {
            _listingAdapters = false;
        }

        OnPropertyChanged(nameof(SelectedAdapter));
        RefreshTorrentNetwork();
    }

    public bool TorrentUseProxy
    {
        get => Engine.TorrentUseProxy;
        set
        {
            if (Engine.TorrentUseProxy == value) return;
            Engine.TorrentUseProxy = value;
            OnPropertyChanged();
            EnginePropertiesChanged();
            RefreshTorrentNetwork();
        }
    }

    /// <summary>"Torrents go through the proxy 10.0.0.2:1080 (UDP trackers … not used: …)."</summary>
    [ObservableProperty] string _torrentNetworkText = "";

    [ObservableProperty] string _bannedText = "";
    [ObservableProperty] bool _hasBanned;

    /// <summary>The words about how torrents reach the network, and the banned peers, again (the settings or the adapters changed).</summary>
    public void RefreshTorrentNetwork()
    {
        if (_manager == null) return;
        TorrentNetworkText = $"Right now torrents go {_manager.DescribeTorrentNetwork()}.";
        var banned = _manager.BannedTorrentPeers;
        HasBanned = banned.Count > 0;
        BannedText = banned.Count switch
        {
            0 => "None. Ban one from a torrent's Details > Peers: it is then never connected to, in any torrent.",
            <= 4 => $"Never connected to, in any torrent: {string.Join(", ", banned)}.",
            _ => $"{banned.Count} addresses are never connected to, in any torrent: {string.Join(", ", banned.Take(4))} …",
        };
    }

    [RelayCommand]
    void UnbanPeers()
    {
        _manager?.UnbanTorrentPeers();
        RefreshTorrentNetwork();
    }

    // ---- magnet links and .torrent files ----

    AssociationStatus _association = new(false, false, false);

    /// <summary>Windows opens magnet links and .torrent files with ShadeNet (registered for the current user).</summary>
    public bool OpenTorrentsWithShadeNet
    {
        get => _association.Registered;
        set
        {
            if (value == _association.Registered) return;
            try
            {
                if (value) _associations.Register();
                else _associations.Unregister();
                AssociationError = null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                AssociationError = "Windows did not allow it: " + ex.Message;
            }

            RefreshAssociations();
        }
    }

    /// <summary>What opens them now, and what to do when it is another program.</summary>
    [ObservableProperty] string _associationText = "";
    [ObservableProperty] string? _associationError;

    /// <summary>Another program opens them: Windows' Default apps is where the user can choose ShadeNet.</summary>
    [ObservableProperty] bool _needsDefaultApps;

    public void RefreshAssociations()
    {
        try
        {
            _association = _associations.GetStatus();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _association = new AssociationStatus(false, false, false);
        }

        OnPropertyChanged(nameof(OpenTorrentsWithShadeNet));
        var s = _association;
        NeedsDefaultApps = s.Registered && !(s.OpensMagnet && s.OpensTorrentFiles);
        AssociationText = !s.Registered
            ? "Off: magnet links and .torrent files open with whatever Windows used before. Paste them into ShadeNet, or drop a .torrent file on its window."
            : (s.OpensMagnet, s.OpensTorrentFiles) switch
            {
                (true, true) => "Magnet links and .torrent files open in ShadeNet.",
                (true, false) => "Magnet links open in ShadeNet. Another program opens .torrent files: choose ShadeNet for them in Windows' Default apps.",
                (false, true) => ".torrent files open in ShadeNet. Another program opens magnet links: choose ShadeNet for them in Windows' Default apps.",
                _ => "Another program opens them: choose ShadeNet for magnet links and .torrent files in Windows' Default apps.",
            };
    }

    [RelayCommand]
    void OpenDefaultApps()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TorrentAssociation.DefaultAppsPage) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AssociationError = "Windows' Default apps could not be opened: " + ex.Message;
        }
    }

    EngineSettings Engine => _settings.Current.Engine;

    /// <summary>Called by the category rows after they changed the engine settings.</summary>
    internal void EnginePropertiesChanged(bool categories = false)
    {
        if (categories) Engine.InvalidateCategories();
        _settings.Save();
        _applyEngine();
        if (categories) _categoriesChanged();
    }

    // ---- folder ----

    [ObservableProperty] string _downloadRootText;
    [ObservableProperty] string _downloadRootError = "";

    // The text box updates this when it loses focus (or on Enter), so a change is a finished edit.
    partial void OnDownloadRootTextChanged(string value) => CommitDownloadRoot();

    void CommitDownloadRoot()
    {
        var text = (DownloadRootText ?? "").Trim();
        if (!Path.IsPathFullyQualified(text) || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            DownloadRootError = "Use a full folder path, such as D:\\Downloads.";
            return;
        }

        DownloadRootError = "";
        if (string.Equals(Engine.DownloadRoot, text, StringComparison.Ordinal)) return;
        Engine.DownloadRoot = text;
        EnginePropertiesChanged();
        RefreshCategoryHints();
    }

    [RelayCommand]
    void BrowseDownloadRoot()
    {
        var dialog = new OpenFolderDialog { Title = "Default download folder", Multiselect = false };
        if (Directory.Exists(Engine.DownloadRoot)) dialog.InitialDirectory = Engine.DownloadRoot;
        if (dialog.ShowDialog() != true) return;

        DownloadRootText = dialog.FolderName;   // the change commits itself
    }

    public bool UseCategoryFolders
    {
        get => Engine.UseCategoryFolders;
        set
        {
            if (Engine.UseCategoryFolders == value) return;
            Engine.UseCategoryFolders = value;
            OnPropertyChanged();
            EnginePropertiesChanged();
            RefreshCategoryHints();
        }
    }

    public int DefaultConnections
    {
        get => Engine.DefaultConnections;
        set
        {
            var v = Math.Clamp(value, 1, 32);
            if (Engine.DefaultConnections == v) return;
            Engine.DefaultConnections = v;
            OnPropertyChanged();
            EnginePropertiesChanged();
        }
    }

    public bool AutoResumeOnStartup
    {
        get => Engine.AutoResumeOnStartup;
        set
        {
            if (Engine.AutoResumeOnStartup == value) return;
            Engine.AutoResumeOnStartup = value;
            OnPropertyChanged();
            EnginePropertiesChanged();
        }
    }

    // ---- recordings ----

    /// <summary>A live recording stops by itself after this many minutes (0 = never).</summary>
    public int MaxRecordingMinutes
    {
        get => (int)Engine.MaxRecordingLength.TotalMinutes;
        set
        {
            var v = Math.Clamp(value, 0, 24 * 60);
            if (v == MaxRecordingMinutes) return;
            Engine.MaxRecordingLength = TimeSpan.FromMinutes(v);
            OnPropertyChanged();
            EnginePropertiesChanged();
        }
    }

    // ---- torrents ----

    /// <summary>A finished torrent is shared until it has sent this many times its size (0 = not shared at all), as text ("1", "1.5").</summary>
    public string TorrentSeedRatioText
    {
        get => Engine.TorrentSeedRatio.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        set
        {
            if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var v)
                && !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                OnPropertyChanged();   // not a number: show the value in use again
                return;
            }

            v = Math.Clamp(Math.Round(v, 2), 0, 100);
            if (Math.Abs(Engine.TorrentSeedRatio - v) < 0.001) return;
            Engine.TorrentSeedRatio = v;
            OnPropertyChanged();
            EnginePropertiesChanged();
        }
    }

    /// <summary>A finished torrent stops being shared after this many minutes (0 = only the ratio stops it).</summary>
    public int TorrentSeedMinutes
    {
        get => Engine.TorrentSeedMinutes;
        set
        {
            var v = Math.Clamp(value, 0, 525_600);
            if (Engine.TorrentSeedMinutes == v) return;
            Engine.TorrentSeedMinutes = v;
            OnPropertyChanged();
            EnginePropertiesChanged();
        }
    }

    /// <summary>Total upload speed of torrents in KB/s (0 = no limit).</summary>
    public int TorrentUploadLimitKb
    {
        get => (int)(Engine.TorrentUploadLimit / 1024);
        set
        {
            var v = Math.Clamp(value, 0, 1_000_000);
            if (TorrentUploadLimitKb == v) return;
            Engine.TorrentUploadLimit = v * 1024L;
            OnPropertyChanged();
            EnginePropertiesChanged();
        }
    }

    /// <summary>The port peers connect to (0 = a free one each start).</summary>
    public int TorrentPort
    {
        get => Engine.TorrentPort;
        set
        {
            var v = value is >= 0 and <= 65535 ? value : Engine.TorrentPort;
            if (Engine.TorrentPort == v)
            {
                OnPropertyChanged();
                return;
            }

            Engine.TorrentPort = v;
            OnPropertyChanged();
            EnginePropertiesChanged();
        }
    }

    public bool TorrentUseDht
    {
        get => Engine.TorrentUseDht;
        set
        {
            if (Engine.TorrentUseDht == value) return;
            Engine.TorrentUseDht = value;
            OnPropertyChanged();
            EnginePropertiesChanged();
            RefreshTorrentNetwork();
        }
    }

    public bool TorrentPortForwarding
    {
        get => Engine.TorrentPortForwarding;
        set
        {
            if (Engine.TorrentPortForwarding == value) return;
            Engine.TorrentPortForwarding = value;
            OnPropertyChanged();
            EnginePropertiesChanged();
            RefreshTorrentNetwork();
        }
    }

    public bool TorrentLocalPeers
    {
        get => Engine.TorrentLocalPeers;
        set
        {
            if (Engine.TorrentLocalPeers == value) return;
            Engine.TorrentLocalPeers = value;
            OnPropertyChanged();
            EnginePropertiesChanged();
            RefreshTorrentNetwork();
        }
    }

    public bool WriteMarkOfTheWeb
    {
        get => Engine.WriteMarkOfTheWeb;
        set
        {
            if (Engine.WriteMarkOfTheWeb == value) return;
            Engine.WriteMarkOfTheWeb = value;
            OnPropertyChanged();
            EnginePropertiesChanged();
        }
    }

    // ---- categories ----

    public ObservableCollection<CategoryRowViewModel> Categories { get; } = new();

    [ObservableProperty] string _newCategoryName = "";
    [ObservableProperty] string _newCategoryExtensions = "";
    [ObservableProperty] string _newCategoryError = "";

    void LoadCategories()
    {
        Categories.Clear();
        foreach (var name in Engine.CategoryNames)
            Categories.Add(new CategoryRowViewModel(this, Engine, name, Engine.CustomCategories.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))));
    }

    void RefreshCategoryHints()
    {
        foreach (var c in Categories) c.Reload();
    }

    [RelayCommand]
    void AddCategory()
    {
        var name = (NewCategoryName ?? "").Trim();
        if (!Engine.IsValidNewCategoryName(name))
        {
            NewCategoryError = string.IsNullOrEmpty(name)
                ? "Give the category a name."
                : "That name is taken or cannot be a folder name (no \\ / : * ? \" < > |).";
            return;
        }

        var extensions = EngineSettings.NormalizeExtensions((NewCategoryExtensions ?? "").Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        Engine.CustomCategories.Add(new CustomCategory { Name = name, Extensions = extensions });
        NewCategoryName = "";
        NewCategoryExtensions = "";
        NewCategoryError = "";
        EnginePropertiesChanged(categories: true);
        LoadCategories();
    }

    internal void RemoveCustomCategory(string name)
    {
        Engine.CustomCategories.RemoveAll(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        Engine.CategoryFolders.Remove(name);
        EnginePropertiesChanged(categories: true);
        LoadCategories();
    }
}

// ================================================================================================================
// Network: proxy, timeouts, per-site limits
// ================================================================================================================

public sealed partial class HostLimitRowViewModel : ObservableObject
{
    public HostLimitRowViewModel(string host, int limit)
    {
        Host = host;
        Limit = limit;
    }

    public string Host { get; }
    public int Limit { get; }
}

public sealed partial class NetworkSettingsViewModel : ObservableObject
{
    readonly SettingsService _settings;
    readonly Action _applyEngine;

    public NetworkSettingsViewModel(SettingsService settings, Action applyEngine)
    {
        _settings = settings;
        _applyEngine = applyEngine;
        _proxyAddress = settings.Current.Engine.ProxyAddress;
        LoadLimits();
    }

    EngineSettings Engine => _settings.Current.Engine;

    void Changed()
    {
        _settings.Save();
        _applyEngine();
    }

    // ---- proxy ----

    public bool ProxySystem
    {
        get => Engine.Proxy == ProxyMode.System;
        set { if (value) SetProxy(ProxyMode.System); }
    }

    public bool ProxyNone
    {
        get => Engine.Proxy == ProxyMode.None;
        set { if (value) SetProxy(ProxyMode.None); }
    }

    public bool ProxyManual
    {
        get => Engine.Proxy == ProxyMode.Manual;
        set { if (value) SetProxy(ProxyMode.Manual); }
    }

    void SetProxy(ProxyMode mode)
    {
        if (Engine.Proxy == mode) return;
        Engine.Proxy = mode;
        OnPropertyChanged(nameof(ProxySystem));
        OnPropertyChanged(nameof(ProxyNone));
        OnPropertyChanged(nameof(ProxyManual));
        ValidateProxy();
        Changed();
    }

    [ObservableProperty] string _proxyAddress;
    [ObservableProperty] string _proxyError = "";

    partial void OnProxyAddressChanged(string value)
    {
        Engine.ProxyAddress = (value ?? "").Trim();
        ValidateProxy();
        Changed();
    }

    void ValidateProxy() =>
        ProxyError = Engine.Proxy == ProxyMode.Manual && !EngineSettings.IsUsableProxyAddress(Engine.ProxyAddress)
            ? "Enter the proxy as host:port (proxy.example.com:8080) or socks5://host:port. Until then Windows' own setting is used."
            : "";

    // ---- timeouts ----

    public int ConnectTimeoutSeconds
    {
        get => (int)Engine.ConnectTimeout.TotalSeconds;
        set
        {
            var v = Math.Clamp(value, 3, 120);
            if (v == ConnectTimeoutSeconds) return;
            Engine.ConnectTimeout = TimeSpan.FromSeconds(v);
            OnPropertyChanged();
            Changed();
        }
    }

    public int MaxRetries
    {
        get => Engine.MaxRetries;
        set
        {
            var v = Math.Clamp(value, 0, 50);
            if (v == Engine.MaxRetries) return;
            Engine.MaxRetries = v;
            OnPropertyChanged();
            Changed();
        }
    }

    public string UserAgent
    {
        get => Engine.UserAgent;
        set
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0 || v.Any(c => c < ' ') || v == Engine.UserAgent) return;   // a header value cannot be empty or hold line breaks
            Engine.UserAgent = v;
            OnPropertyChanged();
            Changed();
        }
    }

    [RelayCommand]
    void ResetUserAgent()
    {
        Engine.UserAgent = new EngineSettings().UserAgent;
        OnPropertyChanged(nameof(UserAgent));
        Changed();
    }

    // ---- connections per site ----

    public ObservableCollection<HostLimitRowViewModel> HostLimits { get; } = new();

    [ObservableProperty] string _newHost = "";
    [ObservableProperty] int _newHostLimit = 2;
    [ObservableProperty] string _hostError = "";

    void LoadLimits()
    {
        HostLimits.Clear();
        foreach (var (host, limit) in Engine.HostConnectionLimits.OrderBy(kv => kv.Key))
            HostLimits.Add(new HostLimitRowViewModel(host, limit));
    }

    [RelayCommand]
    void AddHostLimit()
    {
        var host = (NewHost ?? "").Trim().ToLowerInvariant();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains('/'))
        {
            HostError = "Enter a site name such as files.example.com.";
            return;
        }

        Engine.HostConnectionLimits[host] = Math.Clamp(NewHostLimit, 1, 32);
        NewHost = "";
        HostError = "";
        LoadLimits();
        Changed();
    }

    [RelayCommand]
    void RemoveHostLimit(HostLimitRowViewModel? row)
    {
        if (row == null) return;
        Engine.HostConnectionLimits.Remove(row.Host);
        LoadLimits();
        Changed();
    }
}

// ================================================================================================================
// Queue: how many at once, the schedule, what happens at the end
// ================================================================================================================

public sealed partial class DayToggleViewModel : ObservableObject
{
    readonly Action<DayOfWeek, bool> _changed;

    public DayToggleViewModel(DayOfWeek day, bool isOn, Action<DayOfWeek, bool> changed)
    {
        Day = day;
        _isOn = isOn;
        _changed = changed;
        Label = CultureInfo.CurrentUICulture.DateTimeFormat.GetAbbreviatedDayName(day);
    }

    public DayOfWeek Day { get; }
    public string Label { get; }

    [ObservableProperty] bool _isOn;

    partial void OnIsOnChanged(bool value) => _changed(Day, value);
}

public sealed partial class QueueSettingsViewModel : ObservableObject
{
    readonly SettingsService _settings;
    readonly Action _applyEngine;
    readonly Action _scheduleChanged;

    public QueueSettingsViewModel(SettingsService settings, Action applyEngine, Action scheduleChanged, FinishWatcher? finish)
    {
        _settings = settings;
        _applyEngine = applyEngine;
        _scheduleChanged = scheduleChanged;
        Finish = finish;
        // the status-bar menu can change it too
        if (finish != null) finish.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FinishWatcher.Action)) OnPropertyChanged(nameof(SelectedFinish)); };

        _startText = settings.Current.Schedule.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        _stopText = settings.Current.Schedule.Stop.ToString("HH:mm", CultureInfo.InvariantCulture);

        // Monday first, as the week is shown in most of the world
        Days = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(d => new DayToggleViewModel(d, settings.Current.Schedule.Days.Contains(d), OnDayChanged))
            .ToList();
    }

    ScheduleSettings Schedule => _settings.Current.Schedule;

    /// <summary>"When all downloads are finished" (not saved, see <see cref="FinishWatcher"/>). Null in tests.</summary>
    public FinishWatcher? Finish { get; }

    public sealed record FinishChoice(FinishAction Action, string Label);

    public IReadOnlyList<FinishChoice> FinishChoices { get; } = new FinishChoice[]
    {
        new(FinishAction.None, "Do nothing"),
        new(FinishAction.CloseApp, "Close ShadeNet"),
        new(FinishAction.Sleep, "Put the computer to sleep"),
        new(FinishAction.Hibernate, "Hibernate"),
        new(FinishAction.Shutdown, "Shut down the computer"),
    };

    /// <summary>The choice in the combo box; it is session-only on purpose (see <see cref="FinishWatcher"/>).</summary>
    public FinishChoice SelectedFinish
    {
        get => FinishChoices.First(c => c.Action == (Finish?.Action ?? FinishAction.None));
        set
        {
            if (Finish == null || value == null || Finish.Action == value.Action) return;
            Finish.Action = value.Action;
            OnPropertyChanged();
        }
    }

    public int MaxConcurrentJobs
    {
        get => _settings.Current.Engine.MaxConcurrentJobs;
        set
        {
            var v = Math.Clamp(value, 1, 32);
            if (v == _settings.Current.Engine.MaxConcurrentJobs) return;
            _settings.Current.Engine.MaxConcurrentJobs = v;
            OnPropertyChanged();
            _settings.Save();
            _applyEngine();   // a higher limit starts waiting downloads at once
        }
    }

    public bool ScheduleEnabled
    {
        get => Schedule.Enabled;
        set
        {
            if (Schedule.Enabled == value) return;
            Schedule.Enabled = value;
            OnPropertyChanged();
            ScheduleEdited();
        }
    }

    [ObservableProperty] string _startText;
    [ObservableProperty] string _stopText;
    [ObservableProperty] string _timeError = "";

    public IReadOnlyList<DayToggleViewModel> Days { get; }

    partial void OnStartTextChanged(string value) => TryApplyTime(value, isStart: true);
    partial void OnStopTextChanged(string value) => TryApplyTime(value, isStart: false);

    void TryApplyTime(string text, bool isStart)
    {
        if (!TimeOnly.TryParseExact((text ?? "").Trim(), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            TimeError = "Write times like 01:00 or 23:30.";
            return;
        }

        TimeError = "";
        if (isStart) Schedule.Start = time;
        else Schedule.Stop = time;
        ScheduleEdited();
    }

    void OnDayChanged(DayOfWeek day, bool on)
    {
        if (on && !Schedule.Days.Contains(day)) Schedule.Days.Add(day);
        else if (!on) Schedule.Days.Remove(day);
        ScheduleEdited();
    }

    /// <summary>"01:00 to 07:00": a plain sentence under the schedule so the overnight case is clear.</summary>
    public string ScheduleSummary =>
        !Schedule.Enabled ? "The queue runs whenever it is started."
        : Schedule.Start == Schedule.Stop ? "The queue runs all day on the selected days."
        : Schedule.Start > Schedule.Stop
            ? $"The queue runs from {Schedule.Start:HH:mm} until {Schedule.Stop:HH:mm} the next morning."
            : $"The queue runs from {Schedule.Start:HH:mm} until {Schedule.Stop:HH:mm}.";

    void ScheduleEdited()
    {
        OnPropertyChanged(nameof(ScheduleSummary));
        _settings.Save();
        _scheduleChanged();
    }
}
