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

    /// <summary><c>host:port</c> (or a full <c>http://host:port</c> address) for <see cref="ProxyMode.Manual"/>.</summary>
    public string ProxyAddress { get; set; } = "";

    /// <summary>Total speed of all downloads in bytes per second; 0 = no limit.</summary>
    public long GlobalSpeedLimit { get; set; }

    public bool AutoResumeOnStartup { get; set; }
    public bool WriteMarkOfTheWeb { get; set; } = true;

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

    /// <summary>A live recording stops by itself after this long (zero = it goes on until the stream ends or the user stops it).</summary>
    public TimeSpan MaxRecordingLength { get; set; } = TimeSpan.Zero;

    /// <summary>A stream that is one big file (DASH on-demand) is fetched in pieces of this size, in parallel and resumable.</summary>
    public int StreamChunkBytes { get; set; } = 4 * 1024 * 1024;

    // --- scheduler tuning (IDM report §3.3–3.5) ---
    public int MinSegmentBytes { get; set; } = 256 * 1024;
    public double LookaheadSeconds { get; set; } = 3.0;
    public int TakeoverThresholdBytes { get; set; } = 64;
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan RampUpInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromSeconds(2);

    // --- connection behaviour ---
    public int ReadBufferBytes { get; set; } = 128 * 1024;
    public int MaxRetries { get; set; } = 10;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a kept first response (see <c>ProbeAndHoldAsync</c>) may wait for its download to start before the
    /// address is requested again; servers close idle connections.
    /// </summary>
    public TimeSpan HeldProbeLifetime { get; set; } = TimeSpan.FromSeconds(45);

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
