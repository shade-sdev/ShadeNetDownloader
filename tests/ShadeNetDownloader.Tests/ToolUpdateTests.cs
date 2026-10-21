using System.Net.Http;
using ShadeNet.Core.Tools;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

sealed class FakeTools : IToolUpdates
{
    public Dictionary<ManagedTool, ToolUpdate?> Offers { get; } = new();
    public Dictionary<ManagedTool, ToolSource> Sources { get; } = new()
    {
        [ManagedTool.YtDlp] = ToolSource.Bundled,
        [ManagedTool.Ffmpeg] = ToolSource.Bundled,
    };
    public List<ManagedTool> Checked { get; } = new();
    public List<ManagedTool> Installed { get; } = new();
    public bool Offline { get; set; }
    public bool FailInstall { get; set; }

    public string? Find(ManagedTool tool) => Sources[tool] == ToolSource.None ? null : @"C:\tools\" + ToolManager.NameOf(tool) + ".exe";
    public ToolSource SourceOf(ManagedTool tool) => Sources[tool];
    public Task<string?> VersionAsync(ManagedTool tool, CancellationToken ct) => Task.FromResult<string?>(Sources[tool] == ToolSource.None ? null : "1.0");

    public Task<ToolUpdate?> CheckAsync(ManagedTool tool, CancellationToken ct)
    {
        Checked.Add(tool);
        if (Offline) throw new HttpRequestException("offline");
        return Task.FromResult(Offers.GetValueOrDefault(tool));
    }

    public Task<string> UpdateAsync(ToolUpdate update, CancellationToken ct)
    {
        if (FailInstall) throw new InvalidOperationException("in use");
        Installed.Add(update.Tool);
        return Task.FromResult(update.Latest);
    }

    public static ToolUpdate Offer(ManagedTool tool, string? current, string latest) => new(tool, current, latest, "https://x/a", "https://x/s", "a", null);
}

public class ToolUpdateTests
{
    static (ToolUpdateService Service, FakeTools Tools, SettingsService Settings, ToastService Toasts, TempDir Dir, Func<DateTimeOffset> Now, Action<TimeSpan> Advance) Env()
    {
        var dir = new TempDir();
        var settings = new SettingsService(dir.Path);
        settings.Load();
        var tools = new FakeTools();
        var toasts = new ToastService();
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var service = new ToolUpdateService(tools, settings, toasts, () => now);
        return (service, tools, settings, toasts, dir, () => now, _ => { });
    }

    [Fact]
    public async Task A_newer_version_is_announced_with_an_Update_button_and_nothing_is_installed_unasked()
    {
        var (service, tools, _, toasts, dir, _, _) = Env();
        using var d = dir;
        tools.Offers[ManagedTool.YtDlp] = FakeTools.Offer(ManagedTool.YtDlp, "2026.08.19", "2026.09.01");

        await service.CheckDueAsync();

        Assert.Equal("yt-dlp 2026.09.01 is available (you have 2026.08.19).", toasts.Current!.Message);
        Assert.Equal("Update", toasts.Current.ActionText);
        Assert.Empty(tools.Installed);                                     // it asked; nobody said yes
        Assert.Equal(ManagedTool.YtDlp, Assert.Single(service.Pending).Tool);
    }

    [Fact]
    public async Task Saying_yes_installs_every_offered_update_and_reports_it()
    {
        var (service, tools, _, toasts, dir, _, _) = Env();
        using var d = dir;
        tools.Offers[ManagedTool.YtDlp] = FakeTools.Offer(ManagedTool.YtDlp, "2026.08.19", "2026.09.01");
        tools.Offers[ManagedTool.Ffmpeg] = FakeTools.Offer(ManagedTool.Ffmpeg, "2026-01-01", "2026-03-01");
        await service.CheckDueAsync();
        Assert.Equal("Updates for yt-dlp and ffmpeg are available.", toasts.Current!.Message);

        await service.UpdateAllAsync();

        Assert.Equal(new[] { ManagedTool.YtDlp, ManagedTool.Ffmpeg }, tools.Installed);
        Assert.Empty(service.Pending);
        Assert.Equal("Updated yt-dlp 2026.09.01 and ffmpeg 2026-03-01.", toasts.Current!.Message);
    }

    [Fact]
    public async Task A_failed_update_says_why_and_keeps_asking_later()
    {
        var (service, tools, _, toasts, dir, _, _) = Env();
        using var d = dir;
        tools.Offers[ManagedTool.YtDlp] = FakeTools.Offer(ManagedTool.YtDlp, "1", "2");
        await service.CheckDueAsync();
        tools.FailInstall = true;

        await service.UpdateAllAsync();

        Assert.StartsWith("Update failed.", toasts.Current!.Message);
        Assert.Contains("in use", toasts.Current.Message);
        Assert.Single(service.Pending);
    }

    [Fact]
    public async Task The_automatic_check_runs_once_a_day_and_not_at_all_when_switched_off()
    {
        var (service, tools, settings, _, dir, _, _) = Env();
        using var d = dir;

        await service.CheckDueAsync();
        Assert.Equal(2, tools.Checked.Count);                              // both tools
        Assert.NotNull(settings.Current.Tools.LastCheck);

        await service.CheckDueAsync();                                     // the same day: no second look
        Assert.Equal(2, tools.Checked.Count);

        settings.Current.Tools.LastCheck = new DateTimeOffset(2026, 10, 20, 11, 0, 0, TimeSpan.Zero);   // 25 h ago
        await service.CheckDueAsync();
        Assert.Equal(4, tools.Checked.Count);

        settings.Current.Tools.CheckForUpdates = false;
        settings.Current.Tools.LastCheck = null;
        await service.CheckDueAsync();
        Assert.Equal(4, tools.Checked.Count);                              // off means off
    }

    [Fact]
    public async Task Being_offline_is_not_an_error_and_a_tool_that_is_not_installed_is_not_nagged_about()
    {
        var (service, tools, _, toasts, dir, _, _) = Env();
        using var d = dir;
        tools.Offline = true;
        await service.CheckDueAsync();
        Assert.Null(toasts.Current);
        Assert.Empty(service.Pending);

        tools.Offline = false;
        tools.Offers[ManagedTool.YtDlp] = FakeTools.Offer(ManagedTool.YtDlp, null, "2026.09.01");    // not installed
        ((SettingsService)typeof(ToolUpdateService).GetField("_settings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!).Current.Tools.LastCheck = null;
        await service.CheckDueAsync();
        Assert.Null(toasts.Current);                                       // the automatic check only speaks about what is installed
        Assert.Empty(service.Pending);

        var manual = await service.CheckNowAsync();                        // "Check" in Settings does list it
        Assert.Equal(ManagedTool.YtDlp, Assert.Single(manual).Tool);
        Assert.Empty(service.Pending);
    }

    // ---- Settings ----

    static ToolsSettingsViewModel Page(FakeTools tools, SettingsService settings, ToolUpdateService service, Action? changed = null) =>
        new(settings, tools, service, changed ?? (() => { }));

    [Fact]
    public async Task Each_program_has_a_default_location_a_choice_and_a_way_back_to_the_default()
    {
        var (service, tools, settings, _, dir, _, _) = Env();
        using var d = dir;
        var changes = 0;
        var page = Page(tools, settings, service, () => changes++);
        var yt = page.Rows.Single(r => r.Title == "yt-dlp");
        await yt.RefreshAsync();

        Assert.Equal("", yt.PathText);                                     // the default: the bundled copy
        Assert.StartsWith("Default, with the program", yt.Status);

        tools.Sources[ManagedTool.YtDlp] = ToolSource.Custom;
        yt.PathText = @"D:\mine\yt-dlp.exe";
        Assert.Equal(@"D:\mine\yt-dlp.exe", settings.Current.Engine.YtDlpPath);
        Assert.Equal(1, changes);
        await yt.RefreshAsync();
        Assert.Contains("will not update it", yt.Status);

        tools.Sources[ManagedTool.YtDlp] = ToolSource.Bundled;
        yt.ResetCommand.Execute(null);
        Assert.Equal("", settings.Current.Engine.YtDlpPath);
        Assert.Equal("", yt.PathText);

        var again = new SettingsService(dir.Path);                          // and it is remembered
        again.Load();
        Assert.Equal("", again.Current.Engine.YtDlpPath);
    }

    [Fact]
    public async Task Check_and_Update_in_Settings_show_the_offer_and_install_it()
    {
        var (service, tools, settings, _, dir, _, _) = Env();
        using var d = dir;
        var page = Page(tools, settings, service);
        var ff = page.Rows.Single(r => r.Title == "ffmpeg");

        tools.Offers[ManagedTool.Ffmpeg] = FakeTools.Offer(ManagedTool.Ffmpeg, "2026-01-01", "2026-03-01");
        await ff.CheckCommand.ExecuteAsync(null);
        Assert.True(ff.HasUpdate);
        Assert.Equal("Update available: 2026-01-01 → 2026-03-01", ff.UpdateText);
        Assert.Equal("Update", ff.ActionText);

        await ff.UpdateCommand.ExecuteAsync(null);
        Assert.Equal(new[] { ManagedTool.Ffmpeg }, tools.Installed);
        Assert.False(ff.HasUpdate);
        Assert.Equal("Updated.", ff.UpdateText);

        tools.Offers[ManagedTool.Ffmpeg] = null;
        await ff.CheckCommand.ExecuteAsync(null);
        Assert.Equal("Up to date.", ff.UpdateText);
        Assert.False(ff.HasUpdate);
    }

    [Fact]
    public async Task A_program_that_is_missing_is_offered_as_a_download_and_a_custom_one_is_left_alone()
    {
        var (service, tools, settings, _, dir, _, _) = Env();
        using var d = dir;
        var page = Page(tools, settings, service);
        var yt = page.Rows.Single(r => r.Title == "yt-dlp");

        tools.Sources[ManagedTool.YtDlp] = ToolSource.None;
        tools.Offers[ManagedTool.YtDlp] = FakeTools.Offer(ManagedTool.YtDlp, null, "2026.09.01");
        await yt.CheckCommand.ExecuteAsync(null);
        Assert.Equal("Download", yt.ActionText);
        Assert.Contains("can be downloaded", yt.UpdateText);

        tools.Sources[ManagedTool.YtDlp] = ToolSource.Custom;
        tools.Offers[ManagedTool.YtDlp] = null;
        await yt.CheckCommand.ExecuteAsync(null);
        Assert.Contains("this one is yours", yt.UpdateText);
        Assert.False(yt.HasUpdate);
    }

    [Fact]
    public void The_automatic_check_can_be_switched_off_and_the_choice_is_saved()
    {
        var (service, tools, settings, _, dir, _, _) = Env();
        using var d = dir;
        var page = Page(tools, settings, service);
        Assert.True(page.CheckForUpdates);                                  // on by default

        page.CheckForUpdates = false;
        var again = new SettingsService(dir.Path);
        again.Load();
        Assert.False(again.Current.Tools.CheckForUpdates);
    }
}
