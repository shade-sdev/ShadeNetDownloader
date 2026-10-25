using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ShadeNet.Setup.Tests;

/// <summary>An installed program is updated from a package: everything happens in a throw-away folder and registry key.</summary>
public sealed class UpdateEngineTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "shadenet-upd-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string _registryRoot = @"Software\ShadeNetTests\Update-" + Guid.NewGuid().ToString("N")[..8];
    const string AppKey = @"Microsoft\Windows\CurrentVersion\Uninstall\ShadeNetDownloader";

    public UpdateEngineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registryRoot, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\ShadeNetTests"))
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\ShadeNetTests", throwOnMissingSubKey: false);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    string Dir(string name) => Path.Combine(_root, name);
    string Installed(string relative) => Path.Combine(Dir("app"), relative);

    InstallOptions Options(Action<InstallOptions>? tweak = null)
    {
        var o = new InstallOptions
        {
            InstallDir = Dir("app"), DataDir = Dir("data"), RegistryRoot = _registryRoot,
            StartMenuDir = Dir("startmenu"), DesktopDir = Dir("desktop"), BrowserIntegration = false, QuitWaitMs = 600,
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
        ("Uninstall.exe", "uninstaller v1"),
        ("NativeHost/ShadeNet.NativeHost.exe", "host v1"),
        ("tools/yt-dlp/yt-dlp.exe", "yt-dlp"),
        ("tools/ffmpeg/ffmpeg.exe", "ffmpeg"),
        ("browser/chromium/manifest.json", "{\"v\":1}"),
        ("old-only.dll", "gone in v2"),
    };

    // an update package: the program without tools/, some files changed, one new, one gone
    static readonly (string, string)[] V2 =
    {
        ("ShadeNetDownloader.exe", "app v2"),
        ("Uninstall.exe", "uninstaller v2"),
        ("NativeHost/ShadeNet.NativeHost.exe", "host v2"),
        ("browser/chromium/manifest.json", "{\"v\":2}"),
        ("new-in-v2.dll", "new"),
    };

    InstallEngine InstallV1(Action<InstallOptions>? tweak = null)
    {
        var engine = new InstallEngine(Options(tweak));
        engine.Install(Zip(V1), "1.0.1");
        Directory.CreateDirectory(Dir("data"));
        File.WriteAllText(Path.Combine(Dir("data"), "settings.json"), "the user's own");   // the user's own data, made by using the program
        return engine;
    }

    static string Text(string path) => File.ReadAllText(path);

    // ---- the update ----

    [Fact]
    public void An_update_replaces_adds_and_removes_files_and_keeps_tools_shortcuts_and_data()
    {
        Directory.CreateDirectory(Dir("data"));
        InstallV1(o => o.StartWithWindows = true);
        var engine = new InstallEngine(Options());

        var version = engine.Update(Zip(V2), versionHint: "1.0.2");

        Assert.Equal("1.0.2", version);
        Assert.Equal("app v2", Text(Installed("ShadeNetDownloader.exe")));
        Assert.Equal("uninstaller v2", Text(Installed("Uninstall.exe")));            // the uninstaller is updated too
        Assert.Equal("host v2", Text(Installed(@"NativeHost\ShadeNet.NativeHost.exe")));
        Assert.Equal("{\"v\":2}", Text(Installed(@"browser\chromium\manifest.json")));
        Assert.Equal("new", Text(Installed("new-in-v2.dll")));
        Assert.False(File.Exists(Installed("old-only.dll")));                          // what the new version no longer has is gone
        Assert.Equal("yt-dlp", Text(Installed(@"tools\yt-dlp\yt-dlp.exe")));          // not in the package: kept
        Assert.Equal("ffmpeg", Text(Installed(@"tools\ffmpeg\ffmpeg.exe")));
        Assert.False(Directory.Exists(Installed(".update")));                          // no staging left behind

        var manifest = engine.ReadManifest()!;
        Assert.Equal("1.0.2", manifest.Version);
        Assert.Contains(@"tools\yt-dlp\yt-dlp.exe", manifest.Files);                  // still listed, so an uninstall removes it
        Assert.Contains("new-in-v2.dll", manifest.Files);
        Assert.DoesNotContain("old-only.dll", manifest.Files);
        Assert.NotNull(manifest.StartMenuShortcut);
        Assert.True(File.Exists(Path.Combine(Dir("startmenu"), "ShadeNet Downloader.lnk")));
        Assert.True(File.Exists(Path.Combine(Dir("desktop"), "ShadeNet Downloader.lnk")));
        Assert.Equal("the user's own", Text(Path.Combine(Dir("data"), "settings.json")));

        using var key = Registry.CurrentUser.OpenSubKey($@"{_registryRoot}\{AppKey}")!;
        Assert.Equal("1.0.2", key.GetValue("DisplayVersion"));
        using var run = Registry.CurrentUser.OpenSubKey($@"{_registryRoot}\Microsoft\Windows\CurrentVersion\Run");
        Assert.NotNull(run?.GetValue("ShadeNetDownloader"));                          // start with Windows: the user's choice is kept
    }

    // ---- the shortcuts' pictures (Explorer keeps the icon it saw first, so a new icon needs the shortcuts saved again) ----

    string Lnk(string folder) => Path.Combine(Dir(folder), "ShadeNet Downloader.lnk");

    static readonly DateTime LongAgo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A shortcut as a user could have changed it: the icon, or what it starts.</summary>
    static string IconOf(string link)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        return (string)shell.CreateShortcut(link).IconLocation;
    }

    static void ChangeShortcut(string link, string? icon = null, string? target = null)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic l = shell.CreateShortcut(link);
        if (icon != null) l.IconLocation = icon;
        if (target != null) l.TargetPath = target;
        l.Save();
    }

    [Fact]
    public void An_update_saves_the_shortcuts_again_and_tells_Explorer_so_a_new_icon_shows()
    {
        InstallV1();
        File.SetLastWriteTimeUtc(Lnk("startmenu"), LongAgo);
        File.SetLastWriteTimeUtc(Lnk("desktop"), LongAgo);
        var told = new List<string>();
        var engine = new InstallEngine(Options()) { ShellIconsChanged = told.AddRange };

        engine.Update(Zip(V2), "1.0.2");

        Assert.True(File.GetLastWriteTimeUtc(Lnk("startmenu")) > LongAgo.AddYears(1));
        Assert.True(File.GetLastWriteTimeUtc(Lnk("desktop")) > LongAgo.AddYears(1));
        Assert.Equal(Installed("ShadeNetDownloader.exe") + ",0", IconOf(Lnk("desktop")));
        Assert.Equal(Installed("ShadeNetDownloader.exe"), Shortcuts.TargetOf(Lnk("desktop")));
        Assert.Contains(Lnk("startmenu"), told);
        Assert.Contains(Lnk("desktop"), told);
    }

    [Fact]
    public void A_shortcut_the_user_pointed_elsewhere_or_gave_an_icon_is_not_overwritten_by_an_update()
    {
        InstallV1();
        var elsewhere = Installed("other.exe");
        File.WriteAllText(elsewhere, "not ours");
        ChangeShortcut(Lnk("desktop"), target: elsewhere);                       // the desktop one now starts something else
        ChangeShortcut(Lnk("startmenu"), icon: elsewhere + ",0");                // the Start menu one has an icon of the user's choice
        File.SetLastWriteTimeUtc(Lnk("desktop"), LongAgo);
        var told = new List<string>();

        new InstallEngine(Options()) { ShellIconsChanged = told.AddRange }.Update(Zip(V2), "1.0.2");

        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(Lnk("desktop")));         // not ours any more: untouched
        Assert.DoesNotContain(Lnk("desktop"), told);
        Assert.Equal(elsewhere + ",0", IconOf(Lnk("startmenu")));                // still the user's icon
    }

    [Fact]
    public void An_install_tells_Explorer_about_the_shortcuts_it_made()
    {
        var told = new List<string>();
        new InstallEngine(Options()) { ShellIconsChanged = told.AddRange }.Install(Zip(V1), "1.0.1");

        Assert.Contains(Lnk("startmenu"), told);
        Assert.Contains(Lnk("desktop"), told);
    }

    [Fact]
    public void An_update_still_succeeds_when_Explorer_cannot_be_told()
    {
        InstallV1();
        var engine = new InstallEngine(Options()) { ShellIconsChanged = _ => throw new InvalidOperationException("no shell") };

        Assert.Equal("1.0.2", engine.Update(Zip(V2), "1.0.2"));
        Assert.Equal("app v2", Text(Installed("ShadeNetDownloader.exe")));
    }

    [Fact]
    public void An_update_then_an_uninstall_leaves_nothing_but_the_users_data()
    {
        InstallV1();
        new InstallEngine(Options()).Update(Zip(V2), "1.0.2");

        new InstallEngine(Options()).Uninstall();

        Assert.False(Directory.Exists(Dir("app")));                                    // including tools/: the manifest still knew them
        Assert.True(File.Exists(Path.Combine(Dir("data"), "settings.json")));
        Assert.Null(Registry.CurrentUser.OpenSubKey($@"{_registryRoot}\{AppKey}"));
    }

    [Fact]
    public void A_second_update_works_on_the_result_of_the_first()
    {
        InstallV1();
        var engine = new InstallEngine(Options());
        engine.Update(Zip(V2), "1.0.2");

        engine.Update(Zip(("ShadeNetDownloader.exe", "app v3"), ("Uninstall.exe", "uninstaller v3")), "1.0.3");

        Assert.Equal("app v3", Text(Installed("ShadeNetDownloader.exe")));
        Assert.False(File.Exists(Installed("new-in-v2.dll")));                         // v2's file is stale in v3
        Assert.True(File.Exists(Installed(@"tools\yt-dlp\yt-dlp.exe")));
        Assert.Equal("1.0.3", engine.InstalledVersion);
    }

    // ---- a package that is not what it should be: nothing is touched ----

    void AssertUntouched()
    {
        Assert.Equal("app v1", Text(Installed("ShadeNetDownloader.exe")));
        Assert.Equal("host v1", Text(Installed(@"NativeHost\ShadeNet.NativeHost.exe")));
        Assert.Equal("gone in v2", Text(Installed("old-only.dll")));
        Assert.Equal("1.0.1", new InstallEngine(Options()).InstalledVersion);
        Assert.False(Directory.Exists(Installed(".update")));
        Assert.False(File.Exists(Installed("new-in-v2.dll")));
    }

    [Fact]
    public void Something_that_is_not_a_zip_is_refused_and_nothing_changes()
    {
        InstallV1();
        var ex = Assert.Throws<SetupException>(() => new InstallEngine(Options()).Update(new MemoryStream(Encoding.UTF8.GetBytes("this is not a zip file at all"))));
        Assert.Equal(4, ex.ExitCode);
        AssertUntouched();
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("sub/../../evil.txt")]
    [InlineData("install.json")]                 // a package must not replace the record of what is installed
    [InlineData(".update/old/x.dll")]            // nor reach into the update's own working folder
    public void A_package_with_a_file_where_it_must_not_be_is_refused_whole(string name)
    {
        InstallV1();
        var ex = Assert.Throws<SetupException>(() => new InstallEngine(Options()).Update(Zip(("ShadeNetDownloader.exe", "app v2"), ("new-in-v2.dll", "new"), (name, "boom"))));
        Assert.Equal(4, ex.ExitCode);
        AssertUntouched();                        // not even the good files before it were applied
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public void A_package_without_the_program_is_refused()
    {
        InstallV1();
        var ex = Assert.Throws<SetupException>(() => new InstallEngine(Options()).Update(Zip(("Uninstall.exe", "x"), ("new-in-v2.dll", "new"))));
        Assert.Equal(4, ex.ExitCode);
        AssertUntouched();
    }

    [Fact]
    public void Updating_what_is_not_installed_says_so()
    {
        var ex = Assert.Throws<SetupException>(() => new InstallEngine(Options()).Update(Zip(V2)));
        Assert.Equal(6, ex.ExitCode);
    }

    // ---- a failure half way: the old program is put back ----

    [Fact]
    public void A_file_that_cannot_be_replaced_puts_every_replaced_file_back()
    {
        InstallV1();
        var engine = new InstallEngine(Options());

        // the last file of the package is held open by something else (an antivirus, a browser): its swap fails after the others went in
        var locked = Installed("browser/chromium/manifest.json".Replace('/', '\\'));
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<SetupException>(() => engine.Update(Zip(
            ("ShadeNetDownloader.exe", "app v2"), ("NativeHost/ShadeNet.NativeHost.exe", "host v2"), ("new-in-v2.dll", "new"), ("browser/chromium/manifest.json", "{\"v\":2}"))));

        Assert.Equal(5, ex.ExitCode);
        Assert.Contains("previous version was put back", ex.Message);
        hold.Dispose();
        AssertUntouched();
        Assert.Equal("{\"v\":1}", Text(locked));
    }

    [Fact]
    public void An_update_that_was_cut_off_before_it_finished_is_undone_before_the_next_starts()
    {
        InstallV1();
        // what a killed update leaves: the program half replaced, the originals waiting in the staging folder, no "committed" mark
        var staging = Installed(".update");
        Directory.CreateDirectory(Path.Combine(staging, "old"));
        File.WriteAllText(Path.Combine(staging, "old", "ShadeNetDownloader.exe"), "app v1");
        File.WriteAllText(Installed("ShadeNetDownloader.exe"), "half way, torn");

        // the next update is given a bad package, so only the recovery can be seen
        Assert.Throws<SetupException>(() => new InstallEngine(Options()).Update(Zip(("nothing.dll", "x"))));

        Assert.Equal("app v1", Text(Installed("ShadeNetDownloader.exe")));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void An_update_that_had_finished_is_not_undone()
    {
        InstallV1();
        var staging = Installed(".update");
        Directory.CreateDirectory(Path.Combine(staging, "old"));
        File.WriteAllText(Path.Combine(staging, "old", "ShadeNetDownloader.exe"), "app v1");
        File.WriteAllText(Path.Combine(staging, "committed"), "1.0.2");
        File.WriteAllText(Installed("ShadeNetDownloader.exe"), "app v2 complete");

        Assert.Throws<SetupException>(() => new InstallEngine(Options()).Update(Zip(("nothing.dll", "x"))));

        Assert.Equal("app v2 complete", Text(Installed("ShadeNetDownloader.exe")));   // only the leftovers are cleaned up
        Assert.False(Directory.Exists(staging));
    }

    // ---- how the updater finds out how the program was installed ----

    [Fact]
    public void An_install_in_another_registry_or_data_folder_is_remembered_for_its_updates()
    {
        InstallV1();
        var json = JsonDocument.Parse(File.ReadAllText(Installed("install.json"))).RootElement;
        Assert.Equal(_registryRoot, json.GetProperty("RegistryRoot").GetString());
        Assert.Equal(Dir("data"), json.GetProperty("DataDir").GetString());
    }

    [Fact]
    public void The_updater_adopts_the_places_the_installation_used_unless_it_was_told_otherwise()
    {
        var manifest = new InstallManifest("1.0.1", new List<string>(), null, null, @"Software\Elsewhere", @"C:\somewhere\data");
        var fresh = new InstallOptions();                         // a copy of the uninstaller started without any switch
        InstallEngine.AdoptInstallSettings(fresh, manifest);
        Assert.Equal(@"Software\Elsewhere", fresh.RegistryRoot);
        Assert.Equal(@"C:\somewhere\data", fresh.DataDir);

        var told = new InstallOptions { RegistryRoot = @"Software\Chosen", DataDir = @"C:\chosen" };
        InstallEngine.AdoptInstallSettings(told, manifest);
        Assert.Equal((@"Software\Chosen", @"C:\chosen"), (told.RegistryRoot, told.DataDir));

        var plain = new InstallOptions();                         // a normal installation records nothing, so nothing is changed
        InstallEngine.AdoptInstallSettings(plain, new InstallManifest("1.0.1", new List<string>(), null, null));
        Assert.Equal("Software", plain.RegistryRoot);
    }

    [Fact]
    public void The_command_line_of_an_update()
    {
        var o = InstallOptions.Parse(new[] { "--update", @"C:\x\pkg.zip", "--wait-for", "123", "--dir", @"C:\a b", "--launch", "--silent" });
        Assert.Equal((SetupMode.Update, @"C:\x\pkg.zip", 123, @"C:\a b", true, true), (o.Mode, o.PackagePath, o.WaitForProcessId, o.InstallDir, o.LaunchAfter, o.Silent));
    }
}
