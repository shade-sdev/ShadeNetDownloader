using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShadeNet.Core;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Util;

namespace ShadeNetDownloader.ViewModels;

public enum DetailsTab
{
    Overview,
    Connections,
    Properties,

    /// <summary>A torrent's files: how far each is, and which to download.</summary>
    Files,

    /// <summary>A torrent's trackers (and the DHT).</summary>
    Trackers,
}

/// <summary>One peer of a torrent, as the Peers tab lists it.</summary>
public sealed partial class TorrentPeerRow : ObservableObject
{
    public TorrentPeerRow(string address) => Address = address;

    public string Address { get; }
    [ObservableProperty] string _client = "";
    [ObservableProperty] double _progress;
    [ObservableProperty] string _progressText = "";
    [ObservableProperty] string _speedText = "";
    [ObservableProperty] string _flagsText = "";

    public void Apply(ShadeNet.Core.Torrents.TorrentPeer p)
    {
        Client = p.Client;
        Progress = p.Progress;
        ProgressText = p.IsSeeder ? "has all of it" : $"has {p.Progress:P0}";
        SpeedText = $"↓ {ByteFormat.Speed(p.DownloadSpeed)}   ↑ {ByteFormat.Speed(p.UploadSpeed)}";
        FlagsText = string.Join("  ·  ", new[] { p.Incoming ? "connected to you" : "you connected", p.Encrypted ? "encrypted" : "not encrypted" });
    }
}

/// <summary>One file of a torrent, as the Files tab lists it: how far it is, and its priority (which the user can change).</summary>
public sealed partial class TorrentFileRow : ObservableObject
{
    readonly Action<TorrentFileRow> _changed;
    bool _applying;

    public TorrentFileRow(int index, string path, long length, Action<TorrentFileRow> changed)
    {
        Index = index;
        Path = path;
        SizeText = ByteFormat.Size(length);
        _changed = changed;
    }

    /// <summary>The choices of the priority list.</summary>
    public static IReadOnlyList<ShadeNet.Core.Torrents.TorrentFilePriority> Choices { get; } =
        new[] { ShadeNet.Core.Torrents.TorrentFilePriority.High, ShadeNet.Core.Torrents.TorrentFilePriority.Normal, ShadeNet.Core.Torrents.TorrentFilePriority.Skip };

    public int Index { get; }
    public string Path { get; }
    public string SizeText { get; }
    [ObservableProperty] double _progress;
    [ObservableProperty] string _progressText = "";
    [ObservableProperty] ShadeNet.Core.Torrents.TorrentFilePriority _priority = ShadeNet.Core.Torrents.TorrentFilePriority.Normal;

    partial void OnPriorityChanged(ShadeNet.Core.Torrents.TorrentFilePriority value)
    {
        if (!_applying) _changed(this);
    }

    public void Apply(ShadeNet.Core.Torrents.TorrentFileProgress f)
    {
        _applying = true;
        try
        {
            Priority = f.Priority;
            Progress = f.Progress ?? 0;
            ProgressText = f.Priority == ShadeNet.Core.Torrents.TorrentFilePriority.Skip ? "skipped" : f.Progress is { } p ? $"{p:P0}" : "";
        }
        finally
        {
            _applying = false;
        }
    }
}

/// <summary>One tracker of a torrent (or the DHT), as the Trackers tab lists it.</summary>
public sealed partial class TorrentTrackerRow : ObservableObject
{
    public TorrentTrackerRow(string address) => Address = address;

    public string Address { get; }
    [ObservableProperty] string _status = "";
    [ObservableProperty] string? _message;
    [ObservableProperty] string _tierText = "";

    /// <summary>"120 sharing all of it  ·  30 downloading  ·  5,000 finished", as this tracker last said (null when it did not).</summary>
    [ObservableProperty] string? _countsText;

    /// <summary>A tracker can be taken off; the DHT row cannot.</summary>
    public bool CanRemove => Address != "DHT";

    public void Apply(ShadeNet.Core.Torrents.TorrentTracker t)
    {
        Status = t.Status;
        Message = t.Message;
        TierText = t.Tier > 0 ? $"tier {t.Tier}" : "";
        CountsText = DownloadDetailsViewModel.CountsText(t.Seeds, t.Leechers, t.Downloaded);
    }
}

/// <summary>One line of the Properties tab. <see cref="CanCopy"/> adds a copy button.</summary>
public sealed record PropertyRow(string Label, string Value, bool CanCopy = false);

/// <summary>One working connection: where in the file it is, how far it got, how fast it goes.</summary>
public sealed partial class ConnectionRowViewModel : ObservableObject
{
    public ConnectionRowViewModel(int id)
    {
        Id = id;
        Name = $"Connection {id + 1}";
        Brush = ConnectionPalette.BrushFor(id);
    }

    public int Id { get; }
    public string Name { get; }
    public Brush Brush { get; }

    [ObservableProperty] string _rangeText = "";
    [ObservableProperty] string _speedText = "";
    [ObservableProperty] string _doneText = "";
    [ObservableProperty] double _fraction;

    public void Apply(WorkingSegment segment)
    {
        RangeText = $"{ByteFormat.Size(segment.Start)} → {ByteFormat.Size(segment.End + 1)}";
        SpeedText = segment.Speed > 0 ? ByteFormat.Speed(segment.Speed) : "starting…";
        DoneText = $"{ByteFormat.Size(segment.Downloaded)} of {ByteFormat.Size(segment.Length)}";
        Fraction = segment.Fraction;
    }
}

/// <summary>
/// The side drawer for one download: live numbers, the speed graph, the block map, the working connections, and every
/// fact about the download. Refreshed once per engine snapshot (the card raises <see cref="DownloadItemViewModel.Updated"/>).
/// </summary>
public sealed partial class DownloadDetailsViewModel : ObservableObject, IDisposable
{
    readonly DownloadManager _manager;
    readonly ToastService _toasts;
    DownloadState? _propertiesBuiltFor;
    bool _loading;

    public DownloadDetailsViewModel(DownloadItemViewModel item, DownloadManager manager, ToastService toasts)
    {
        Item = item;
        _manager = manager;
        _toasts = toasts;

        _loading = true;
        LimitText = item.Job.SpeedLimit is { } limit ? (limit / 1024).ToString() : "";
        ConnectionLimit = Math.Clamp(item.Job.MaxConnections, 1, 32);
        _loading = false;

        item.Updated += Refresh;
        Refresh();
    }

    public DownloadItemViewModel Item { get; }

    public void Dispose()
    {
        _disposed = true;
        Item.Updated -= Refresh;
    }

    bool _disposed;

    // ---- tabs ----

    [ObservableProperty] DetailsTab _tab = DetailsTab.Overview;

    public bool IsOverview
    {
        get => Tab == DetailsTab.Overview;
        set { if (value) Tab = DetailsTab.Overview; }
    }

    public bool IsConnections
    {
        get => Tab == DetailsTab.Connections;
        set { if (value) Tab = DetailsTab.Connections; }
    }

    public bool IsProperties
    {
        get => Tab == DetailsTab.Properties;
        set { if (value) Tab = DetailsTab.Properties; }
    }

    public bool IsFiles
    {
        get => Tab == DetailsTab.Files;
        set { if (value) Tab = DetailsTab.Files; }
    }

    public bool IsTrackers
    {
        get => Tab == DetailsTab.Trackers;
        set { if (value) Tab = DetailsTab.Trackers; }
    }

    partial void OnTabChanged(DetailsTab value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsConnections));
        OnPropertyChanged(nameof(IsProperties));
        OnPropertyChanged(nameof(IsFiles));
        OnPropertyChanged(nameof(IsTrackers));
        if (IsTorrent) MaybeRefreshTorrent(force: true);
    }

    // ---- a torrent ----

    public bool IsTorrent => Item.IsTorrent;

    public string MapTitle => IsTorrent ? "Pieces" : "File map";

    /// <summary>The second tab: a torrent's peers, a web download's connections.</summary>
    public string ConnectionsTabTitle => IsTorrent ? "Peers" : "Connections";

    public ObservableCollection<TorrentPeerRow> Peers { get; } = new();
    public ObservableCollection<TorrentFileRow> TorrentFiles { get; } = new();
    public ObservableCollection<TorrentTrackerRow> Trackers { get; } = new();

    [ObservableProperty] bool _hasPeers;
    [ObservableProperty] string _filesSummary = "";
    [ObservableProperty] bool _hasTorrentFiles;
    [ObservableProperty] string _trackersSummary = "";

    /// <summary>The whole swarm as the trackers count it ("The trackers count 120 sharing all of it and 30 downloading."), null when none said.</summary>
    [ObservableProperty] string? _swarmText;

    /// <summary>A private torrent: its tracker alone decides who may join, so no tracker or peer can be added.</summary>
    [ObservableProperty] bool _isPrivateTorrent;

    /// <summary>Running now (peers can be added, and asked for).</summary>
    [ObservableProperty] bool _torrentRunning;

    /// <summary>The Add box of the Peers tab ("address:port").</summary>
    [ObservableProperty] string _newPeer = "";

    /// <summary>The Add box of the Trackers tab (one address per line).</summary>
    [ObservableProperty] string _newTrackers = "";

    /// <summary>"120 sharing all of it  ·  30 downloading  ·  5,000 finished"; null when nothing is known.</summary>
    internal static string? CountsText(int? seeds, int? leechers, int? downloaded)
    {
        if (seeds == null && leechers == null) return null;
        var parts = new List<string> { $"{seeds ?? 0:N0} sharing all of it", $"{leechers ?? 0:N0} downloading" };
        if (downloaded is > 0) parts.Add($"{downloaded:N0} finished");
        return string.Join("  ·  ", parts);
    }

    [RelayCommand]
    async Task AddPeer()
    {
        var text = NewPeer.Trim();
        if (text.Length == 0) return;
        try
        {
            await _manager.AddTorrentPeerAsync(Item.Id, text);
            NewPeer = "";
            _toasts.Show($"Connecting to {text}…", ToastKind.Info);
            MaybeRefreshTorrent(force: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    /// <summary>Bans a peer's address from every torrent (Settings > Torrents lists the banned ones).</summary>
    [RelayCommand]
    async Task BanPeer(TorrentPeerRow? row)
    {
        if (row == null) return;
        try
        {
            await _manager.BanTorrentPeerAsync(row.Address);
            _toasts.Show($"Banned {HostOf(row.Address)}: it is disconnected, and not connected to again. Settings > Torrents can unban it.", ToastKind.Info);
            MaybeRefreshTorrent(force: true);
        }
        catch (ArgumentException ex)
        {
            _toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    static string HostOf(string address) => address.StartsWith('[') ? address[..(address.IndexOf(']') + 1)] : address.Split(':')[0];

    [RelayCommand]
    async Task AddTrackers()
    {
        if (string.IsNullOrWhiteSpace(NewTrackers)) return;
        try
        {
            var added = await _manager.AddTorrentTrackersAsync(Item.Id, NewTrackers);
            NewTrackers = "";
            _toasts.Show(added == 0 ? "The torrent has those trackers already." : added == 1 ? "Tracker added." : $"{added} trackers added.", ToastKind.Info);
            MaybeRefreshTorrent(force: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    async Task RemoveTracker(TorrentTrackerRow? row)
    {
        if (row is not { CanRemove: true }) return;
        try
        {
            await _manager.RemoveTorrentTrackerAsync(Item.Id, row.Address);
            Trackers.Remove(row);
            MaybeRefreshTorrent(force: true);
        }
        catch (InvalidOperationException ex)
        {
            _toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    /// <summary>"Ask for peers now": every tracker, the DHT and the local network at once.</summary>
    [RelayCommand]
    void AskForPeers()
    {
        try
        {
            _toasts.Show(_manager.ReannounceTorrent(Item.Id)
                ? "Asking the trackers and the DHT for peers…"
                : "It is not running: peers are asked for when it starts.", ToastKind.Info);
        }
        catch (InvalidOperationException ex)
        {
            _toasts.Show(ex.Message, ToastKind.Error);
        }

        MaybeRefreshTorrent(force: true);
    }

    [RelayCommand]
    Task CheckFilesAgain() => CheckTorrentFilesAgainAsync(_manager, _toasts, Item);

    /// <summary>"Check the files again" (the drawer and the card's menu), and what the user is told.</summary>
    internal static async Task CheckTorrentFilesAgainAsync(DownloadManager manager, ToastService toasts, DownloadItemViewModel item)
    {
        try
        {
            var result = await manager.RecheckTorrentAsync(item.Id);
            toasts.Show(result switch
            {
                ShadeNet.Core.Torrents.TorrentRecheck.Checking or ShadeNet.Core.Torrents.TorrentRecheck.Restarted =>
                    $"Checking {item.FileName}: whatever is not right is downloaded again.",
                _ => $"{item.FileName} is checked when it next starts.",
            }, ToastKind.Info);
        }
        catch (InvalidOperationException ex)
        {
            toasts.Show(ex.Message, ToastKind.Error);
        }
    }

    /// <summary>Fetch the pieces in order (a film can be watched from its start while the rest arrives).</summary>
    [ObservableProperty] bool _sequential;

    // the torrent's tiles on the Overview
    [ObservableProperty] string _seedsText = "0";
    [ObservableProperty] string _sharedText = "-";
    [ObservableProperty] string _ratioText = "-";
    [ObservableProperty] string _piecesText = "-";

    /// <summary>The picture of the file: a web download's parts, a torrent's pieces (as runs of pieces that are in or not).</summary>
    [ObservableProperty] IReadOnlyList<SegmentInfo> _mapSegments = Array.Empty<SegmentInfo>();
    [ObservableProperty] long _mapTotal;

    bool _applyingTorrent;
    bool _torrentBusy;
    long _lastTorrentRefresh;   // (0: the first one is due at once)

    partial void OnSequentialChanged(bool value)
    {
        if (_applyingTorrent || !IsTorrent) return;
        _ = SetSequentialAsync(value);
    }

    async Task SetSequentialAsync(bool value)
    {
        try
        {
            await _manager.SetTorrentSequentialAsync(Item.Id, value);
            _toasts.Show(value ? "Pieces are now fetched in order, from the start." : "Pieces are now fetched the usual way.", ToastKind.Info);
        }
        catch (Exception ex)
        {
            _toasts.Show($"Could not change the order: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>At most once a second (a running torrent publishes about four times a second), or at once when asked.</summary>
    void MaybeRefreshTorrent(bool force = false)
    {
        if (!IsTorrent || _torrentBusy || _disposed) return;
        var now = Environment.TickCount64;
        if (!force && _lastTorrentRefresh != 0 && now - _lastTorrentRefresh < 1000) return;
        _lastTorrentRefresh = now;
        _ = RefreshTorrentAsync();
    }

    async Task RefreshTorrentAsync()
    {
        _torrentBusy = true;
        try
        {
            var details = await _manager.GetTorrentDetailsAsync(Item.Id);
            if (!_disposed) ApplyTorrent(details);
        }
        catch (Exception)
        {
            // it was removed, or stopped in between: the next refresh shows what is there
        }
        finally
        {
            _torrentBusy = false;
        }
    }

    internal void ApplyTorrent(ShadeNet.Core.Torrents.TorrentDetails d)
    {
        _applyingTorrent = true;
        try
        {
            Sequential = d.Sequential;

            // peers
            for (var i = Peers.Count - 1; i >= 0; i--)
                if (d.Peers.All(p => p.Address != Peers[i].Address)) Peers.RemoveAt(i);
            foreach (var p in d.Peers.OrderByDescending(p => p.DownloadSpeed + p.UploadSpeed))
            {
                var row = Peers.FirstOrDefault(r => r.Address == p.Address);
                if (row == null) Peers.Add(row = new TorrentPeerRow(p.Address));
                row.Apply(p);
            }

            HasPeers = Peers.Count > 0;
            var seeds = d.Peers.Count(p => p.IsSeeder);
            SeedsText = seeds.ToString();

            // files
            if (TorrentFiles.Count != d.Files.Count || TorrentFiles.Zip(d.Files).Any(x => x.First.Index != x.Second.Index))
            {
                TorrentFiles.Clear();
                foreach (var f in d.Files) TorrentFiles.Add(new TorrentFileRow(f.Index, f.Path, f.Length, OnFilePriorityChanged));
            }

            for (var i = 0; i < d.Files.Count; i++) TorrentFiles[i].Apply(d.Files[i]);
            HasTorrentFiles = TorrentFiles.Count > 0;
            var picked = d.Files.Where(f => f.Priority != ShadeNet.Core.Torrents.TorrentFilePriority.Skip).ToList();
            FilesSummary = TorrentFiles.Count == 0
                ? "The list of files comes from the people sharing the torrent; it shows here once it has arrived."
                : $"{picked.Count} of {d.Files.Count} files chosen  ·  {ByteFormat.Size(picked.Sum(f => f.Length))}. Change a file's priority to skip it or fetch it first"
                  + (Item.State == DownloadState.Completed ? "; a file added now is downloaded." : ".");

            // trackers
            for (var i = Trackers.Count - 1; i >= 0; i--)
                if (d.Trackers.All(t => t.Address != Trackers[i].Address)) Trackers.RemoveAt(i);
            foreach (var t in d.Trackers)
            {
                var row = Trackers.FirstOrDefault(r => r.Address == t.Address);
                if (row == null) Trackers.Add(row = new TorrentTrackerRow(t.Address));
                row.Apply(t);
            }

            // in the engine's order (trackers by tier, the DHT last)
            for (var i = 0; i < d.Trackers.Count; i++)
            {
                var at = IndexOf(Trackers, d.Trackers[i].Address);
                if (at != i && at >= 0) Trackers.Move(at, i);
            }

            var trackers = d.Trackers.Count(t => t.Tier > 0);
            TrackersSummary = (!d.Running
                                  ? "The torrent is not running: its trackers are asked for peers when it does."
                                  : trackers == 0
                                      ? "This torrent has no tracker: its peers come from the DHT and from other peers."
                                      : $"{trackers} tracker{(trackers == 1 ? "" : "s")}, all asked at once.")
                              + (d.IsPrivate ? " It is private: only its own tracker may be used." : "");
            IsPrivateTorrent = d.IsPrivate;
            TorrentRunning = d.Running;
            var swarm = CountsText(d.SwarmSeeds, d.SwarmLeechers, null);
            SwarmText = swarm == null ? null : $"The whole swarm, as the trackers count it: {swarm}.";

            // the piece map
            var have = d.Pieces.Count(p => p);
            PiecesText = d.Pieces.Count > 0 ? $"{have} of {d.Pieces.Count}" : "-";
            MapSegments = PieceRuns(d.Pieces, d.PieceLength, d.Size);
            MapTotal = d.Size;
            HasBlocks = d.Pieces.Count > 0 && d.Size > 0;
            BlocksText = d.Pieces.Count > 0 ? $"{have} of {d.Pieces.Count} pieces" : "";
            BlocksCaption = $"The whole torrent, piece by piece ({ByteFormat.Size(d.PieceLength)} each), the skipped files too. A piece counts once it has passed its check.";

            var data = Item.Job.Torrent;
            SharedText = data is { Uploaded: > 0 } ? ByteFormat.Size(data.Uploaded) : "-";
            RatioText = data != null ? $"{data.Ratio:0.00}" : "-";
        }
        finally
        {
            _applyingTorrent = false;
        }
    }

    static int IndexOf(ObservableCollection<TorrentTrackerRow> rows, string address)
    {
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Address == address) return i;
        return -1;
    }

    /// <summary>A torrent's pieces as runs for the map: each run of pieces that are in is one finished part, each gap one waiting part.</summary>
    internal static IReadOnlyList<SegmentInfo> PieceRuns(IReadOnlyList<bool> pieces, int pieceLength, long size)
    {
        var runs = new List<SegmentInfo>();
        if (pieces.Count == 0 || pieceLength <= 0 || size <= 0) return runs;
        var start = 0;
        for (var i = 1; i <= pieces.Count; i++)
        {
            if (i < pieces.Count && pieces[i] == pieces[start]) continue;
            var from = (long)start * pieceLength;
            var to = Math.Min(size, (long)i * pieceLength) - 1;
            if (to >= from)
                runs.Add(new SegmentInfo(from, to, pieces[start] ? to - from + 1 : 0, pieces[start] ? SegmentState.Done : SegmentState.Pending));
            start = i;
        }

        return runs;
    }

    void OnFilePriorityChanged(TorrentFileRow row)
    {
        if (_applyingTorrent) return;
        _ = SetFilesAsync();
    }

    async Task SetFilesAsync()
    {
        var priorities = TorrentFiles.OrderBy(f => f.Index).Select(f => f.Priority).ToList();
        try
        {
            await _manager.SetTorrentFilesAsync(Item.Id, priorities);
        }
        catch (ArgumentException)
        {
            _toasts.Show("A torrent needs at least one file to download.", ToastKind.Error);
        }
        catch (Exception ex)
        {
            _toasts.Show($"Could not change the files: {ex.Message}", ToastKind.Error);
        }

        _propertiesBuiltFor = null;   // the Properties tab counts the files
        MaybeRefreshTorrent(force: true);
    }

    // ---- overview ----

    [ObservableProperty] string _headline = "";
    [ObservableProperty] string _subHeadline = "";
    [ObservableProperty] string _etaText = "-";
    [ObservableProperty] string _progressText = "";
    [ObservableProperty] string _averageText = "-";
    [ObservableProperty] string _peakText = "-";
    [ObservableProperty] string _blocksText = "";
    [ObservableProperty] string _connectionsText = "";
    [ObservableProperty] bool _hasSamples;
    [ObservableProperty] bool _hasBlocks;
    [ObservableProperty] string _blocksCaption = "";

    // ---- connections ----

    public ObservableCollection<ConnectionRowViewModel> Connections { get; } = new();
    [ObservableProperty] string _connectionsSummary = "";
    [ObservableProperty] bool _hasConnections;

    // ---- properties ----

    [ObservableProperty] IReadOnlyList<PropertyRow> _properties = Array.Empty<PropertyRow>();

    // ---- things the user can change while looking ----

    [ObservableProperty] string _limitText = "";
    [ObservableProperty] int _connectionLimit = 8;
    [ObservableProperty] string _limitStatus = "";

    public bool CanEdit => Item.State != DownloadState.Completed;

    partial void OnConnectionLimitChanged(int value)
    {
        if (_loading || !CanEdit) return;
        _manager.SetMaxConnections(Item.Id, value);
        OnPropertyChanged(nameof(ConnectionLimitHint));
    }

    /// <summary>The engine reads the connection limit when a download (re)starts, so a running one keeps its current number.</summary>
    public string ConnectionLimitHint => Item.IsBusy ? "Takes effect the next time this download starts." : "Used the next time it starts.";

    [RelayCommand]
    void ApplyLimit()
    {
        var text = (LimitText ?? "").Trim();
        long? bytesPerSecond = null;

        if (text.Length > 0)
        {
            if (!long.TryParse(text, out var kb) || kb < 0 || kb > 10_000_000)
            {
                LimitStatus = "Enter a number of KB per second, or leave it empty for no limit.";
                return;
            }

            if (kb > 0) bytesPerSecond = kb * 1024;
        }

        _manager.SetJobSpeedLimit(Item.Id, bytesPerSecond);
        LimitText = bytesPerSecond is { } v ? (v / 1024).ToString() : "";
        LimitStatus = bytesPerSecond is { } limit ? $"Limited to {ByteFormat.Speed(limit)}." : "No limit.";
        _propertiesBuiltFor = null;   // the Properties tab shows the limit
        Refresh();
    }

    [RelayCommand]
    void CopyValue(PropertyRow? row)
    {
        if (row is not { CanCopy: true }) return;
        try
        {
            Clipboard.SetText(row.Value);
            _toasts.Show($"{row.Label} copied.", ToastKind.Success);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _toasts.Show("The clipboard is in use by another program. Try again.", ToastKind.Error);
        }
    }

    // ---- refresh ----

    void Refresh()
    {
        var item = Item;
        var downloading = item.State == DownloadState.Downloading;

        var samples = item.SpeedSamples;
        var running = samples.Where(x => x > 0).ToArray();

        Headline = item.State switch
        {
            DownloadState.Downloading => ByteFormat.Speed(item.Speed),
            DownloadState.Probing => "Connecting",
            DownloadState.Queued => "Waiting",
            DownloadState.Paused or DownloadState.Stopping => "Paused",
            DownloadState.Completed => "Finished",
            DownloadState.Failed => "Stopped",
            _ => "",
        };
        SubHeadline = item.State switch
        {
            DownloadState.Downloading => "current speed",
            DownloadState.Completed => item.Job.CompletedAt is { } done ? $"finished {done.LocalDateTime:g}" : "",
            DownloadState.Failed => item.DetailText,
            _ => "",
        };

        EtaText = downloading && item.Eta != null ? ByteFormat.Eta(item.Eta) : "-";
        ProgressText = item.TotalBytes is { } total
            ? $"{ByteFormat.Size(item.DownloadedBytes)} of {ByteFormat.Size(total)}"
            : ByteFormat.Size(item.DownloadedBytes);
        AverageText = running.Length > 0 ? ByteFormat.Speed(running.Average()) : "-";
        PeakText = item.PeakSpeed > 0 ? ByteFormat.Speed(item.PeakSpeed) : "-";
        HasSamples = samples.Length > 1;

        var summary = SegmentSummary.Of(item.Segments);
        if (IsTorrent)
        {
            MaybeRefreshTorrent();   // the map, peers, files and trackers come from the torrent engine
        }
        else
        {
            MapSegments = item.Segments;
            MapTotal = item.TotalBytes ?? 0;
            HasBlocks = item.TotalBytes is > 0 && summary.Total > 0;
            BlocksText = summary.Total == 0
                ? ""
                : item.State == DownloadState.Completed
                    ? $"all {summary.Total} parts finished"
                    : $"{summary.Done} of {summary.Total} parts finished";
            BlocksCaption = item.AcceptsRanges || summary.Total > 1
                ? "Each square is a slice of the file. Rings show where a connection is writing."
                : "This server sends the file in one piece, so there is a single connection.";
        }

        ConnectionsText = $"{item.ConnectionCount} now · peak {item.PeakConnections}";
        UpdateConnections(summary);

        if (_propertiesBuiltFor != item.State)
        {
            _propertiesBuiltFor = item.State;
            Properties = BuildProperties();
            OnPropertyChanged(nameof(CanEdit));
        }

        OnPropertyChanged(nameof(ConnectionLimitHint));
    }

    void UpdateConnections(SegmentSummary summary)
    {
        var working = summary.Working;

        for (var i = Connections.Count - 1; i >= 0; i--)
            if (working.All(w => w.Connection != Connections[i].Id)) Connections.RemoveAt(i);

        foreach (var segment in working)
        {
            var row = Connections.FirstOrDefault(c => c.Id == segment.Connection);
            if (row == null)
            {
                row = new ConnectionRowViewModel(segment.Connection);
                var at = Connections.Count(c => c.Id < segment.Connection);   // keep the list ordered by connection
                Connections.Insert(at, row);
            }

            row.Apply(segment);
        }

        HasConnections = Connections.Count > 0;
        if (Item.IsTorrent)
        {
            // a torrent talks to peers, not to a server over byte ranges
            var peers = Item.ConnectionCount == 1 ? "1 peer" : $"{Item.ConnectionCount} peers";
            ConnectionsSummary = Item.State switch
            {
                DownloadState.Downloading => $"Connected to {peers} (at most {Item.PeakConnections} so far). Their speeds count the protocol's own messages too.",
                DownloadState.Completed when Item.IsSeeding => $"Sharing with {peers}.",
                DownloadState.Completed => "Nothing running: the torrent is finished and not being shared.",
                DownloadState.Paused or DownloadState.Stopping => "Paused. Peers are looked for again when you resume.",
                DownloadState.Failed => "Stopped by an error.",
                _ => "Looking for peers…",
            };
            return;
        }

        ConnectionsSummary = Item.State switch
        {
            DownloadState.Downloading when Connections.Count > 0 =>
                $"{Connections.Count} connection{(Connections.Count == 1 ? "" : "s")} working · {summary.Pending} part{(summary.Pending == 1 ? "" : "s")} waiting · {summary.Done} finished",
            DownloadState.Downloading => "Connecting…",
            DownloadState.Completed => "Nothing running: the download is finished.",
            DownloadState.Paused or DownloadState.Stopping => "Paused. Connections are opened again when you resume.",
            DownloadState.Failed => "Stopped by an error.",
            _ => "No connections yet.",
        };
    }

    IReadOnlyList<PropertyRow> BuildProperties()
    {
        var job = Item.Job;
        if (job.Kind == DownloadKind.Torrent && job.Torrent is { } torrent) return TorrentProperties(job, torrent);

        var rows = new List<PropertyRow> { new("Address", job.Url, CanCopy: true) };

        if (!string.IsNullOrEmpty(job.FinalUrl) && !string.Equals(job.FinalUrl, job.Url, StringComparison.Ordinal))
            rows.Add(new("Redirected to", job.FinalUrl, CanCopy: true));
        if (!string.IsNullOrEmpty(job.Referer)) rows.Add(new("Referring page", job.Referer, CanCopy: true));

        rows.Add(new("Saved to", Path.Combine(job.SaveDirectory, job.FileName), CanCopy: true));
        rows.Add(new("Category", job.Category));
        rows.Add(new("Added", job.CreatedAt.LocalDateTime.ToString("g")));
        if (job.CompletedAt is { } completed) rows.Add(new("Finished", completed.LocalDateTime.ToString("g")));

        if (job.Kind == DownloadKind.Hls && job.Hls is { } stream)
        {
            rows.Add(new("Kind", "Video stream (HLS), joined into one file when all pieces are in"));
            rows.Add(new("Quality", stream.Height > 0 ? $"{stream.Height}p" : "Best available"));
            rows.Add(new("Pieces", stream.Parts > 0 ? $"{stream.PartsDone} of {stream.Parts} downloaded" : "Not read yet"));
            rows.Add(new("Size", job.TotalSize is { } done ? $"{ByteFormat.Size(done)} ({done:N0} bytes)" : stream.EstimatedTotal is { } guess ? $"about {ByteFormat.Size(guess)} (an estimate)" : "Not known yet"));
            rows.Add(new("Pause and resume", "Supported: finished pieces are kept"));
        }
        else
        {
            rows.Add(new("Size", job.TotalSize is { } size ? $"{ByteFormat.Size(size)} ({size:N0} bytes)" : "Not announced by the server"));
            rows.Add(new("Pause and resume", job.Probed ? (job.AcceptsRanges ? "Supported" : "Not supported by this server") : "Not checked yet"));
        }

        if (!string.IsNullOrEmpty(job.ContentType)) rows.Add(new("Type", job.ContentType));
        if (!string.IsNullOrEmpty(job.ETag)) rows.Add(new("Version tag", job.ETag));

        rows.Add(new("Connections", $"up to {job.MaxConnections}"));
        rows.Add(new("Speed limit", job.SpeedLimit is { } limit ? ByteFormat.Speed(limit) : "None"));
        rows.Add(new("Browser session", !string.IsNullOrEmpty(job.Cookies) ? "Cookies are sent with this download" : "None"));
        return rows;
    }

    static IReadOnlyList<PropertyRow> TorrentProperties(DownloadJob job, ShadeNet.Core.Torrents.TorrentJobData torrent)
    {
        var rows = new List<PropertyRow>
        {
            new("Kind", torrent.IsFolder ? "Torrent, saved as a folder of files" : "Torrent"),
            new("Magnet link", torrent.Magnet, CanCopy: true),
        };
        if (!string.Equals(job.Url, torrent.Magnet, StringComparison.Ordinal)) rows.Add(new("Added from", job.Url, CanCopy: true));
        if (!string.IsNullOrEmpty(torrent.InfoHash)) rows.Add(new("Info hash", torrent.InfoHash, CanCopy: true));
        rows.Add(new("Saved to", Path.Combine(job.SaveDirectory, job.FileName), CanCopy: true));
        rows.Add(new("Category", job.Category));
        rows.Add(new("Added", job.CreatedAt.LocalDateTime.ToString("g")));
        if (job.CompletedAt is { } completed) rows.Add(new("Finished", completed.LocalDateTime.ToString("g")));

        if (torrent.Files is { } files)
        {
            var picked = files.Count(f => f.Selected);
            rows.Add(new("Files", picked == files.Count ? $"{files.Count}" : $"{picked} of {files.Count} (the others are skipped)"));
            rows.Add(new("Size", $"{ByteFormat.Size(torrent.WantedBytes)} ({torrent.WantedBytes:N0} bytes)"));
        }
        else
        {
            rows.Add(new("Files", "Not known yet: the people sharing it send the list"));
        }

        rows.Add(new("Shared with others", torrent.Uploaded > 0
            ? $"{ByteFormat.Size(torrent.Uploaded)} ({torrent.Ratio:0.00}× its size)"
            : "Nothing yet"));
        if (torrent.Seeding) rows.Add(new("Sharing", "On, until the limits in Settings > Torrents"));
        rows.Add(new("Pause and resume", "Supported: finished pieces are kept"));
        rows.Add(new("Speed limit", job.SpeedLimit is { } limit ? ByteFormat.Speed(limit) : "None"));
        return rows;
    }
}

/// <summary>A fixed set of colours so a connection looks the same on the block map and in the list.</summary>
public static class ConnectionPalette
{
    static readonly Color[] Colors =
    {
        Color.FromRgb(0x3B, 0x82, 0xF6),   // blue
        Color.FromRgb(0x14, 0xB8, 0xA6),   // teal
        Color.FromRgb(0xA8, 0x55, 0xF7),   // violet
        Color.FromRgb(0xF5, 0x9E, 0x0B),   // amber
        Color.FromRgb(0xEC, 0x48, 0x99),   // pink
        Color.FromRgb(0x22, 0xC5, 0x5E),   // green
        Color.FromRgb(0xF9, 0x73, 0x16),   // orange
        Color.FromRgb(0x06, 0xB6, 0xD4),   // cyan
    };

    static readonly Brush[] Brushes = Colors.Select(c =>
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return (Brush)b;
    }).ToArray();

    public static Color ColorFor(int connection) => Colors[Math.Abs(connection) % Colors.Length];
    public static Brush BrushFor(int connection) => Brushes[Math.Abs(connection) % Brushes.Length];
}
