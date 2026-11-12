using System.Text.Json;

namespace ShadeNet.Core.Torrents;

/// <summary>One file of a torrent, as the job remembers it.</summary>
public sealed class TorrentFileEntry
{
    /// <summary>The path inside the torrent ("Season 1\episode 01.mkv"), as the torrent names it.</summary>
    public string Path { get; set; } = "";
    public long Length { get; set; }

    /// <summary>The user wants this file; the others are skipped.</summary>
    public bool Selected { get; set; } = true;

    /// <summary>Fetched before the others (the user marked it "High").</summary>
    public bool High { get; set; }
}

/// <summary>What a torrent download keeps between runs (persisted as JSON with the job).</summary>
public sealed class TorrentJobData
{
    /// <summary>The torrent's identity (SHA-1 info hash, upper-case hex): the same torrent is the same download.</summary>
    public string InfoHash { get; set; } = "";

    /// <summary>A magnet link for it (also what "Copy link" gives when the torrent came from a file).</summary>
    public string Magnet { get; set; } = "";

    /// <summary>The torrent's own name (the folder name for a torrent of several files).</summary>
    public string? Name { get; set; }

    /// <summary>The files, once the torrent's file list is known (a magnet link has none until peers send it).</summary>
    public List<TorrentFileEntry>? Files { get; set; }

    /// <summary>A torrent of several files is saved as a folder (the job's target); one file is saved as that file.</summary>
    public bool IsFolder { get; set; }

    /// <summary>Bytes of the chosen files that are in (for progress while not running).</summary>
    public long BytesDone { get; set; }

    /// <summary>Bytes sent to other people, over all runs.</summary>
    public long Uploaded { get; set; }

    /// <summary>Finished, and still sharing it with others (until the share limits are reached or the user stops).</summary>
    public bool Seeding { get; set; }

    /// <summary>How long it has been shared so far, over all runs (the time limit counts this).</summary>
    public double SeedSeconds { get; set; }

    /// <summary>Pieces are fetched in order, from the start of the torrent (a film can be watched while the rest arrives).</summary>
    public bool Sequential { get; set; }

    /// <summary>Trackers the user added (Details > Trackers), asked as well as the torrent's own.</summary>
    public List<string>? AddedTrackers { get; set; }

    /// <summary>Trackers of the torrent's own the user took off: they are not asked.</summary>
    public List<string>? RemovedTrackers { get; set; }

    static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static TorrentJobData FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TorrentJobData();
        try
        {
            return JsonSerializer.Deserialize<TorrentJobData>(json, Options) ?? new TorrentJobData();
        }
        catch (JsonException)
        {
            return new TorrentJobData();
        }
    }

    /// <summary>The total size of the chosen files.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long WantedBytes => Files?.Where(f => f.Selected).Sum(f => f.Length) ?? 0;

    /// <summary>Shared with others ÷ the size of the chosen files.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double Ratio => WantedBytes > 0 ? (double)Uploaded / WantedBytes : 0;
}

/// <summary>What the Add dialog shows about a torrent before it is added.</summary>
/// <param name="Metadata">The .torrent file's bytes: handed back to <see cref="Models.AddDownloadRequest.Torrent"/> so nothing is fetched twice.</param>
public sealed record TorrentInfo(
    string Name,
    string InfoHash,
    long Size,
    IReadOnlyList<TorrentFileEntry> Files,
    byte[] Metadata,
    string Magnet,
    bool IsPrivate,
    string? Comment);
