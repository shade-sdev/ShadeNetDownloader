using System.Text.RegularExpressions;

namespace ShadeNetDownloader.Util;

public static partial class UrlParser
{
    [GeneratedRegex(@"https?://[^\s<>""'“”‘’]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>Pulls every distinct http(s) link out of arbitrary text (a pasted page, a list, a .url file, …).</summary>
    public static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match m in UrlRegex().Matches(text))
        {
            // Trailing punctuation that belongs to the sentence, not the link.
            var url = m.Value.TrimEnd('.', ',', ';', ')', ']', '}', '!', '?');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            if (seen.Add(url)) result.Add(url);
        }

        return result;
    }

    /// <summary>True when the whole text is one link (used to decide whether a clipboard paste should open the Add dialog).</summary>
    public static bool IsSingleUrl(string? text)
    {
        var t = text?.Trim();
        return !string.IsNullOrEmpty(t) && !t.Any(char.IsWhiteSpace)
               && Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
    }
}
