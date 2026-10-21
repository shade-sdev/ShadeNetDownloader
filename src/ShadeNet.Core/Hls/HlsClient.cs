using System.Text;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;

namespace ShadeNet.Core.Hls;

/// <summary>What the Add dialog shows about a stream before it is downloaded.</summary>
/// <param name="Url">The playlist as the server finally delivered it.</param>
/// <param name="Variants">The qualities on offer, best first; empty when the address is a single stream.</param>
public sealed record HlsStreamInfo(
    Uri Url,
    IReadOnlyList<HlsVariant> Variants,
    double DurationSeconds,
    bool IsLive,
    HlsEncryption Encryption,
    string? EncryptionNote,
    bool SeparateAudio,
    string SuggestedName,
    IReadOnlyList<string>? Subtitles = null,
    bool IsDash = false)
{
    /// <summary>Why this stream cannot be downloaded, or <c>null</c> when it can. (A live stream can: it is recorded.)</summary>
    public string? Problem =>
        Encryption == HlsEncryption.Unsupported
            ? $"This stream is protected ({EncryptionNote ?? "DRM"}) and cannot be downloaded."
            : null;
}

/// <summary>Fetches playlists with the identity the browser had (cookies, referrer, headers).</summary>
internal static class HlsClient
{
    const int MaxPlaylistBytes = 16 * 1024 * 1024;

    /// <summary>The address says it is a playlist: <c>.m3u8</c> path or an HLS content type.</summary>
    public static bool LooksLikeHls(string url, string? contentType)
    {
        if (contentType != null)
        {
            var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
            if (type is "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "audio/mpegurl" or "audio/x-mpegurl" or "application/mpegurl" or "application/dash+xml")
                return true;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                   || uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<(string Text, Uri Url)> FetchTextAsync(HttpClient http, RequestSpec spec, string defaultUserAgent, CancellationToken ct)
    {
        var (response, finalUrl) = await HttpFetcher.GetAsync(http, spec, defaultUserAgent, null, ct).ConfigureAwait(false);
        using (response)
        {
            if ((int)response.StatusCode >= 400)
                throw new HttpStatusException((int)response.StatusCode, HttpFetcher.RetryAfter(response));

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxPlaylistBytes) throw new HlsFormatException("The playlist is unreasonably large.");
            }

            return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), new Uri(finalUrl));
        }
    }

    public static async Task<HlsStreamInfo> DescribeAsync(HttpClient http, RequestSpec spec, string defaultUserAgent, CancellationToken ct)
    {
        var (text, url) = await FetchTextAsync(http, spec, defaultUserAgent, ct).ConfigureAwait(false);
        var name = SuggestName(url);

        if (DashParser.LooksLikeManifest(text))
        {
            var dash = DashParser.Parse(text, url);
            var variants = dash.VideoVariants().Select(v => v.Variant).OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).ToList();
            return new HlsStreamInfo(url, variants, dash.IsDynamic ? 0 : dash.DurationSeconds, dash.IsDynamic,
                dash.Protection != null ? HlsEncryption.Unsupported : HlsEncryption.None, dash.Protection,
                dash.Audio.Count > 0 && dash.Video.Count > 0, name, dash.Text.Select(t => t.Name).ToList(), IsDash: true);
        }

        var playlist = HlsParser.Parse(text, url);

        if (playlist is HlsMasterPlaylist master)
        {
            var variants = master.Variants.OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).ToList();

            // One look at the best quality's own playlist tells the length and whether it is live or protected.
            var best = HlsChoice.Best(variants);
            var (mediaText, mediaUrl) = await FetchTextAsync(http, spec with { Url = best.Url.AbsoluteUri }, defaultUserAgent, ct).ConfigureAwait(false);
            var media = (HlsMediaPlaylist)HlsParser.Parse(mediaText, mediaUrl);
            var separateAudio = HlsChoice.AudioFor(master, best) != null;
            return Describe(url, variants, media, separateAudio, name) with
            {
                Subtitles = HlsChoice.SubtitlesFor(master, best).Select(s => s.Name).ToList(),
            };
        }

        return Describe(url, Array.Empty<HlsVariant>(), (HlsMediaPlaylist)playlist, false, name);
    }

    static HlsStreamInfo Describe(Uri url, IReadOnlyList<HlsVariant> variants, HlsMediaPlaylist media, bool separateAudio, string name)
    {
        var note = media.Segments.Select(s => s.Key).FirstOrDefault(k => k?.Method == HlsEncryption.Unsupported)?.Description;
        return new HlsStreamInfo(url, variants, media.TotalDuration, media.IsLive, media.Encryption, note, separateAudio, name);
    }

    static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "index", "master", "playlist", "manifest", "stream", "chunklist", "main", "prog_index", "hls", "video", "media",
    };

    /// <summary>A file name (no extension) for a stream address: the last path part, unless that is a generic word like "index".</summary>
    public static string SuggestName(Uri url)
    {
        var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (parts.Count > 0)
        {
            var last = Path.GetFileNameWithoutExtension(parts[^1]);
            if (last.Length > 0 && !GenericNames.Contains(last)) return last;

            // ".../my-movie/index.m3u8": the folder is the better name
            for (var i = parts.Count - 2; i >= 0; i--)
                if (parts[i].Length > 2 && !GenericNames.Contains(parts[i]) && !parts[i].All(char.IsDigit)) return parts[i];
        }

        return url.Host;
    }
}
