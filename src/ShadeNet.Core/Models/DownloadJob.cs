namespace ShadeNet.Core.Models;

public sealed class DownloadJob
{
    public Guid Id { get; init; } = Guid.NewGuid();

    // Request identity (captured from the browser or typed by the user)
    public string Url { get; set; } = "";
    public string? FinalUrl { get; set; }
    public string? Referer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();

    public DownloadKind Kind { get; set; } = DownloadKind.File;

    /// <summary>For <see cref="DownloadKind.Hls"/>: the chosen quality and the progress so far. Persisted.</summary>
    public ShadeNet.Core.Hls.HlsJobData? Hls { get; set; }

    // Target
    public string FileName { get; set; } = "download";
    public string SaveDirectory { get; set; } = "";
    public string Category { get; set; } = "General";

    /// <summary>Name/folder were guessed from the URL before any probe; the probe may refine them (not persisted).</summary>
    public bool NameProvisional { get; set; }
    public bool FolderProvisional { get; set; }

    // Remote file facts (filled by the probe)
    public bool Probed { get; set; }
    public long? TotalSize { get; set; }
    public string? ContentType { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public bool AcceptsRanges { get; set; }

    // Control
    public DownloadState State { get; set; } = DownloadState.Queued;
    public long? SpeedLimit { get; set; }
    public int MaxConnections { get; set; } = 8;
    public int Priority { get; set; }
    public string? Error { get; set; }
    public JobErrorKind ErrorKind { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }

    // Segments + counters
    public List<Segment> Segments { get; set; } = new();
    public int NextSegmentId { get; set; }

    public JobStats Stats { get; } = new();

    /// <summary>
    /// The first response of this address, still open (not persisted). The first connection reads it instead of
    /// requesting the address again, because some sites answer a second request for the same link with a web page.
    /// </summary>
    internal ShadeNet.Core.Net.LiveResponse? FirstResponse { get; set; }

    /// <summary>Where <see cref="FirstResponse"/> starts in the file.</summary>
    internal long FirstResponseStart { get; set; }

    /// <summary>Guards <see cref="Segments"/> and every <see cref="Segment"/> field while a runner is active.</summary>
    internal object Sync { get; } = new();

    public string TargetPath => Path.Combine(SaveDirectory, FileName);
    public string PartPath => TargetPath + ".sndpart";

    /// <summary>The folder a stream download keeps its finished segments in until they are joined.</summary>
    public string WorkFolder => TargetPath + ".sndhls";

    public bool IsActive => State is DownloadState.Probing or DownloadState.Downloading or DownloadState.Stopping;

    /// <summary>Consistent copy of the segment list (progress only; no runtime ownership).</summary>
    public List<Segment> CaptureSegments()
    {
        lock (Sync)
        {
            return Segments.Select(s => new Segment
            {
                Id = s.Id, Start = s.Start, End = s.End, Downloaded = s.Downloaded, State = s.State,
            }).ToList();
        }
    }

    public long DownloadedBytes
    {
        get
        {
            lock (Sync)
            {
                long sum = 0;
                foreach (var s in Segments) sum += s.Downloaded;
                return sum;
            }
        }
    }
}
