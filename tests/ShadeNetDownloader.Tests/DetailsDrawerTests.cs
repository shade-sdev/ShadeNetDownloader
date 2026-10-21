using System.IO;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

public class DetailsDrawerTests
{
    sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly SettingsService Settings;
        public readonly DownloadManager Manager;
        public readonly ToastService Toasts = new();

        public Env()
        {
            Settings = new SettingsService(Dir.Path);
            Settings.Load();
            Settings.Current.Engine.DownloadRoot = Dir.Path;
            Settings.Current.Engine.UseCategoryFolders = false;
            Manager = new DownloadManager(Settings.Current.Engine);
        }

        public DownloadItemViewModel Card(string name = "big.iso", long? total = 1000, DownloadState state = DownloadState.Downloading)
        {
            var job = Manager.Add(new AddDownloadRequest { Url = "http://127.0.0.1:9/" + name, FileName = name, Mode = AddMode.Hold });
            job.TotalSize = total;
            var item = new DownloadItemViewModel(job, Snap(job, state, 0, total, 0), null!);
            return item;
        }

        public void Dispose()
        {
            Manager.Dispose();
            Dir.Dispose();
        }
    }

    static JobSnapshot Snap(DownloadJob job, DownloadState state, long downloaded, long? total, double speed,
        IReadOnlyList<SegmentInfo>? segments = null, int connections = 0, TimeSpan? eta = null,
        int splits = 0, int takeovers = 0, int retries = 0, int fallbacks = 0, int peak = 0) =>
        new(job.Id, state, job.FileName, job.SaveDirectory, downloaded, total, speed, eta, connections,
            segments ?? Array.Empty<SegmentInfo>(), true, null, JobErrorKind.None, splits, takeovers, retries, fallbacks, peak);

    static SegmentInfo Seg(long start, long end, long done, SegmentState state, int conn = -1, double speed = 0) =>
        new(start, end, done, state, conn, speed);

    // ---- the card keeps what the drawer needs ----

    [Fact]
    public void A_card_keeps_the_segments_counters_and_eta_of_the_latest_snapshot()
    {
        using var e = new Env();
        var item = e.Card();
        var segments = new[] { Seg(0, 499, 100, SegmentState.Active, 0, 2000), Seg(500, 999, 0, SegmentState.Pending) };

        item.Update(Snap(item.Job, DownloadState.Downloading, 100, 1000, 2000, segments, 2, TimeSpan.FromSeconds(30), 1, 2, 3, 4, 5));

        Assert.Same(segments, item.Segments);
        Assert.Equal((100L, 1000L), (item.DownloadedBytes, item.TotalBytes));
        Assert.Equal(TimeSpan.FromSeconds(30), item.Eta);
        Assert.Equal((2, 1, 2, 3, 4, 5), (item.ConnectionCount, item.Splits, item.Takeovers, item.Retries, item.Fallbacks, item.PeakConnections));
    }

    [Fact]
    public void Speed_is_sampled_once_a_second_and_only_while_downloading()
    {
        using var e = new Env();
        var item = e.Card(state: DownloadState.Paused);   // a paused card takes no sample when it is created
        var now = 10_000L;
        item.Clock = () => now;

        for (var i = 0; i < 10; i++)   // ten updates inside one second: one sample
        {
            item.Update(Snap(item.Job, DownloadState.Downloading, i, 1000, 5_000));
            now += 100;
        }

        Assert.Single(item.SpeedSamples);

        now += 1000;
        item.Update(Snap(item.Job, DownloadState.Downloading, 50, 1000, 7_000));
        Assert.Equal(new double[] { 5_000, 7_000 }, item.SpeedSamples);
        Assert.Equal(7_000, item.PeakSpeed);

        now += 5000;
        item.Update(Snap(item.Job, DownloadState.Paused, 50, 1000, 0));   // not downloading: nothing recorded
        Assert.Equal(2, item.SpeedSamples.Length);
    }

    [Fact]
    public void A_card_tells_its_listeners_once_per_update()
    {
        using var e = new Env();
        var item = e.Card();
        var raised = 0;
        item.Updated += () => raised++;
        item.Update(Snap(item.Job, DownloadState.Downloading, 1, 1000, 1));
        item.Update(Snap(item.Job, DownloadState.Downloading, 2, 1000, 1));
        Assert.Equal(2, raised);
    }

    // ---- the details view-model ----

    [Fact]
    public void The_overview_describes_a_running_download()
    {
        using var e = new Env();
        var item = e.Card(total: 10_000_000);
        var details = new DownloadDetailsViewModel(item, e.Manager, e.Toasts);
        var segments = new[]
        {
            Seg(0, 3_999_999, 4_000_000, SegmentState.Done),
            Seg(4_000_000, 6_999_999, 1_000_000, SegmentState.Active, 0, 3_000_000),
            Seg(7_000_000, 9_999_999, 0, SegmentState.Pending),
        };
        item.Update(Snap(item.Job, DownloadState.Downloading, 5_000_000, 10_000_000, 3_145_728, segments, 1, TimeSpan.FromSeconds(95), peak: 3));

        Assert.Equal("3.00 MB/s", details.Headline);
        Assert.Equal("1 min 35 s", details.EtaText);
        Assert.Contains("of", details.ProgressText);
        Assert.Equal("1 of 3 parts finished", details.BlocksText);
        Assert.Equal("1 now · peak 3", details.ConnectionsText);
        Assert.True(details.HasBlocks);
    }

    [Fact]
    public void The_headline_follows_the_state()
    {
        using var e = new Env();
        var item = e.Card();
        var details = new DownloadDetailsViewModel(item, e.Manager, e.Toasts);

        item.Update(Snap(item.Job, DownloadState.Paused, 10, 1000, 0));
        Assert.Equal("Paused", details.Headline);
        item.Update(Snap(item.Job, DownloadState.Probing, 0, null, 0));
        Assert.Equal("Connecting", details.Headline);
        item.Update(Snap(item.Job, DownloadState.Completed, 1000, 1000, 0));
        Assert.Equal("Finished", details.Headline);
        Assert.Equal("-", details.EtaText);
    }

    [Fact]
    public void Connections_appear_in_order_update_in_place_and_leave_when_they_stop()
    {
        using var e = new Env();
        var item = e.Card(total: 4_000);
        var details = new DownloadDetailsViewModel(item, e.Manager, e.Toasts);

        item.Update(Snap(item.Job, DownloadState.Downloading, 100, 4000, 1000, new[]
        {
            Seg(2000, 2999, 100, SegmentState.Active, 2, 500),
            Seg(0, 999, 10, SegmentState.Active, 0, 1500),
        }, 2));
        Assert.Equal(new[] { 0, 2 }, details.Connections.Select(c => c.Id).ToArray());
        var first = details.Connections[0];
        Assert.Equal("Connection 1", first.Name);

        item.Update(Snap(item.Job, DownloadState.Downloading, 500, 4000, 1000, new[]
        {
            Seg(0, 999, 500, SegmentState.Active, 0, 2500),
            Seg(2000, 2999, 100, SegmentState.Active, 2, 500),
            Seg(1000, 1999, 0, SegmentState.Active, 1, 100),
        }, 3));
        Assert.Equal(new[] { 0, 1, 2 }, details.Connections.Select(c => c.Id).ToArray());
        Assert.Same(first, details.Connections[0]);   // the row was updated, not replaced
        Assert.Equal(0.5, first.Fraction, 3);

        item.Update(Snap(item.Job, DownloadState.Downloading, 900, 4000, 1000, new[]
        {
            Seg(0, 999, 1000, SegmentState.Done),
            Seg(2000, 2999, 100, SegmentState.Active, 2, 500),
        }, 1));
        Assert.Equal(new[] { 2 }, details.Connections.Select(c => c.Id).ToArray());
        Assert.Contains("1 connection working", details.ConnectionsSummary);
    }

    [Fact]
    public void The_connections_summary_explains_what_is_going_on_in_each_state()
    {
        using var e = new Env();
        var item = e.Card();
        var details = new DownloadDetailsViewModel(item, e.Manager, e.Toasts);

        item.Update(Snap(item.Job, DownloadState.Paused, 10, 1000, 0));
        Assert.Contains("Paused", details.ConnectionsSummary);
        Assert.False(details.HasConnections);

        item.Update(Snap(item.Job, DownloadState.Completed, 1000, 1000, 0));
        Assert.Contains("finished", details.ConnectionsSummary);
    }

    [Fact]
    public void Properties_list_the_facts_and_offer_copy_for_addresses_and_paths()
    {
        using var e = new Env();
        var item = e.Card("report.pdf", total: 2_546_126_931);
        item.Job.FinalUrl = "https://cdn.example/f/report.pdf";
        item.Job.Referer = "https://example.com/page";
        item.Job.ETag = "\"abc\"";
        item.Job.ContentType = "application/pdf";
        item.Job.Probed = true;
        item.Job.AcceptsRanges = true;
        item.Job.Cookies = "k=v";
        var details = new DownloadDetailsViewModel(item, e.Manager, e.Toasts);
        var rows = details.Properties.ToDictionary(r => r.Label);

        Assert.True(rows["Address"].CanCopy);
        Assert.Equal("https://cdn.example/f/report.pdf", rows["Redirected to"].Value);
        Assert.Equal("https://example.com/page", rows["Referring page"].Value);
        Assert.Contains("2,546,126,931 bytes", rows["Size"].Value);
        Assert.Equal("Supported", rows["Pause and resume"].Value);
        Assert.Equal("application/pdf", rows["Type"].Value);
        Assert.Equal("Cookies are sent with this download", rows["Browser session"].Value);
        Assert.EndsWith("report.pdf", rows["Saved to"].Value);
        Assert.False(rows["Category"].CanCopy);
    }

    [Fact]
    public void Cookie_values_never_appear_in_the_properties()
    {
        using var e = new Env();
        var item = e.Card();
        item.Job.Cookies = "accountToken=SECRET-VALUE";
        var details = new DownloadDetailsViewModel(item, e.Manager, e.Toasts);
        Assert.DoesNotContain(details.Properties, r => r.Value.Contains("SECRET-VALUE") || r.Label.Contains("SECRET"));
    }

    [Fact]
    public void A_direct_address_has_no_redirect_row_and_no_session_row_content()
    {
        using var e = new Env();
        var item = e.Card();
        var rows = new DownloadDetailsViewModel(item, e.Manager, e.Toasts).Properties.ToDictionary(r => r.Label);
        Assert.False(rows.ContainsKey("Redirected to"));
        Assert.False(rows.ContainsKey("Referring page"));
        Assert.Equal("None", rows["Browser session"].Value);
        Assert.Equal("None", rows["Speed limit"].Value);
    }

    // ---- editing limits from the drawer ----

    [Theory]
    [InlineData("512", 512L * 1024)]
    [InlineData(" 2048 ", 2048L * 1024)]
    [InlineData("0", null)]
    [InlineData("", null)]
    public void A_per_download_speed_limit_is_set_from_kilobytes(string text, long? expected)
    {
        using var e = new Env();
        var item = e.Card();
        var details = new DownloadDetailsViewModel(item, e.Manager, e.Toasts) { LimitText = text };

        details.ApplyLimitCommand.Execute(null);

        Assert.Equal(expected, item.Job.SpeedLimit);
        Assert.Contains(expected == null ? "No limit" : "Limited to", details.LimitStatus);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("-5")]
    [InlineData("12.5")]
    [InlineData("99999999999")]
    public void A_nonsense_limit_is_refused_and_changes_nothing(string text)
    {
        using var e = new Env();
        var item = e.Card();
        e.Manager.SetJobSpeedLimit(item.Id, 1_000_000);
        var details = new DownloadDetailsViewModel(item, e.Manager, e.Toasts) { LimitText = text };

        details.ApplyLimitCommand.Execute(null);

        Assert.Equal(1_000_000, item.Job.SpeedLimit);
        Assert.Contains("Enter a number", details.LimitStatus);
    }

    [Fact]
    public void The_limit_box_starts_with_the_current_limit()
    {
        using var e = new Env();
        var item = e.Card();
        e.Manager.SetJobSpeedLimit(item.Id, 3 * 1024 * 1024);
        Assert.Equal("3072", new DownloadDetailsViewModel(item, e.Manager, e.Toasts).LimitText);
    }

    [Fact]
    public void The_connection_slider_sets_the_downloads_connection_limit_but_not_while_loading()
    {
        using var e = new Env();
        var item = e.Card();
        item.Job.MaxConnections = 6;
        var details = new DownloadDetailsViewModel(item, e.Manager, e.Toasts);
        Assert.Equal(6, details.ConnectionLimit);
        Assert.Equal(6, item.Job.MaxConnections);   // opening the drawer changed nothing

        details.ConnectionLimit = 12;
        Assert.Equal(12, item.Job.MaxConnections);
    }

    [Fact]
    public void A_finished_download_cannot_be_edited()
    {
        using var e = new Env();
        var item = e.Card();
        item.Job.MaxConnections = 8;
        var details = new DownloadDetailsViewModel(item, e.Manager, e.Toasts);
        item.Update(Snap(item.Job, DownloadState.Completed, 1000, 1000, 0));

        Assert.False(details.CanEdit);
        details.ConnectionLimit = 3;
        Assert.Equal(8, item.Job.MaxConnections);
    }

    [Fact]
    public void A_closed_drawer_stops_listening_to_its_card()
    {
        using var e = new Env();
        var item = e.Card();
        var details = new DownloadDetailsViewModel(item, e.Manager, e.Toasts);
        item.Update(Snap(item.Job, DownloadState.Downloading, 10, 1000, 2_000_000));
        var before = details.Headline;

        details.Dispose();
        item.Update(Snap(item.Job, DownloadState.Downloading, 20, 1000, 9_000_000));

        Assert.Equal(before, details.Headline);
    }
}

public class DrawerOnTheListTests
{
    sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly DownloadManager Manager;
        public readonly DownloadsViewModel Downloads;

        public Env(params string[] names)
        {
            var settings = new EngineSettings { DownloadRoot = Dir.Path, UseCategoryFolders = false };
            Manager = new DownloadManager(settings);
            foreach (var n in names)
                Manager.Add(new AddDownloadRequest { Url = "http://127.0.0.1:9/" + n, FileName = n, Mode = AddMode.Hold });
            Downloads = new DownloadsViewModel(Manager, new ToastService());
        }

        public DownloadItemViewModel Item(string name) => Downloads.Items.Single(i => i.FileName == name);

        public void Dispose()
        {
            Manager.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public void Opening_the_drawer_shows_that_download_and_marks_its_card()
    {
        using var e = new Env("a.zip", "b.zip");
        Assert.False(e.Downloads.IsDetailsOpen);

        e.Downloads.OpenDetails(e.Item("a.zip"));

        Assert.True(e.Downloads.IsDetailsOpen);
        Assert.Same(e.Item("a.zip"), e.Downloads.Details!.Item);
        Assert.True(e.Item("a.zip").IsDetailsTarget);
        Assert.False(e.Item("b.zip").IsDetailsTarget);
    }

    [Fact]
    public void The_drawer_follows_a_new_single_selection_and_keeps_its_tab()
    {
        using var e = new Env("a.zip", "b.zip");
        e.Downloads.OpenDetails(e.Item("a.zip"));
        e.Downloads.Details!.Tab = DetailsTab.Connections;

        e.Downloads.SetSelection(new[] { e.Item("b.zip") });

        Assert.Same(e.Item("b.zip"), e.Downloads.Details!.Item);
        Assert.Equal(DetailsTab.Connections, e.Downloads.Details.Tab);
        Assert.False(e.Item("a.zip").IsDetailsTarget);
        Assert.True(e.Item("b.zip").IsDetailsTarget);
    }

    [Fact]
    public void Selecting_nothing_or_several_leaves_the_drawer_alone()
    {
        using var e = new Env("a.zip", "b.zip");
        e.Downloads.OpenDetails(e.Item("a.zip"));

        e.Downloads.SetSelection(Array.Empty<DownloadItemViewModel>());
        Assert.Same(e.Item("a.zip"), e.Downloads.Details!.Item);

        e.Downloads.SetSelection(new[] { e.Item("a.zip"), e.Item("b.zip") });
        Assert.Same(e.Item("a.zip"), e.Downloads.Details!.Item);
    }

    [Fact]
    public void A_closed_drawer_is_not_reopened_by_selecting()
    {
        using var e = new Env("a.zip", "b.zip");
        e.Downloads.SetSelection(new[] { e.Item("a.zip") });
        Assert.False(e.Downloads.IsDetailsOpen);
    }

    [Fact]
    public void Close_unmarks_the_card_and_closes()
    {
        using var e = new Env("a.zip");
        e.Downloads.OpenDetails(e.Item("a.zip"));
        e.Downloads.CloseDetails();
        Assert.False(e.Downloads.IsDetailsOpen);
        Assert.False(e.Item("a.zip").IsDetailsTarget);
        Assert.Null(e.Downloads.Details);
    }

    [Fact]
    public void Removing_the_download_closes_its_drawer()
    {
        using var e = new Env("a.zip", "b.zip");
        e.Downloads.OpenDetails(e.Item("a.zip"));
        e.Downloads.RemoveSelectionOf(e.Item("a.zip"), deleteFiles: false);
        Assert.False(e.Downloads.IsDetailsOpen);
    }

    [Fact]
    public void Removing_another_download_does_not()
    {
        using var e = new Env("a.zip", "b.zip");
        e.Downloads.OpenDetails(e.Item("a.zip"));
        e.Downloads.RemoveSelectionOf(e.Item("b.zip"), deleteFiles: false);
        Assert.True(e.Downloads.IsDetailsOpen);
    }

    [Fact]
    public void Alt_Enter_toggles_the_drawer_for_the_selection()
    {
        using var e = new Env("a.zip", "b.zip");
        e.Downloads.ToggleDetails();   // nothing selected: nothing to show
        Assert.False(e.Downloads.IsDetailsOpen);

        e.Downloads.SetSelection(new[] { e.Item("b.zip") });
        e.Downloads.ToggleDetails();
        Assert.Same(e.Item("b.zip"), e.Downloads.Details!.Item);

        e.Downloads.ToggleDetails();
        Assert.False(e.Downloads.IsDetailsOpen);
    }

    [Fact]
    public void The_card_command_opens_the_drawer()
    {
        using var e = new Env("a.zip");
        e.Item("a.zip").ShowDetailsCommand.Execute(null);
        Assert.Same(e.Item("a.zip"), e.Downloads.Details!.Item);
    }
}

public class SpeedLimitFlyoutTests
{
    sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly SettingsService Settings;
        public readonly DownloadManager Manager;
        public readonly SpeedLimitViewModel Vm;

        public Env(long startLimit = 0)
        {
            Settings = new SettingsService(Dir.Path);
            Settings.Load();
            Settings.Current.Engine.DownloadRoot = Dir.Path;
            Settings.Current.Engine.GlobalSpeedLimit = startLimit;
            Manager = new DownloadManager(Settings.Current.Engine);
            Vm = new SpeedLimitViewModel(Manager, Settings);
        }

        public void Dispose()
        {
            Manager.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public void With_no_limit_the_flyout_says_so_and_marks_no_limit()
    {
        using var e = new Env();
        Assert.Equal("Limit: off", e.Vm.StatusText);
        Assert.False(e.Vm.IsActive);
        Assert.Equal(new[] { "No limit" }, e.Vm.Presets.Where(p => p.IsSelected).Select(p => p.Label).ToArray());
    }

    [Fact]
    public void A_limit_from_the_last_session_is_shown_and_its_preset_is_marked()
    {
        using var e = new Env(5 * 1024 * 1024);
        Assert.Equal("Limit: 5.00 MB/s", e.Vm.StatusText);
        Assert.True(e.Vm.IsActive);
        Assert.Equal(new[] { "5 MB/s" }, e.Vm.Presets.Where(p => p.IsSelected).Select(p => p.Label).ToArray());
    }

    [Fact]
    public void Choosing_a_preset_limits_the_engine_saves_the_choice_and_tells_the_status_bar()
    {
        using var e = new Env();
        var changed = 0;
        e.Vm.Changed += () => changed++;

        e.Vm.ChooseCommand.Execute(e.Vm.Presets.Single(p => p.Label == "2 MB/s"));

        Assert.Equal(2 * 1024 * 1024, e.Manager.GlobalLimiter.BytesPerSecond);
        Assert.Equal(2 * 1024 * 1024, e.Settings.Current.Engine.GlobalSpeedLimit);
        Assert.Contains("\"GlobalSpeedLimit\": 2097152", File.ReadAllText(e.Settings.SettingsPath));
        Assert.Equal("Limit: 2.00 MB/s", e.Vm.StatusText);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void No_limit_removes_the_limit_everywhere()
    {
        using var e = new Env(1024 * 1024);
        e.Vm.ChooseCommand.Execute(e.Vm.Presets[0]);
        Assert.Equal(0, e.Manager.GlobalLimiter.BytesPerSecond);
        Assert.Equal(0, e.Settings.Current.Engine.GlobalSpeedLimit);
        Assert.Equal("Limit: off", e.Vm.StatusText);
    }

    [Theory]
    [InlineData("3.5", true, 3.5 * 1024 * 1024)]
    [InlineData("3,5", true, 3.5 * 1024 * 1024)]
    [InlineData("800", false, 800.0 * 1024)]
    [InlineData(" 1 ", true, 1024.0 * 1024)]
    public void A_typed_speed_is_read_in_the_chosen_unit(string text, bool megabytes, double expectedBytes)
    {
        using var e = new Env();
        e.Vm.CustomInMegabytes = megabytes;
        e.Vm.CustomText = text;

        e.Vm.ApplyCustomCommand.Execute(null);

        Assert.Equal("", e.Vm.Error);
        Assert.Equal(expectedBytes, e.Manager.GlobalLimiter.BytesPerSecond, 1);
        Assert.False(e.Vm.IsOpen);   // done: the flyout closes
    }

    [Theory]
    [InlineData("", "Type a speed")]
    [InlineData("fast", "not a number")]
    [InlineData("0", "above zero")]
    [InlineData("-4", "above zero")]
    [InlineData("1e12", "above zero")]
    [InlineData("NaN", "not a number")]
    public void A_typed_speed_that_makes_no_sense_is_explained_and_changes_nothing(string text, string message)
    {
        using var e = new Env(1024 * 1024);
        e.Vm.CustomText = text;

        e.Vm.ApplyCustomCommand.Execute(null);

        Assert.Contains(message, e.Vm.Error);
        Assert.Equal(1024 * 1024, e.Manager.GlobalLimiter.BytesPerSecond);
    }

    [Fact]
    public void The_unit_buttons_are_two_sides_of_one_choice()
    {
        using var e = new Env();
        Assert.True(e.Vm.CustomInMegabytes);
        e.Vm.CustomInKilobytes = true;
        Assert.False(e.Vm.CustomInMegabytes);
        e.Vm.CustomInMegabytes = true;
        Assert.False(e.Vm.CustomInKilobytes);
    }

    [Fact]
    public void The_status_bar_text_of_the_shell_follows_the_flyout_at_once()
    {
        using var e = new Env();
        var toasts = new ToastService();
        var downloads = new DownloadsViewModel(e.Manager, toasts);
        var main = new MainViewModel(e.Manager, e.Settings, new FakeDialogs(), toasts, downloads,
            new SettingsViewModel(e.Settings, new AppThemeService()));
        Assert.Equal("Limit: off", main.LimitText);

        main.Limit.ChooseCommand.Execute(main.Limit.Presets.Single(p => p.Label == "10 MB/s"));

        Assert.Equal("Limit: 10.0 MB/s", main.LimitText);
    }
}
