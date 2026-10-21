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
/// Registers the native messaging host with the Chromium browsers so that the extension can reach the app:
/// a manifest file plus one registry value per browser, all under HKEY_CURRENT_USER (no administrator rights, nothing
/// machine-wide). Everything it writes can be removed again with <see cref="Uninstall"/>.
/// </summary>
public sealed class BrowserIntegrationInstaller
{
    /// <summary>
    /// The extension's ID is fixed by the <c>key</c> in its manifest (<c>extension/chromium/manifest.json</c>), which is
    /// what lets the host manifest name exactly one extension in <c>allowed_origins</c>.
    /// </summary>
    public const string ExtensionId = "bcegodlanfeghhakadmalbkbhbhbejop";

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

        return new IntegrationStatus(File.Exists(HostExecutable), Directory.Exists(ExtensionFolder), registered);
    }

    /// <summary>Writes the manifest, the host's config and the registry entries. Safe to run again.</summary>
    public void Install()
    {
        if (!File.Exists(HostExecutable))
            throw new FileNotFoundException("The browser host is missing from this installation.", HostExecutable);

        Directory.CreateDirectory(HostDirectory);
        var dotnetRoot = NeedsDotnetRoot() ? CurrentDotnetRoot() : null;

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
    }

    /// <summary>Removes everything <see cref="Install"/> wrote.</summary>
    public void Uninstall()
    {
        foreach (var browser in Browsers)
            Registry.CurrentUser.DeleteSubKey(KeyPath(browser), throwOnMissingSubKey: false);

        foreach (var file in new[] { ManifestPath, LauncherPath, HostConfigPath })
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
