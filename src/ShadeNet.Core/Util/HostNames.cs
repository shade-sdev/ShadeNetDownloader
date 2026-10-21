using System.Net;

namespace ShadeNet.Core.Util;

/// <summary>Host-name rules shared by cookie scoping and link refresh (same "site", not same host).</summary>
public static class HostNames
{
    // Second-level labels that are part of a public suffix ("example.co.uk" is the site, "co.uk" is not).
    static readonly HashSet<string> SecondLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        "co", "com", "org", "net", "gov", "edu", "ac", "or", "ne", "go", "gob", "nom", "mil", "ltd", "plc", "sch",
    };

    /// <summary>
    /// The domain a site owns: <c>file-ap-sgp-3.gofile.io</c> → <c>gofile.io</c>, <c>a.b.example.co.uk</c> →
    /// <c>example.co.uk</c>. IP addresses and single-label hosts are returned as they are. This is the same idea as
    /// IDM's cookie lookup (<c>host_key LIKE '%domain'</c> after stripping all but the last two labels).
    /// </summary>
    public static string RegistrableDomain(string host)
    {
        if (string.IsNullOrEmpty(host)) return host;
        host = host.Trim('.').ToLowerInvariant();
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;

        var labels = host.Split('.');
        if (labels.Length <= 2) return host;

        var take = 2;
        if (labels[^1].Length == 2 && SecondLevel.Contains(labels[^2]) && labels.Length >= 3) take = 3;
        return string.Join('.', labels[^take..]);
    }

    public static bool IsIp(string host) => IPAddress.TryParse(host.Trim('[', ']'), out _);

    /// <summary>RFC 6265 domain-match: equal, or <paramref name="host"/> ends with ".<paramref name="domain"/>".</summary>
    public static bool DomainMatches(string host, string domain)
    {
        domain = domain.TrimStart('.');
        if (host.Equals(domain, StringComparison.OrdinalIgnoreCase)) return true;
        return !IsIp(host) && host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when both hosts belong to the same site (share a registrable domain).</summary>
    public static bool SameSite(string a, string b) =>
        string.Equals(RegistrableDomain(a), RegistrableDomain(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A cookie may not be set for a bare public suffix such as ".com" or ".co.uk".</summary>
    public static bool IsPublicSuffix(string domain)
    {
        domain = domain.Trim('.').ToLowerInvariant();
        if (domain.Length == 0 || IsIp(domain)) return false;
        var labels = domain.Split('.');
        if (labels.Length == 1) return true;
        return labels.Length == 2 && labels[1].Length == 2 && SecondLevel.Contains(labels[0]);
    }
}
