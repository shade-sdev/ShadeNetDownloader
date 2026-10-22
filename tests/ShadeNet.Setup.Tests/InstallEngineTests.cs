using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32;

namespace ShadeNet.Setup.Tests;

/// <summary>Everything happens in a throw-away folder and a throw-away registry key: the real system is never touched.</summary>
public sealed class InstallEngineTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "shadenet-setup-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string _registryRoot = @"Software\ShadeNetTests\Setup-" + Guid.NewGuid().ToString("N")[..8];

    public InstallEngineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registryRoot, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\ShadeNetTests"))
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\ShadeNetTests", throwOnMissingSubKey: false);
        foreach (var p in Process.GetProcessesByName("ShadeNetDownloader"))
        {
            try
            {
                if (p.MainModule?.FileName.StartsWith(_root, StringComparison.OrdinalIgnoreCase) == true) p.Kill();
            }
            catch
            {
                // gone
            }
        }

        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // best effort
        }
    }

    string Dir(string name) => Path.Combine(_root, name);

    InstallOptions Options(string name = "app", Action<InstallOptions>? tweak = null)
    {
        var o = new InstallOptions
        {
            InstallDir = Dir(name),
            DataDir = Dir("data"),
            RegistryRoot = _registryRoot,
            StartMenuDir = Dir("startmenu"),
            DesktopDir = Dir("desktop"),
            BrowserIntegration = false,
            QuitWaitMs = 600,
        };
        tweak?.Invoke(o);
        return o;
    }

    static MemoryStream Zip(params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }

        stream.Position = 0;
        return stream;
    }

    static readonly (string, string)[] V1 =
    {
        ("ShadeNetDownloader.exe", "app v1"),
        ("Uninstall.exe", "uninstaller"),
        ("NativeHost/ShadeNet.NativeHost.exe", "host"),
        ("tools/yt-dlp/yt-dlp.exe", "yt-dlp"),
        ("tools/ffmpeg/ffmpeg.exe", "ffmpeg"),
        ("browser/chromium/manifest.json", "{}"),
        ("old-only.dll", "gone in v2"),
    };

    const string AppKey = @"Microsoft\Windows\CurrentVersion\Uninstall\ShadeNetDownloader";

    // ---- arguments ----

    [Fact]
    public void The_command_line_understands_the_forms_installers_use()
    {
        var o = InstallOptions.Parse(new[] { "/S", "/D=C:\\Apps\\ShadeNet", "--no-desktop-shortcut", "--startup", "--no-browser-integration", "--launch" });
        Assert.True(o.Silent);
        Assert.Equal(@"C:\Apps\ShadeNet", o.InstallDir);
        Assert.False(o.DesktopShortcut);
        Assert.True(o.StartWithWindows);
        Assert.False(o.BrowserIntegration);
        Assert.True(o.LaunchAfter);
        Assert.Equal(SetupMode.Install, o.Mode);

        var u = InstallOptions.Parse(new[] { "--uninstall", "--silent", "--dir", @"C:\x y", "--remove-data", "--registry-root", "Software\\T" });
        Assert.Equal((SetupMode.Uninstall, true, @"C:\x y", true, "Software\\T"), (u.Mode, u.Silent, u.InstallDir, u.RemoveData, u.RegistryRoot));
        Assert.Equal(SetupMode.UninstallStage2, InstallOptions.Parse(new[] { "--uninstall-stage2" }).Mode);

        var defaults = new InstallOptions();
        Assert.EndsWith(@"Programs\ShadeNet Downloader", defaults.InstallDir);          // per user: no administrator rights
        Assert.False(defaults.StartWithWindows);                                        // nothing starts by itself unless asked
        Assert.False(defaults.RemoveData);
    }

    // ---- installing ----

    [Fact]
    public void A_fresh_install_unpacks_everything_and_registers_itself()
    {
        var options = Options(tweak: o => o.StartWithWindows = false);
        var engine = new InstallEngine(options);

        engine.Install(Zip(V1), "1.0.0");

        foreach (var (name, content) in V1) Assert.Equal(content, File.ReadAllText(Path.Combine(options.InstallDir, name.Replace('/', '\\'))));
        Assert.Empty(Directory.GetFiles(options.InstallDir, "*.setup-new", SearchOption.AllDirectories));

        var manifest = engine.ReadManifest()!;
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal(V1.Length, manifest.Files.Count);
        Assert.Contains(@"tools\ffmpeg\ffmpeg.exe", manifest.Files);

        // shortcuts that point to the program
        Assert.Equal(options.AppExe, Shortcuts.TargetOf(Path.Combine(options.StartMenuDir, "ShadeNet Downloader.lnk")));
        Assert.Equal(options.AppExe, Shortcuts.TargetOf(Path.Combine(options.DesktopDir, "ShadeNet Downloader.lnk")));

        // "Apps & features"
        using var key = Registry.CurrentUser.OpenSubKey($@"{_registryRoot}\{AppKey}")!;
        Assert.Equal("ShadeNet Downloader", key.GetValue("DisplayName"));
        Assert.Equal("1.0.0", key.GetValue("DisplayVersion"));
        Assert.Equal(options.InstallDir, key.GetValue("InstallLocation"));
        var uninstall = (string)key.GetValue("UninstallString")!;
        Assert.Contains(Path.Combine(options.InstallDir, "Uninstall.exe"), uninstall);
        Assert.Contains("--uninstall", uninstall);
        Assert.Contains("--silent", (string)key.GetValue("QuietUninstallString")!);
        Assert.Equal(1, key.GetValue("NoModify"));

        // nothing starts with Windows unless asked
        using var run = Registry.CurrentUser.OpenSubKey($@"{_registryRoot}\Microsoft\Windows\CurrentVersion\Run");
        Assert.Null(run?.GetValue("ShadeNetDownloader"));
    }

    [Fact]
    public void The_options_for_shortcuts_and_startup_are_followed()
    {
        var options = Options(tweak: o => { o.DesktopShortcut = false; o.StartWithWindows = true; });
        new InstallEngine(options).Install(Zip(V1), "1.0.0");

        Assert.True(File.Exists(Path.Combine(options.StartMenuDir, "ShadeNet Downloader.lnk")));
        Assert.False(File.Exists(Path.Combine(options.DesktopDir, "ShadeNet Downloader.lnk")));

        using var run = Registry.CurrentUser.OpenSubKey($@"{_registryRoot}\Microsoft\Windows\CurrentVersion\Run")!;
        Assert.Equal($"\"{options.AppExe}\" --minimized", run.GetValue("ShadeNetDownloader"));   // what the app's own switch writes
    }

    [Fact]
    public void A_folder_that_is_not_ours_is_never_touched()
    {
        var options = Options("someone-elses");
        Directory.CreateDirectory(options.InstallDir);
        File.WriteAllText(Path.Combine(options.InstallDir, "thesis.docx"), "precious");

        var ex = Assert.Throws<SetupException>(() => new InstallEngine(options).Install(Zip(V1), "1.0.0"));

        Assert.Equal(2, ex.ExitCode);
        Assert.Equal(new[] { "thesis.docx" }, Directory.GetFiles(options.InstallDir).Select(Path.GetFileName));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(options.InstallDir, "thesis.docx")));
        Assert.False(Directory.Exists(Dir("startmenu")));                              // and nothing else was done
    }

    [Fact]
    public void A_package_with_a_path_that_escapes_the_folder_is_refused()
    {
        var options = Options();
        var ex = Assert.Throws<SetupException>(() =>
            new InstallEngine(options).Install(Zip(("ShadeNetDownloader.exe", "x"), ("../evil.txt", "boom")), "1.0.0"));

        Assert.Equal(4, ex.ExitCode);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    // ---- upgrading ----

    [Fact]
    public void An_upgrade_replaces_files_removes_stale_ones_and_keeps_what_belongs_to_the_user()
    {
        var options = Options();
        var engine = new InstallEngine(options);
        engine.Install(Zip(V1), "1.0.0");
        File.WriteAllText(Path.Combine(options.InstallDir, "my-notes.txt"), "mine");              // not from the package
        Directory.CreateDirectory(options.DataDir);
        File.WriteAllText(Path.Combine(options.DataDir, "settings.json"), "{\"keep\":true}");

        engine.Install(Zip(
            ("ShadeNetDownloader.exe", "app v2"),
            ("Uninstall.exe", "uninstaller v2"),
            ("NativeHost/ShadeNet.NativeHost.exe", "host"),
            ("tools/yt-dlp/yt-dlp.exe", "yt-dlp v2"),
            ("new-in-v2.dll", "new")), "2.0.0");

        Assert.Equal("app v2", File.ReadAllText(options.AppExe));
        Assert.Equal("yt-dlp v2", File.ReadAllText(Path.Combine(options.InstallDir, @"tools\yt-dlp\yt-dlp.exe")));
        Assert.True(File.Exists(Path.Combine(options.InstallDir, "new-in-v2.dll")));
        Assert.False(File.Exists(Path.Combine(options.InstallDir, "old-only.dll")));              // the old version's file, gone
        Assert.False(File.Exists(Path.Combine(options.InstallDir, @"tools\ffmpeg\ffmpeg.exe")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(options.InstallDir, "my-notes.txt")));
        Assert.Equal("{\"keep\":true}", File.ReadAllText(Path.Combine(options.DataDir, "settings.json")));
        Assert.Equal("2.0.0", engine.InstalledVersion);
        using var key = Registry.CurrentUser.OpenSubKey($@"{_registryRoot}\{AppKey}")!;
        Assert.Equal("2.0.0", key.GetValue("DisplayVersion"));
    }

    [Fact]
    public void A_running_program_that_does_not_quit_stops_the_install_before_anything_is_replaced()
    {
        var options = Options();
        var engine = new InstallEngine(options);
        engine.Install(Zip(V1), "1.0.0");

        // the running program, which ignores the request to quit (a stand-in of our own, started for real in the installed folder)
        using var running = new StandInProgram(options.InstallDir, obeysQuit: false);

        var ex = Assert.Throws<SetupException>(() => engine.Install(Zip(("ShadeNetDownloader.exe", "app v2"), ("Uninstall.exe", "u")), "2.0.0"));

        Assert.False(running.Process.HasExited);                                                  // it was asked, and carried on: that is the case
        Assert.Equal(3, ex.ExitCode);
        Assert.Contains("still running", ex.Message);
        Assert.Equal("1.0.0", engine.InstalledVersion);                                           // nothing was replaced
        Assert.True(File.Exists(Path.Combine(options.InstallDir, "old-only.dll")));
    }

    [Fact]
    public void A_program_that_quits_when_asked_does_not_stop_the_install()
    {
        var options = Options(tweak: o => o.QuitWaitMs = 8000);
        var engine = new InstallEngine(options);
        engine.Install(Zip(V1), "1.0.0");

        using var running = new StandInProgram(options.InstallDir, obeysQuit: true);

        engine.Install(Zip(("ShadeNetDownloader.exe", "app v2"), ("Uninstall.exe", "u")), "2.0.0");

        Assert.True(running.Process.HasExited);                                                   // "--quit" reached it, and it ended
        Assert.Equal("app v2", File.ReadAllText(options.AppExe));
        Assert.Equal("2.0.0", engine.InstalledVersion);
    }

    // ---- uninstalling ----

    [Fact]
    public void An_uninstall_removes_the_program_and_everything_it_registered_but_not_the_users_data_or_files()
    {
        var options = Options(tweak: o => o.StartWithWindows = true);
        var engine = new InstallEngine(options);
        engine.Install(Zip(V1), "1.0.0");
        File.WriteAllText(Path.Combine(options.InstallDir, "my-notes.txt"), "mine");
        Directory.CreateDirectory(options.DataDir);
        File.WriteAllText(Path.Combine(options.DataDir, "settings.json"), "{}");

        engine.Uninstall();

        Assert.False(File.Exists(options.AppExe));
        Assert.False(Directory.Exists(Path.Combine(options.InstallDir, "tools")));              // emptied folders go
        Assert.Equal(new[] { "my-notes.txt" }, Directory.GetFiles(options.InstallDir).Select(Path.GetFileName));   // the user's own file stays, so does its folder
        Assert.False(File.Exists(Path.Combine(options.StartMenuDir, "ShadeNet Downloader.lnk")));
        Assert.False(File.Exists(Path.Combine(options.DesktopDir, "ShadeNet Downloader.lnk")));
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"{_registryRoot}\{AppKey}"));
        using var run = Registry.CurrentUser.OpenSubKey($@"{_registryRoot}\Microsoft\Windows\CurrentVersion\Run");
        Assert.Null(run?.GetValue("ShadeNetDownloader"));
        Assert.True(File.Exists(Path.Combine(options.DataDir, "settings.json")));              // the data stays unless asked
        Assert.Null(engine.ReadManifest());
    }

    [Fact]
    public void An_uninstall_can_also_remove_the_data_and_leaves_an_empty_folder_behind_nowhere()
    {
        var options = Options(tweak: o => o.RemoveData = true);
        var engine = new InstallEngine(options);
        engine.Install(Zip(V1), "1.0.0");
        Directory.CreateDirectory(options.DataDir);
        File.WriteAllText(Path.Combine(options.DataDir, "settings.json"), "{}");

        engine.Uninstall();

        Assert.False(Directory.Exists(options.DataDir));
        Assert.False(Directory.Exists(options.InstallDir));                                     // nothing of the user's in it: the folder itself goes
    }

    [Fact]
    public void Another_installations_startup_entry_is_not_removed_and_uninstalling_nothing_says_so()
    {
        var options = Options(tweak: o => o.StartWithWindows = false);
        var engine = new InstallEngine(options);
        engine.Install(Zip(V1), "1.0.0");
        using (var run = Registry.CurrentUser.CreateSubKey($@"{_registryRoot}\Microsoft\Windows\CurrentVersion\Run"))
            run.SetValue("ShadeNetDownloader", "\"D:\\elsewhere\\ShadeNetDownloader.exe\" --minimized");   // some other copy

        engine.Uninstall();

        using var after = Registry.CurrentUser.OpenSubKey($@"{_registryRoot}\Microsoft\Windows\CurrentVersion\Run")!;
        Assert.NotNull(after.GetValue("ShadeNetDownloader"));

        var ex = Assert.Throws<SetupException>(() => new InstallEngine(Options("never-installed")).Uninstall());
        Assert.Equal(6, ex.ExitCode);
    }
}
