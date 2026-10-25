using ShadeNet.Bridge;
using ShadeNet.Core;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Util;

namespace ShadeNetDownloader.Services.Browser;

/// <summary>A download the browser handed over, cleaned and ready to become a download.</summary>
public sealed record CaptureInfo(
    string Url,
    string? Referrer,
    string? Cookies,
    string? UserAgent,
    IReadOnlyDictionary<string, string> Headers,
    string? FileName,
    long? Size,
    string Source,
    string? AudioUrl = null,
    long? AudioSize = null,
    double? Duration = null,
    IReadOnlyList<string>? Pieces = null,
    string? Init = null,
    IReadOnlyList<string>? AudioPieces = null,
    string? AudioInit = null,
    IReadOnlyList<string>? Played = null);

/// <summary>What the processor needs from the UI; implemented by the app, faked in tests.</summary>
public interface ICaptureUi
{
    /// <summary>Show the Add dialog for this capture (the user decides).</summary>
    void ShowCapture(CaptureInfo capture);

    void Notify(string message, ToastKind kind);

    void BringToFront();

    /// <summary>Quit the program for real (not just hide it in the tray).</summary>
    void Quit();
}

/// <summary>
/// Decides what happens to a capture message from the extension: validate it (it is untrusted input, even though
/// only our extension can reach the pipe), match it against a download that is waiting for a fresh link, or add it.
/// Nothing here touches WPF.
/// </summary>
public sealed class CaptureProcessor
{
    static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Accept-Language", "Origin",
        // the browser's client hints: some sites take a Chrome User-Agent without them for a bot
        "Sec-CH-UA", "Sec-CH-UA-Mobile", "Sec-CH-UA-Platform",
    };

    const int MaxUrl = 8192;
    const int MaxCookies = 32 * 1024;
    const int MaxHeaderValue = 8192;
    const int MaxUserAgent = 512;
    const int MaxPieces = 5000;

    readonly DownloadManager _manager;
    readonly Func<BrowserSettings> _settings;
    readonly ICaptureUi _ui;
    readonly Func<DateTimeOffset> _now;
    readonly object _gate = new();
    readonly List<(Guid JobId, DateTimeOffset Expires)> _waiting = new();

    public CaptureProcessor(DownloadManager manager, Func<BrowserSettings> settings, ICaptureUi ui, Func<DateTimeOffset>? now = null)
    {
        _manager = manager;
        _settings = settings;
        _ui = ui;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>How long a download waits for the user to start it again from the page (IDM allows one at a time).</summary>
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(10);

    public bool IsWaitingForRefresh(Guid jobId)
    {
        lock (_gate)
        {
            Prune();
            return _waiting.Any(w => w.JobId == jobId);
        }
    }

    /// <summary>
    /// The next capture that is the same file as this failed download replaces its address instead of becoming a new
    /// download (IDM's "Refresh download address": the user starts the download again from the page).
    /// </summary>
    public void ExpectRefresh(Guid jobId)
    {
        lock (_gate)
        {
            // IDM: "Only one expired download should use this feature at the same time."
            _waiting.Clear();
            _waiting.Add((jobId, _now() + RefreshWindow));
        }
    }

    void Prune() => _waiting.RemoveAll(w => w.Expires <= _now());

    public async Task<CaptureResult> HandleAsync(CaptureRequest request)
    {
        try
        {
            return await HandleCoreAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result(request, false, CaptureReasons.Error + ": " + ex.Message);
        }
    }

    async Task<CaptureResult> HandleCoreAsync(CaptureRequest request)
    {
        var settings = _settings();
        if (!settings.Enabled) return Result(request, false, CaptureReasons.Disabled);

        var capture = Sanitize(request);
        if (capture == null) return Result(request, false, CaptureReasons.Invalid);

        var host = new Uri(capture.Url).Host;
        if (settings.ExcludedHosts.Any(p => HostMatches(host, p))) return Result(request, false, CaptureReasons.Excluded);
        if (capture.Size is { } size && settings.MinSizeBytes > 0 && size < settings.MinSizeBytes)
            return Result(request, false, CaptureReasons.Excluded);

        // 1. a download is waiting for exactly this: give it the new address
        if (TryMatchWaiting(capture) is { } waiting)
        {
            Refresh(waiting, capture);
            _ui.Notify($"New link received for {waiting.FileName}.", ToastKind.Success);
            return Result(request, true, CaptureReasons.Refreshed, waiting.Id);
        }

        // 2. the same address is already known
        if (_manager.FindDuplicate(capture.Url) is { } existing)
        {
            if (existing.State == DownloadState.Failed)
            {
                // The user retries a failed download from the browser: that is the fresh session it was missing.
                Refresh(existing, capture);
                _ui.Notify($"Resuming {existing.FileName} with your browser session.", ToastKind.Success);
                return Result(request, true, CaptureReasons.Refreshed, existing.Id);
            }

            if (existing.State != DownloadState.Completed)
            {
                _ui.Notify($"{existing.FileName} is already in your list.", ToastKind.Info);
                _ui.BringToFront();
                return Result(request, true, CaptureReasons.Duplicate, existing.Id);
            }
        }

        // 3. a new download
        // A video page (YouTube, or any page the extension sends as "page": its video is not a file with an address,
        // Facebook's for example) is not a file: yt-dlp has to say what is behind it, which takes seconds, so the dialog does that.
        if (capture.Source == "page" || ShadeNet.Core.Sites.YtDlp.IsSupportedPage(capture.Url))
        {
            _ui.ShowCapture(capture);
            _ui.BringToFront();
            return Result(request, true, CaptureReasons.Added);
        }

        if (settings.Mode == CaptureMode.StartImmediately)
        {
            // a video stream the extension found (or an .m3u8 address): the best quality, saved as one video file
            var isTracks = capture.Source == "tracks";
            var isRecord = capture.Source == "record";   // an endless live response (an FLV pull): ffmpeg records it
            var isPieces = capture.Source == "pieces";   // pieces a player asked for with no playlist: joined like a playlist's
            var isStream = isTracks || isRecord || isPieces || capture.Source == "media" || DownloadManager.IsStreamAddress(capture.Url);
            var job = _manager.Add(new AddDownloadRequest
            {
                Stream = isTracks ? new StreamChoice(Direct: true, AudioUrl: capture.AudioUrl, Seconds: capture.Duration ?? 0)
                    : isRecord ? new StreamChoice(Subtitles: false, Record: true)
                    : isPieces ? PiecesChoice(capture)
                    : isStream ? new StreamChoice(AudioUrl: capture.Source == "media" ? capture.AudioUrl : null, Played: capture.Played) : null,
                // a stream, and a video file the page's own button sent, are named after the video, not after the address
                FileName = (isStream || capture.Source == "video") && !string.IsNullOrWhiteSpace(capture.FileName) ? capture.FileName : null,
                Url = capture.Url,
                Referer = capture.Referrer,
                Cookies = capture.Cookies,
                UserAgent = capture.UserAgent,
                Headers = capture.Headers.ToDictionary(h => h.Key, h => h.Value),
                Mode = AddMode.Start,
            });
            _ui.Notify($"Downloading {job.FileName}", ToastKind.Success);
            return Result(request, true, CaptureReasons.Added, job.Id);
        }

        _ui.ShowCapture(capture);
        _ui.BringToFront();
        return Result(request, true, CaptureReasons.Added);
    }

    DownloadJob? TryMatchWaiting(CaptureInfo capture)
    {
        lock (_gate)
        {
            Prune();
            foreach (var (jobId, _) in _waiting)
            {
                var job = _manager.Find(jobId);
                if (job == null || job.IsActive || job.State == DownloadState.Completed) continue;
                if (!SameFile(job, capture)) continue;

                _waiting.RemoveAll(w => w.JobId == jobId);
                return job;
            }
        }

        return null;
    }

    /// <summary>Same size when both are known, otherwise the same name; the same rule IDM's refresh uses.</summary>
    static bool SameFile(DownloadJob job, CaptureInfo capture)
    {
        if (job.TotalSize is { } known && capture.Size is { } size) return known == size;
        if (string.IsNullOrEmpty(capture.FileName)) return false;
        return string.Equals(BaseName(job.FileName), BaseName(capture.FileName), StringComparison.OrdinalIgnoreCase);
    }

    static string BaseName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @" \(\d+\)(\.[^.]*)?$", "$1");

    void Refresh(DownloadJob job, CaptureInfo capture) =>
        _manager.RefreshAddress(job.Id, capture.Url, capture.Cookies, capture.Referrer, capture.UserAgent, capture.Headers);

    /// <summary>Everything from the pipe is untrusted: keep only well-formed values within sane limits.</summary>
    internal static CaptureInfo? Sanitize(CaptureRequest request)
    {
        if (!IsWebUrl(request.Url)) return null;

        string? referrer = IsWebUrl(request.Referrer) ? request.Referrer : null;

        string? cookies = null;
        if (!string.IsNullOrWhiteSpace(request.Cookies) && request.Cookies.Length <= MaxCookies)
            cookies = CookieHeader.Normalize(request.Cookies);

        string? userAgent = request.UserAgent is { Length: > 0 and <= MaxUserAgent } ua && ua.All(c => c >= ' ' && c != 127) ? ua : null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Headers != null)
        {
            foreach (var (name, value) in request.Headers)
            {
                if (!AllowedHeaders.Contains(name)) continue;
                if (string.IsNullOrEmpty(value) || value.Length > MaxHeaderValue || value.Any(c => c is '\r' or '\n')) continue;
                headers[name] = value;
            }
        }

        string? fileName = string.IsNullOrWhiteSpace(request.FileName) ? null : FileNameHelper.Sanitize(request.FileName);
        long? size = request.Size is > 0 ? request.Size : null;

        // "media": a stream the extension saw playing; "tracks": picture and sound the player fetched as separate files;
        // "video": a plain video file the page plays, sent with the video's name;
        // "page": the address of a page whose video has no address of its own (yt-dlp is asked about it);
        // "record": a live stream that is one endless response (an FLV pull), recorded as it arrives;
        // "pieces": what a player that keeps its list of pieces itself asked for (see CaptureRequest.Pieces)
        var source = request.Source is "contextmenu" or "media" or "tracks" or "video" or "page" or "record" or "pieces" ? request.Source : "download";
        // (the sound as a second playlist belongs to a "media" stream; as a second file, to "tracks")
        string? audioUrl = source is "tracks" or "media" && IsWebUrl(request.AudioUrl) ? request.AudioUrl : null;
        long? audioSize = audioUrl != null && request.AudioSize is > 0 ? request.AudioSize : null;
        double? duration = source is "tracks" or "pieces" && request.Duration is > 0 and < 7 * 24 * 3600 ? request.Duration : null;

        IReadOnlyList<string>? pieces = null, audioPieces = null;
        string? init = null, audioInit = null;
        if (source == "pieces")
        {
            pieces = WebUrls(request.Pieces);
            if (pieces == null) return null;   // nothing that could be joined
            init = IsWebUrl(request.Init) ? request.Init : null;
            audioPieces = WebUrls(request.AudioPieces);
            audioInit = audioPieces != null && IsWebUrl(request.AudioInit) ? request.AudioInit : null;
        }

        // the addresses the player loaded a list of qualities' playlists from (with a key of its own, perhaps)
        var played = source == "media" ? WebUrls(request.Played) : null;

        return new CaptureInfo(request.Url, referrer, cookies, userAgent, headers, fileName, size, source, audioUrl, audioSize, duration,
            pieces, init, audioPieces, audioInit, played);
    }

    static IReadOnlyList<string>? WebUrls(string[]? urls) => urls is { Length: > 0 and <= MaxPieces } && urls.All(IsWebUrl) ? urls : null;

    /// <summary>The stream job for pieces a player asked for: downloaded in this order and joined after their initialisation part.</summary>
    internal static StreamChoice PiecesChoice(CaptureInfo capture) =>
        new(Subtitles: false, Seconds: capture.Duration ?? 0, Pieces: capture.Pieces, Init: capture.Init, AudioPieces: capture.AudioPieces, AudioInit: capture.AudioInit);

    static bool IsWebUrl(string? url) =>
        url is { Length: > 0 and <= MaxUrl } && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    internal static bool HostMatches(string host, string pattern)
    {
        var p = pattern.Trim().TrimStart('*').TrimStart('.');
        return p.Length > 0 && (host.Equals(p, StringComparison.OrdinalIgnoreCase)
                                || host.EndsWith("." + p, StringComparison.OrdinalIgnoreCase));
    }

    static CaptureResult Result(CaptureRequest request, bool accepted, string reason, Guid? jobId = null) =>
        new() { Id = request.Id, Accepted = accepted, Reason = reason, JobId = jobId?.ToString() };
}
