using System.Text;

namespace ShadeNet.Core.Util;

public static class FileNameHelper
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["video/x-matroska"] = ".mkv", ["video/quicktime"] = ".mov",
        ["audio/mpeg"] = ".mp3", ["audio/mp4"] = ".m4a", ["audio/ogg"] = ".ogg", ["audio/flac"] = ".flac", ["audio/wav"] = ".wav",
        ["application/pdf"] = ".pdf", ["application/zip"] = ".zip", ["application/x-7z-compressed"] = ".7z",
        ["application/x-rar-compressed"] = ".rar", ["application/gzip"] = ".gz", ["application/x-msdownload"] = ".exe",
        ["application/vnd.microsoft.portable-executable"] = ".exe", ["application/x-iso9660-image"] = ".iso",
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/gif"] = ".gif", ["image/webp"] = ".webp",
        ["text/plain"] = ".txt", ["text/html"] = ".html", ["application/json"] = ".json",
        ["application/vnd.apple.mpegurl"] = ".m3u8", ["application/x-mpegurl"] = ".m3u8",
    };

    /// <summary>Makes a string safe to use as a single Windows file name (no path, no reserved names, no ADS syntax).</summary>
    public static string Sanitize(string? name, string fallback = "download")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        // Path traversal / directories: keep only the last component.
        var idx = name.LastIndexOfAny(new[] { '/', '\\' });
        if (idx >= 0) name = name[(idx + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(c < 32 || Array.IndexOf(invalid, c) >= 0 || c == ':' ? '_' : c);

        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0 || result.All(c => c is '.' or '_')) return fallback;

        var stem = Path.GetFileNameWithoutExtension(result);
        if (Reserved.Contains(stem)) result = "_" + result;

        const int maxLength = 200;
        if (result.Length > maxLength)
        {
            var ext = Path.GetExtension(result);
            if (ext.Length > 20) ext = ext[..20];
            result = result[..(maxLength - ext.Length)] + ext;
        }

        return result;
    }

    public static string? ExtensionForMime(string? mime)
    {
        if (string.IsNullOrEmpty(mime)) return null;
        var semi = mime.IndexOf(';');
        if (semi >= 0) mime = mime[..semi];
        return MimeExtensions.GetValueOrDefault(mime.Trim());
    }

    /// <summary>
    /// Servers frequently put raw UTF-8 bytes into a quoted <c>filename=</c>; HttpClient surfaces them as Latin-1 chars.
    /// Re-decodes such strings so "Ã©" becomes "é".
    /// </summary>
    public static string FixMojibake(string value)
    {
        var hasHigh = false;
        foreach (var c in value)
        {
            if (c > 0xFF) return value; // already real Unicode
            if (c >= 0x80) hasHigh = true;
        }

        if (!hasHigh) return value;

        try
        {
            var bytes = Encoding.Latin1.GetBytes(value);
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return value;
        }
    }

    /// <summary>Returns <paramref name="fileName"/> or "name (1).ext", "name (2).ext"… so it does not collide.</summary>
    public static string MakeUnique(string directory, string fileName, Func<string, bool>? isReserved = null)
    {
        bool Taken(string n)
        {
            var p = Path.Combine(directory, n);
            return File.Exists(p) || File.Exists(p + ".sndpart") || (isReserved?.Invoke(p) ?? false);
        }

        if (!Taken(fileName)) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        // Keep double extensions like .tar.gz readable.
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            ext = ".tar" + ext;
            stem = stem[..^4];
        }

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!Taken(candidate)) return candidate;
        }

        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }
}
