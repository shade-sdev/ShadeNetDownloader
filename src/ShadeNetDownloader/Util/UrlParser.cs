using System.IO;
using System.Text.RegularExpressions;

namespace ShadeNetDownloader.Util;

public static partial class UrlParser
{
    [GeneratedRegex(@"https?://[^\s<>""'“”‘’]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>A magnet link (a torrent named by its identity).</summary>
    [GeneratedRegex(@"magnet:\?[^\s<>""'“”‘’]+", RegexOptions.IgnoreCase)]
    private static partial Regex MagnetRegex();

    /// <summary>A .torrent file on this computer, on a line of its own ("C:\...\x.torrent", quotes allowed).</summary>
    [GeneratedRegex(@"^\s*""?(?<path>(?:[A-Za-z]:\\|\\\\)[^""<>|?*\r\n]+?\.torrent)""?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex TorrentFileRegex();

    /// <summary>
    /// Pulls every distinct link out of arbitrary text (a pasted page, a list, a .url file, …): http(s) links, magnet links, and paths of
    /// .torrent files on this computer.
    /// </summary>
    public static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<(int At, string Link)>();
        foreach (Match m in UrlRegex().Matches(text))
        {
            // Trailing punctuation that belongs to the sentence, not the link.
            var url = m.Value.TrimEnd('.', ',', ';', ')', ']', '}', '!', '?');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            if (seen.Add(url)) found.Add((m.Index, url));
        }

        foreach (Match m in MagnetRegex().Matches(text))
        {
            var magnet = m.Value.TrimEnd('.', ',', ';', ')', ']', '}', '!');
            if (magnet.Contains("xt=", StringComparison.OrdinalIgnoreCase) && seen.Add(magnet)) found.Add((m.Index, magnet));
        }

        foreach (Match m in TorrentFileRegex().Matches(text))
        {
            var path = m.Groups["path"].Value.Trim();
            if (seen.Add(path)) found.Add((m.Index, path));
        }

        // in the order they appear in the text
        return found.OrderBy(f => f.At).Select(f => f.Link).ToList();
    }

    /// <summary>True when the whole text is one link (used to decide whether a clipboard paste should open the Add dialog).</summary>
    public static bool IsSingleUrl(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return false;
        if (t.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) && !t.Any(char.IsWhiteSpace)) return true;
        return !t.Any(char.IsWhiteSpace)
               && Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
    }

    /// <summary>A local path of a .torrent file (as <see cref="Extract"/> finds them).</summary>
    public static bool IsTorrentFilePath(string link) =>
        link.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(link);
}
