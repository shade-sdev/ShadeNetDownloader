namespace ShadeNet.Setup;

public enum SetupMode
{
    Install,

    /// <summary>The first step of an uninstall: copies itself out of the folder that is about to be deleted and runs there.</summary>
    Uninstall,

    /// <summary>The copy that does the work (it runs from a temp folder, so the install folder can be deleted completely).</summary>
    UninstallStage2,

    /// <summary>
    /// Replaces the files of an installed program with a newer version from an update package (a zip without yt-dlp and ffmpeg).
    /// The program starts this from a copy of the uninstaller in the temp folder, then quits; shortcuts, start-up and the
    /// browser connection stay as the user set them, and so does everything in the data folder.
    /// </summary>
    Update,
}

/// <summary>What to install and where. Everything that touches the system can be pointed elsewhere, so tests never touch the real one.</summary>
public sealed class InstallOptions
{
    public const string ProductName = "ShadeNet Downloader";

    public SetupMode Mode { get; set; } = SetupMode.Install;
    public bool Silent { get; set; }

    /// <summary>Per-user, no administrator rights: <c>%LOCALAPPDATA%\Programs\ShadeNet Downloader</c>.</summary>
    public string InstallDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);

    public bool StartMenuShortcut { get; set; } = true;
    public bool DesktopShortcut { get; set; } = true;
    public bool StartWithWindows { get; set; }

    /// <summary>Register the native-messaging host for Chrome/Edge/Brave (runs the installed app's own <c>--install-browser-integration</c>).</summary>
    public bool BrowserIntegration { get; set; } = true;

    public bool LaunchAfter { get; set; }

    /// <summary>On uninstall: also delete the download list and settings (<see cref="DataDir"/>). Off by default.</summary>
    public bool RemoveData { get; set; }

    /// <summary>The user's data (settings, download list, updated tools): left alone by installs and, by default, uninstalls.</summary>
    public string DataDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShadeNet");

    // ---- where the system parts live: tests and dry runs redirect these ----

    /// <summary>Below HKCU; <c>Software</c> for real, a throw-away key in tests.</summary>
    public string RegistryRoot { get; set; } = "Software";

    public string StartMenuDir { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    public string DesktopDir { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>A zip to install instead of the one inside the setup program (tests, and "setup from a folder").</summary>
    public string? PayloadPath { get; set; }

    /// <summary>With <see cref="SetupMode.Update"/>: the update package.</summary>
    public string? PackagePath { get; set; }

    /// <summary>Used by <see cref="SetupMode.UninstallStage2"/>: wait for this process (the first stage) to end first.</summary>
    public int WaitForProcessId { get; set; }

    /// <summary>How long a running ShadeNet gets to quit when it is asked to, before setup gives up.</summary>
    public int QuitWaitMs { get; set; } = 15000;

    public string AppExe => Path.Combine(InstallDir, InstallEngine.AppExe);

    public static InstallOptions Parse(IEnumerable<string> args)
    {
        var o = new InstallOptions();
        var list = args.ToList();

        string? Next(ref int i) => i + 1 < list.Count ? list[++i] : null;

        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            var lower = a.ToLowerInvariant();

            // the "/D=path" and "/DIR=path" forms of classic installers
            if (lower.StartsWith("/d=") || lower.StartsWith("/dir=") || lower.StartsWith("--dir="))
            {
                o.InstallDir = a[(a.IndexOf('=') + 1)..].Trim('"');
                continue;
            }

            switch (lower)
            {
                case "--silent" or "/s" or "/silent" or "/verysilent" or "-s": o.Silent = true; break;
                case "--dir" or "/dir": o.InstallDir = Next(ref i) ?? o.InstallDir; break;
                case "--uninstall" or "/uninstall": o.Mode = SetupMode.Uninstall; break;
                case "--uninstall-stage2": o.Mode = SetupMode.UninstallStage2; break;
                case "--update": o.Mode = SetupMode.Update; o.PackagePath = Next(ref i); break;
                case "--wait-for": int.TryParse(Next(ref i), out var pid); o.WaitForProcessId = pid; break;
                case "--no-desktop-shortcut": o.DesktopShortcut = false; break;
                case "--no-start-menu-shortcut": o.StartMenuShortcut = false; break;
                case "--startup": o.StartWithWindows = true; break;
                case "--no-browser-integration": o.BrowserIntegration = false; break;
                case "--launch": o.LaunchAfter = true; break;
                case "--remove-data": o.RemoveData = true; break;
                case "--data-dir": o.DataDir = Next(ref i) ?? o.DataDir; break;
                case "--registry-root": o.RegistryRoot = Next(ref i) ?? o.RegistryRoot; break;
                case "--start-menu-dir": o.StartMenuDir = Next(ref i) ?? o.StartMenuDir; break;
                case "--desktop-dir": o.DesktopDir = Next(ref i) ?? o.DesktopDir; break;
                case "--payload": o.PayloadPath = Next(ref i); break;
            }
        }

        return o;
    }
}
