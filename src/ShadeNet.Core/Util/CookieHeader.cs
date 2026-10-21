using System.Text.RegularExpressions;

namespace ShadeNet.Core.Util;

/// <summary>Cleans up a cookie string pasted from a browser's developer tools into a valid <c>Cookie</c> header value.</summary>
public static partial class CookieHeader
{
    /// <summary>
    /// Accepts <c>a=1; b=2</c>, <c>Cookie: a=1; b=2</c>, a <c>-H 'cookie: ...'</c> fragment or one pair per line, and
    /// returns <c>a=1; b=2</c>; <c>null</c> when nothing usable is left.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parts = new List<string>();
        foreach (var raw in lines)
        {
            var line = Prefix().Replace(raw, "").Trim().Trim('\'', '"').Trim().TrimEnd(';').Trim();
            if (line.Length > 0) parts.Add(line);
        }

        var joined = string.Join("; ", parts);
        return joined.Contains('=') ? joined : null;
    }

    [GeneratedRegex(@"^(?:-H\s+)?['""]?\s*cookie\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Prefix();
}
