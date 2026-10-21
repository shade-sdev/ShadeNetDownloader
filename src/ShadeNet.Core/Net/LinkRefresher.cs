using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Net;

public enum RefreshOutcome
{
    /// <summary>Exactly one link on the owner page is the same file; use <see cref="RefreshResult.NewUrl"/>.</summary>
    Found,

    /// <summary>The page still offers the very same address (the session, not the link, is probably what expired).</summary>
    Unchanged,

    /// <summary>Several links look like the same file; refusing to guess.</summary>
    Ambiguous,

    NotFound,

    /// <summary>The download has no recorded owner page.</summary>
    NoOwnerPage,

    /// <summary>The owner page itself could not be loaded.</summary>
    PageUnavailable,
}

public sealed record RefreshResult(RefreshOutcome Outcome, string? NewUrl, string Message);

/// <summary>
/// IDM's "Refresh download address" without a browser (report §13.6, <c>0x5CC4D0</c>): download the page the link came
/// from, extract its links, and look for the same file again. A candidate must be on the same site, have the same file
/// type, answer with the same size and name; it is adopted only when exactly one candidate qualifies.
/// </summary>
public static partial class LinkRefresher
{
    const int MaxCandidates = 100;
    const int MaxPageBytes = 4 * 1024 * 1024;

    public static async Task<RefreshResult> FindNewAddressAsync(DownloadJob job, EngineSettings settings, CancellationToken ct)
    {
        if (!Uri.TryCreate(job.Referer, UriKind.Absolute, out var pageUri) || pageUri.Scheme is not ("http" or "https"))
            return new(RefreshOutcome.NoOwnerPage, null, "This download does not know which page it came from.");

        using var http = HttpFetcher.CreateClient(2, settings);
        var jar = new SessionCookies(job.Cookies, pageUri.Host);

        // 1. the owner page, with the download's own cookies
        string html;
        Uri finalPage;
        try
        {
            var spec = new RequestSpec(pageUri.AbsoluteUri, null, job.Cookies, job.UserAgent, null) { Jar = jar };
            var (response, finalUrl) = await HttpFetcher.GetAsync(http, spec, settings.UserAgent, null, ct).ConfigureAwait(false);
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    return new(RefreshOutcome.PageUnavailable, null, $"The page answered {(int)response.StatusCode}.");
                html = await ReadTextAsync(response, ct).ConfigureAwait(false);
                finalPage = new Uri(finalUrl);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or DownloadException)
        {
            return new(RefreshOutcome.PageUnavailable, null, "The page could not be loaded: " + ex.Message);
        }

        // 2. its links
        var links = ExtractLinks(html, finalPage);
        if (links.Any(l => string.Equals(l, job.Url, StringComparison.OrdinalIgnoreCase)))
            return new(RefreshOutcome.Unchanged, null, "The page still shows the same address.");

        // 3. candidates: same site, same file type
        var original = new Uri(job.Url);
        var wantedExt = Path.GetExtension(original.AbsolutePath);
        var candidates = links
            .Where(l => Uri.TryCreate(l, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            .Select(l => new Uri(l))
            .Where(u => HostNames.SameSite(u.Host, original.Host) || YoutubeFamily(u.Host, original.Host))
            .Where(u => !u.AbsoluteUri.Equals(finalPage.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            .Where(u => SameKind(Path.GetExtension(u.AbsolutePath), wantedExt))
            .Take(MaxCandidates)
            .ToList();

        // 4. verify by asking: same size, same name, same validator
        var matches = new List<string>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var spec = new RequestSpec(candidate.AbsoluteUri, finalPage.AbsoluteUri, job.Cookies, job.UserAgent, null) { Jar = jar };
                var probe = await HttpProbe.ProbeAsync(http, spec, settings.UserAgent, ct).ConfigureAwait(false);
                if (SameFile(job, probe)) matches.Add(candidate.AbsoluteUri);
                if (matches.Count > 1) break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or DownloadException)
            {
                // a link that does not answer is not our file
            }
        }

        return matches.Count switch
        {
            1 => new(RefreshOutcome.Found, matches[0], "Found the same file at a new address."),
            > 1 => new(RefreshOutcome.Ambiguous, null, "Several links on the page look like this file."),
            _ => new(RefreshOutcome.NotFound, null, "The page does not offer this file any more."),
        };
    }

    /// <summary>Same size (when both known), same name, and the same strong validator when both have one.</summary>
    static bool SameFile(DownloadJob job, ProbeResult probe)
    {
        if (job.TotalSize is { } size)
        {
            if (probe.TotalSize != size) return false;
        }
        else if (probe.TotalSize == null)
        {
            return false;
        }

        if (job.ETag != null && probe.ETag != null && job.ETag != probe.ETag) return false;
        if (job.LastModified != null && probe.LastModified != null && job.LastModified != probe.LastModified) return false;

        // The job's own name may carry a " (1)" added to avoid a clash on disk.
        return string.Equals(BaseName(job.FileName), BaseName(probe.FileName), StringComparison.OrdinalIgnoreCase);
    }

    static string BaseName(string name) => UniqueSuffix().Replace(name, "$1");

    [GeneratedRegex(@" \(\d+\)(\.[^.]*)?$")]
    private static partial Regex UniqueSuffix();

    static bool SameKind(string candidateExt, string wantedExt)
    {
        if (!string.IsNullOrEmpty(wantedExt)) return candidateExt.Equals(wantedExt, StringComparison.OrdinalIgnoreCase);
        // No extension to compare: at least skip what is plainly page furniture.
        return candidateExt.ToLowerInvariant() is not (".html" or ".htm" or ".css" or ".js" or ".png" or ".jpg" or ".jpeg" or ".gif"
            or ".svg" or ".ico" or ".woff" or ".woff2" or ".json" or ".xml");
    }

    static bool YoutubeFamily(string a, string b) =>
        (a.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase) && b.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase))
        || (a.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) && b.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase));

    static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while (memory.Length < MaxPageBytes && (n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            memory.Write(buffer, 0, n);

        var charset = response.Content.Headers.ContentType?.CharSet;
        Encoding encoding;
        try
        {
            encoding = charset != null ? Encoding.GetEncoding(charset) : Encoding.UTF8;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(memory.GetBuffer(), 0, (int)memory.Length);
    }

    /// <summary>
    /// Attribute values (<c>href</c>, <c>src</c>, <c>data-*</c>, <c>content</c> of a meta refresh) and absolute URLs
    /// embedded in scripts or JSON, resolved against the page, de-duplicated, in page order.
    /// </summary>
    public static IReadOnlyList<string> ExtractLinks(string html, Uri page)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string raw)
        {
            var text = WebUtility.HtmlDecode(raw.Replace("\\/", "/").Replace("\\u0026", "&").Replace("\\u003d", "=")).Trim();
            if (text.Length == 0 || text.StartsWith('#') || text.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                return;

            if (!Uri.TryCreate(page, text, out var resolved) || resolved.Scheme is not ("http" or "https")) return;
            if (seen.Add(resolved.AbsoluteUri)) found.Add(resolved.AbsoluteUri);
        }

        foreach (Match m in AttributeValue().Matches(html)) Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        foreach (Match m in MetaRefresh().Matches(html)) Add(m.Groups[1].Value);
        foreach (Match m in AbsoluteUrl().Matches(html)) Add(m.Value);
        return found;
    }

    [GeneratedRegex(@"\b(?:href|src|data-[\w-]*(?:url|href|src|link))\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex AttributeValue();

    [GeneratedRegex(@"http-equiv\s*=\s*[""']?refresh[""']?[^>]*content\s*=\s*[""'][^""']*url\s*=\s*([^""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefresh();

    [GeneratedRegex(@"https?:(?://|\\/\\/)[^\s""'<>\\)]+(?:\\/[^\s""'<>\\)]*)*", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrl();
}
