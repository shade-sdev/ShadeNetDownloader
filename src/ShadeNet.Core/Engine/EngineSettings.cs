using System.Text.Json.Serialization;
using ShadeNet.Core.Net;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Engine;

public enum ProxyMode
{
    /// <summary>Use the proxy Windows is configured with (the default of .NET's HTTP stack).</summary>
    System,

    /// <summary>Connect directly, ignoring any system proxy.</summary>
    None,

    /// <summary>Use <see cref="EngineSettings.ProxyAddress"/>.</summary>
    Manual,
}

/// <summary>How torrents encrypt their connections to peers.</summary>
public enum TorrentEncryption
{
    /// <summary>Encrypted when the other side can; plain otherwise (the most peers).</summary>
    Prefer,

    /// <summary>Encrypted only: peers that cannot are not used (fewer peers; some networks slow plain torrent traffic down).</summary>
    Require,

    /// <summary>Plain only.</summary>
    Off,
}

/// <summary>A category the user added: a name and the file types that belong to it.</summary>
public sealed class CustomCategory
{
    public string Name { get; set; } = "";
    public List<string> Extensions { get; set; } = new();
}

/// <summary>Tunables for the engine. Defaults follow the values derived from the IDM analysis.</summary>
public sealed class EngineSettings
{
    /// <summary>Root folder; categories live in sub-folders unless overridden in <see cref="CategoryFolders"/>.</summary>
    public string DownloadRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Category name → absolute folder. Missing categories use <c>DownloadRoot\Category</c>.</summary>
    public Dictionary<string, string> CategoryFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool UseCategoryFolders { get; set; } = true;

    // --- categories: which file types belong where ---

    /// <summary>Built-in category → extensions that replace its default list (lower case, no dot).</summary>
    public Dictionary<string, List<string>> CategoryExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Categories the user added.</summary>
    public List<CustomCategory> CustomCategories { get; set; } = new();

    Dictionary<string, string>? _extensionMap;

    /// <summary>Call after changing <see cref="CategoryExtensions"/> or <see cref="CustomCategories"/>.</summary>
    public void InvalidateCategories() => _extensionMap = null;

    /// <summary>All category names in sidebar order: built-ins, then the user's, then <see cref="CategoryResolver.General"/>.</summary>
    public IReadOnlyList<string> CategoryNames =>
        CategoryResolver.Categories.Where(c => c != CategoryResolver.General)
            .Concat(CustomCategories.Select(c => c.Name))
            .Append(CategoryResolver.General)
            .ToList();

    /// <summary>The category of a file by the user's rules (custom categories win over built-in ones for the same type).</summary>
    public string CategoryOf(string fileName)
    {
        var map = _extensionMap ??= BuildExtensionMap();
        return map.GetValueOrDefault(Path.GetExtension(fileName), CategoryResolver.General);
    }

    /// <summary>The extensions that currently belong to a category (its override, its default, or the custom list).</summary>
    public IReadOnlyList<string> ExtensionsOf(string category)
    {
        if (CustomCategories.FirstOrDefault(c => string.Equals(c.Name, category, StringComparison.OrdinalIgnoreCase)) is { } custom)
            return NormalizeExtensions(custom.Extensions);
        if (CategoryExtensions.TryGetValue(category, out var overridden)) return NormalizeExtensions(overridden);
        return CategoryResolver.DefaultExtensions.TryGetValue(category, out var defaults) ? defaults : Array.Empty<string>();
    }

    Dictionary<string, string> BuildExtensionMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Built-ins the user has not touched first, then the ones they edited: a type the user gave to Video must not be
        // taken back by another built-in's default list that still names it.
        var builtIns = CategoryResolver.DefaultExtensions.Keys.ToList();
        foreach (var builtIn in builtIns.Where(b => !CategoryExtensions.ContainsKey(b)).Concat(builtIns.Where(CategoryExtensions.ContainsKey)))
            foreach (var e in ExtensionsOf(builtIn)) map["." + e] = builtIn;

        foreach (var custom in CustomCategories)
            foreach (var e in NormalizeExtensions(custom.Extensions)) map["." + e] = custom.Name;

        return map;
    }

    /// <summary>"MP4", ".mp4 " and "*.mp4" all become "mp4"; anything that is not a plain extension is dropped.</summary>
    public static List<string> NormalizeExtensions(IEnumerable<string> raw) =>
        raw.Select(e => e.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length is > 0 and <= 16 && e.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '+'))
            .Distinct()
            .ToList();

    /// <summary>
    /// A category name becomes a folder name, so it must be one: not empty, not too long, no path characters, not already
    /// taken (case-insensitively), and not a name reserved for the built-ins.
    /// </summary>
    public bool IsValidNewCategoryName(string? name, string? except = null)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n) || n.Length > 40) return false;
        if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || n.EndsWith('.') || n.StartsWith('.')) return false;
        return !CategoryNames.Any(c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase)
                                       && !string.Equals(c, except, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="address"/> can be used as the manual proxy ("host:port" or a full http address).</summary>
    public static bool IsUsableProxyAddress(string? address) => HttpFetcher.TryParseProxy(address) != null;

    /// <summary>Rebuilds what JSON loading loses (case-insensitive keys) and drops entries that cannot work.</summary>
    public void Normalize()
    {
        CategoryFolders = new Dictionary<string, string>(CategoryFolders ?? new(), StringComparer.OrdinalIgnoreCase);
        HostConnectionLimits = new Dictionary<string, int>(HostConnectionLimits ?? new(), StringComparer.OrdinalIgnoreCase);
        CategoryExtensions = new Dictionary<string, List<string>>(CategoryExtensions ?? new(), StringComparer.OrdinalIgnoreCase);
        CustomCategories = (CustomCategories ?? new()).Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
        MaxConcurrentJobs = Math.Clamp(MaxConcurrentJobs, 1, 32);
        DefaultConnections = Math.Clamp(DefaultConnections, 1, 32);
        TorrentSeedRatio = double.IsFinite(TorrentSeedRatio) ? Math.Clamp(TorrentSeedRatio, 0, 100) : 1.0;
        TorrentSeedMinutes = Math.Clamp(TorrentSeedMinutes, 0, 525_600);
        TorrentUploadLimit = Math.Max(0, TorrentUploadLimit);
        TorrentPort = TorrentPort is >= 0 and <= 65535 ? TorrentPort : 0;
        if (!Enum.IsDefined(TorrentEncryption)) TorrentEncryption = TorrentEncryption.Prefer;
        // (the adapter used to be a torrents-only setting: a choice made there now holds for everything)
        if (string.IsNullOrWhiteSpace(NetworkAdapter) && !string.IsNullOrWhiteSpace(TorrentNetworkAdapter))
        {
            NetworkAdapter = TorrentNetworkAdapter;
            NetworkAdapterName ??= TorrentNetworkAdapterName;
        }

        TorrentNetworkAdapter = null;
        TorrentNetworkAdapterName = null;
        NetworkAdapter = string.IsNullOrWhiteSpace(NetworkAdapter) ? null : NetworkAdapter.Trim();
        InvalidateCategories();
    }

    public int DefaultConnections { get; set; } = 8;
    public int MaxConcurrentJobs { get; set; } = 3;

    /// <summary>Hosts that must not receive more than N connections (host → N).</summary>
    public Dictionary<string, int> HostConnectionLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36 ShadeNet/1.0";

    // --- network ---
    public ProxyMode Proxy { get; set; } = ProxyMode.System;

    /// <summary>
    /// <c>host:port</c> (an HTTP proxy), or a full address: <c>http://host:port</c>, <c>socks5://host:port</c> (also socks5h, socks4,
    /// socks4a), with <c>user:password@</c> before the host when the proxy wants a sign-in. For <see cref="ProxyMode.Manual"/>.
    /// </summary>
    public string ProxyAddress { get; set; } = "";

    /// <summary>Total speed of all downloads in bytes per second; 0 = no limit.</summary>
    public long GlobalSpeedLimit { get; set; }

    /// <summary>
    /// The network adapter ShadeNet is bound to (its Windows id), e.g. a VPN's: downloads, videos, recordings and torrents use only its
    /// addresses, and wait (they never fall back to another network) while it is not connected. Null: any.
    /// </summary>
    public string? NetworkAdapter { get; set; }

    /// <summary>The name of <see cref="NetworkAdapter"/> when it was chosen, shown while it is not there.</summary>
    public string? NetworkAdapterName { get; set; }

    public bool AutoResumeOnStartup { get; set; }
    public bool WriteMarkOfTheWeb { get; set; } = true;

    /// <summary>A finished archive (zip, 7z, rar, tar …, one file or all its parts) is unpacked into a folder next to it.</summary>
    public bool ExtractArchives { get; set; }

    /// <summary>After it was unpacked, the archive (all its parts) goes to the Recycle Bin (the app does that).</summary>
    public bool DeleteArchivesAfterExtracting { get; set; }

    /// <summary>ffmpeg for joining streams: a file, or a folder that holds it. Empty = look next to the program, then on the PATH.</summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>yt-dlp for YouTube and similar sites: a file, or a folder that holds it. Empty = next to the program (tools\yt-dlp), then the PATH.</summary>
    public string YtDlpPath { get; set; } = "";

    /// <summary>Where downloaded updates of yt-dlp and ffmpeg live (the app sets it to a folder in its data folder); not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ToolsFolder { get; set; }

    /// <summary>The ffmpeg in use: the user's choice, else a downloaded update, else the one that came with the program, else the PATH.</summary>
    public string? FindFfmpeg() => ShadeNet.Core.Hls.Ffmpeg.Locate(FfmpegPath, userFolder: ToolsFolder);

    public string? FindYtDlp() => ShadeNet.Core.Sites.YtDlp.Locate(YtDlpPath, userFolder: ToolsFolder);

    /// <summary>7-Zip (7z.exe, with its 7z.dll) for unpacking archives: a file, or a folder that holds it. Empty = look for it.</summary>
    public string SevenZipPath { get; set; } = "";

    public string? FindSevenZip() => ShadeNet.Core.Archives.SevenZip.Locate(SevenZipPath, userFolder: ToolsFolder);

    /// <summary>A live recording stops by itself after this long (zero = it goes on until the stream ends or the user stops it).</summary>
    public TimeSpan MaxRecordingLength { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// A live recording begins this far behind the live edge, like a player does, not at the start of the stream's list of pieces: for
    /// some sites (YouTube among them) that list reaches back for hours, and recording it all would be gigabytes nobody asked for.
    /// Zero = everything the list names.
    /// </summary>
    [JsonIgnore]
    public TimeSpan LiveBacklog { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// After "Stop and save" (or the recording length limit) the pieces already being fetched, and those already waiting, get this long
    /// to arrive; what is not there by then is left out of the file, so stopping never waits on a slow or stuck server.
    /// </summary>
    [JsonIgnore]
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A stream that is one big file (DASH on-demand) is fetched in pieces of this size, in parallel and resumable.</summary>
    [JsonIgnore]
    public int StreamChunkBytes { get; set; } = 4 * 1024 * 1024;

    // --- scheduler tuning (IDM report §3.3–3.5) ---
    [JsonIgnore]
    public int MinSegmentBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Which part to split when a connection is free: the one with the most left after this many seconds at its connection's
    /// speed (a slow connection's part looks bigger, so it is helped first). It no longer decides WHETHER to split: any part
    /// with room for two halves of <see cref="MinSegmentBytes"/> is split, however fast its connection is.
    /// </summary>
    [JsonIgnore]
    public double LookaheadSeconds { get; set; } = 3.0;

    [JsonIgnore]
    public int TakeoverThresholdBytes { get; set; } = 64;
    [JsonIgnore]
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Only under a speed limit: how often the number of connections may double (without one they all open at once).</summary>
    [JsonIgnore]
    public TimeSpan RampUpInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A download opens one connection per this many bytes at the start (at most its connection count): a small file is not
    /// cut into slivers that cost more requests than they save.
    /// </summary>
    [JsonIgnore]
    public int InitialPieceBytes { get; set; } = 512 * 1024;

    /// <summary>
    /// How often progress is made safe on the disk (the file flushed, then the parts saved); a crash costs about this much
    /// downloading again. A flush makes every write to the file wait on NTFS, so a disk that takes long to flush is flushed
    /// less often: at least ten times the last flush's duration apart (connections keep receiving meanwhile).
    /// </summary>
    [JsonIgnore]
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromSeconds(3);

    // --- connection behaviour ---
    [JsonIgnore]
    public int ReadBufferBytes { get; set; } = 128 * 1024;

    /// <summary>
    /// A connection collects this much before writing it, and goes on receiving while the write runs: the network is not
    /// held up by the disk, and the disk gets fewer, larger writes.
    /// </summary>
    [JsonIgnore]
    public int WriteBatchBytes { get; set; } = 256 * 1024;

    public int MaxRetries { get; set; } = 10;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);
    [JsonIgnore]
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The wait before the second try of a failed connection, doubling after that. The first try is at once.</summary>
    [JsonIgnore]
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    [JsonIgnore]
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A connection far slower than the best one this download had (under 1/<see cref="SlowConnectionRatio"/> of it, after
    /// <see cref="SlowConnectionGrace"/> on its answer, with more than that much work left at its pace) asks again on a new
    /// connection: a slow route or a slow server slot rarely lasts past a new request. Never under a speed limit.
    /// </summary>
    [JsonIgnore]
    public bool ReplaceSlowConnections { get; set; } = true;

    [JsonIgnore]
    public TimeSpan SlowConnectionGrace { get; set; } = TimeSpan.FromSeconds(2);
    [JsonIgnore]
    public int SlowConnectionRatio { get; set; } = 4;

    /// <summary>
    /// How long a kept first response (see <c>ProbeAndHoldAsync</c>) may wait for its download to start before the
    /// address is requested again; servers close idle connections (a closed one is simply asked again at once).
    /// </summary>
    [JsonIgnore]
    public TimeSpan HeldProbeLifetime { get; set; } = TimeSpan.FromMinutes(2);

    // --- torrents ---

    /// <summary>
    /// After a torrent finishes it is shared with others until it has sent this many times its size (0 = do not share at all after
    /// finishing) or <see cref="TorrentSeedMinutes"/> have passed, whichever comes first.
    /// </summary>
    public double TorrentSeedRatio { get; set; } = 1.0;

    /// <summary>A finished torrent stops being shared after this many minutes (0 = only the ratio stops it).</summary>
    public int TorrentSeedMinutes { get; set; } = 120;

    /// <summary>Total upload speed of all torrents in bytes per second; 0 = no limit.</summary>
    public long TorrentUploadLimit { get; set; }

    /// <summary>The port other peers connect to; 0 = a free one is chosen each time ShadeNet starts.</summary>
    public int TorrentPort { get; set; }

    /// <summary>Find peers without a tracker (the DHT network); needed by most magnet links.</summary>
    public bool TorrentUseDht { get; set; } = true;

    /// <summary>Ask the router to forward <see cref="TorrentPort"/> (UPnP / NAT-PMP), so peers can connect in.</summary>
    public bool TorrentPortForwarding { get; set; } = true;

    /// <summary>Find peers on the local network.</summary>
    public bool TorrentLocalPeers { get; set; } = true;

    /// <summary>
    /// With a proxy of ShadeNet's own (<see cref="ProxyMode.Manual"/>), torrents go through it too: trackers and the peers ShadeNet
    /// connects to. What cannot pass a proxy is then not used at all (UDP trackers, the DHT, local peers, port forwarding), so nothing
    /// of a torrent leaves this computer except through the proxy.
    /// </summary>
    public bool TorrentUseProxy { get; set; } = true;

    /// <summary>The adapter of an earlier version's torrents-only setting; read once, moved to <see cref="NetworkAdapter"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TorrentNetworkAdapter { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TorrentNetworkAdapterName { get; set; }

    /// <summary>Whether connections to peers are encrypted.</summary>
    public TorrentEncryption TorrentEncryption { get; set; } = TorrentEncryption.Prefer;

    /// <summary>Where the torrent engine keeps the .torrent files and the resume data (the app sets it in its data folder); not saved.</summary>
    [JsonIgnore]
    public string? TorrentFolder { get; set; }

    /// <summary>The address the torrent engine listens on (tests use the loopback address); not saved.</summary>
    [JsonIgnore]
    public System.Net.IPAddress TorrentListenAddress { get; set; } = System.Net.IPAddress.Any;

    /// <summary>The most peers one torrent is connected to at once.</summary>
    [JsonIgnore]
    public int TorrentMaxPeers { get; set; } = 60;

    /// <summary>The folder a category would use if the user had not chosen one for it.</summary>
    public string ResolveCategoryFolderDefault(string category) =>
        UseCategoryFolders && category != CategoryResolver.General ? Path.Combine(DownloadRoot, category) : DownloadRoot;

    public string ResolveCategoryFolder(string category)
    {
        if (CategoryFolders.TryGetValue(category, out var folder) && !string.IsNullOrWhiteSpace(folder)) return folder;
        return UseCategoryFolders && category != CategoryResolver.General
            ? Path.Combine(DownloadRoot, category)
            : DownloadRoot;
    }

    public int EffectiveConnections(string host, int requested)
    {
        var n = Math.Max(1, requested);
        if (!string.IsNullOrEmpty(host) && HostConnectionLimits.TryGetValue(host, out var cap)) n = Math.Min(n, Math.Max(1, cap));
        return n;
    }
}
