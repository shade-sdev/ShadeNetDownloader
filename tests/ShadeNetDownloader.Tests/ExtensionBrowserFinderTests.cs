using ShadeNetDownloader.Services.Browser;

namespace ShadeNetDownloader.Tests;

public class ExtensionBrowserFinderTests
{
    [Fact]
    public void Only_the_browsers_that_are_installed_are_offered()
    {
        var installed = ExtensionBrowserFinder.Installed(b => b.Name is "Edge" or "Brave");
        Assert.Equal(new[] { "Edge", "Brave" }, installed.Select(b => b.Name));
        Assert.Empty(ExtensionBrowserFinder.Installed(_ => false));
    }

    [Fact]
    public void Each_browser_opens_on_its_own_extensions_page()
    {
        Assert.Equal(
            new[] { ("chrome.exe", "chrome://extensions"), ("msedge.exe", "edge://extensions"), ("brave.exe", "brave://extensions") },
            ExtensionBrowserFinder.Browsers.Select(b => (b.Exe, b.ExtensionsAddress)));
    }
}
