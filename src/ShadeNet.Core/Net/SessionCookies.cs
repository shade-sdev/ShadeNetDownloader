using System.Globalization;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Net;

/// <summary>
/// The cookies of one download session. Seeded with the <c>Cookie</c> header the user or the browser captured, then
/// updated from every <c>Set-Cookie</c> the servers send on the way (redirects included), the way IDM keeps its own jar
/// (<c>0x585F60</c> in the report, §13.2). Matching follows RFC 6265: domain suffix, path prefix, <c>Secure</c> only on
/// https. Captured cookies are scoped to the <b>site</b> they were captured for (the registrable domain), so a login
/// cookie reaches the CDN sub-domain a download redirects to, but never another site.
/// </summary>
public sealed class SessionCookies
{
    sealed class Entry
    {
        public required string Name { get; init; }
        public required string Value { get; set; }
        public required string Domain { get; init; }     // lower-case, no leading dot
        public bool HostOnly { get; init; }
        public required string Path { get; init; }
        public bool Secure { get; init; }
        public DateTimeOffset? Expires { get; set; }
        public long Created { get; init; }
    }

    readonly object _gate = new();
    readonly List<Entry> _entries = new();
    long _sequence;

    public SessionCookies(string? captured, string originHost)
    {
        if (string.IsNullOrWhiteSpace(captured)) return;

        var domain = HostNames.IsIp(originHost) ? originHost.ToLowerInvariant() : HostNames.RegistrableDomain(originHost);
        foreach (var pair in captured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            Add(new Entry
            {
                Name = pair[..eq].Trim(),
                Value = pair[(eq + 1)..].Trim(),
                Domain = domain,
                HostOnly = HostNames.IsIp(originHost),
                Path = "/",
                Created = _sequence++,
            });
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (_gate) return _entries.Count == 0;
        }
    }

    /// <summary>The <c>Cookie</c> header value for a request to <paramref name="uri"/>, or <c>null</c> when none apply.</summary>
    public string? HeaderFor(Uri uri)
    {
        var now = DateTimeOffset.UtcNow;
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        var https = uri.Scheme == Uri.UriSchemeHttps;

        lock (_gate)
        {
            _entries.RemoveAll(e => e.Expires is { } x && x <= now);

            var matching = _entries
                .Where(e => (e.HostOnly ? uri.Host.Equals(e.Domain, StringComparison.OrdinalIgnoreCase) : HostNames.DomainMatches(uri.Host, e.Domain))
                            && PathMatches(path, e.Path)
                            && (!e.Secure || https))
                .OrderByDescending(e => e.Path.Length)
                .ThenBy(e => e.Created)
                .ToList();

            return matching.Count == 0 ? null : string.Join("; ", matching.Select(e => $"{e.Name}={e.Value}"));
        }
    }

    /// <summary>Applies the <c>Set-Cookie</c> headers of a response received from <paramref name="uri"/>.</summary>
    public void Store(Uri uri, IEnumerable<string>? setCookieHeaders)
    {
        if (setCookieHeaders == null) return;
        foreach (var header in setCookieHeaders) StoreOne(uri, header);
    }

    void StoreOne(Uri uri, string header)
    {
        var parts = header.Split(';', StringSplitOptions.TrimEntries);
        var first = parts[0];
        var eq = first.IndexOf('=');
        if (eq <= 0) return;

        var name = first[..eq].Trim();
        var value = first[(eq + 1)..].Trim();

        string? domainAttr = null;
        string? pathAttr = null;
        var secure = false;
        DateTimeOffset? expires = null;
        var maxAgeSet = false;

        foreach (var attr in parts.Skip(1))
        {
            var i = attr.IndexOf('=');
            var key = (i < 0 ? attr : attr[..i]).Trim();
            var val = i < 0 ? "" : attr[(i + 1)..].Trim();

            if (key.Equals("Domain", StringComparison.OrdinalIgnoreCase)) domainAttr = val.TrimStart('.').ToLowerInvariant();
            else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase)) pathAttr = val;
            else if (key.Equals("Secure", StringComparison.OrdinalIgnoreCase)) secure = true;
            else if (key.Equals("Max-Age", StringComparison.OrdinalIgnoreCase) && long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                maxAgeSet = true;
                expires = DateTimeOffset.UtcNow.AddSeconds(seconds);
            }
            else if (key.Equals("Expires", StringComparison.OrdinalIgnoreCase) && !maxAgeSet
                     && DateTimeOffset.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            {
                expires = date;
            }
        }

        string domain;
        var hostOnly = true;
        if (!string.IsNullOrEmpty(domainAttr))
        {
            // The server may only set cookies for its own domain or a parent of it, never for a public suffix.
            if (!HostNames.DomainMatches(uri.Host, domainAttr) || HostNames.IsPublicSuffix(domainAttr)) return;
            domain = domainAttr;
            hostOnly = false;
        }
        else
        {
            domain = uri.Host.ToLowerInvariant();
        }

        var path = pathAttr is { Length: > 0 } p && p[0] == '/' ? p : DefaultPath(uri.AbsolutePath);

        lock (_gate)
        {
            _entries.RemoveAll(e => e.Name == name && e.Domain == domain && e.Path == path && e.HostOnly == hostOnly);
            if (expires is { } x && x <= DateTimeOffset.UtcNow) return; // an expired Set-Cookie deletes the cookie

            _entries.Add(new Entry
            {
                Name = name,
                Value = value,
                Domain = domain,
                HostOnly = hostOnly,
                Path = path,
                Secure = secure,
                Expires = expires,
                Created = _sequence++,
            });
        }
    }

    void Add(Entry entry)
    {
        lock (_gate) _entries.Add(entry);
    }

    static string DefaultPath(string requestPath)
    {
        if (string.IsNullOrEmpty(requestPath) || requestPath[0] != '/') return "/";
        var last = requestPath.LastIndexOf('/');
        return last <= 0 ? "/" : requestPath[..last];
    }

    static bool PathMatches(string requestPath, string cookiePath)
    {
        if (requestPath == cookiePath) return true;
        if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal)) return false;
        return cookiePath.EndsWith('/') || requestPath[cookiePath.Length] == '/';
    }
}
