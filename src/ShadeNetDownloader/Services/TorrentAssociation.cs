using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ShadeNetDownloader.Services;

/// <summary>Whether Windows opens magnet links and .torrent files with ShadeNet.</summary>
/// <param name="Registered">ShadeNet is offered for them (in "Open with" and Windows' Default apps).</param>
/// <param name="OpensMagnet">A magnet link opens ShadeNet now.</param>
/// <param name="OpensTorrentFiles">A double-clicked .torrent file opens ShadeNet now.</param>
public sealed record AssociationStatus(bool Registered, bool OpensMagnet, bool OpensTorrentFiles);

/// <summary>
/// Lets Windows open magnet links and .torrent files with ShadeNet, for the current user only (HKEY_CURRENT_USER, no administrator rights).
/// ShadeNet is registered as a program that can open them (its own ProgIDs, "Open with", and Windows' Default apps); it becomes the one
/// that opens them only where no other program does yet. Another torrent program the user chose is never pushed aside: Windows keeps that
/// choice to the user (Settings > Apps > Default apps, which <see cref="DefaultAppsPage"/> opens). Everything written is removed again by
/// <see cref="Unregister"/>, and by the uninstaller.
/// </summary>
public sealed class TorrentAssociation
{
    public const string TorrentProgId = "ShadeNet.Torrent";
    public const string MagnetProgId = "ShadeNet.Magnet";
    public const string AppName = "ShadeNet Downloader";

    /// <summary>Windows' Default apps page for ShadeNet (Windows 11 opens it on ShadeNet's own entry).</summary>
    public const string DefaultAppsPage = "ms-settings:defaultapps?registeredAppUser=ShadeNet%20Downloader";

    readonly string _exe;
    readonly string _root;
    readonly Func<string, string?> _currentCommand;

    /// <param name="exePath">The program that opens them (this one by default).</param>
    /// <param name="registryRoot">Below HKCU; <c>Software</c> in production, a throw-away key in tests.</param>
    /// <param name="currentCommand">The command Windows runs now for "magnet" or ".torrent" (null when none); the shell is asked by default.</param>
    public TorrentAssociation(string? exePath = null, string registryRoot = "Software", Func<string, string?>? currentCommand = null)
    {
        _exe = exePath ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ShadeNetDownloader.exe");
        _root = registryRoot;
        _currentCommand = currentCommand ?? ShellCommand;
    }

    string Classes => $@"{_root}\Classes";
    string CapabilitiesPath => $@"{_root}\ShadeNet\Capabilities";
    string OwnedPath => $@"{_root}\ShadeNet\Associations";
    string RegisteredApplicationsPath => $@"{_root}\RegisteredApplications";

    /// <summary>What Windows runs: the program with the link or file.</summary>
    public string Command => $"\"{_exe}\" \"%1\"";

    bool IsOurs(string? command) => command != null && command.Contains(_exe, StringComparison.OrdinalIgnoreCase);

    public AssociationStatus GetStatus()
    {
        using var progId = Registry.CurrentUser.OpenSubKey($@"{Classes}\{TorrentProgId}\shell\open\command");
        var registered = string.Equals(progId?.GetValue(null) as string, Command, StringComparison.OrdinalIgnoreCase);
        return new AssociationStatus(registered, IsOurs(_currentCommand("magnet")), IsOurs(_currentCommand(".torrent")));
    }

    /// <summary>Registers ShadeNet for magnet links and .torrent files. Safe to run again.</summary>
    public void Register()
    {
        // what is free is taken (nothing opens it now); what another program opens stays with it
        var magnetFree = _currentCommand("magnet") is not { } magnetNow || IsOurs(magnetNow);
        var torrentFree = _currentCommand(".torrent") is not { } torrentNow || IsOurs(torrentNow);

        var cu = Registry.CurrentUser;
        WriteProgId(cu, TorrentProgId, "Torrent file", urlProtocol: false);
        WriteProgId(cu, MagnetProgId, "Magnet link", urlProtocol: true);

        // "Open with" for .torrent files
        using (var openWith = cu.CreateSubKey($@"{Classes}\.torrent\OpenWithProgids"))
            openWith.SetValue(TorrentProgId, Array.Empty<byte>(), RegistryValueKind.None);

        // Windows' Default apps lists ShadeNet for both
        using (var caps = cu.CreateSubKey(CapabilitiesPath))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "Downloads files, videos and torrents.");
            using (var files = caps.CreateSubKey("FileAssociations")) files.SetValue(".torrent", TorrentProgId);
            using (var urls = caps.CreateSubKey("URLAssociations")) urls.SetValue("magnet", MagnetProgId);
        }

        using (var apps = cu.CreateSubKey(RegisteredApplicationsPath))
            apps.SetValue(AppName, CapabilitiesPath);

        using var owned = cu.CreateSubKey(OwnedPath);
        if (magnetFree)
        {
            WriteProgId(cu, "magnet", "URL:Magnet link", urlProtocol: true);
            owned.SetValue("Magnet", 1, RegistryValueKind.DWord);
        }

        if (torrentFree)
        {
            using var ext = cu.CreateSubKey($@"{Classes}\.torrent");
            ext.SetValue(null, TorrentProgId);
            owned.SetValue("TorrentFiles", 1, RegistryValueKind.DWord);
        }

        NotifyShell();
    }

    /// <summary>
    /// Removes everything <see cref="Register"/> wrote (what another program wrote stays). Only for this program: a registration that
    /// points at another copy of ShadeNet (another installation) is left alone.
    /// </summary>
    public void Unregister()
    {
        var cu = Registry.CurrentUser;
        using (var progId = cu.OpenSubKey($@"{Classes}\{TorrentProgId}\shell\open\command"))
        {
            if (progId?.GetValue(null) is string command && !IsOurs(command)) return;
        }

        using (var owned = cu.OpenSubKey(OwnedPath))
        {
            if (owned?.GetValue("Magnet") is int magnet && magnet == 1)
            {
                using var command = cu.OpenSubKey($@"{Classes}\magnet\shell\open\command");
                if (IsOurs(command?.GetValue(null) as string)) cu.DeleteSubKeyTree($@"{Classes}\magnet", throwOnMissingSubKey: false);
            }

            if (owned?.GetValue("TorrentFiles") is int files && files == 1)
            {
                using var ext = cu.OpenSubKey($@"{Classes}\.torrent", writable: true);
                if (string.Equals(ext?.GetValue(null) as string, TorrentProgId, StringComparison.Ordinal)) ext!.DeleteValue("", throwOnMissingValue: false);
            }
        }

        using (var openWith = cu.OpenSubKey($@"{Classes}\.torrent\OpenWithProgids", writable: true))
            openWith?.DeleteValue(TorrentProgId, throwOnMissingValue: false);
        using (var apps = cu.OpenSubKey(RegisteredApplicationsPath, writable: true))
            apps?.DeleteValue(AppName, throwOnMissingValue: false);

        cu.DeleteSubKeyTree($@"{Classes}\{TorrentProgId}", throwOnMissingSubKey: false);
        cu.DeleteSubKeyTree($@"{Classes}\{MagnetProgId}", throwOnMissingSubKey: false);
        cu.DeleteSubKeyTree(CapabilitiesPath, throwOnMissingSubKey: false);
        cu.DeleteSubKeyTree(OwnedPath, throwOnMissingSubKey: false);
        NotifyShell();
    }

    void WriteProgId(RegistryKey cu, string name, string description, bool urlProtocol)
    {
        using var key = cu.CreateSubKey($@"{Classes}\{name}");
        key.SetValue(null, description);
        if (urlProtocol) key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{_exe}\",0");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue(null, Command);
    }

    // ---- the shell ----

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? output, ref uint length);

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    const int AssocStrCommand = 1;
    const int SHCNE_ASSOCCHANGED = 0x08000000;

    /// <summary>The command Windows runs to open a link type or file type now (the user's own choice included), null when there is none.</summary>
    static string? ShellCommand(string assoc)
    {
        try
        {
            uint length = 0;
            if (AssocQueryString(0, AssocStrCommand, assoc, "open", null, ref length) != 1 || length == 0) return null;   // S_FALSE: the size
            var output = new StringBuilder((int)length);
            return AssocQueryString(0, AssocStrCommand, assoc, "open", output, ref length) == 0 ? output.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    void NotifyShell()
    {
        if (_root != "Software") return;   // (a test's own key: nothing for the shell to learn)
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // the shell picks the change up later
        }
    }
}
