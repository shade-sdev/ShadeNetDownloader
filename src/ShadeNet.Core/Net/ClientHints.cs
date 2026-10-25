using System.Text.RegularExpressions;

namespace ShadeNet.Core.Net;

/// <summary>
/// The <c>Sec-CH-UA</c> client hints a Chromium browser sends with every request. A request whose User-Agent says Chrome
/// but that carries none of them looks like a bot to sites that check, and some answer it with something else than
/// the browser gets (a video host's decoy playlist that leads only to itself, for one). The browser's own hints are
/// used when the extension passed them on; otherwise they are worked out from the User-Agent, the way Chrome forms them.
/// Firefox and Safari send none, so a User-Agent of theirs gets none either.
/// </summary>
public static partial class ClientHints
{
    public const string Brands = "Sec-CH-UA";
    public const string Mobile = "Sec-CH-UA-Mobile";
    public const string Platform = "Sec-CH-UA-Platform";

    /// <summary>Whether a header is one of the client hints (which a browser sends to every site, not only the page's).</summary>
    public static bool IsHint(string name) => name.StartsWith("Sec-CH-UA", StringComparison.OrdinalIgnoreCase);

    /// <summary>The hints to send: the browser's own from <paramref name="captured"/>, else those its User-Agent implies.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> For(string? userAgent, IReadOnlyDictionary<string, string>? captured = null)
    {
        var own = captured?.Where(h => IsHint(h.Key)).ToList();
        if (own is { Count: > 0 }) return own;
        return FromUserAgent(userAgent);
    }

    public static IReadOnlyList<KeyValuePair<string, string>> FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent) || userAgent.Contains("Firefox/", StringComparison.Ordinal)) return [];
        var chrome = ChromeVersion().Match(userAgent);
        if (!chrome.Success) return [];   // (Safari, and anything that is not Chromium)

        var major = chrome.Groups[1].Value;
        var (brand, version) = EdgeVersion().Match(userAgent) is { Success: true } edge ? ("Microsoft Edge", edge.Groups[1].Value)
            : OperaVersion().Match(userAgent) is { Success: true } opera ? ("Opera", opera.Groups[1].Value)
            : ("Google Chrome", major);

        var platform = userAgent.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : userAgent.Contains("Android", StringComparison.Ordinal) ? "Android"
            : userAgent.Contains("CrOS", StringComparison.Ordinal) ? "Chrome OS"
            : userAgent.Contains("Macintosh", StringComparison.Ordinal) ? "macOS"
            : userAgent.Contains("Linux", StringComparison.Ordinal) ? "Linux"
            : "Unknown";
        var mobile = userAgent.Contains("Mobile", StringComparison.Ordinal) ? "?1" : "?0";

        return
        [
            new(Brands, $"\"{brand}\";v=\"{version}\", \"Chromium\";v=\"{major}\", \"Not_A Brand\";v=\"24\""),
            new(Mobile, mobile),
            new(Platform, $"\"{platform}\""),
        ];
    }

    [GeneratedRegex(@"Chrome/(\d+)")]
    private static partial Regex ChromeVersion();

    [GeneratedRegex(@"Edg(?:e|A|iOS)?/(\d+)")]
    private static partial Regex EdgeVersion();

    [GeneratedRegex(@"OPR/(\d+)")]
    private static partial Regex OperaVersion();
}
