namespace ShadeNet.Core.Models;

public enum DownloadState
{
    Queued,
    Probing,
    Downloading,
    Paused,
    Completed,
    Failed,
    Stopping,
}

/// <summary>What kind of thing a job downloads.</summary>
public enum DownloadKind
{
    /// <summary>One file, fetched in byte ranges over several connections.</summary>
    File,

    /// <summary>An HLS stream: many small media segments, joined into one video file at the end.</summary>
    Hls,
}

public enum SegmentState
{
    Pending,
    Active,
    Done,
}

/// <summary>Coarse failure classification so the UI can offer the right one-click fix.</summary>
public enum JobErrorKind
{
    None,
    Network,
    HttpStatus,
    RangeIgnored,
    FileChanged,
    DataCorruption,
    Disk,
    AddressExpired,
    Other,

    /// <summary>The link names a file but the server answered with a web page (sign-in wall, cookie check, expired share).</summary>
    WebPage,

    /// <summary>A stream that cannot be downloaded: live, or protected by DRM.</summary>
    Unsupported,

    /// <summary>A helper program (ffmpeg) is missing or failed.</summary>
    Tool,
}

/// <summary>Where a queued download goes within the queue.</summary>
public enum QueueMove
{
    Top,
    Up,
    Down,
    Bottom,
}
