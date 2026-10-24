using System.Net;
using System.Net.Http.Headers;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Net;

public sealed record ProbeResult(
    string FinalUrl,
    string FileName,
    long? TotalSize,
    bool AcceptsRanges,
    string? ContentType,
    string? ETag,
    DateTimeOffset? LastModified,
    int StatusCode)
{
    /// <summary>
    /// The still-open first response (see <see cref="HttpProbe.ProbeAsync"/>, <c>keepOpen</c>). A download that starts
    /// from it never has to request the address a second time, which sites with one-time links do not allow.
    /// </summary>
    internal LiveResponse? Live { get; init; }

    /// <summary>
    /// Releases the kept connection when the probe is not going to be used (dialog cancelled, link edited). Does
    /// nothing once a download has taken the connection over.
    /// </summary>
    public void Discard()
    {
        if (Live is { } live && live.TryClaim()) live.Dispose();
    }

    /// <summary>Hands the kept response to the download that will use it.</summary>
    internal LiveResponse? DetachLive()
    {
        var live = Live;
        return live != null && live.TryClaim() ? live : null;
    }
}

/// <summary>An open response whose body has not been read yet, plus the client that owns the connection (if any).</summary>
internal sealed class LiveResponse : IDisposable
{
    readonly HttpClient? _ownedClient;
    int _claimed;
    int _disposed;

    public LiveResponse(HttpResponseMessage response, HttpClient? ownedClient)
    {
        Response = response;
        _ownedClient = ownedClient;
        CreatedUtc = DateTime.UtcNow;
    }

    public HttpResponseMessage Response { get; }
    public DateTime CreatedUtc { get; }
    public TimeSpan Age => DateTime.UtcNow - CreatedUtc;

    /// <summary>One consumer only; true for the first caller.</summary>
    public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Response.Dispose();
        _ownedClient?.Dispose();
    }
}

/// <summary>
/// First contact with a URL: <c>GET Range: bytes=0-0</c> tells us the size (from <c>Content-Range</c>), whether
/// ranges work (206 vs 200), the validators and the suggested file name, without transferring the body.
/// With <c>keepOpen</c> the request is <c>Range: bytes=0-</c> and the response is kept, so that it becomes the first
/// download connection (IDM's "start downloading immediately", report §13.4).
/// </summary>
public static class HttpProbe
{
    public static Task<ProbeResult> ProbeAsync(
        HttpClient http, RequestSpec spec, string defaultUserAgent, CancellationToken ct) =>
        ProbeAsync(http, spec, defaultUserAgent, keepOpen: false, clientToOwn: null, rangeStart: 0, ct);

    /// <param name="keepOpen">Keep the response (and read nothing) so a download can continue on it.</param>
    /// <param name="clientToOwn">A client whose lifetime moves into the kept response; disposed with it.</param>
    /// <param name="rangeStart">With <paramref name="keepOpen"/>: where the kept stream starts (a resumed download needs the first missing byte).</param>
    internal static async Task<ProbeResult> ProbeAsync(
        HttpClient http, RequestSpec spec, string defaultUserAgent, bool keepOpen, HttpClient? clientToOwn, long rangeStart,
        CancellationToken ct)
    {
        var from = keepOpen ? rangeStart : 0;
        var (response, finalUrl) = await HttpFetcher.GetAsync(http, spec, defaultUserAgent,
            r => r.Headers.Range = new RangeHeaderValue(from, keepOpen ? null : 0), ct).ConfigureAwait(false);

        HttpResponseMessage? current = response;
        try
        {
            var rangedRequest = true;

            // Some servers reject ranged requests outright - retry plain before giving up.
            if ((int)response.StatusCode is >= 400 and not 416)
            {
                var status = (int)response.StatusCode;
                var retryAfter = HttpFetcher.RetryAfter(response);
                response.Dispose();
                current = null;

                var (plain, plainUrl) = await HttpFetcher.GetAsync(http, spec, defaultUserAgent, null, ct).ConfigureAwait(false);
                current = plain;
                if (!plain.IsSuccessStatusCode)
                    throw new HttpStatusException((int)plain.StatusCode, HttpFetcher.RetryAfter(plain) ?? retryAfter, await HttpFetcher.ReasonAsync(plain, ct).ConfigureAwait(false));

                response = plain;
                finalUrl = plainUrl;
                rangedRequest = false;
            }

            var result = Interpret(response, finalUrl, spec, rangedRequest, from);

            if (keepOpen && result.TotalSize != 0)
            {
                current = null; // ownership moves into the live response
                return result with { Live = new LiveResponse(response, clientToOwn) };
            }

            return result;
        }
        finally
        {
            current?.Dispose();
        }
    }

    static ProbeResult Interpret(HttpResponseMessage response, string finalUrl, RequestSpec spec, bool rangedRequest, long expectedFrom)
    {
        var status = (int)response.StatusCode;
        long? total = null;
        var ranges = false;

        if (status == (int)HttpStatusCode.PartialContent && response.Content.Headers.ContentRange is { } cr)
        {
            total = cr.Length;
            ranges = cr.From == expectedFrom;
        }
        else if (status == (int)HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // "bytes */0": an empty file.
            total = response.Content.Headers.ContentRange?.Length ?? 0;
            ranges = false;
        }
        else
        {
            // 200: the server ignored Range (or we asked without one). Only trust an explicit Accept-Ranges for plain GETs.
            total = response.Content.Headers.ContentLength;
            ranges = !rangedRequest && response.Headers.AcceptRanges.Contains("bytes");
        }

        var etag = response.Headers.ETag;
        var tag = etag is { IsWeak: false } ? etag.Tag : null; // weak validators are not allowed in If-Range
        var contentType = response.Content.Headers.ContentType?.MediaType;

        if (IsWebPageInsteadOfFile(response, contentType, spec.Url))
        {
            var moved = !string.Equals(finalUrl, spec.Url, StringComparison.OrdinalIgnoreCase)
                        && Uri.TryCreate(finalUrl, UriKind.Absolute, out var target)
                ? target.Host
                : null;
            throw new WebPageException(moved);
        }

        return new ProbeResult(
            finalUrl,
            ResolveFileName(response, finalUrl, spec.Url, contentType),
            total,
            ranges && total is > 0,
            contentType,
            tag,
            response.Content.Headers.LastModified,
            status);
    }

    /// <summary>
    /// The address (or the file name) ends in a known file type but the answer is HTML and not an attachment: the
    /// server is showing a page (login wall, "open this in your browser", expired or already used link) where the
    /// file should be. IDM discards the type in the same situation and then asks the user (report §13.3).
    /// </summary>
    internal static bool IsWebPageInsteadOfFile(HttpResponseMessage response, string? contentType, params string?[] urlsOrNames)
    {
        if (!string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contentType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            return false;

        if (response.Content.Headers.ContentDisposition?.DispositionType?.Equals("attachment", StringComparison.OrdinalIgnoreCase) == true)
            return false;

        foreach (var candidate in urlsOrNames)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var name = candidate.Contains("://", StringComparison.Ordinal) ? FromUrl(candidate) : candidate;
            if (name != null && CategoryResolver.FromFileName(name) != CategoryResolver.General) return true;
        }

        return false;
    }

    static string ResolveFileName(HttpResponseMessage response, string finalUrl, string originalUrl, string? contentType)
    {
        string? name = null;

        var cd = response.Content.Headers.ContentDisposition;
        if (cd != null)
        {
            name = cd.FileNameStar ?? cd.FileName;
            if (!string.IsNullOrEmpty(name))
            {
                name = name.Trim('"');
                if (cd.FileNameStar == null) name = FileNameHelper.FixMojibake(name);
            }
        }

        name ??= FromUrl(finalUrl) ?? FromUrl(originalUrl);
        name = FileNameHelper.Sanitize(name);

        if (!Path.HasExtension(name) && FileNameHelper.ExtensionForMime(contentType) is { } ext)
            name += ext;

        return name;
    }

    static string? FromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var last = uri.Segments.Length > 0 ? uri.Segments[^1].TrimEnd('/') : "";
        if (last.Length == 0) return null;
        try
        {
            return Uri.UnescapeDataString(last);
        }
        catch (UriFormatException)
        {
            return last;
        }
    }
}
