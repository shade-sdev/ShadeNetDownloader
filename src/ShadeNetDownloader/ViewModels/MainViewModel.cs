using System.Collections.Concurrent;
using System.Windows;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShadeNet.Core;
using ShadeNet.Core.Models;
using ShadeNet.Core.Util;
using ShadeNetDownloader.Models;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Util;
using Wpf.Ui.Controls;

namespace ShadeNetDownloader.ViewModels;

/// <summary>Shell state: sidebar navigation, which page is showing, and the live numbers in the title and status bars.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    readonly DownloadManager _manager;
    readonly SettingsService _settings;
    readonly IDialogService _dialogs;
    readonly ConcurrentDictionary<Guid, double> _speeds = new();
    readonly DispatcherTimer _timer;

    public MainViewModel(DownloadManager manager, SettingsService settings, IDialogService dialogs, ToastService toasts,
        DownloadsViewModel downloads, SettingsViewModel settingsPage, FinishWatcher? finish = null)
    {
        _manager = manager;
        _settings = settings;
        _isSidebarExpanded = !settings.Current.SidebarCollapsed;
        _dialogs = dialogs;
        Toasts = toasts;
        Downloads = downloads;
        SettingsPage = settingsPage;
        Limit = new SpeedLimitViewModel(manager, settings);
        Limit.Changed += Refresh;   // the status bar follows a change at once, not at the next tick

        Primary = new ObservableCollection<NavItem>
        {
            new(NavKind.All, "All downloads", SymbolRegular.ArrowDownload24),
            new(NavKind.Downloading, "Downloading", SymbolRegular.Flash24),
            new(NavKind.Queued, "Queued", SymbolRegular.Clock24),
            new(NavKind.Paused, "Paused", SymbolRegular.Pause24),
            new(NavKind.Completed, "Completed", SymbolRegular.CheckmarkCircle24),
            new(NavKind.Failed, "Failed", SymbolRegular.ErrorCircle24),
        };

        Finish = finish;
        if (finish != null) finish.PropertyChanged += (_, _) => UpdateFinishText();
        Categories = new ObservableCollection<NavItem>(_manager.Settings.CategoryNames.Select(c =>
            new NavItem(NavKind.Category, c, CategoryIcon(c), c)));
        SettingsPage.CategoriesChanged += RebuildCategories;
        _manager.QueueStateChanged += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);

        SettingsItem = new NavItem(NavKind.Settings, "Settings", SymbolRegular.Settings24);

        _manager.JobUpdated += OnJobUpdated;
        _manager.JobRemoved += id => _speeds.TryRemove(id, out _);

        // Removing / undoing changes what the user sees at once, so recount at once instead of on the next tick.
        Downloads.PendingRemovalChanged += Refresh;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Select(Primary[0]);
        Refresh();
    }

    public ToastService Toasts { get; }
    public DownloadsViewModel Downloads { get; }
    public SettingsViewModel SettingsPage { get; }

    /// <summary>The total-speed limit flyout in the status bar.</summary>
    public SpeedLimitViewModel Limit { get; }
    public ObservableCollection<NavItem> Primary { get; }
    public ObservableCollection<NavItem> Categories { get; }
    public NavItem SettingsItem { get; }

    [ObservableProperty] object? _currentPage;
    [ObservableProperty] NavItem? _selected;
    [ObservableProperty] string _searchText = "";
    [ObservableProperty] string _speedText = "0 B/s";
    [ObservableProperty] string _statusText = "";
    [ObservableProperty] string _diskText = "";
    [ObservableProperty] string _limitText = "Limit: off";

    // ---- the sidebar: icons with their names, or icons only ----

    public const double SidebarExpandedWidth = 248;
    public const double SidebarCollapsedWidth = 68;

    [ObservableProperty] bool _isSidebarExpanded;

    public GridLength SidebarWidth => new(IsSidebarExpanded ? SidebarExpandedWidth : SidebarCollapsedWidth);

    /// <summary>The drop hint covers the content, not the sidebar.</summary>
    public Thickness DropMargin => new(SidebarWidth.Value, 0, 12, 12);

    public string SidebarToggleTip => IsSidebarExpanded ? "Show icons only" : "Show names";

    partial void OnIsSidebarExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(DropMargin));
        OnPropertyChanged(nameof(SidebarToggleTip));
        if (_settings.Current.SidebarCollapsed == !value) return;
        _settings.Current.SidebarCollapsed = !value;
        _settings.Save();
    }

    [RelayCommand]
    void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    static SymbolRegular CategoryIcon(string category) => category switch
    {
        "Video" => SymbolRegular.Video24,
        "Music" => SymbolRegular.MusicNote124,
        "Documents" => SymbolRegular.Document24,
        "Programs" => SymbolRegular.Apps24,
        "Compressed" => SymbolRegular.Archive24,
        "Images" => SymbolRegular.Image24,
        _ => SymbolRegular.Folder24,
    };

    [RelayCommand]
    void Select(NavItem item)
    {
        if (Selected != null) Selected.IsSelected = false;
        item.IsSelected = true;
        Selected = item;

        if (item.Kind == NavKind.Settings)
        {
            CurrentPage = SettingsPage;
        }
        else
        {
            Downloads.SetFilter(item);
            CurrentPage = Downloads;
        }
    }

    partial void OnSearchTextChanged(string value) => Downloads.SearchText = value;

    [RelayCommand]
    Task AddDownload() => _dialogs.ShowAddDownloadAsync();

    /// <summary>"When finished": what happens to the computer after the last download. <c>null</c> in tests.</summary>
    public FinishWatcher? Finish { get; }

    [ObservableProperty] string _finishText = "When finished: nothing";
    [ObservableProperty] bool _finishArmed;

    void UpdateFinishText()
    {
        if (Finish is not { } f) return;
        FinishArmed = f.IsArmed;
        FinishText = f.IsCountingDown
            ? $"{FinishWatcher.Describe(f.Action)} in {f.SecondsLeft} s"
            : $"When finished: {FinishWatcher.ShortName(f.Action)}";
    }

    /// <summary>The user added, renamed or removed categories: the sidebar follows.</summary>
    void RebuildCategories()
    {
        var wanted = _manager.Settings.CategoryNames;
        var selectedCategory = Selected is { Kind: NavKind.Category } s ? s.Category : null;

        // reuse the entries that stay, so selection and counts are not lost
        for (var i = Categories.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Categories[i].Category!, StringComparer.OrdinalIgnoreCase)) Categories.RemoveAt(i);

        for (var i = 0; i < wanted.Count; i++)
        {
            var existing = Categories.FirstOrDefault(c => string.Equals(c.Category, wanted[i], StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                Categories.Insert(Math.Min(i, Categories.Count), new NavItem(NavKind.Category, wanted[i], CategoryIcon(wanted[i]), wanted[i]));
            }
            else if (Categories.IndexOf(existing) != i)
            {
                Categories.Move(Categories.IndexOf(existing), i);
            }
        }

        if (selectedCategory != null && Categories.All(c => c.Category != selectedCategory)) Select(Primary[0]);
        Refresh();
    }

    // ---- the queue ----

    [ObservableProperty] bool _queueRunning = true;
    [ObservableProperty] string _queueText = "";

    [RelayCommand]
    void ToggleQueue() => _manager.SetQueueRunning(!_manager.QueueRunning, pauseQueueDownloads: false);

    // ---- tray and taskbar ----

    [ObservableProperty] string _trayTooltip = "ShadeNet Downloader";
    [ObservableProperty] System.Windows.Shell.TaskbarItemProgressState _taskbarState = System.Windows.Shell.TaskbarItemProgressState.None;
    [ObservableProperty] double _taskbarProgress;

    [RelayCommand]
    void PauseAll() => _manager.PauseAll();

    [RelayCommand]
    void ResumeAll() => _manager.ResumeAll();

    void OnJobUpdated(JobSnapshot s)
    {
        if (s.State == DownloadState.Downloading) _speeds[s.Id] = s.Speed;
        else _speeds.TryRemove(s.Id, out _);
    }

    /// <summary>Twice a second: sidebar counts, status bar, speed pill.</summary>
    internal void Refresh()
    {
        // Downloads waiting out their undo window are already gone from the list, so they are not counted either.
        var jobs = _manager.Jobs.Where(j => !Downloads.IsPendingRemoval(j.Id)).ToList();

        int Count(Func<DownloadJob, bool> pred) => jobs.Count(pred);
        Primary[0].Count = jobs.Count;
        Primary[1].Count = Count(j => j.State is DownloadState.Probing or DownloadState.Downloading or DownloadState.Stopping);
        Primary[2].Count = Count(j => j.State == DownloadState.Queued);
        Primary[3].Count = Count(j => j.State == DownloadState.Paused);
        Primary[4].Count = Count(j => j.State == DownloadState.Completed);
        Primary[5].Count = Count(j => j.State == DownloadState.Failed);
        foreach (var c in Categories) c.Count = Count(j => j.Category == c.Category);

        var active = Primary[1].Count;
        StatusText = $"{active} active  ·  {Primary[2].Count} queued  ·  {Primary[4].Count} completed";

        var speed = _speeds.Values.Sum();
        SpeedText = ByteFormat.Speed(speed);

        var limit = _manager.GlobalLimiter.BytesPerSecond;
        LimitText = limit > 0 ? $"Limit: {ByteFormat.Speed(limit)}" : "Limit: off";

        QueueRunning = _manager.QueueRunning;
        QueueText = _manager.QueueRunning ? "" : "Queue stopped";
        if (!_manager.QueueRunning && Primary[2].Count > 0) StatusText += "  ·  queue stopped";

        TrayTooltip = active > 0
            ? $"ShadeNet · {active} downloading · {SpeedText}"
            : Primary[2].Count > 0 ? $"ShadeNet · {Primary[2].Count} queued" : "ShadeNet Downloader";

        // overall progress on the taskbar button: the downloads running now, by bytes
        var running = jobs.Where(j => j.State is DownloadState.Downloading or DownloadState.Probing).ToList();
        var totals = running.Where(j => j.TotalSize is > 0).ToList();
        if (running.Count == 0)
        {
            TaskbarState = System.Windows.Shell.TaskbarItemProgressState.None;
            TaskbarProgress = 0;
        }
        else if (totals.Count == 0)
        {
            TaskbarState = System.Windows.Shell.TaskbarItemProgressState.Indeterminate;
        }
        else
        {
            var all = totals.Sum(j => (double)j.TotalSize!.Value);
            var done = totals.Sum(j => (double)Math.Min(j.DownloadedBytes, j.TotalSize!.Value));
            TaskbarState = System.Windows.Shell.TaskbarItemProgressState.Normal;
            TaskbarProgress = all > 0 ? Math.Clamp(done / all, 0, 1) : 0;
        }

        DiskText = FreeSpaceText(_settings.Current.Engine.DownloadRoot);
    }

    static string FreeSpaceText(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (root == null) return "";
            var drive = new DriveInfo(root);
            return $"{ByteFormat.Size(drive.AvailableFreeSpace)} free on {drive.Name.TrimEnd('\\')}";
        }
        catch
        {
            return "";
        }
    }
}
