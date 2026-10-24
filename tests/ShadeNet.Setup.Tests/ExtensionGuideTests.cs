using System.IO.Compression;

namespace ShadeNet.Setup.Tests;

/// <summary>The last step of setup (adding the extension) points at real things: the folder the installer unpacks and browsers by their programs.</summary>
public sealed class ExtensionGuideTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "shadenet-guide-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void The_folder_to_load_is_where_the_installer_unpacks_the_extension()
    {
        var options = new InstallOptions
        {
            InstallDir = Path.Combine(_root, "app"),
            DataDir = Path.Combine(_root, "data"),
            RegistryRoot = @"Software\ShadeNetTests\Guide-" + Guid.NewGuid().ToString("N")[..8],
            StartMenuDir = Path.Combine(_root, "startmenu"),
            DesktopDir = Path.Combine(_root, "desktop"),
            BrowserIntegration = false,
        };

        try
        {
            using var payload = new MemoryStream();
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var name in new[] { "ShadeNetDownloader.exe", "Uninstall.exe", "browser/chromium/manifest.json" })
                    using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write("x");
            }

            payload.Position = 0;
            new InstallEngine(options).Install(payload, "1.0.0");

            Assert.True(File.Exists(Path.Combine(ExtensionGuide.FolderFor(options), "manifest.json")));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(options.RegistryRoot, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Only_the_browsers_that_are_installed_are_offered()
    {
        var installed = ExtensionGuide.Installed(b => b.Name is "Edge" or "Brave");
        Assert.Equal(new[] { "Edge", "Brave" }, installed.Select(b => b.Name));
        Assert.Empty(ExtensionGuide.Installed(_ => false));
    }

    [Fact]
    public void Each_browser_opens_on_its_own_extensions_page()
    {
        Assert.Equal(
            new[] { ("chrome.exe", "chrome://extensions"), ("msedge.exe", "edge://extensions"), ("brave.exe", "brave://extensions") },
            ExtensionGuide.Browsers.Select(b => (b.Exe, b.ExtensionsAddress)));
    }
}
