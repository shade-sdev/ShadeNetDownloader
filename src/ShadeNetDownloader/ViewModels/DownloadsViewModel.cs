using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShadeNet.Core;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNetDownloader.Models;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Services.Browser;

namespace ShadeNetDownloader.ViewModels;

/// <summary>
/// The list page. Engine events arrive on thread-pool threads at up to 4 Hz per job; they are coalesced here and
/// applied to the cards in one batch on the UI thread every 100 ms, so hundreds of downloads never flood the dispatcher.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(6);

    readonly DownloadManager _manager;
    readonly ToastService _toasts;
    readonly IBrowserBridge? _bridge;
    readonly Dispatcher _ui;
    readonly DispatcherTimer _tick;
    readonly ConcurrentDictionary<Guid, JobSnapshot> _pending = new();
    readonly Dictionary<Guid, DownloadItemViewModel> _byId = new();
    readonly HashSet<Guid> _hidden = new();
    List<DownloadItemViewModel> _selection = new();
    NavItem _filter = new(NavKind.All, "All downloads", Wpf.Ui.Controls.SymbolRegular.ArrowDownload24);
    string _search = "";

    public DownloadsViewModel(DownloadManager manager, ToastService toasts, IBrowserBridge? bridge = null)
    {
        _manager = manager;
        _toasts = toasts;
        _bridge = bridge;
        _ui = Dispatcher.CurrentDispatcher;

        Items = new ObservableCollection<DownloadItemViewModel>();
        View = new ListCollectionView(Items) { Filter = Matches };
        View.IsLiveFiltering = true;
        View.LiveFilteringProperties.Add(nameof(DownloadItemViewModel.State));
        View.LiveFilteringProperties.Add(nameof(DownloadItemViewModel.IsPendingRemoval));
        View.IsLiveSorting = true;
        View.LiveSortingProperties.Add(nameof(DownloadItemViewModel.Priority));
        ((INotifyCollectionChanged)View).CollectionChanged += (_, _) => UpdateTexts();

        foreach (var job in _manager.Jobs.OrderBy(j => j.CreatedAt)) AddItem(job);

        _manager.JobAdded += job => _ui.BeginInvoke(() => AddItem(job));
        _manager.JobRemoved += id => _ui.BeginInvoke(() => DropItem(id));
        _manager.JobUpdated += s => _pending[s.Id] = s;
        _manager.JobCompleted += OnCompleted;

        _tick = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => ApplyPending();
        _tick.Start();

        UpdateTexts();
    }

    /// <summary>Raised when cards are hidden for removal or brought back by Undo (the shell recounts its badges).</summary>
    public event Action? PendingRemovalChanged;

    /// <summary>True while a download waits out its undo window: it is gone from the UI, not yet from the engine.</summary>
    public bool IsPendingRemoval(Guid id) => _hidden.Contains(id);

    public ObservableCollection<DownloadItemViewModel> Items { get; }
    public ListCollectionView View { get; }

    [ObservableProperty] string _title = "All downloads";
    [ObservableProperty] string _subtitle = "";
    [ObservableProperty] bool _isEmpty = true;
    [ObservableProperty] string _emptyTitle = "Nothing here yet";
    [ObservableProperty] string _emptyHint = "";
    [ObservableProperty] bool _showClearCompleted;

    /// <summary>Text typed in the title-bar search box.</summary>
    public string SearchText
    {
        get => _search;
        set
        {
            value ??= "";
            if (_search == value) return;
            _search = value;
            OnPropertyChanged();
            View.Refresh();
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Filtering
    // ------------------------------------------------------------------------------------------------------------

    public void SetFilter(NavItem item)
    {
        _filter = item;

        // The queue is shown in the order it will run; every other list keeps "newest first".
        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            if (item.Kind == NavKind.Queued)
            {
                View.SortDescriptions.Add(new SortDescription(nameof(DownloadItemViewModel.Priority), ListSortDirection.Descending));
                View.SortDescriptions.Add(new SortDescription(nameof(DownloadItemViewModel.CreatedAt), ListSortDirection.Ascending));
            }
        }

        OnPropertyChanged(nameof(IsQueueView));
        View.Refresh();
        UpdateTexts();
    }

    /// <summary>The Queued list is showing (it gets the start/stop button and the order controls).</summary>
    public bool IsQueueView => _filter.Kind == NavKind.Queued;

    /// <summary>Moves queued downloads within the queue. For several selected cards "top" and "bottom" keep their relative order.</summary>
    public void MoveInQueue(DownloadItemViewModel clicked, QueueMove move)
    {
        var targets = Targets(clicked).Where(i => i.CanMoveInQueue).OrderBy(i => i.Priority).ToList();   // lowest first
        if (targets.Count == 0) return;

        if (move is QueueMove.Top)
            foreach (var i in targets) _manager.MoveInQueue(i.Id, QueueMove.Top);          // last moved ends first: highest priority stays on top
        else if (move is QueueMove.Bottom)
            foreach (var i in targets.AsEnumerable().Reverse()) _manager.MoveInQueue(i.Id, QueueMove.Bottom);
        else
            _manager.MoveInQueue(clicked.Id, move);
    }

    /// <summary>"Add to queue" for paused or failed downloads.</summary>
    public void EnqueueSelectionOf(DownloadItemViewModel clicked)
    {
        foreach (var i in Targets(clicked).Where(i => i.CanQueue)) _manager.Enqueue(i.Id);
    }

    bool Matches(object o)
    {
        var item = (DownloadItemViewModel)o;
        if (item.IsPendingRemoval) return false;

        var ok = _filter.Kind switch
        {
            NavKind.Downloading => item.State is DownloadState.Probing or DownloadState.Downloading or DownloadState.Stopping,
            NavKind.Queued => item.State == DownloadState.Queued,
            NavKind.Paused => item.State == DownloadState.Paused,
            NavKind.Completed => item.State == DownloadState.Completed,
            NavKind.Failed => item.State == DownloadState.Failed,
            NavKind.Category => item.Category == _filter.Category,
            _ => true,
        };
        if (!ok) return false;

        if (_search.Length == 0) return true;
        foreach (var term in _search.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!item.FileName.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !item.Host.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !item.Url.Contains(term, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    void UpdateTexts()
    {
        var count = View.Count;
        Title = _filter.Title;
        IsEmpty = count == 0;
        ShowClearCompleted = _filter.Kind == NavKind.Completed && count > 0;
        Subtitle = count switch
        {
            0 => "",
            1 => "1 download",
            _ => $"{count} downloads",
        };

        var anyVisibleAtAll = Items.Any(i => !i.IsPendingRemoval);
        if (_search.Length > 0 && anyVisibleAtAll)
        {
            (EmptyTitle, EmptyHint) = ($"No results for “{_search}”", "Try another name, or part of the web address.");
            return;
        }

        (EmptyTitle, EmptyHint) = _filter.Kind switch
        {
            NavKind.All => ("Nothing downloading, yet", "Paste a link, drop one anywhere on this window,\nor use the browser extension."),
            NavKind.Downloading => ("Nothing is downloading right now", "Active downloads show up here with live speed and progress."),
            NavKind.Queued => ("The queue is empty", "Downloads waiting for a free slot are listed here."),
            NavKind.Paused => ("No paused downloads", "Pause a download and it will wait here until you resume it."),
            NavKind.Completed => ("No finished downloads", "Completed files are listed here so you can open them again."),
            NavKind.Failed => ("No failed downloads", "If something goes wrong you will see why and how to fix it."),
            NavKind.Category => ($"Nothing in {_filter.Title}", "Files are sorted into categories automatically."),
            _ => ("Nothing here yet", ""),
        };
    }

    // ------------------------------------------------------------------------------------------------------------
    // Engine → UI
    // ------------------------------------------------------------------------------------------------------------

    void AddItem(DownloadJob job)
    {
        if (_byId.ContainsKey(job.Id)) return;
        var item = new DownloadItemViewModel(job, _manager.Snapshot(job), this);
        _byId[job.Id] = item;
        Items.Insert(0, item); // newest on top
    }

    void DropItem(Guid id)
    {
        _pending.TryRemove(id, out _);
        _hidden.Remove(id);
        if (_byId.Remove(id, out var item))
        {
            if (Details != null && ReferenceEquals(Details.Item, item)) CloseDetails();
            Items.Remove(item);
        }
    }

    // ---- details drawer ----

    /// <summary>The drawer's content; <c>null</c> while it is closed.</summary>
    [ObservableProperty] DownloadDetailsViewModel? _details;

    public bool IsDetailsOpen => Details != null;

    partial void OnDetailsChanged(DownloadDetailsViewModel? value) => OnPropertyChanged(nameof(IsDetailsOpen));

    /// <summary>Shows the drawer for this download (keeping the tab the user was on).</summary>
    public void OpenDetails(DownloadItemViewModel item)
    {
        var previous = Details;
        var tab = previous?.Tab ?? DetailsTab.Overview;
        if (previous != null) previous.Item.IsDetailsTarget = false;
        previous?.Dispose();

        item.IsDetailsTarget = true;
        Details = new DownloadDetailsViewModel(item, _manager, _toasts) { Tab = tab };
    }

    [RelayCommand]
    public void CloseDetails()
    {
        var previous = Details;
        Details = null;
        if (previous != null) previous.Item.IsDetailsTarget = false;
        previous?.Dispose();
    }

    /// <summary>Alt+Enter: open the drawer for the selected download, or close it if it is open.</summary>
    public void ToggleDetails()
    {
        if (Details != null) CloseDetails();
        else if (_selection.Count > 0) OpenDetails(_selection[0]);
    }

    void ApplyPending()
    {
        if (_pending.IsEmpty) return;

        foreach (var (id, snapshot) in _pending.ToArray())
        {
            if (!_byId.TryGetValue(id, out var item)) continue; // card not created yet: keep for the next tick
            _pending.TryRemove(new KeyValuePair<Guid, JobSnapshot>(id, snapshot));
            item.Update(snapshot);
        }
    }

    void OnCompleted(DownloadJob job) =>
        _toasts.Show($"{job.FileName} finished", ToastKind.Success, "Open", () => OpenPath(job.TargetPath));

    // ------------------------------------------------------------------------------------------------------------
    // Selection-aware actions
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Called by the view whenever the list selection changes.</summary>
    public void SetSelection(IEnumerable<DownloadItemViewModel> selected)
    {
        _selection = selected.ToList();

        // an open drawer shows whatever single download is selected now
        if (Details != null && _selection.Count == 1 && !ReferenceEquals(_selection[0], Details.Item)) OpenDetails(_selection[0]);
    }

    /// <summary>The cards an action applies to: the whole selection if the clicked card is in it, else just that card.</summary>
    List<DownloadItemViewModel> Targets(DownloadItemViewModel clicked) =>
        _selection.Contains(clicked) ? _selection.ToList() : new List<DownloadItemViewModel> { clicked };

    public void RunPrimary(DownloadItemViewModel item)
    {
        switch (item.State)
        {
            case DownloadState.Completed:
                OpenFile(item);
                break;
            case DownloadState.Paused or DownloadState.Failed:
                ResumeSelectionOf(item);
                break;
            default:
                if (item.IsRecording) FinishRecording(item);
                else PauseSelectionOf(item);
                break;
        }
    }

    /// <summary>Ends a live recording and saves what it has so far.</summary>
    public void FinishRecording(DownloadItemViewModel item) => _manager.FinishRecording(item.Id);

    public void PauseSelectionOf(DownloadItemViewModel clicked)
    {
        foreach (var i in Targets(clicked).Where(i => i.CanPause)) _manager.Pause(i.Id);
    }

    public void ResumeSelectionOf(DownloadItemViewModel clicked)
    {
        foreach (var i in Targets(clicked).Where(i => i.CanResume)) _manager.Start(i.Id);
    }

    /// <summary>Space on the list: pause what is running, resume what is paused.</summary>
    public void ToggleSelection()
    {
        foreach (var i in _selection)
        {
            if (i.CanPause) _manager.Pause(i.Id);
            else if (i.CanResume) _manager.Start(i.Id);
        }
    }

    public void OpenFile(DownloadItemViewModel item)
    {
        if (item.State != DownloadState.Completed) return;
        if (!File.Exists(item.Job.TargetPath))
        {
            _toasts.Show("That file is no longer where it was saved.", ToastKind.Error);
            return;
        }

        OpenPath(item.Job.TargetPath);
    }

    public void OpenSelected()
    {
        foreach (var i in _selection) OpenFile(i);
    }

    /// <summary>"Open with…": Windows' own program chooser for the finished file.</summary>
    public void OpenWith(DownloadItemViewModel item)
    {
        if (item.State != DownloadState.Completed) return;
        if (!File.Exists(item.Job.TargetPath))
        {
            _toasts.Show("That file is no longer where it was saved.", ToastKind.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.Job.TargetPath) { UseShellExecute = true, Verb = "openas" });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // the user closed the chooser: nothing to report
        }
        catch (Exception ex)
        {
            _toasts.Show($"Could not show the program list: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>IDM's "Refresh download address": look for the same file on the page the link came from.</summary>
    public async Task RefreshAddressAsync(DownloadItemViewModel item)
    {
        if (!item.CanRefreshAddress) return;
        _toasts.Show("Looking for a fresh link on the page…", ToastKind.Info);

        try
        {
            var result = await _manager.RefreshFromOwnerPageAsync(item.Id);
            if (result.Outcome == RefreshOutcome.Found)
            {
                _toasts.Show("Found the new address - resuming.", ToastKind.Success);
                return;
            }

            // The page does not hand out a usable link on its own (a script makes it, or the session ended). IDM's other
            // route: the user starts the download again in the browser and the extension delivers the new address.
            var connected = _bridge?.IsBrowserConnected == true;
            if (_bridge != null) _bridge.ExpectRefresh(item.Id);
            OpenOwnerPage(item);

            var reason = result.Outcome == RefreshOutcome.Unchanged
                ? "The page still shows the same address; your sign-in may have expired."
                : result.Message;
            _toasts.Show(connected
                    ? $"{reason} Start the download again on the page that just opened and ShadeNet will pick up the new link."
                    : $"{reason} Start the download again on the page that just opened. (The browser extension is not connected: install it in Settings so the new link can be picked up automatically.)",
                ToastKind.Info);
        }
        catch (Exception ex)
        {
            _toasts.Show($"Could not refresh the address: {ex.Message}", ToastKind.Error);
        }
    }

    public void OpenOwnerPage(DownloadItemViewModel item)
    {
        if (!item.HasOwnerPage) return;
        try
        {
            Process.Start(new ProcessStartInfo(item.Job.Referer!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toasts.Show($"Could not open the page: {ex.Message}", ToastKind.Error);
        }
    }

    public void ShowInFolder(DownloadItemViewModel item)
    {
        var path = item.State == DownloadState.Completed ? item.Job.TargetPath : item.Job.PartPath;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(item.Job.SaveDirectory))
                Process.Start(new ProcessStartInfo(item.Job.SaveDirectory) { UseShellExecute = true });
            else
                _toasts.Show("The save folder does not exist any more.", ToastKind.Error);
        }
        catch (Exception ex)
        {
            _toasts.Show($"Could not open the folder: {ex.Message}", ToastKind.Error);
        }
    }

    public void CopyLinks(DownloadItemViewModel clicked)
    {
        var urls = string.Join(Environment.NewLine, Targets(clicked).Select(i => i.Job.Url));
        try
        {
            Clipboard.SetText(urls);
            _toasts.Show(Targets(clicked).Count == 1 ? "Link copied" : "Links copied", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _toasts.Show($"Could not copy: {ex.Message}", ToastKind.Error);
        }
    }

    static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // no associated program: Explorer already shows its own message
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Removal with undo
    // ------------------------------------------------------------------------------------------------------------

    public void RemoveSelectionOf(DownloadItemViewModel clicked, bool deleteFiles) =>
        RemoveWithUndo(Targets(clicked), deleteFiles);

    /// <summary>Delete key.</summary>
    public void RemoveSelected() => RemoveWithUndo(_selection.ToList(), deleteFiles: false);

    [RelayCommand]
    void ClearCompleted() =>
        RemoveWithUndo(Items.Where(i => i.State == DownloadState.Completed && !i.IsPendingRemoval).ToList(), deleteFiles: false);

    /// <summary>
    /// Cards disappear at once; the engine only forgets them (and deletes files) after a few seconds, so "Undo" is
    /// simply "show them again". Running downloads are paused while they wait.
    /// </summary>
    void RemoveWithUndo(IReadOnlyList<DownloadItemViewModel> items, bool deleteFiles)
    {
        if (items.Count == 0) return;

        foreach (var i in items)
        {
            i.IsPendingRemoval = true;
            if (Details != null && ReferenceEquals(Details.Item, i)) CloseDetails();
            _hidden.Add(i.Id);
            if (i.CanPause) _manager.Pause(i.Id);
        }

        View.Refresh();
        UpdateTexts();
        PendingRemovalChanged?.Invoke();

        var cancelled = false;
        var what = items.Count == 1 ? $"“{items[0].FileName}”" : $"{items.Count} downloads";
        var verb = deleteFiles ? "Deleted" : "Removed";

        _toasts.Show($"{verb} {what}", ToastKind.Info, "Undo", () =>
        {
            cancelled = true;
            foreach (var i in items)
            {
                i.IsPendingRemoval = false;
                _hidden.Remove(i.Id);
            }

            View.Refresh();
            UpdateTexts();
            PendingRemovalChanged?.Invoke();
        }, UndoWindow);

        var timer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = UndoWindow + TimeSpan.FromMilliseconds(300) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (cancelled) return;
            foreach (var i in items) await _manager.RemoveAsync(i.Id, deleteFiles);
        };
        timer.Start();
    }
}
