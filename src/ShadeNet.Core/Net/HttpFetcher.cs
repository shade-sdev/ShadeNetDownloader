using System.Net;
using System.Net.Http.Headers;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;

namespace ShadeNet.Core.Net;

/// <summary>What the browser (or user) knew about a request; replayed on every connection.</summary>
public sealed record RequestSpec(
    string Url,
    string? Referer = null,
    string? Cookies = null,
    string? UserAgent = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? OriginHost = null)
{
    /// <summary>
    /// The host the cookies and custom headers belong to. For a job this is the host of the address the user gave,
    /// NOT of the redirect target we ended up at. Custom headers stay on that exact host; cookies follow the cookie
    /// jar's site rules (the same site, never another one).
    /// </summary>
    public string CookieHost => OriginHost ?? new Uri(Url).Host;

    /// <summary>The cookie jar of the whole session; shared by all connections of a job. Created per call when absent.</summary>
    public SessionCookies? Jar { get; init; }

    public static RequestSpec From(DownloadJob job, string? userAgentFallback = null, SessionCookies? jar = null) =>
        new(job.FinalUrl ?? job.Url, job.Referer, job.Cookies, job.UserAgent ?? userAgentFallback, job.Headers,
            new Uri(job.Url).Host) { Jar = jar };
}

internal static class HttpFetcher
{
    const int MaxRedirects = 20;

    public static HttpClient CreateClient(int maxConnections, EngineSettings settings) =>
        new(CreateHandler(maxConnections, settings), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    internal static SocketsHttpHandler CreateHandler(int maxConnections, EngineSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Max(2, maxConnections),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ConnectTimeout = settings.ConnectTimeout,
            AutomaticDecompression = DecompressionMethods.None, // ranges are byte-exact
            UseCookies = false,                                  // cookies come from the capture, never leak across hosts
            AllowAutoRedirect = false,                           // redirects are followed by hand so credentials stay on-host
        };

        switch (settings.Proxy)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;
            case ProxyMode.Manual when TryParseProxy(settings.ProxyAddress) is { } proxy:
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(proxy);
                break;
            // System (and a manual proxy with an unusable address): the proxy Windows is configured with
        }

        return handler;
    }

    /// <summary>"host:port" or a full address; null when it cannot be a proxy.</summary>
    public static Uri? TryParseProxy(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var text = address.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host)
            ? uri
            : null;
    }

    /// <summary>
    /// GET with the captured identity (UA, Referer, Cookie, headers). HTTP/1.1 is forced: HTTP/2 would multiplex all
    /// "connections" over one TCP stream and defeat segmentation. Redirects are followed manually; the Cookie and
    /// custom headers are only sent to the original host.
    /// </summary>
    public static async Task<(HttpResponseMessage Response, string FinalUrl)> GetAsync(
        HttpClient http, RequestSpec spec, string defaultUserAgent, Action<HttpRequestMessage>? configure, CancellationToken ct)
    {
        var url = spec.Url;
        var originHost = spec.CookieHost;
        var jar = spec.Jar ?? new SessionCookies(spec.Cookies, originHost);
        var origin = OriginOf(spec.Referer);

        for (var hop = 0; ; hop++)
        {
            var uri = new Uri(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };

            var sameHost = uri.Host.Equals(originHost, StringComparison.OrdinalIgnoreCase);
            request.Headers.TryAddWithoutValidation("User-Agent", spec.UserAgent ?? defaultUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            if (!string.IsNullOrEmpty(spec.Referer)) request.Headers.TryAddWithoutValidation("Referer", spec.Referer);
            if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);

            var cookie = jar.HeaderFor(uri);
            if (cookie != null) request.Headers.TryAddWithoutValidation("Cookie", cookie);

            if (sameHost && spec.Headers != null)
            {
                foreach (var (k, v) in spec.Headers)
                {
                    // A header the caller supplied (their own Origin, Cookie, ...) replaces the derived one.
                    request.Headers.Remove(k);
                    request.Headers.TryAddWithoutValidation(k, v);
                }
            }

            configure?.Invoke(request);

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // Cookies set on the way (login redirects, CDN session cookies) are honoured, even on a redirect response.
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies)) jar.Store(uri, setCookies);

            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null || hop >= MaxRedirects)
                    throw new DownloadException(JobErrorKind.Network, "Too many redirects or a redirect without a location.");

                url = (location.IsAbsoluteUri ? location : new Uri(uri, location)).AbsoluteUri;
                continue;
            }

            return (response, uri.AbsoluteUri);
        }
    }

    /// <summary>The OS UI language, like IDM's <c>Accept-Language</c> from the system locale.</summary>
    static readonly string AcceptLanguage = BuildAcceptLanguage();

    static string BuildAcceptLanguage()
    {
        var culture = System.Globalization.CultureInfo.InstalledUICulture;
        var name = culture.Name;
        if (string.IsNullOrEmpty(name)) return "en-US,en;q=0.9";
        var language = culture.TwoLetterISOLanguageName;
        return name.Equals(language, StringComparison.OrdinalIgnoreCase)
            ? $"{name},en;q=0.8"
            : $"{name},{language};q=0.9,en;q=0.8";
    }

    /// <summary><c>scheme://host[:port]</c> of the referring page: sites that check where a download was started look at it.</summary>
    static string? OriginOf(string? referer)
    {
        if (string.IsNullOrWhiteSpace(referer) || !Uri.TryCreate(referer, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? ra = response.Headers.RetryAfter;
        if (ra == null) return null;
        if (ra.Delta is { } d) return d;
        if (ra.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
