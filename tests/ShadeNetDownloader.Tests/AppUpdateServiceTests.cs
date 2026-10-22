using System.IO;
using ShadeNet.Core.Updates;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

sealed class FakeAppUpdates : IAppUpdates
{
    public AppUpdate? Offer { get; set; }
    public bool Offline { get; set; }
    public bool FailDownload { get; set; }
    public int Checks { get; private set; }
    public List<AppUpdate> Downloaded { get; } = new();

    public Task<AppUpdate?> CheckAsync(CancellationToken ct)
    {
        Checks++;
        if (Offline) throw new UpdateException("GitHub could not be reached: offline");
        return Task.FromResult(Offer);
    }

    public Task<string> DownloadAsync(AppUpdate update, IProgress<double>? progress, CancellationToken ct)
    {
        if (FailDownload) throw new UpdateException("The downloaded update does not match its published checksum; it was discarded and nothing was installed.");
        progress?.Report(0.5);
        progress?.Report(1);
        Downloaded.Add(update);
        return Task.FromResult(@"C:\data\updates\" + update.LatestText + @"\package.zip");
    }

    public static AppUpdate Newer(string current = "1.0.1", string latest = "1.0.2") =>
        new(Version.Parse(current), Version.Parse(latest), new UpdateManifest(1, latest, $"ShadeNetDownloader-Update-{latest}.zip", new string('a', 64), 1000, "o/r"),
            new Uri($"https://github.com/o/r/releases/download/v{latest}/p.zip"), new Uri($"https://github.com/o/r/releases/tag/v{latest}"));
}

sealed class FakeApplier : IAppUpdateApplier
{
    public bool CanApply { get; set; } = true;
    public bool Fail { get; set; }
    public List<string> Started { get; } = new();

    public void Start(string packagePath)
    {
        if (Fail) throw new InvalidOperationException("Uninstall.exe is missing");
        Started.Add(packagePath);
    }
}

public class AppUpdateServiceTests
{
    sealed class Env : IDisposable
    {
        public TempDir Dir = new();
        public SettingsService Settings;
        public FakeAppUpdates Updates = new();
        public FakeApplier Applier = new();
        public ToastService Toasts = new();
        public int Exits;
        public List<Uri> Opened = new();
        public int Active;
        public DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public AppUpdateService Service;

        public Env()
        {
            Settings = new SettingsService(Dir.Path);
            Settings.Load();
            Service = new AppUpdateService(Updates, Settings, Toasts, Applier, () => Exits++, Opened.Add, () => Active, () => Now);
        }

        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public async Task A_newer_version_is_announced_with_an_Update_button_and_nothing_is_installed_unasked()
    {
        using var e = new Env();
        e.Updates.Offer = FakeAppUpdates.Newer();

        await e.Service.CheckDueAsync();

        Assert.Equal(AppUpdateState.Available, e.Service.State);
        Assert.Contains("ShadeNet 1.0.2 is available (you have 1.0.1)", e.Toasts.Current!.Message);
        Assert.Equal("Update", e.Toasts.Current.ActionText);
        Assert.Empty(e.Updates.Downloaded);          // it asked; nobody said yes
        Assert.Empty(e.Applier.Started);
        Assert.Equal(0, e.Exits);
    }

    [Fact]
    public async Task Saying_yes_downloads_hands_over_and_quits_so_the_files_can_be_replaced()
    {
        using var e = new Env();
        e.Updates.Offer = FakeAppUpdates.Newer();
        await e.Service.CheckDueAsync();

        e.Toasts.Current!.Action!();                 // the Update button of the notification
        for (var i = 0; i < 100 && e.Exits == 0; i++) await Task.Delay(20);

        Assert.Equal(@"C:\data\updates\1.0.2\package.zip", Assert.Single(e.Applier.Started));
        Assert.Equal(1, e.Exits);
        Assert.Equal(AppUpdateState.Starting, e.Service.State);
    }

    [Fact]
    public async Task The_automatic_look_happens_at_most_once_a_day_and_not_when_switched_off()
    {
        using var e = new Env();
        e.Updates.Offer = null;

        await e.Service.CheckDueAsync();
        await e.Service.CheckDueAsync();
        Assert.Equal(1, e.Updates.Checks);

        e.Now = e.Now.AddHours(23);
        await e.Service.CheckDueAsync();
        Assert.Equal(1, e.Updates.Checks);

        e.Now = e.Now.AddHours(2);
        await e.Service.CheckDueAsync();
        Assert.Equal(2, e.Updates.Checks);

        e.Settings.Current.Updates.CheckForUpdates = false;
        e.Now = e.Now.AddDays(5);
        await e.Service.CheckDueAsync();
        Assert.Equal(2, e.Updates.Checks);
    }

    [Fact]
    public async Task The_same_version_is_announced_once_not_every_day()
    {
        using var e = new Env();
        e.Updates.Offer = FakeAppUpdates.Newer();
        await e.Service.CheckDueAsync();
        e.Toasts.Dismiss();

        e.Now = e.Now.AddDays(2);
        await e.Service.CheckDueAsync();

        Assert.Null(e.Toasts.Current);               // already told; the Settings page still shows it
        Assert.Equal(AppUpdateState.Available, e.Service.State);
    }

    [Fact]
    public async Task Being_offline_is_silent_for_the_automatic_look_and_a_message_for_a_click()
    {
        using var e = new Env();
        e.Updates.Offline = true;

        await e.Service.CheckDueAsync();
        Assert.Null(e.Toasts.Current);

        await e.Service.CheckNowAsync();
        Assert.Equal(AppUpdateState.Failed, e.Service.State);
        Assert.Contains("offline", e.Service.Error);
    }

    [Fact]
    public async Task Check_now_ignores_the_schedule_and_says_when_there_is_nothing_newer()
    {
        using var e = new Env();
        await e.Service.CheckDueAsync();

        await e.Service.CheckNowAsync();

        Assert.Equal(2, e.Updates.Checks);
        Assert.Equal(AppUpdateState.UpToDate, e.Service.State);
    }

    [Fact]
    public async Task A_copy_that_was_not_installed_by_the_setup_cannot_update_itself_and_points_to_the_release()
    {
        using var e = new Env();
        e.Applier.CanApply = false;                  // a build folder, a portable copy
        e.Updates.Offer = FakeAppUpdates.Newer();

        await e.Service.CheckDueAsync();
        Assert.Contains("cannot update itself", e.Toasts.Current!.Message);
        Assert.Equal("What's new", e.Toasts.Current.ActionText);
        e.Toasts.Current.Action!();
        Assert.Equal("https://github.com/o/r/releases/tag/v1.0.2", Assert.Single(e.Opened).ToString());

        await e.Service.InstallAsync();
        Assert.Equal(AppUpdateState.Failed, e.Service.State);
        Assert.Empty(e.Updates.Downloaded);
        Assert.Equal(0, e.Exits);
    }

    [Fact]
    public async Task A_download_that_fails_the_checksum_keeps_the_program_running_and_says_why()
    {
        using var e = new Env();
        e.Updates.Offer = FakeAppUpdates.Newer();
        e.Updates.FailDownload = true;
        await e.Service.CheckNowAsync();

        await e.Service.InstallAsync();

        Assert.Equal(AppUpdateState.Failed, e.Service.State);
        Assert.Contains("checksum", e.Service.Error);
        Assert.Equal(0, e.Exits);                    // the program is still there
        Assert.Empty(e.Applier.Started);
        Assert.Contains("Update failed", e.Toasts.Current!.Message);
    }

    [Fact]
    public async Task When_the_update_cannot_be_started_the_program_does_not_quit()
    {
        using var e = new Env();
        e.Updates.Offer = FakeAppUpdates.Newer();
        e.Applier.Fail = true;
        await e.Service.CheckNowAsync();

        await e.Service.InstallAsync();

        Assert.Equal(AppUpdateState.Failed, e.Service.State);
        Assert.Equal(0, e.Exits);
    }

    [Fact]
    public async Task Progress_is_reported_while_downloading_and_running_downloads_are_mentioned()
    {
        using var e = new Env();
        e.Active = 2;
        e.Updates.Offer = FakeAppUpdates.Newer();
        var seen = new List<AppUpdateState>();
        e.Service.Changed += () => seen.Add(e.Service.State);

        await e.Service.CheckDueAsync();
        Assert.Contains("Running downloads are paused", e.Toasts.Current!.Message);
        await e.Service.InstallAsync();

        Assert.Contains(AppUpdateState.Downloading, seen);
        Assert.Contains(AppUpdateState.Starting, seen);
        Assert.Equal(1, e.Service.Progress);
    }

    [Fact]
    public async Task A_late_progress_report_never_pulls_the_state_back_from_starting_to_downloading()
    {
        // Progress<T> delivers on another thread, so a report can arrive after the download has ended: whatever the timing, the
        // update that was handed over stays "starting" and ends at 100 % (found on a CI runner, where the timing differed)
        for (var i = 0; i < 300; i++)
        {
            using var e = new Env();
            e.Updates.Offer = FakeAppUpdates.Newer();
            await e.Service.CheckNowAsync();

            await e.Service.InstallAsync();
            for (var wait = 0; wait < 5; wait++) await Task.Yield();       // give any late report the chance to land

            Assert.Equal(AppUpdateState.Starting, e.Service.State);
            Assert.Equal(1, e.Service.Progress);
            Assert.Equal(1, e.Exits);
        }
    }

    // ---- the Settings part ----

    [Fact]
    public async Task The_settings_part_shows_each_state_and_offers_the_right_button()
    {
        using var e = new Env();
        var vm = new UpdatesSettingsViewModel(e.Settings, e.Service);
        Assert.Equal("Not checked yet.", vm.StatusText);
        Assert.True(vm.CanCheck);
        Assert.False(vm.CanInstall);

        await e.Service.CheckNowAsync();
        vm.Refresh();
        Assert.Equal("You have the newest version.", vm.StatusText);

        e.Updates.Offer = FakeAppUpdates.Newer();
        e.Active = 3;
        await e.Service.CheckNowAsync();
        vm.Refresh();
        Assert.Equal("Update to 1.0.2", vm.InstallText);
        Assert.True(vm.CanInstall);
        Assert.False(vm.CanOpenPage);
        Assert.Contains("3 running download(s) are paused", vm.StatusText);

        e.Applier.CanApply = false;
        vm.Refresh();
        Assert.False(vm.CanInstall);
        Assert.True(vm.CanOpenPage);
        Assert.Contains("cannot update itself", vm.StatusText);
    }

    [Fact]
    public void The_automatic_look_can_be_switched_off_and_the_choice_is_saved()
    {
        using var e = new Env();
        var vm = new UpdatesSettingsViewModel(e.Settings, e.Service);
        Assert.True(vm.CheckForUpdates);

        vm.CheckForUpdates = false;

        Assert.False(e.Settings.Current.Updates.CheckForUpdates);
        var reloaded = new SettingsService(e.Dir.Path);
        reloaded.Load();
        Assert.False(reloaded.Current.Updates.CheckForUpdates);
    }

    [Fact]
    public void The_real_applier_needs_an_installation_made_by_the_setup()
    {
        using var dir = new TempDir();
        var applier = new UpdateApplier(dir.Path);
        Assert.False(applier.CanApply);                                              // a build folder

        File.WriteAllText(Path.Combine(dir.Path, "install.json"), "{}");
        Assert.False(applier.CanApply);                                              // the record without the setup program
        File.WriteAllText(Path.Combine(dir.Path, "Uninstall.exe"), "x");
        Assert.True(applier.CanApply);
    }
}
