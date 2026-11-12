namespace ShadeNet.Core.Torrents;

/// <summary>What "Check the files again" did.</summary>
public enum TorrentRecheck
{
    /// <summary>The torrent is running: it is being checked now.</summary>
    Checking,

    /// <summary>It was finished: it started to check (and to download what is not right).</summary>
    Restarted,

    /// <summary>It is paused: it is checked when it next starts.</summary>
    OnNextStart,
}

/// <summary>How much a torrent's file is wanted.</summary>
public enum TorrentFilePriority
{
    /// <summary>Not downloaded.</summary>
    Skip,
    Normal,

    /// <summary>Fetched before the files of normal priority.</summary>
    High,
}

/// <summary>One person the torrent is connected to.</summary>
/// <param name="Address">Their address and port.</param>
/// <param name="Client">The program they use ("qBittorrent 4.6", "Transmission" …), as their peer ID says.</param>
/// <param name="Progress">How much of the torrent they have, 0 to 1.</param>
/// <param name="Incoming">They connected to us (rather than we to them).</param>
public sealed record TorrentPeer(string Address, string Client, double Progress, double DownloadSpeed, double UploadSpeed, bool IsSeeder,
    bool Encrypted, bool Incoming);

/// <summary>One file of the torrent and how far it is.</summary>
/// <param name="Index">Its place in the torrent (what <see cref="DownloadManager.SetTorrentFilesAsync"/> is given).</param>
/// <param name="Progress">0 to 1, from the pieces that are in; null when that is not known (a magnet link still without its file list).</param>
public sealed record TorrentFileProgress(int Index, string Path, long Length, double? Progress, TorrentFilePriority Priority);

/// <summary>A tracker of the torrent, or one of the other ways it finds peers (DHT).</summary>
/// <param name="Status">"Working", "Not contacted yet", "Not working" …</param>
/// <param name="Message">What the tracker said when it refused or warned.</param>
/// <param name="Seeds">How many have all of the torrent, as this tracker last said (null: it did not say).</param>
/// <param name="Leechers">How many are downloading it, as this tracker last said.</param>
/// <param name="Downloaded">How many have finished it, ever, as this tracker last said.</param>
public sealed record TorrentTracker(string Address, string Status, string? Message, int Tier, int? Seeds = null, int? Leechers = null, int? Downloaded = null);

/// <summary>How big a torrent's swarm is, as its trackers say.</summary>
/// <param name="Seeds">The most that have all of it any tracker reported (null when none answered).</param>
/// <param name="Leechers">The most that are downloading it.</param>
/// <param name="Answered">Trackers that answered, of <paramref name="Asked"/>.</param>
public sealed record TorrentSwarmCount(int? Seeds, int? Leechers, int Answered, int Asked);

/// <summary>Everything the details drawer shows about a torrent.</summary>
/// <param name="Running">It is in the torrent engine now (downloading or being shared); otherwise what is shown comes from what was saved.</param>
/// <param name="Pieces">One flag per piece: it is on the disk. Empty when not known.</param>
/// <param name="Size">The whole torrent's size (the pieces cover every file, the skipped ones too).</param>
/// <param name="SwarmSeeds">Everyone who has all of it, as the trackers count (not only the peers connected); null when no tracker said.</param>
/// <param name="SwarmLeechers">Everyone downloading it, as the trackers count.</param>
/// <param name="IsPrivate">Its tracker decides who may join: no other trackers, no DHT.</param>
public sealed record TorrentDetails(
    bool Running,
    IReadOnlyList<TorrentPeer> Peers,
    IReadOnlyList<TorrentFileProgress> Files,
    IReadOnlyList<TorrentTracker> Trackers,
    IReadOnlyList<bool> Pieces,
    int PieceLength,
    long Size,
    bool Sequential,
    int? SwarmSeeds = null,
    int? SwarmLeechers = null,
    bool IsPrivate = false)
{
    public static TorrentDetails Empty { get; } = new(false, Array.Empty<TorrentPeer>(), Array.Empty<TorrentFileProgress>(),
        Array.Empty<TorrentTracker>(), Array.Empty<bool>(), 0, 0, false);
}
