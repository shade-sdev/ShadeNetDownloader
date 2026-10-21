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

    public void Dispose() => Item.Updated -= Refresh;

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

    partial void OnTabChanged(DetailsTab value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsConnections));
        OnPropertyChanged(nameof(IsProperties));
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
        HasBlocks = item.TotalBytes is > 0 && summary.Total > 0;
        BlocksText = summary.Total == 0
            ? ""
            : item.State == DownloadState.Completed
                ? $"all {summary.Total} parts finished"
                : $"{summary.Done} of {summary.Total} parts finished";
        BlocksCaption = item.AcceptsRanges || summary.Total > 1
            ? "Each square is a slice of the file. Rings show where a connection is writing."
            : "This server sends the file in one piece, so there is a single connection.";

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
