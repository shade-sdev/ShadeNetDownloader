using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using ShadeNet.Bridge;

namespace ShadeNetDownloader.Services.Browser;

public sealed record BrowserTarget(string Name, string VendorPath);

public sealed record IntegrationStatus(bool HostFilesPresent, bool ExtensionFolderPresent, IReadOnlyList<string> RegisteredBrowsers)
{
    public bool IsInstalled => RegisteredBrowsers.Count > 0;
}

/// <summary>
/// Registers the native messaging host with the Chromium browsers and Firefox so that the extension can reach the app:
/// a manifest file (one for the Chromium browsers, one for Firefox, which names extensions its own way) plus one registry value
/// per browser, all under HKEY_CURRENT_USER (no administrator rights, nothing machine-wide). Everything it writes can be removed
/// again with <see cref="Uninstall"/>.
/// </summary>
public sealed class BrowserIntegrationInstaller
{
    /// <summary>
    /// The extension's ID is fixed by the <c>key</c> in its manifest (<c>extension/chromium/manifest.json</c>), which is
    /// what lets the host manifest name exactly one extension in <c>allowed_origins</c>.
    /// </summary>
    public const string ExtensionId = "bcegodlanfeghhakadmalbkbhbhbejop";

    /// <summary>The Firefox add-on's ID (<c>browser_specific_settings.gecko.id</c> in <c>extension/firefox/manifest.json</c>).</summary>
    public const string FirefoxExtensionId = "browser-integration@shadenet-downloader";

    public const string FirefoxName = "Mozilla Firefox";

    public static readonly IReadOnlyList<BrowserTarget> Browsers = new[]
    {
        new BrowserTarget("Google Chrome", @"Google\Chrome"),
        new BrowserTarget("Microsoft Edge", @"Microsoft\Edge"),
        new BrowserTarget("Brave", @"BraveSoftware\Brave-Browser"),
        new BrowserTarget("Chromium", @"Chromium"),
    };

    readonly string _dataDir;
    readonly string _appDir;
    readonly string _registryRoot;

    /// <param name="registryRoot">Below HKCU; <c>Software</c> in production, a throw-away key in tests.</param>
    public BrowserIntegrationInstaller(string dataDir, string? appDir = null, string registryRoot = "Software")
    {
        _dataDir = dataDir;
        _appDir = appDir ?? AppContext.BaseDirectory;
        _registryRoot = registryRoot;
    }

    public string HostDirectory => Path.Combine(_dataDir, "native-host");
    public string ManifestPath => Path.Combine(HostDirectory, BridgeProtocol.HostName + ".json");
    public string HostExecutable => Path.Combine(_appDir, "NativeHost", "ShadeNet.NativeHost.exe");
    public string ExtensionFolder => Path.Combine(_appDir, "browser", "chromium");
    public string FirefoxManifestPath => Path.Combine(HostDirectory, BridgeProtocol.HostName + ".firefox.json");
    public string FirefoxExtensionFolder => Path.Combine(_appDir, "browser", "firefox");

    /// <summary>The Firefox add-on as a file: the signed one when the release carries it, else none (see <see cref="FirefoxExtensionFolder"/>).</summary>
    public string? FirefoxPackage => File.Exists(Path.Combine(_appDir, "browser", "shadenet-firefox.xpi")) ? Path.Combine(_appDir, "browser", "shadenet-firefox.xpi") : null;

    string FirefoxKeyPath => $@"{_registryRoot}\Mozilla\NativeMessagingHosts\{BridgeProtocol.HostName}";

    /// <summary>The host carries its own .NET runtime (hostfxr next to it): it runs on a computer that has none installed.</summary>
    public bool HostIsSelfContained => File.Exists(Path.Combine(_appDir, "NativeHost", "hostfxr.dll"));
    string HostConfigPath => Path.Combine(_dataDir, "host.json");
    string LauncherPath => Path.Combine(HostDirectory, "ShadeNetHost.cmd");

    string KeyPath(BrowserTarget browser) => $@"{_registryRoot}\{browser.VendorPath}\NativeMessagingHosts\{BridgeProtocol.HostName}";

    public IntegrationStatus GetStatus()
    {
        var registered = new List<string>();
        foreach (var browser in Browsers)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath(browser));
            if (key?.GetValue(null) as string is { } path && string.Equals(path, ManifestPath, StringComparison.OrdinalIgnoreCase) && File.Exists(ManifestPath))
                registered.Add(browser.Name);
        }

        using (var firefox = Registry.CurrentUser.OpenSubKey(FirefoxKeyPath))
        {
            if (firefox?.GetValue(null) as string is { } path && string.Equals(path, FirefoxManifestPath, StringComparison.OrdinalIgnoreCase) && File.Exists(FirefoxManifestPath))
                registered.Add(FirefoxName);
        }

        return new IntegrationStatus(File.Exists(HostExecutable), Directory.Exists(ExtensionFolder), registered);
    }

    /// <summary>Writes the manifest, the host's config and the registry entries. Safe to run again.</summary>
    public void Install()
    {
        if (!File.Exists(HostExecutable))
            throw new FileNotFoundException("The browser host is missing from this installation.", HostExecutable);

        Directory.CreateDirectory(HostDirectory);
        // A self-contained host (the released program) brings its own runtime; only a framework-dependent one needs DOTNET_ROOT.
        var dotnetRoot = !HostIsSelfContained && NeedsDotnetRoot() ? CurrentDotnetRoot() : null;

        // How the host finds the app if it is not running (and with which runtime).
        File.WriteAllText(HostConfigPath, JsonSerializer.Serialize(
            new { appPath = Environment.ProcessPath, dotnetRoot }, BridgeProtocol.Json));

        // The browser starts "path" directly. A framework-dependent host outside the machine-wide runtime needs
        // DOTNET_ROOT, which only a small launcher can provide; a self-contained host would not need it.
        string path = HostExecutable;
        if (dotnetRoot != null)
        {
            File.WriteAllText(LauncherPath, $"@echo off\r\nset \"DOTNET_ROOT={dotnetRoot}\"\r\n\"{HostExecutable}\" %*\r\n");
            path = LauncherPath;
        }

        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new
        {
            name = BridgeProtocol.HostName,
            description = "ShadeNet Downloader browser integration",
            path,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" },
        }, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var browser in Browsers)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath(browser));
            key.SetValue(null, ManifestPath, RegistryValueKind.String);
        }

        // Firefox: the same host, allowed for exactly the ShadeNet add-on (by its ID: Firefox has no extension origins here)
        File.WriteAllText(FirefoxManifestPath, JsonSerializer.Serialize(new
        {
            name = BridgeProtocol.HostName,
            description = "ShadeNet Downloader browser integration",
            path,
            type = "stdio",
            allowed_extensions = new[] { FirefoxExtensionId },
        }, new JsonSerializerOptions { WriteIndented = true }));
        using (var firefox = Registry.CurrentUser.CreateSubKey(FirefoxKeyPath))
            firefox.SetValue(null, FirefoxManifestPath, RegistryValueKind.String);
    }

    /// <summary>Removes everything <see cref="Install"/> wrote.</summary>
    public void Uninstall()
    {
        foreach (var browser in Browsers)
            Registry.CurrentUser.DeleteSubKey(KeyPath(browser), throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKey(FirefoxKeyPath, throwOnMissingSubKey: false);

        foreach (var file in new[] { ManifestPath, FirefoxManifestPath, LauncherPath, HostConfigPath })
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (IOException)
            {
                // a browser may still hold the launcher; it is harmless without the registry entries
            }
        }
    }

    /// <summary>The machine-wide runtime is found by apphosts on their own; anything else needs DOTNET_ROOT.</summary>
    static bool NeedsDotnetRoot()
    {
        var root = CurrentDotnetRoot();
        var machineWide = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        return !string.Equals(Path.GetFullPath(root).TrimEnd('\\'), Path.GetFullPath(machineWide).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    static string CurrentDotnetRoot() =>
        Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
}
