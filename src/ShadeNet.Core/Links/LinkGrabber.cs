using System.Net;
using System.Text.RegularExpressions;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Links;

/// <summary>A link found on a page: its address and the words it was shown with (null when it had none: a picture, a bare address).</summary>
public sealed record GrabbedLink(string Url, string? Text);

/// <summary>A page's links, for the link grabber.</summary>
/// <param name="Url">The page (what the downloads' referrer becomes).</param>
/// <param name="Title">Its title, when it has one (the folder a package of its files can go to).</param>
public sealed record GrabbedPage(string Url, string? Title, IReadOnlyList<GrabbedLink> Links);

/// <summary>
/// Finds the links of a web page: the addresses it links to (<c>a</c>, <c>area</c>), the files it shows or plays (<c>img</c>,
/// <c>video</c>, <c>audio</c>, <c>source</c>, <c>embed</c>) and addresses written out in its text, made absolute against the page
/// (or its <c>&lt;base href&gt;</c>). Only what can be downloaded is kept: web, FTP and magnet addresses, each once.
/// </summary>
public static partial class LinkGrabber
{
    /// <summary>More than this many links on one page are not shown (a page that is nothing but links is not a list to choose from).</summary>
    public const int MaxLinks = 5000;

    // (a link's words are what is between its tags; a picture or a player has none, and what is inside a player is read on its own)
    [GeneratedRegex(@"<(?<tag>a|area)\b(?<attrs>[^>]*)>(?:(?<text>.*?)</\k<tag>\s*>)?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();

    [GeneratedRegex(@"<(?<tag>img|video|audio|source|embed|track)\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex Media();

    [GeneratedRegex(@"\b(?<name>href|src|data-src|data-href|poster)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<base\b[^>]*\bhref\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex Base();

    [GeneratedRegex(@"<title\b[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Title();

    [GeneratedRegex(@"<(script|style|noscript)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"(?:https?|ftp)://[^\s""'<>()\[\]{}]+|magnet:\?[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareAddress();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    /// <summary>The page's links, as described above.</summary>
    public static GrabbedPage FromHtml(string html, Uri page)
    {
        var baseUri = page;
        if (Base().Match(html) is { Success: true } b && Uri.TryCreate(page, WebUtility.HtmlDecode(b.Groups["v"].Value.Trim()), out var declared)
            && declared.Scheme is "http" or "https")
            baseUri = declared;

        var found = new List<GrabbedLink>();
        var visible = Hidden().Replace(html, " ");
        foreach (var e in Anchor().Matches(visible).Concat(Media().Matches(visible)).OrderBy(m => m.Index))
        {
            var text = Words(e.Groups["text"].Value);
            foreach (Match a in Attribute().Matches(e.Groups["attrs"].Value))
            {
                var value = WebUtility.HtmlDecode(a.Groups["v"].Value).Trim();
                if (value.Length == 0) continue;
                found.Add(new GrabbedLink(value, a.Groups["name"].Value.Equals("href", StringComparison.OrdinalIgnoreCase) ? text : null));
            }
        }

        // addresses written out as text (a forum post, a list of mirrors)
        foreach (Match m in BareAddress().Matches(WebUtility.HtmlDecode(Tag().Replace(visible, " "))))
            found.Add(new GrabbedLink(m.Value.TrimEnd('.', ',', ';', ':', '!', '?'), null));

        var title = Title().Match(html) is { Success: true } t ? Words(t.Groups["t"].Value) : null;
        return new GrabbedPage(page.AbsoluteUri, string.IsNullOrEmpty(title) ? null : title, Clean(found, baseUri));
    }

    /// <summary>
    /// Absolute, downloadable addresses (http, https, ftp, magnet), each once (the first time it is named with words keeps them),
    /// without the page's own anchors; at most <see cref="MaxLinks"/>. Also what is done with the links a browser sends (untrusted).
    /// </summary>
    public static IReadOnlyList<GrabbedLink> Clean(IEnumerable<GrabbedLink> links, Uri? page)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<GrabbedLink>();
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Url) || link.Url.Length > 8192) continue;
            var raw = link.Url.Trim();
            string url;
            if (raw.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                url = raw;
            }
            else
            {
                if (!(page != null ? Uri.TryCreate(page, raw, out var abs) : Uri.TryCreate(raw, UriKind.Absolute, out abs))) continue;
                if (abs.Scheme is not ("http" or "https" or "ftp")) continue;
                // the page itself, or a place on it (#section), is not something to download
                if (page != null && string.Equals(abs.GetLeftPart(UriPartial.Query), page.GetLeftPart(UriPartial.Query), StringComparison.Ordinal)) continue;
                url = new UriBuilder(abs) { Fragment = "" }.Uri.AbsoluteUri;
            }

            var text = string.IsNullOrWhiteSpace(link.Text) ? null : Words(link.Text);
            if (text is { Length: > 300 }) text = text[..300];
            if (seen.TryGetValue(url, out var at))
            {
                if (result[at].Text == null && text != null) result[at] = result[at] with { Text = text };
                continue;
            }

            if (result.Count >= MaxLinks) break;
            seen[url] = result.Count;
            result.Add(new GrabbedLink(url, text));
        }

        return result;
    }

    static string Words(string html) => Space().Replace(WebUtility.HtmlDecode(Tag().Replace(html, " ")), " ").Trim();

    /// <summary>What the link's file would be called: the last part of its path, a magnet link's name, else the host.</summary>
    public static string NameOf(string url)
    {
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            var dn = Regex.Match(url, @"[?&]dn=([^&]+)", RegexOptions.IgnoreCase);
            return dn.Success ? Uri.UnescapeDataString(dn.Groups[1].Value.Replace('+', ' ')) : "Magnet link";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var last = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "");
        return last.Length > 0 ? last : uri.Host;
    }

    static readonly HashSet<string> PageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", ".html", ".htm", ".xhtml", ".shtml", ".php", ".asp", ".aspx", ".jsp", ".cgi", ".pl", ".cfm", ".do", ".action",
    };

    /// <summary>The link names a file (by its type), a .torrent or a magnet link, rather than another page.</summary>
    public static bool LooksLikeFile(string url)
    {
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var name = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        var ext = Path.GetExtension(name);
        return ext.Length is > 1 and <= 8 && !PageTypes.Contains(ext) && ext.Skip(1).All(char.IsLetterOrDigit);
    }

    /// <summary>A name for the folder a page's files are put in: its title (or host), made safe for a folder.</summary>
    public static string FolderNameOf(GrabbedPage page)
    {
        var name = page.Title;
        if (string.IsNullOrWhiteSpace(name) && Uri.TryCreate(page.Url, UriKind.Absolute, out var u)) name = u.Host;
        name = FileNameHelper.Sanitize(name ?? "Links", "Links");
        return name.Length > 80 ? name[..80].TrimEnd(' ', '.') : name;
    }
}
