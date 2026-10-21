using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Hls;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Sites;

namespace ShadeNet.Core.Tools;

/// <summary>The helper programs ShadeNet ships and keeps up to date.</summary>
public enum ManagedTool
{
    YtDlp,
    Ffmpeg,
}

/// <summary>Where the program in use comes from.</summary>
public enum ToolSource
{
    None,

    /// <summary>The user chose its location in Settings: theirs to manage, never updated by ShadeNet.</summary>
    Custom,

    /// <summary>A copy ShadeNet downloaded into the user's own folder (updates go here: the install folder is not writable).</summary>
    User,

    /// <summary>The copy that came with the program.</summary>
    Bundled,

    /// <summary>Found on the PATH: not ShadeNet's to manage.</summary>
    Path,
}

/// <param name="Current">The version in use; <c>null</c> when the tool is not installed at all.</param>
public sealed record ToolUpdate(ManagedTool Tool, string? Current, string Latest, string AssetUrl, string ChecksumUrl, string AssetName, DateTimeOffset? Published)
{
    public string Name => ToolManager.NameOf(Tool);
}

/// <summary>What the rest of the program needs from the tool manager (faked in tests).</summary>
public interface IToolUpdates
{
    /// <summary>The program in use, or null.</summary>
    string? Find(ManagedTool tool);

    ToolSource SourceOf(ManagedTool tool);

    Task<string?> VersionAsync(ManagedTool tool, CancellationToken ct);

    /// <summary>The newer version that is on offer, or <c>null</c> when this one is current (or is not ShadeNet's to update).</summary>
    Task<ToolUpdate?> CheckAsync(ManagedTool tool, CancellationToken ct);

    /// <returns>The version that is installed now.</returns>
    Task<string> UpdateAsync(ToolUpdate update, CancellationToken ct);
}

/// <summary>
/// Finds yt-dlp and ffmpeg (the user's choice, else a downloaded update, else the bundled copy, else the PATH), checks the
/// projects' release pages for newer versions, and installs an update into the user's own folder after the user agreed.
/// Downloads are verified against the checksums published with the release before anything is replaced.
/// </summary>
public sealed class ToolManager : IToolUpdates
{
    /// <summary>ffmpeg is rebuilt daily and rarely matters: it is only offered when the build on offer is this much newer.</summary>
    public static readonly TimeSpan FfmpegMinimumAge = TimeSpan.FromDays(30);

    const string YtDlpApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
    const string YtDlpDownload = "https://github.com/yt-dlp/yt-dlp/releases/download";
    const string FfmpegApi = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest";
    const string FfmpegAsset = "ffmpeg-master-latest-win64-lgpl.zip";
    const string FfmpegDownload = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest";

    readonly EngineSettings _settings;
    readonly string _userTools;
    readonly string _appFolder;
    readonly Func<ManagedTool, string, CancellationToken, Task<string?>> _versionOf;
    readonly string? _pathVariable;
    HttpClient? _http;

    /// <param name="userTools">Where updates are installed (<c>&lt;data folder&gt;\tools</c>).</param>
    /// <param name="versionOf">How to ask a program for its version; tests replace it.</param>
    /// <param name="pathVariable">The PATH to search; null = the real one (tests pass an empty one).</param>
    public ToolManager(EngineSettings settings, string userTools, string? appFolder = null, HttpClient? http = null,
        Func<ManagedTool, string, CancellationToken, Task<string?>>? versionOf = null, string? pathVariable = null)
    {
        _pathVariable = pathVariable;
        _settings = settings;
        _userTools = userTools;
        _appFolder = appFolder ?? AppContext.BaseDirectory;
        _http = http;
        _versionOf = versionOf ?? DefaultVersionOf;
    }

    public static string NameOf(ManagedTool tool) => tool == ManagedTool.YtDlp ? "yt-dlp" : "ffmpeg";

    static string ExeOf(ManagedTool tool) => NameOf(tool) + (OperatingSystem.IsWindows() ? ".exe" : "");

    string ConfiguredPath(ManagedTool tool) => (tool == ManagedTool.YtDlp ? _settings.YtDlpPath : _settings.FfmpegPath) ?? "";

    string UserExe(ManagedTool tool) => Path.Combine(_userTools, NameOf(tool), ExeOf(tool));

    string? BundledExe(ManagedTool tool) =>
        new[] { Path.Combine(_appFolder, "tools", NameOf(tool), ExeOf(tool)), Path.Combine(_appFolder, NameOf(tool), ExeOf(tool)), Path.Combine(_appFolder, ExeOf(tool)) }
            .FirstOrDefault(File.Exists);

    // ---- which one is in use ----

    public string? Find(ManagedTool tool) =>
        tool == ManagedTool.YtDlp
            ? YtDlp.Locate(_settings.YtDlpPath, _appFolder, _pathVariable, _userTools)
            : Ffmpeg.Locate(_settings.FfmpegPath, _appFolder, _pathVariable, _userTools);

    public ToolSource SourceOf(ManagedTool tool)
    {
        var found = Find(tool);
        if (found == null) return ToolSource.None;
        if (!string.IsNullOrWhiteSpace(ConfiguredPath(tool))) return ToolSource.Custom;
        if (IsUnder(found, _userTools)) return ToolSource.User;
        if (IsUnder(found, _appFolder)) return ToolSource.Bundled;
        return ToolSource.Path;
    }

    static bool IsUnder(string file, string folder)
    {
        var full = Path.GetFullPath(file);
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> VersionAsync(ManagedTool tool, CancellationToken ct)
    {
        var exe = Find(tool);
        return exe == null ? null : await VersionOfProgramAsync(tool, exe, ct).ConfigureAwait(false);
    }

    /// <summary>The version of a program: what it says, or for ffmpeg the date of the build when that is what was recorded.</summary>
    async Task<string?> VersionOfProgramAsync(ManagedTool tool, string exe, CancellationToken ct)
    {
        if (tool == ManagedTool.Ffmpeg && ReadRecord(exe) is { } record) return record.Version;
        return await _versionOf(tool, exe, ct).ConfigureAwait(false);
    }

    static async Task<string?> DefaultVersionOf(ManagedTool tool, string exe, CancellationToken ct)
    {
        if (tool == ManagedTool.YtDlp) return await YtDlp.VersionAsync(exe, ct).ConfigureAwait(false);

        try
        {
            var info = new ProcessStartInfo(exe, "-version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var process = Process.Start(info)!;
            var first = (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false))?.Trim();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0 ? first : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    // ---- what was installed, and when ----

    sealed record Record(string Version, DateTimeOffset? Published);

    static string RecordPath(string exe) => Path.Combine(Path.GetDirectoryName(exe)!, "tool.json");

    static Record? ReadRecord(string exe)
    {
        try
        {
            var path = RecordPath(exe);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            DateTimeOffset? published = doc.RootElement.TryGetProperty("published", out var p) && p.ValueKind == JsonValueKind.String
                                        && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
            return version == null ? null : new Record(version, published);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static void WriteRecord(string exe, string version, DateTimeOffset? published)
    {
        var json = JsonSerializer.Serialize(new { version, published = published?.ToString("O", CultureInfo.InvariantCulture) });
        File.WriteAllText(RecordPath(exe), json);
    }

    /// <summary>When the build in <paramref name="exe"/> was made: the record, else the file's own date.</summary>
    static DateTimeOffset BuiltAt(string exe) => ReadRecord(exe)?.Published ?? new DateTimeOffset(File.GetLastWriteTimeUtc(exe), TimeSpan.Zero);

    // ---- looking for something newer ----

    public async Task<ToolUpdate?> CheckAsync(ManagedTool tool, CancellationToken ct)
    {
        await PruneStaleAsync(tool, ct).ConfigureAwait(false);

        var source = SourceOf(tool);
        if (source is ToolSource.Custom or ToolSource.Path) return null;   // not ours to update

        return tool == ManagedTool.YtDlp ? await CheckYtDlpAsync(ct).ConfigureAwait(false) : await CheckFfmpegAsync(ct).ConfigureAwait(false);
    }

    async Task<ToolUpdate?> CheckYtDlpAsync(CancellationToken ct)
    {
        using var json = await GetJsonAsync(YtDlpApi, ct).ConfigureAwait(false);
        var latest = json.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        if (string.IsNullOrEmpty(latest)) return null;

        var current = await VersionAsync(ManagedTool.YtDlp, ct).ConfigureAwait(false);   // null when not installed
        if (current != null && !IsNewerVersion(latest, current)) return null;

        return new ToolUpdate(ManagedTool.YtDlp, current, latest, $"{YtDlpDownload}/{latest}/yt-dlp.exe", $"{YtDlpDownload}/{latest}/SHA2-256SUMS", "yt-dlp.exe", null);
    }

    async Task<ToolUpdate?> CheckFfmpegAsync(CancellationToken ct)
    {
        using var json = await GetJsonAsync(FfmpegApi, ct).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("assets", out var assets)) return null;

        DateTimeOffset? built = null;
        foreach (var asset in assets.EnumerateArray())
            if (asset.TryGetProperty("name", out var n) && n.GetString() == FfmpegAsset && asset.TryGetProperty("updated_at", out var u)
                && DateTimeOffset.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                built = parsed;
        if (built == null) return null;

        var exe = Find(ManagedTool.Ffmpeg);
        string? current = null;
        if (exe != null)
        {
            if (built.Value - BuiltAt(exe) < FfmpegMinimumAge) return null;
            current = ReadRecord(exe)?.Version ?? BuiltAt(exe).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return new ToolUpdate(ManagedTool.Ffmpeg, current, built.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            $"{FfmpegDownload}/{FfmpegAsset}", $"{FfmpegDownload}/checksums.sha256", FfmpegAsset, built);
    }

    /// <summary>Version strings of yt-dlp are dates ("2026.08.19", "2026.08.19.1"): compare them number by number.</summary>
    public static bool IsNewerVersion(string candidate, string current)
    {
        static int[] Parts(string v) => v.Split('.', '-', ' ').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var a = Parts(candidate);
        var b = Parts(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }

        return false;
    }

    /// <summary>
    /// A downloaded copy older than the one that came with a newer program version would hide it: remove it, so the bundled
    /// one is used again.
    /// </summary>
    async Task PruneStaleAsync(ManagedTool tool, CancellationToken ct)
    {
        var user = UserExe(tool);
        var bundled = BundledExe(tool);
        if (bundled == null || !File.Exists(user)) return;

        bool bundledIsNewer;
        if (tool == ManagedTool.Ffmpeg)
        {
            bundledIsNewer = BuiltAt(bundled) >= BuiltAt(user);
        }
        else
        {
            var userVersion = await _versionOf(tool, user, ct).ConfigureAwait(false);
            var bundledVersion = await _versionOf(tool, bundled, ct).ConfigureAwait(false);
            bundledIsNewer = userVersion == null || (bundledVersion != null && !IsNewerVersion(userVersion, bundledVersion));
        }

        if (!bundledIsNewer) return;
        try
        {
            File.Delete(user);
            File.Delete(RecordPath(user));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // in use right now: it is tried again at the next check
        }
    }

    // ---- installing ----

    public async Task<string> UpdateAsync(ToolUpdate update, CancellationToken ct)
    {
        var tool = update.Tool;
        var folder = Path.Combine(_userTools, NameOf(tool));
        var target = UserExe(tool);
        var staged = Path.Combine(folder, "staged-" + ExeOf(tool));
        var temp = Path.Combine(Path.GetTempPath(), "shadenet-tool-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(temp);

        try
        {
            var download = Path.Combine(temp, update.AssetName);
            await DownloadAsync(update.AssetUrl, download, ct).ConfigureAwait(false);
            await VerifyAsync(download, update, ct).ConfigureAwait(false);

            if (tool == ManagedTool.YtDlp)
            {
                File.Copy(download, staged, overwrite: true);
            }
            else
            {
                using var zip = ZipFile.OpenRead(download);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(ExeOf(tool), StringComparison.OrdinalIgnoreCase) && e.FullName.Contains("bin/", StringComparison.OrdinalIgnoreCase))
                            ?? zip.Entries.FirstOrDefault(e => e.Name.Equals(ExeOf(tool), StringComparison.OrdinalIgnoreCase))
                            ?? throw new DownloadException(JobErrorKind.Tool, $"The download does not contain {ExeOf(tool)}.");
                entry.ExtractToFile(staged, overwrite: true);
            }

            // it must start before it replaces a program that works
            var version = await _versionOf(tool, staged, ct).ConfigureAwait(false);
            if (version == null)
            {
                TryDelete(staged);
                throw new DownloadException(JobErrorKind.Tool, $"The new {update.Name} does not start on this computer; the old one was kept.");
            }

            try
            {
                File.Move(staged, target, overwrite: true);
            }
            catch (IOException ex)
            {
                TryDelete(staged);
                throw new DownloadException(JobErrorKind.Tool, $"{update.Name} is in use and cannot be replaced right now. Try again when no download is joining or resolving.", ex);
            }

            var installed = tool == ManagedTool.Ffmpeg ? update.Latest : version;
            WriteRecord(target, installed, update.Published ?? DateTimeOffset.UtcNow);
            return installed;
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the system's temp cleaner gets it
            }
        }
    }

    async Task VerifyAsync(string file, ToolUpdate update, CancellationToken ct)
    {
        var sums = await GetStringAsync(update.ChecksumUrl, ct).ConfigureAwait(false);
        var line = sums.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.EndsWith(update.AssetName, StringComparison.OrdinalIgnoreCase) && l.Length > update.AssetName.Length + 60);
        if (line == null) throw new DownloadException(JobErrorKind.Tool, $"The release has no checksum for {update.AssetName}; nothing was installed.");

        var expected = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        string actual;
        await using (var stream = File.OpenRead(file))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();

        if (actual != expected)
            throw new DownloadException(JobErrorKind.DataCorruption, $"The download of {update.Name} does not match its published checksum; nothing was installed.");
    }

    // ---- network ----

    HttpClient Http => _http ??= CreateClient();

    HttpClient CreateClient()
    {
        var handler = HttpFetcher.CreateHandler(2, _settings);
        handler.AllowAutoRedirect = true;   // release downloads are redirects
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ShadeNetDownloader/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpStatusException((int)response.StatusCode);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
    }

    async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpStatusException((int)response.StatusCode);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    async Task DownloadAsync(string url, string path, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpStatusException((int)response.StatusCode);
        await using var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = File.Create(path);
        await network.CopyToAsync(file, ct).ConfigureAwait(false);
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
