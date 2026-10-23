using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;

namespace ShadeNet.Core.Sites;

/// <summary>One way to download a video: which quality, and the addresses of its parts.</summary>
/// <param name="VideoUrl">The picture (or a file with both picture and sound; or an HLS playlist when <paramref name="IsHls"/>).</param>
/// <param name="AudioUrl">The sound, when it is a file of its own.</param>
/// <param name="Size">The expected download in bytes (0 = unknown).</param>
/// <param name="Referer">The page yt-dlp says the file must be asked for from (TikTok's servers answer 403 without it).</param>
/// <param name="Cookies">The cookies yt-dlp collected for this file's address, as a <c>Cookie</c> header value (TikTok's servers want them too).
/// These are what yt-dlp itself got from the site, not the user's browser session.</param>
/// <param name="Headers">Any other header yt-dlp says the site's servers want for this file (the ones ShadeNet sets itself are left out).</param>
public sealed record YtDlpOption(string Label, int Height, string VideoUrl, string? AudioUrl, string Extension, long Size, string? UserAgent, bool IsHls,
    string? Referer = null, string? Cookies = null, IReadOnlyDictionary<string, string>? Headers = null);

public sealed record YtDlpInfo(string Title, double Seconds, bool IsLive, IReadOnlyList<YtDlpOption> Options);

/// <summary>
/// Turns the address of a page on a video site (YouTube first) into the addresses of the files behind it, by asking
/// <c>yt-dlp</c>, which follows the site's changes. ShadeNet's own engine then does the downloading (parallel ranges,
/// speed limits, pause and resume, joining with ffmpeg); yt-dlp is only asked "what is behind this page?".
/// </summary>
public static partial class YtDlp
{
    static readonly string Exe = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

    /// <summary>The yt-dlp to use: the chosen one (file or folder), one next to the program (or in <c>tools\yt-dlp</c>), or one on the PATH.</summary>
    public static string? Locate(string? configured, string? appFolder = null, string? pathVariable = null, string? userFolder = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var chosen = configured.Trim().Trim('"');
            if (File.Exists(chosen)) return chosen;
            if (Directory.Exists(chosen) && File.Exists(Path.Combine(chosen, Exe))) return Path.Combine(chosen, Exe);
            return null;   // an explicit choice that does not exist is not silently replaced
        }

        // a copy ShadeNet downloaded as an update (in the user's own folder) comes before the one that was installed with the program
        if (!string.IsNullOrWhiteSpace(userFolder) && File.Exists(Path.Combine(userFolder, "yt-dlp", Exe))) return Path.Combine(userFolder, "yt-dlp", Exe);

        appFolder ??= AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(appFolder, Exe), Path.Combine(appFolder, "tools", "yt-dlp", Exe), Path.Combine(appFolder, "yt-dlp", Exe) })
            if (File.Exists(candidate)) return candidate;

        foreach (var folder in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim().Trim('"'), Exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // a malformed PATH entry
            }
        }

        return null;
    }

    /// <summary>A page yt-dlp should be asked about: a YouTube video, short, live stream or youtu.be link.</summary>
    public static bool IsSupportedPage(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host is "youtu.be") return uri.AbsolutePath.Length > 1;
        if (host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or "youtube-nocookie.com" or "www.youtube-nocookie.com")) return false;

        var path = uri.AbsolutePath;
        return path.StartsWith("/shorts/", StringComparison.Ordinal) || path.StartsWith("/live/", StringComparison.Ordinal)
               || path.StartsWith("/embed/", StringComparison.Ordinal)
               || (path == "/watch" && uri.Query.Contains("v=", StringComparison.Ordinal));
    }

    // ---- asking yt-dlp ----

    /// <param name="cookies">The browser's sign-in for the page's site, as a <c>Cookie</c> header value. It goes to yt-dlp in a temporary file that is
    /// deleted when yt-dlp is done, and only this once: a live stream or a private video cannot be read without a signed-in session.</param>
    public static async Task<YtDlpInfo> ResolveAsync(string exe, string pageUrl, CancellationToken ct, TimeSpan? timeout = null, string? cookies = null)
    {
        var arguments = new List<string> { "-J", "--no-playlist", "--no-warnings", "--no-update" };
        string? cookieFile = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(cookies) && WriteCookieFile(pageUrl, cookies) is { } file)
            {
                cookieFile = file;
                arguments.AddRange(new[] { "--cookies", file });
            }

            arguments.Add(pageUrl);
            return await ResolveCoreAsync(exe, arguments, ct, timeout).ConfigureAwait(false);
        }
        finally
        {
            if (cookieFile != null)
            {
                try
                {
                    File.Delete(cookieFile);
                }
                catch (IOException)
                {
                    // the user's temp folder: it is cleaned up with it
                }
            }
        }
    }

    /// <summary>
    /// A cookie file in the Netscape format yt-dlp reads, for the whole site of <paramref name="pageUrl"/> (its sub-domains too: TikTok's
    /// live API is on another host). <c>null</c> when the address or the cookies cannot be used.
    /// </summary>
    internal static string? WriteCookieFile(string pageUrl, string cookies)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps && page.Scheme != Uri.UriSchemeHttp) return null;

        var domain = Util.HostNames.IsIp(page.Host) ? page.Host : "." + Util.HostNames.RegistrableDomain(page.Host);
        var expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();   // the file lives for one run; the jar needs a date in the future
        var lines = new List<string> { "# Netscape HTTP Cookie File" };
        foreach (var pair in cookies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var name = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();
            if (name.Any(c => c <= ' ' || c == 127) || value.Any(c => c < ' ' || c == 127)) continue;   // a tab or a line break would break the file
            lines.Add($"{domain}\t{(domain.StartsWith('.') ? "TRUE" : "FALSE")}\t/\t{(page.Scheme == Uri.UriSchemeHttps ? "TRUE" : "FALSE")}\t{expires}\t{name}\t{value}");
        }

        if (lines.Count == 1) return null;

        var path = Path.Combine(Path.GetTempPath(), "shadenet-" + Guid.NewGuid().ToString("N")[..12] + ".cookies.txt");
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return path;
    }

    static async Task<YtDlpInfo> ResolveCoreAsync(string exe, IEnumerable<string> arguments, CancellationToken ct, TimeSpan? timeout)
    {
        var (code, output, error) = await RunAsync(exe, arguments, ct, timeout ?? TimeSpan.FromSeconds(120))
            .ConfigureAwait(false);

        if (code != 0)
        {
            var reason = error.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                         ?? error.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? $"exit code {code}";
            throw new DownloadException(JobErrorKind.Tool, "yt-dlp could not read this page: " + reason.Replace("ERROR: ", "") + HintFor(reason));
        }

        return ParseInfo(output);
    }

    static string HintFor(string reason) =>
        reason.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)
            ? " (yt-dlp does not know this site, so ShadeNet cannot read this page.)"
            : reason.Contains("not currently live", StringComparison.OrdinalIgnoreCase) || reason.Contains("is not live", StringComparison.OrdinalIgnoreCase)
            ? " (If it is live, the site may only show it to a signed-in browser: sign in there, and keep \"Use my browser's sign-in for video pages\" on in Settings > Browser.)"
            : reason.Contains("Sign in", StringComparison.OrdinalIgnoreCase) || reason.Contains("bot", StringComparison.OrdinalIgnoreCase)
            ? " (The site wants a signed-in browser for this video.)"
            : reason.Contains("not available", StringComparison.OrdinalIgnoreCase) || reason.Contains("Unable to extract", StringComparison.OrdinalIgnoreCase)
                ? " (yt-dlp may be out of date: Settings > Downloads > Update.)"
                : "";

    public static async Task<string?> VersionAsync(string exe, CancellationToken ct)
    {
        try
        {
            var (code, output, _) = await RunAsync(exe, new[] { "--version" }, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            return code == 0 ? output.Trim() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    static async Task<(int Code, string Output, string Error)> RunAsync(string exe, IEnumerable<string> arguments, CancellationToken ct, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        info.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new DownloadException(JobErrorKind.Tool, "yt-dlp could not be started: " + ex.Message, ex);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it ended by itself
            }

            if (ct.IsCancellationRequested) throw;
            throw new DownloadException(JobErrorKind.Tool, "yt-dlp took too long to answer.");
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    // ---- reading its answer ----

    /// <summary>The <c>-J</c> output as qualities to choose from, best first. Pure, so it is tested with files.</summary>
    public static YtDlpInfo ParseInfo(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new DownloadException(JobErrorKind.Tool, "yt-dlp's answer could not be read: " + ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var title = CleanTitle(Str(root, "title") ?? "video");
            var seconds = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            var live = root.TryGetProperty("is_live", out var l) && l.ValueKind == JsonValueKind.True;

            var formats = new List<Format>();
            if (root.TryGetProperty("formats", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var f in list.EnumerateArray())
                    if (Format.From(f) is { } format) formats.Add(format);

            var options = live ? new List<YtDlpOption>() : BuildDirect(formats);
            if (options.Count == 0) options = BuildHls(formats);
            if (options.Count == 0) options = BuildDash(formats);   // live or not: Facebook, Instagram, X ... may offer nothing else
            if (options.Count == 0) throw new DownloadException(JobErrorKind.Unsupported, "yt-dlp found nothing downloadable on this page.");

            return new YtDlpInfo(title, seconds, live, options.OrderByDescending(o => o.Height).ToList());
        }
    }

    /// <summary>Social sites put counters and their own name around a title: "119 reactions · 1.4K shares | Name on Reels | Facebook".</summary>
    public static string CleanTitle(string title)
    {
        var t = Counters().Replace(title, "");
        t = SiteSuffix().Replace(t, "");
        return t.Trim().Length > 0 ? t.Trim() : title;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*[\d.,]+\s*[KMB]?\s+(views?|reactions?|likes?)(\s*[·•]\s*[\d.,]+\s*[KMB]?\s+(shares?|reactions?|comments?|likes?|views?))*\s*\|\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Counters();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\|\s*(Facebook|Instagram|TikTok)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SiteSuffix();

    sealed record Format(string Id, string Url, string Protocol, string Ext, string VCodec, string ACodec, int Height, double Fps, double Tbr, long Size, string? UserAgent, double Quality = 0,
        string? Referer = null, string? Cookies = null, IReadOnlyDictionary<string, string>? Headers = null, string? ManifestUrl = null)
    {
        public bool HasVideo => VCodec != "none" && VCodec.Length > 0;
        public bool HasAudio => ACodec != "none" && ACodec.Length > 0;
        public bool IsFile => Protocol is "https" or "http";
        public bool IsHls => Protocol.StartsWith("m3u8", StringComparison.Ordinal);
        public bool IsDash => Protocol == "http_dash_segments";

        public static Format? From(JsonElement f)
        {
            var url = Str(f, "url");
            if (string.IsNullOrEmpty(url)) return null;
            string? ua = null, referer = null;
            Dictionary<string, string>? extra = null;
            if (f.TryGetProperty("http_headers", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                if (Str(h, "User-Agent") is { Length: > 0 } agent) ua = agent;
                if (Str(h, "Referer") is { Length: > 0 } page) referer = page;
                extra = ExtraHeaders(h);
            }
            var ext = Str(f, "ext") ?? "";
            var vcodec = Str(f, "vcodec");
            var acodec = Str(f, "acodec");
            // Some sites (Facebook: "hd - unknown") say nothing about the codecs. A progressive file in a video container is then
            // what yt-dlp itself would pick as "best": picture and sound together.
            if (vcodec == null && acodec == null && ext is "mp4" or "webm" or "mkv" or "mov" or "flv" or "m4v") (vcodec, acodec) = ("unknown", "unknown");
            return new Format(Str(f, "format_id") ?? "", url, Str(f, "protocol") ?? "", ext, vcodec ?? "none", acodec ?? "none",
                (int)Num(f, "height"), Num(f, "fps"), Num(f, "tbr"), (long)(Num(f, "filesize") > 0 ? Num(f, "filesize") : Num(f, "filesize_approx")), ua, Num(f, "quality"),
                referer, CookieHeaderFrom(Str(f, "cookies")), extra, Str(f, "manifest_url"));
        }
    }

    /// <summary>
    /// For each height: the picture format that plays everywhere first (H.264), then VP9, then AV1; and a sound track of the
    /// same family (m4a for MP4, otherwise the best). Files with picture and sound together are used when nothing is split.
    /// </summary>
    static List<YtDlpOption> BuildDirect(List<Format> formats)
    {
        var files = formats.Where(f => f.IsFile).ToList();
        var videos = files.Where(f => f.HasVideo && !f.HasAudio).ToList();
        var audios = files.Where(f => f.HasAudio && !f.HasVideo).ToList();
        var options = new List<YtDlpOption>();

        static int Preference(Format f) => f.VCodec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) ? 0 : f.VCodec.StartsWith("vp9", StringComparison.OrdinalIgnoreCase) || f.VCodec.StartsWith("vp09", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

        foreach (var group in videos.GroupBy(v => v.Height).Where(g => g.Key > 0))
        {
            var video = group.OrderBy(Preference).ThenByDescending(v => v.Fps).ThenByDescending(v => v.Tbr).First();
            Format? audio = null;
            if (audios.Count > 0)
            {
                var wantM4a = video.Ext == "mp4";
                audio = audios.Where(a => wantM4a ? a.Ext is "m4a" or "mp4" : a.Ext is "webm" or "opus").OrderByDescending(a => a.Tbr).FirstOrDefault()
                        ?? audios.OrderByDescending(a => a.Tbr).First();
            }

            var mp4 = video.Ext == "mp4" && (audio == null || audio.Ext is "m4a" or "mp4");
            var fps = video.Fps > 30 ? $"{(int)Math.Round(video.Fps)}" : "";
            var codec = Preference(video) switch { 0 => "H.264", 1 => "VP9", _ => "AV1" };
            var size = video.Size + (audio?.Size ?? 0);
            var label = $"{video.Height}p{fps} · {codec}" + (size > 0 ? $" · about {FormatSize(size)}" : "");
            options.Add(new YtDlpOption(label, video.Height, video.Url, audio?.Url, mp4 ? ".mp4" : ".mkv", size, video.UserAgent, false, video.Referer, video.Cookies, video.Headers));
        }

        if (options.Count == 0)
        {
            // nothing is split: picture and sound already together
            // (a site that does not say how tall a file is, Facebook's "hd" and "sd", is told apart by its own name, best first)
            foreach (var group in files.Where(f => f.HasVideo && f.HasAudio).GroupBy(f => f.Height > 0 ? f.Height.ToString() : f.Id).OrderByDescending(g => g.Max(x => x.Height)).ThenByDescending(g => g.Max(x => x.Quality)))
            {
                var f = group.OrderByDescending(x => x.Tbr).First();
                var name = f.Height > 0 ? $"{f.Height}p" : f.Id.Length > 0 ? f.Id.ToUpperInvariant() : "video";
                options.Add(new YtDlpOption($"{name} · {f.Ext}" + (f.Size > 0 ? $" · about {FormatSize(f.Size)}" : ""), f.Height, f.Url, null,
                    f.Ext is "mp4" or "mkv" or "webm" ? (f.Ext == "webm" ? ".mkv" : "." + f.Ext) : ".mp4", f.Size, f.UserAgent, false, f.Referer, f.Cookies, f.Headers));
            }
        }

        return options;
    }

    /// <summary>Streams delivered as HLS (live broadcasts, some videos): the playlist per height goes to the HLS engine.</summary>
    static List<YtDlpOption> BuildHls(List<Format> formats) =>
        formats.Where(f => f.IsHls && f.HasVideo).GroupBy(f => f.Height).Select(g =>
        {
            var f = g.OrderByDescending(x => x.Tbr).First();
            return new YtDlpOption($"{f.Height}p · stream" + (f.Height == 0 ? " (best)" : ""), f.Height, f.Url, null, ".mp4", 0, f.UserAgent, true, f.Referer, f.Cookies, f.Headers);
        }).ToList();

    /// <summary>
    /// A DASH presentation (yt-dlp lists every picture and sound of it as a format of its own, all pointing at one manifest): one
    /// stream per manifest, which the stream engine reads itself (a live one is recorded until it ends, like a live playlist).
    /// </summary>
    static List<YtDlpOption> BuildDash(List<Format> formats) =>
        formats.Where(f => f.IsDash && f.HasVideo).GroupBy(f => f.ManifestUrl ?? f.Url).Select(g =>
        {
            var f = g.OrderByDescending(x => x.Height).ThenByDescending(x => x.Tbr).First();
            return new YtDlpOption($"{f.Height}p · stream" + (f.Height == 0 ? " (best)" : ""), f.Height, g.Key, null, ".mp4", 0, f.UserAgent, true, f.Referer, f.Cookies, f.Headers);
        }).ToList();

    static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// The headers yt-dlp sends that ShadeNet does not set (or handle) itself: a site may want a custom one (an <c>Origin</c>, a token),
    /// and a download that leaves it out is refused. The ones a request carries anyway, or that only make sense for yt-dlp's own request, stay out.
    /// </summary>
    static Dictionary<string, string>? ExtraHeaders(JsonElement headers)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "User-Agent", "Referer", "Cookie", "Host", "Accept", "Accept-Language", "Accept-Encoding", "Connection", "Keep-Alive", "Upgrade-Insecure-Requests",
            "Content-Length", "Content-Type", "Transfer-Encoding", "TE", "Range", "If-Range", "Expect",
        };

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers.EnumerateObject())
        {
            if (found.Count >= 20 || header.Value.ValueKind != JsonValueKind.String) continue;
            var name = header.Name;
            var value = header.Value.GetString() ?? "";
            if (skip.Contains(name) || name.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Length == 0 || value.Length == 0 || value.Length > 2000 || name.Any(c => c <= ' ' || c == ':' || c > '~') || value.Any(c => c < ' ' || c == 127)) continue;
            found[name] = value;
        }

        return found.Count == 0 ? null : found;
    }

    static readonly string[] CookieAttributes = { "domain", "path", "expires", "max-age", "version", "samesite", "comment" };

    /// <summary>
    /// yt-dlp writes a format's cookies the way a server sets them: <c>name=value; Domain=.site.com; Path=/; Secure; name2=value2; ...</c>,
    /// with a value that has special characters in quotes. A request needs only <c>name=value; name2=value2</c>, unquoted.
    /// </summary>
    public static string? CookieHeaderFrom(string? cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies)) return null;

        var pairs = new List<string>();
        foreach (var part in cookies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;                                            // a flag such as Secure or HttpOnly
            var name = part[..eq].Trim();
            if (CookieAttributes.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            pairs.Add($"{name}={UnquoteCookie(part[(eq + 1)..].Trim())}");
        }

        return pairs.Count == 0 ? null : string.Join("; ", pairs);
    }

    /// <summary>The reverse of Python's cookie quoting: the quotes go, <c>\"</c> and <c>\\</c> become the character, <c>\ooo</c> (octal) too.</summary>
    static string UnquoteCookie(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;
        return System.Text.RegularExpressions.Regex.Replace(value[1..^1], @"\\(?:([0-3][0-7]{2})|(.))",
            m => m.Groups[1].Success ? ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString() : m.Groups[2].Value);
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
