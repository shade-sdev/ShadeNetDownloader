using System.Diagnostics;
using Microsoft.Win32;

namespace ShadeNet.Setup;

/// <summary>A Chromium browser that can load the extension, and the address of its extensions page.</summary>
public sealed record ExtensionBrowser(string Name, string Exe, string ExtensionsAddress);

/// <summary>
/// Chrome, Edge and Brave do not let a program add an extension for the user (that takes administrator rights and a browser
/// policy, or a store listing), so the last step stays with the user. This makes it as short as it can be: setup finds the
/// browsers that are installed, opens the one the user picks straight on its extensions page and has the folder path ready
/// on the clipboard.
/// </summary>
public static class ExtensionGuide
{
    public static readonly IReadOnlyList<ExtensionBrowser> Browsers = new[]
    {
        new ExtensionBrowser("Chrome", "chrome.exe", "chrome://extensions"),
        new ExtensionBrowser("Edge", "msedge.exe", "edge://extensions"),
        new ExtensionBrowser("Brave", "brave.exe", "brave://extensions"),
    };

    /// <summary>Where the installed program keeps the extension: the folder the browser is asked to load.</summary>
    public static string FolderFor(InstallOptions options) => Path.Combine(options.InstallDir, "browser", "chromium");

    /// <param name="isInstalled">Whether a browser's program is installed; only tests pass one.</param>
    public static IReadOnlyList<ExtensionBrowser> Installed(Func<ExtensionBrowser, bool>? isInstalled = null) =>
        Browsers.Where(isInstalled ?? HasAppPath).ToList();

    /// <summary>Opens the browser on its extensions page (the shell finds the program through "App Paths"). False when it cannot be started.</summary>
    public static bool TryOpen(ExtensionBrowser browser)
    {
        try
        {
            Process.Start(new ProcessStartInfo(browser.Exe, browser.ExtensionsAddress) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    static bool HasAppPath(ExtensionBrowser browser)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{browser.Exe}");
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return true;
        }

        return false;
    }
}
