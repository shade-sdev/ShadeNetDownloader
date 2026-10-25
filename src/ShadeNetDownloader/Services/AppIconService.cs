using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ShadeNetDownloader.Services.Tray;

namespace ShadeNetDownloader.Services;

/// <summary>One app icon one can choose: its .ico (window, taskbar, tray) and its 64 px picture (title bar, the picker).</summary>
public sealed record AppIconChoice(string Id, string Name, string IconPath, string Preview);

/// <summary>What the Appearance page needs of the app icons: the choice of them, and choosing one.</summary>
public interface IAppIconApplier
{
    IReadOnlyList<AppIconChoice> Choices { get; }

    /// <summary>Shows this icon everywhere ShadeNet shows one; an unknown id shows ShadeNet's own.</summary>
    void Apply(string id);
}

/// <summary>
/// The app icon, like Discord's in-app icons: ShadeNet's own or one of Assets/Icons (listed with their names in
/// icons.json). Choosing one changes the window's icon (its taskbar button and Alt+Tab), the tray icon and the mark in
/// the title bar, at once. Shortcuts keep the program's own icon (a pinned taskbar button too, while ShadeNet is closed).
/// </summary>
public sealed partial class AppIconService : ObservableObject, IAppIconApplier
{
    public const string DefaultId = "shadenet";

    readonly TrayIcon? _tray;
    Window? _window;

    public AppIconService(TrayIcon? tray = null, string? folder = null)
    {
        _tray = tray;
        var assets = folder ?? Path.Combine(AppContext.BaseDirectory, "Assets");
        Choices = Read(assets);
        _logo = Picture(Choices[0].Preview);
    }

    public IReadOnlyList<AppIconChoice> Choices { get; }

    /// <summary>The title bar's mark (the main window binds its brand picture to it).</summary>
    [ObservableProperty] ImageSource? _logo;

    public string Current { get; private set; } = DefaultId;

    /// <summary>ShadeNet's own icon first, then every one of icons.json whose files are there.</summary>
    static IReadOnlyList<AppIconChoice> Read(string assets)
    {
        var list = new List<AppIconChoice>
        {
            new(DefaultId, "ShadeNet", Path.Combine(assets, "app.ico"), "pack://application:,,,/Assets/logo.png"),
        };

        var folder = Path.Combine(assets, "Icons");
        try
        {
            var listing = Path.Combine(folder, "icons.json");
            if (!File.Exists(listing)) return list;
            using var json = JsonDocument.Parse(File.ReadAllText(listing));
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                var id = entry.TryGetProperty("id", out var i) ? i.GetString() : null;
                var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                var ico = Path.Combine(folder, id + ".ico");
                var png = Path.Combine(folder, id + ".png");
                if (File.Exists(ico) && File.Exists(png)) list.Add(new AppIconChoice(id, name ?? id, ico, png));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // only ShadeNet's own then
        }

        return list;
    }

    /// <summary>Called once by the main window (its icon is the taskbar button's).</summary>
    public void Attach(Window window) => _window = window;

    public void Apply(string id)
    {
        var choice = Choices.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Choices[0];
        Current = choice.Id;
        Logo = Picture(choice.Preview) ?? Logo;

        if (_window != null)
        {
            try
            {
                // (the decoder keeps every size of the .ico: the taskbar and Alt+Tab take the ones they need)
                _window.Icon = BitmapFrame.Create(new Uri(choice.IconPath, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FileFormatException
                                           or UriFormatException or System.Runtime.InteropServices.COMException)
            {
                // the window keeps the icon it has
            }
        }

        _tray?.SetIcon(choice.IconPath);
    }

    /// <summary>A picture from a file or a pack address (which only exists once the app runs: tests get null).</summary>
    public static ImageSource? Picture(string address)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(address, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FileFormatException
                                       or UriFormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;   // (a pack address needs the running app; tests have none)
        }
    }
}
