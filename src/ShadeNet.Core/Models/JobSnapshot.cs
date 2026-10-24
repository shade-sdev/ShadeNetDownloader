namespace ShadeNet.Core.Models;

/// <summary>Immutable progress report published by the engine ~4×/s per running job and on every state change.</summary>
public sealed record JobSnapshot(
    Guid Id,
    DownloadState State,
    string FileName,
    string SaveDirectory,
    long Downloaded,
    long? Total,
    double Speed,
    TimeSpan? Eta,
    int Connections,
    IReadOnlyList<SegmentInfo> Segments,
    bool AcceptsRanges,
    string? Error,
    JobErrorKind ErrorKind,
    int Splits = 0,
    int Takeovers = 0,
    int Retries = 0,
    int Fallbacks = 0,
    int PeakConnections = 0,
    string? Note = null,
    int Parts = 0,
    int PartsDone = 0,
    bool Live = false,
    double Seconds = 0)
{
    public double Fraction => Total is > 0 ? Math.Clamp((double)Downloaded / Total.Value, 0, 1) : 0;
}

public sealed class AddDownloadRequest
{
    public required string Url { get; init; }
    public string? Referer { get; init; }
    public string? Cookies { get; init; }
    public string? UserAgent { get; init; }
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>Optional overrides; when null they are resolved from the probe result and the category rules.</summary>
    public string? FileName { get; init; }
    public string? SaveDirectory { get; init; }
    public string? Category { get; init; }
    public int? MaxConnections { get; init; }
    public long? SpeedLimit { get; init; }

    /// <summary>Set for an HLS stream: which quality to download. The job then is a stream download, not a file.</summary>
    public StreamChoice? Stream { get; init; }
    public AddMode Mode { get; init; } = AddMode.Start;
}

/// <summary>The quality picked for a stream (see <see cref="ShadeNet.Core.Hls.HlsChoice"/>).</summary>
/// <param name="Height">0 = the best one.</param>
/// <param name="Subtitles">Also save the stream's subtitles as files next to the video.</param>
/// <param name="EmbedSubtitles">Put WebVTT subtitles inside the video file (MP4/MKV, needs ffmpeg) instead of beside it.</param>
/// <param name="Direct">No playlist: the job's address is the picture (or a file with both), downloaded as a whole in parallel ranges.</param>
/// <param name="AudioUrl">With <paramref name="Direct"/>: the sound, kept apart, to be joined with the picture.</param>
/// <param name="Seconds">With <paramref name="Direct"/>: the length, for progress.</param>
/// <param name="Record">The address is one endless live response (an FLV pull), recorded with ffmpeg until it ends or the user stops it.</param>
/// <param name="Pieces">No playlist: the pieces a player asked for (it keeps their list itself), in that order, after <paramref name="Init"/>
/// (their initialisation part, none for MPEG-TS); <paramref name="AudioPieces"/> and <paramref name="AudioInit"/> when the sound is apart.</param>
public sealed record StreamChoice(int Height = 0, long Bandwidth = 0, string? AudioName = null, bool Subtitles = true, bool EmbedSubtitles = false,
    bool Direct = false, string? AudioUrl = null, double Seconds = 0, bool Record = false,
    IReadOnlyList<string>? Pieces = null, string? Init = null, IReadOnlyList<string>? AudioPieces = null, string? AudioInit = null);

public enum AddMode
{
    /// <summary>Begin right away (user pressed "Download now").</summary>
    Start,

    /// <summary>Wait in the queue until a slot is free.</summary>
    Queue,

    /// <summary>Add as paused; the user starts it later.</summary>
    Hold,
}
