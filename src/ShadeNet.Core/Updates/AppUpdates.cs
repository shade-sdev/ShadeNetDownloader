using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Net;

namespace ShadeNet.Core.Updates;

/// <summary>A problem the user can read: the update information is unreadable, the download does not match its checksum.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// What a release says about itself (the <c>update.json</c> asset the release workflow publishes next to the installer):
/// which version it is, the update package (the program without yt-dlp and ffmpeg) and the package's checksum.
/// </summary>
public sealed record UpdateManifest(int Schema, string Version, string Package, string Sha256, long Size, string? Repository);

/// <summary>A newer version than the one running, with where to get it.</summary>
public sealed record AppUpdate(Version Current, Version Latest, UpdateManifest Manifest, Uri PackageUrl, Uri? ReleasePage)
{
    public string LatestText => Latest.ToString(3);
}

public interface IAppUpdates
{
    /// <summary>The newer release, or <c>null</c> when this is the newest (or there is no release yet). Throws when GitHub cannot be asked.</summary>
    Task<AppUpdate?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads the package and verifies it (size and SHA-256); returns the file. Nothing unverified is ever left behind.</summary>
    Task<string> DownloadAsync(AppUpdate update, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>
/// Looks for a newer ShadeNet on the repository's releases and fetches its update package. The newest release is read from
/// <c>releases/latest/download/update.json</c>: a plain file download (no API, no rate limit), and "latest" is by definition
/// the newest published release that is neither a draft nor a pre-release.
/// </summary>
public sealed class AppUpdater : IAppUpdates, IDisposable
{
    static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,120}\.zip$", RegexOptions.Compiled);
    static readonly Regex Hex64 = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);
    const long MaxPackageBytes = 1L << 30;

    readonly string _repository;
    readonly Version _current;
    readonly string _folder;
    readonly Uri? _override;
    readonly HttpClient _http;
    readonly bool _ownsHttp;

    /// <param name="repository">"owner/name" on GitHub.</param>
    /// <param name="current">The running version.</param>
    /// <param name="updatesFolder">Where packages are kept while they are downloaded and applied (under the data folder).</param>
    /// <param name="manifestOverride">Tests and trials: another place for <c>update.json</c>, only on this computer (localhost).</param>
    public AppUpdater(string repository, Version current, string updatesFolder, EngineSettings? settings = null, Uri? manifestOverride = null, HttpClient? http = null)
    {
        if (!Regex.IsMatch(repository ?? "", @"^[\w.-]+/[\w.-]+$")) throw new ArgumentException("The repository must look like owner/name.", nameof(repository));
        if (manifestOverride != null && !IsLoopback(manifestOverride))
            throw new ArgumentException("An update address other than GitHub is only accepted on this computer.", nameof(manifestOverride));

        _repository = repository!;
        _current = Normalize(current);
        _folder = updatesFolder;
        _override = manifestOverride;
        if (http != null)
        {
            _http = http;
        }
        else
        {
            var handler = HttpFetcher.CreateHandler(2, settings ?? new EngineSettings());
            handler.AllowAutoRedirect = true;   // release downloads are redirects
            _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(15) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("ShadeNetDownloader/" + _current.ToString(3));
            _ownsHttp = true;
        }
    }

    public Uri ManifestUrl => _override ?? new Uri($"https://github.com/{_repository}/releases/latest/download/update.json");

    static bool IsLoopback(Uri uri) => uri.Scheme is "http" or "https" && (uri.IsLoopback || uri.Host == "localhost");

    /// <summary>1.2 and 1.2.0 are the same version; a version with a fourth part keeps three.</summary>
    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(core, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }

    public async Task<AppUpdate?> CheckAsync(CancellationToken ct)
    {
        using var response = await GetAsync(ManifestUrl, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;   // no release has been published yet
        if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub answered {(int)response.StatusCode} when asked for the newest version.");

        var manifest = Parse(await ReadLimitedAsync(response, 64 * 1024, ct).ConfigureAwait(false));
        TryParseVersion(manifest.Version, out var latest);
        if (latest <= _current) return null;

        var package = _override != null
            ? new Uri(_override, manifest.Package)
            : new Uri($"https://github.com/{_repository}/releases/download/v{latest.ToString(3)}/{manifest.Package}");
        Uri? page = _override != null ? null : new Uri($"https://github.com/{_repository}/releases/tag/v{latest.ToString(3)}");
        return new AppUpdate(_current, latest, manifest, package, page);
    }

    /// <summary>Reads and checks <c>update.json</c>; everything in it is untrusted until it has passed.</summary>
    public static UpdateManifest Parse(string json)
    {
        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new UpdateException("The update information is not readable.", ex);
        }

        if (manifest == null || manifest.Schema != 1) throw new UpdateException("The update information is from a newer kind of release that this version cannot read.");
        if (!TryParseVersion(manifest.Version, out _)) throw new UpdateException("The update information has no valid version.");
        if (!SafeName.IsMatch(manifest.Package ?? "")) throw new UpdateException("The update information names an unusable package file.");
        if (!Hex64.IsMatch(manifest.Sha256 ?? "")) throw new UpdateException("The update information has no valid checksum.");
        if (manifest.Size is <= 0 or > MaxPackageBytes) throw new UpdateException("The update information has an unlikely package size.");
        return manifest;
    }

    public async Task<string> DownloadAsync(AppUpdate update, IProgress<double>? progress, CancellationToken ct)
    {
        var folder = Path.Combine(_folder, update.LatestText);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, update.Manifest.Package);
        var part = target + ".part";

        // an earlier download that finished and matches is used as it is
        if (File.Exists(target) && await MatchesAsync(target, update.Manifest, ct).ConfigureAwait(false))
        {
            progress?.Report(1);
            return target;
        }

        TryDelete(target);
        TryDelete(part);

        try
        {
            using var response = await GetAsync(update.PackageUrl, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"The update could not be downloaded (the server answered {(int)response.StatusCode}).");

            var total = response.Content.Headers.ContentLength ?? update.Manifest.Size;
            await using (var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await network.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > MaxPackageBytes || done > update.Manifest.Size + 1024) throw new UpdateException("The download is larger than the release says; it was stopped.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report(Math.Min(1, done / (double)Math.Max(1, total)));
                }
            }

            if (!await MatchesAsync(part, update.Manifest, ct).ConfigureAwait(false))
                throw new UpdateException("The downloaded update does not match its published checksum; it was discarded and nothing was installed.");

            File.Move(part, target, overwrite: true);
            DeleteOtherVersions(update.LatestText);
            progress?.Report(1);
            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            TryDelete(part);
            throw new UpdateException("The update could not be downloaded: " + ex.Message, ex);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    static async Task<bool> MatchesAsync(string file, UpdateManifest manifest, CancellationToken ct)
    {
        if (new FileInfo(file).Length != manifest.Size) return false;
        await using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        return actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            return await _http.GetAsync(url, option, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new UpdateException("GitHub could not be reached: " + ex.Message, ex);
        }
    }

    static async Task<string> ReadLimitedAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > max) throw new UpdateException("The update information is unexpectedly large.");
            memory.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    void DeleteOtherVersions(string keep)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(_folder))
                if (!string.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // old packages in the way of nothing
        }
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

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
