using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShadeNetDownloader.Services;

/// <summary>A theme file could not be read: the message says why, in words for the user.</summary>
public sealed class ThemeFileException : Exception
{
    public ThemeFileException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>What a theme file holds once checked: its name, light or dark, the look, and its picture if it has one.</summary>
public sealed record ThemeFileContent(string Name, AppTheme Theme, AppearanceSettings Appearance, byte[]? Image, string? ImageExtension, int Dropped);

/// <summary>
/// Themes as files to share (".shadenet-theme"): plain JSON with the look's colours and, when the look has a background
/// picture, the picture itself. Text and layout (font, size, density ...) are the user's own and are not part of a theme.
/// Reading one trusts nothing in it: the size is limited, the format and version are checked, every colour goes through
/// the same checks as the settings file, and the picture must decode as a picture before it is used.
/// </summary>
public static class ThemeFile
{
    public const string Extension = ".shadenet-theme";
    public const string FormatName = "shadenet-theme";
    public const int Version = 1;

    /// <summary>Larger is not a theme (a picture of 30 MB is about 40 MB as text).</summary>
    public const long MaxFileBytes = 45L * 1024 * 1024;

    static readonly string[] PictureExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".jxr" };

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 16,
    };

    sealed class Document
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Name { get; set; }
        public AppTheme Theme { get; set; } = AppTheme.System;
        public AppearanceSettings? Appearance { get; set; }
        public Picture? Image { get; set; }
    }

    sealed class Picture
    {
        public string? Extension { get; set; }
        public string? Data { get; set; }
    }

    /// <summary>The theme as file content. The look's picture goes in when it can be read (and is not too large).</summary>
    public static string Write(string name, AppTheme theme, AppearanceSettings look, long maxImageBytes)
    {
        var copy = look.Clone();
        Picture? picture = null;
        if (copy.Image != null)
        {
            try
            {
                var info = new FileInfo(copy.Image);
                if (info.Exists && info.Length <= maxImageBytes)
                    picture = new Picture { Extension = PictureExtension(info.Extension), Data = Convert.ToBase64String(File.ReadAllBytes(info.FullName)) };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the theme goes without its picture
            }
        }

        copy.Image = null;   // a path on this computer means nothing on another
        copy.Preset = ThemePresets.CustomId;
        return JsonSerializer.Serialize(new Document
        {
            Format = FormatName,
            Version = Version,
            Name = string.IsNullOrWhiteSpace(name) ? "My theme" : name.Trim(),
            Theme = theme,
            Appearance = copy,
            Image = picture,
        }, Json);
    }

    public static ThemeFileContent Read(string path)
    {
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ThemeFileException("The file could not be opened: " + ex.Message, ex);
        }

        if (size > MaxFileBytes) throw new ThemeFileException("That file is too large to be a ShadeNet theme.");
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ThemeFileException("The file could not be read: " + ex.Message, ex);
        }

        return Parse(text);
    }

    public static ThemeFileContent Parse(string text)
    {
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(text, Json);
        }
        catch (JsonException ex)
        {
            throw new ThemeFileException("That file is not a ShadeNet theme (it could not be read as one).", ex);
        }

        if (document == null || !string.Equals(document.Format, FormatName, StringComparison.OrdinalIgnoreCase) || document.Appearance == null)
            throw new ThemeFileException("That file is not a ShadeNet theme.");
        if (document.Version > Version)
            throw new ThemeFileException("That theme was made by a newer ShadeNet. Update ShadeNet to use it.");
        if (!Enum.IsDefined(document.Theme)) document.Theme = AppTheme.System;

        var look = document.Appearance;
        look.Image = null;   // only a picture carried in the file is used, never a path
        var dropped = look.Normalize();
        look.Preset = ThemePresets.CustomId;

        byte[]? image = null;
        string? extension = null;
        if (!string.IsNullOrEmpty(document.Image?.Data))
        {
            try
            {
                image = Convert.FromBase64String(document.Image.Data);
                extension = PictureExtension(document.Image.Extension);
            }
            catch (FormatException)
            {
                dropped++;   // a picture that is no picture: the theme comes without it
            }
        }

        var name = string.IsNullOrWhiteSpace(document.Name) ? "Imported theme" : document.Name.Trim();
        if (name.Length > 80) name = name[..80];
        return new ThemeFileContent(name, document.Theme, look, image, extension, dropped);
    }

    /// <summary>A picture's file ending as kept on disk: one of the known ones, else ".png" (pictures are read by content anyway).</summary>
    static string PictureExtension(string? extension)
    {
        var e = (extension ?? "").Trim().ToLowerInvariant();
        if (e.Length > 0 && e[0] != '.') e = "." + e;
        return PictureExtensions.Contains(e) ? e : ".png";
    }
}
