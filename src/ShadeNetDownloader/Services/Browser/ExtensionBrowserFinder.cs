using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ShadeNetDownloader.Services.Browser;

/// <summary>A Chromium browser that can load the extension, and the address of its extensions page.</summary>
public sealed record ExtensionBrowserTarget(string Name, string Exe, string ExtensionsAddress);

/// <summary>
/// Finds the browsers that are installed and opens one on its extensions page, so that adding the extension (the one step a
/// browser keeps for the user) is a click. The setup program has the same list for its last page: the two do not share a
/// project, so a change here belongs there too (<c>ShadeNet.Setup/ExtensionGuide.cs</c>).
/// </summary>
public static class ExtensionBrowserFinder
{
    public static readonly IReadOnlyList<ExtensionBrowserTarget> Browsers = new[]
    {
        new ExtensionBrowserTarget("Chrome", "chrome.exe", "chrome://extensions"),
        new ExtensionBrowserTarget("Edge", "msedge.exe", "edge://extensions"),
        new ExtensionBrowserTarget("Brave", "brave.exe", "brave://extensions"),
    };

    /// <param name="isInstalled">Whether a browser's program is installed; only tests pass one.</param>
    public static IReadOnlyList<ExtensionBrowserTarget> Installed(Func<ExtensionBrowserTarget, bool>? isInstalled = null) =>
        Browsers.Where(isInstalled ?? HasAppPath).ToList();

    /// <summary>The shell finds the program through "App Paths"; chrome:// addresses cannot be opened any other way. False when it cannot be started.</summary>
    public static bool TryOpen(ExtensionBrowserTarget browser)
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

    static bool HasAppPath(ExtensionBrowserTarget browser)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{browser.Exe}");
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return true;
        }

        return false;
    }
}
