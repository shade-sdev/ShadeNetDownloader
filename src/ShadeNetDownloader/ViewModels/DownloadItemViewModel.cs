using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Util;
using Wpf.Ui.Controls;

namespace ShadeNetDownloader.ViewModels;

/// <summary>One download card: everything the row shows, refreshed from engine snapshots.</summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    readonly DownloadsViewModel _owner;

    public DownloadItemViewModel(DownloadJob job, JobSnapshot snapshot, DownloadsViewModel owner)
    {
        Job = job;
        _owner = owner;
        Host = HostOf(job.Url);
        Update(snapshot);
    }

    public DownloadJob Job { get; }
    public Guid Id => Job.Id;

    /// <summary>A video stream (HLS): pieces instead of byte ranges, no connection map.</summary>
    public bool IsStream => Job.Kind == DownloadKind.Hls;
    public DateTimeOffset CreatedAt => Job.CreatedAt;

    /// <summary>When the card was added to the list, as a counter (the later, the higher): breaks ties when sorting.</summary>
    internal long Sequence { get; set; }
    public string Host { get; }
    public string Url => Job.Url;
    public ImageSource? Icon => FileIconCache.Get(FileName);

    [ObservableProperty] string _fileName = "";
    [ObservableProperty] string _category = "";
    [ObservableProperty] DownloadState _state;
    [ObservableProperty] double _fraction;
    [ObservableProperty] bool _isIndeterminate;
    [ObservableProperty] string _statusText = "";
    [ObservableProperty] string _detailText = "";
    [ObservableProperty] string _percentText = "";
    [ObservableProperty] string _hintText = "";
    [ObservableProperty] SymbolRegular _primaryIcon = SymbolRegular.Pause24;
    [ObservableProperty] string _primaryTooltip = "";
    [ObservableProperty] bool _hasPrimaryAction;
    [ObservableProperty] bool _isCompleted;
    [ObservableProperty] double _speed;
    [ObservableProperty] bool _isPendingRemoval;

    /// <summary>A live stream that is being recorded: it has no end of its own, the user ends it.</summary>
    [ObservableProperty] bool _isLive;

    public bool IsRecording => IsLive && State == DownloadState.Downloading;

    partial void OnIsLiveChanged(bool value) => OnPropertyChanged(nameof(IsRecording));

    /// <summary>Just added to the list: the card slides in (for a moment, then false).</summary>
    [ObservableProperty] bool _isNew;

    /// <summary>Just finished: its label pops (for a moment, then false).</summary>
    [ObservableProperty] bool _justCompleted;

    /// <summary>This card's details are open in the drawer (the card is marked in the list).</summary>
    [ObservableProperty] bool _isDetailsTarget;

    /// <summary>Place in the queue: a higher number starts earlier (set by the engine when the queue is reordered).</summary>
    [ObservableProperty] int _priority;

    // ---- what the details drawer shows (cards do not bind these, so they cost nothing in the list) ----

    [ObservableProperty] IReadOnlyList<SegmentInfo> _segments = Array.Empty<SegmentInfo>();
    [ObservableProperty] long? _totalBytes;
    [ObservableProperty] long _downloadedBytes;
    [ObservableProperty] TimeSpan? _eta;
    [ObservableProperty] int _connectionCount;
    [ObservableProperty] int _splits;
    [ObservableProperty] int _takeovers;
    [ObservableProperty] int _retries;
    [ObservableProperty] int _fallbacks;
    [ObservableProperty] int _peakConnections;
    [ObservableProperty] bool _acceptsRanges;

    /// <summary>The last two minutes of speed, one sample per second, oldest first; replaced (not changed) on each new sample.</summary>
    [ObservableProperty] double[] _speedSamples = Array.Empty<double>();

    readonly SpeedHistory _history = new();

    /// <summary>Time source for the history; tests replace it.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>Raised once at the end of every <see cref="Update"/>, after all properties are set.</summary>
    public event Action? Updated;

    [ObservableProperty] JobErrorKind _errorKind;

    /// <summary>The page this link came from is known, so it can be searched for a fresh address or opened.</summary>
    public bool HasOwnerPage => Uri.TryCreate(Job.Referer, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    /// <summary>A failed download whose link looks used up or session-bound, with a page to look for the new one.</summary>
    public bool CanRefreshAddress =>
        State == DownloadState.Failed && HasOwnerPage
        && ErrorKind is JobErrorKind.AddressExpired or JobErrorKind.WebPage or JobErrorKind.HttpStatus;

    partial void OnErrorKindChanged(JobErrorKind value) => OnPropertyChanged(nameof(CanRefreshAddress));

    public bool CanMoveInQueue => State == DownloadState.Queued;

    /// <summary>A paused or failed download can be put into the queue to run in turn.</summary>
    public bool CanQueue => State is DownloadState.Paused or DownloadState.Failed;

    public bool CanPause => State is DownloadState.Queued or DownloadState.Probing or DownloadState.Downloading;
    public bool CanResume => State is DownloadState.Paused or DownloadState.Failed;
    public bool IsBusy => State is DownloadState.Probing or DownloadState.Downloading or DownloadState.Stopping;

    partial void OnStateChanged(DownloadState value)
    {
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanMoveInQueue));
        OnPropertyChanged(nameof(CanQueue));
        OnPropertyChanged(nameof(CanRefreshAddress));
        OnPropertyChanged(nameof(IsRecording));
    }

    /// <summary>Applies a fresh engine snapshot (called on the UI thread, at most ~10×/s).</summary>
    public void Update(JobSnapshot s)
    {
        FileName = s.FileName;
        Category = Job.Category;
        IsLive = s.Live;
        State = s.State;
        ErrorKind = s.State == DownloadState.Failed ? s.ErrorKind : JobErrorKind.None;
        Speed = s.Speed;
        IsCompleted = s.State == DownloadState.Completed;

        var total = s.Total;
        IsIndeterminate = s.State == DownloadState.Downloading && total == null;
        Fraction = s.State == DownloadState.Completed ? 1 : s.Fraction;

        var done = ByteFormat.Size(s.Downloaded);
        var of = total is { } t ? $"{done} of {(IsStream && s.State != DownloadState.Completed ? "about " : "")}{ByteFormat.Size(t)}" : done;

        switch (s.State)
        {
            case DownloadState.Queued:
                StatusText = "Queued";
                DetailText = total != null ? $"{ByteFormat.Size(total.Value)} · waiting for a free slot" : "Waiting for a free slot";
                break;
            case DownloadState.Probing:
                StatusText = "Connecting";
                DetailText = $"Contacting {Host}…";
                break;
            case DownloadState.Downloading:
                StatusText = s.Live ? "Recording" : "Downloading";
                var parts = new List<string> { s.Live ? $"{done}  ·  {Elapsed(s.Seconds)} recorded" : of };
                if (s.Speed > 0) parts.Add(ByteFormat.Speed(s.Speed));
                if (s.Eta != null) parts.Add($"{ByteFormat.Eta(s.Eta)} left");
                if (IsStream)
                {
                    if (s.Parts > 0 && !s.Live) parts.Add($"piece {Math.Min(s.PartsDone + 1, s.Parts)} of {s.Parts}");
                }
                else if (s.Connections > 1)
                {
                    parts.Add($"{s.Connections} connections");
                }

                DetailText = string.Join("  ·  ", parts);
                if (s.Note != null)
                {
                    StatusText = "Joining";
                    DetailText = s.Note;
                }
                break;
            case DownloadState.Stopping:
                StatusText = "Pausing";
                DetailText = "Saving progress…";
                break;
            case DownloadState.Paused:
                StatusText = "Paused";
                DetailText = s.Downloaded > 0 ? $"{of}  ·  paused" : "Not started yet";
                break;
            case DownloadState.Completed:
                StatusText = "Completed";
                DetailText = $"{(total is { } ct ? ByteFormat.Size(ct) : done)}  ·  {Host}"
                             + (Job.CompletedAt is { } when ? $"  ·  {WhenText(when)}" : "");
                break;
            case DownloadState.Failed:
                StatusText = "Failed";
                DetailText = s.Error ?? "The download failed.";
                break;
        }

        HintText = s.State == DownloadState.Failed ? HintFor(s.ErrorKind) : "";
        PercentText = s.State is DownloadState.Downloading or DownloadState.Paused or DownloadState.Stopping && total > 0
            ? $"{s.Fraction:P0}"
            : "";

        (PrimaryIcon, PrimaryTooltip, HasPrimaryAction) = s.State switch
        {
            DownloadState.Downloading when s.Live => (SymbolRegular.Stop24, "Stop and save the recording", true),
            DownloadState.Queued or DownloadState.Probing or DownloadState.Downloading =>
                (SymbolRegular.Pause24, "Pause", true),
            DownloadState.Paused => (SymbolRegular.Play24, "Resume", true),
            DownloadState.Failed => (SymbolRegular.ArrowClockwise24, "Try again", true),
            DownloadState.Completed => (SymbolRegular.Open24, "Open file", true),
            _ => (SymbolRegular.Pause24, "", false),
        };

        Priority = Job.Priority;
        Segments = s.Segments;
        TotalBytes = total;
        DownloadedBytes = s.Downloaded;
        Eta = s.Eta;
        ConnectionCount = s.Connections;
        Splits = s.Splits;
        Takeovers = s.Takeovers;
        Retries = s.Retries;
        Fallbacks = s.Fallbacks;
        PeakConnections = s.PeakConnections;
        AcceptsRanges = s.AcceptsRanges;

        if (s.State == DownloadState.Downloading && _history.TryAdd(s.Speed, Clock()))
            SpeedSamples = _history.ToArray();

        Updated?.Invoke();
    }

    /// <summary>The highest speed in the remembered window.</summary>
    public double PeakSpeed => _history.Peak;

    /// <summary>"2:13" or "1:02:13".</summary>
    static string Elapsed(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    static string HintFor(JobErrorKind kind) => kind switch
    {
        JobErrorKind.WebPage => "The site sent a web page instead of the file: the link needs your browser session or was already used. Try \"Refresh address\", or add it again with the Cookie header (Advanced).",
        JobErrorKind.AddressExpired => "The link may have expired. Use \"Refresh address\" to look for the new one on the page it came from.",
        JobErrorKind.FileChanged => "The file on the server changed. Remove this download and start it again.",
        JobErrorKind.Disk => "Check the save folder and free disk space, then try again.",
        JobErrorKind.DataCorruption => "The server sent inconsistent data. Try again with fewer connections.",
        JobErrorKind.Network => "Check your connection, then try again.",
        JobErrorKind.Tool => "ffmpeg is needed to join this stream. Install it or set its location in Settings > Downloads, then try again: the pieces are kept.",
        JobErrorKind.Unsupported => "This kind of stream cannot be downloaded.",
        _ => "",
    };

    static string WhenText(DateTimeOffset when)
    {
        var local = when.LocalDateTime;
        return local.Date == DateTime.Today ? $"today {local:t}" : local.Date == DateTime.Today.AddDays(-1) ? $"yesterday {local:t}" : $"{local:d}";
    }

    static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    // ---- commands (the owner expands them to the whole selection when this card is part of it) ----

    [RelayCommand]
    void PrimaryAction() => _owner.RunPrimary(this);

    [RelayCommand]
    void Pause() => _owner.PauseSelectionOf(this);

    /// <summary>"Stop and save": ends a recording and keeps what was recorded.</summary>
    [RelayCommand]
    void StopAndSave() => _owner.FinishRecording(this);

    [RelayCommand]
    void Resume() => _owner.ResumeSelectionOf(this);

    [RelayCommand]
    void OpenFile() => _owner.OpenFile(this);

    [RelayCommand]
    void OpenWith() => _owner.OpenWith(this);

    [RelayCommand]
    void ShowInFolder() => _owner.ShowInFolder(this);

    [RelayCommand]
    void ShowDetails() => _owner.OpenDetails(this);

    [RelayCommand]
    void MoveToTop() => _owner.MoveInQueue(this, QueueMove.Top);

    [RelayCommand]
    void MoveUp() => _owner.MoveInQueue(this, QueueMove.Up);

    [RelayCommand]
    void MoveDown() => _owner.MoveInQueue(this, QueueMove.Down);

    [RelayCommand]
    void MoveToBottom() => _owner.MoveInQueue(this, QueueMove.Bottom);

    [RelayCommand]
    void AddToQueue() => _owner.EnqueueSelectionOf(this);

    [RelayCommand]
    Task RefreshAddress() => _owner.RefreshAddressAsync(this);

    [RelayCommand]
    void OpenOwnerPage() => _owner.OpenOwnerPage(this);

    [RelayCommand]
    void CopyLink() => _owner.CopyLinks(this);

    [RelayCommand]
    void RemoveFromList() => _owner.RemoveSelectionOf(this, deleteFiles: false);

    [RelayCommand]
    void RemoveAndDelete() => _owner.RemoveSelectionOf(this, deleteFiles: true);
}
