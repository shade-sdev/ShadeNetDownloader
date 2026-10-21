namespace ShadeNet.Core.Util;

/// <summary>The built-in categories and their default file types. User changes live in <c>EngineSettings</c>.</summary>
public static class CategoryResolver
{
    public const string General = "General";

    /// <summary>Built-in category names in sidebar order, <see cref="General"/> last.</summary>
    public static readonly IReadOnlyList<string> Categories =
        new[] { "Video", "Music", "Documents", "Programs", "Compressed", "Images", General };

    /// <summary>The built-in categories and the extensions (lower case, no dot) each one claims by default.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> DefaultExtensions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Video"] = Split("mp4 mkv avi mov wmv flv webm m4v ts mpg mpeg 3gp ogv"),
        ["Music"] = Split("mp3 flac wav aac ogg m4a wma opus mid"),
        ["Documents"] = Split("pdf doc docx xls xlsx ppt pptx txt epub rtf csv odt ods mobi djvu"),
        ["Programs"] = Split("exe msi msix appx apk dmg iso bat jar deb rpm"),
        ["Compressed"] = Split("zip rar 7z tar gz bz2 xz zst cab tgz"),
        ["Images"] = Split("jpg jpeg png gif webp bmp svg tiff ico heic avif psd"),
    };

    static readonly Dictionary<string, string> ByExtension = Build();

    static string[] Split(string list) => list.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, extensions) in DefaultExtensions)
            foreach (var e in extensions) map["." + e] = category;
        return map;
    }

    /// <summary>The category by the built-in rules only (what a "known file type" means). Use <c>EngineSettings.CategoryOf</c> for the user's rules.</summary>
    public static string FromFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ByExtension.GetValueOrDefault(ext, General);
    }
}
