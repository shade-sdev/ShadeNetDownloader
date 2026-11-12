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
    double Seconds = 0,
    TorrentStatus? Torrent = null,
    string? Waiting = null)
{
    public double Fraction => Total is > 0 ? Math.Clamp((double)Downloaded / Total.Value, 0, 1) : 0;
}

/// <summary>A torrent's side of a snapshot: the people it is connected to and what it has shared.</summary>
/// <param name="Peers">Connected peers (seeds included).</param>
/// <param name="Seeds">Connected peers that have the whole torrent.</param>
/// <param name="UploadSpeed">Bytes per second sent to others.</param>
/// <param name="Uploaded">Bytes sent to others over all runs.</param>
/// <param name="Ratio">Shared ÷ size of the chosen files.</param>
/// <param name="Seeding">Finished and still sharing it.</param>
/// <param name="Activity">What it is doing when it is not simply downloading ("Getting the file list from peers…").</param>
/// <param name="SwarmSeeds">Everyone who has all of it, as the trackers count (null: no tracker said).</param>
/// <param name="SwarmLeechers">Everyone downloading it, as the trackers count.</param>
public sealed record TorrentStatus(int Peers, int Seeds, double UploadSpeed, long Uploaded, double Ratio, bool Seeding, string? Activity,
    int? SwarmSeeds = null, int? SwarmLeechers = null);

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

    /// <summary>
    /// Set for a torrent (a magnet link in <see cref="Url"/> is one even without it): the .torrent file, when it is known, and the files to
    /// download. <see cref="Url"/> is the magnet link, the .torrent file's path or its web address.
    /// </summary>
    public TorrentChoice? Torrent { get; init; }

    public AddMode Mode { get; init; } = AddMode.Start;
}

/// <param name="Metadata">The .torrent file's bytes (from <c>DescribeTorrentAsync</c>); null for a magnet link whose file list is not known yet.</param>
/// <param name="Files">The indexes of the files to download (in the torrent's order); null = all of them.</param>
public sealed record TorrentChoice(byte[]? Metadata = null, IReadOnlyList<int>? Files = null, bool Sequential = false);

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
/// <param name="Played">With a list of qualities: the addresses the browser's player loaded its playlists from (it may add a key of its own).</param>
public sealed record StreamChoice(int Height = 0, long Bandwidth = 0, string? AudioName = null, bool Subtitles = true, bool EmbedSubtitles = false,
    bool Direct = false, string? AudioUrl = null, double Seconds = 0, bool Record = false,
    IReadOnlyList<string>? Pieces = null, string? Init = null, IReadOnlyList<string>? AudioPieces = null, string? AudioInit = null,
    IReadOnlyList<string>? Played = null);

public enum AddMode
{
    /// <summary>Begin right away (user pressed "Download now").</summary>
    Start,

    /// <summary>Wait in the queue until a slot is free.</summary>
    Queue,

    /// <summary>Add as paused; the user starts it later.</summary>
    Hold,
}
