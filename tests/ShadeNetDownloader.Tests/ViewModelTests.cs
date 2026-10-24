using ShadeNet.Core;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Models;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Util;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

sealed class FakeDialogs : IDialogService
{
    public List<IReadOnlyList<string>?> Opened { get; } = new();

    public List<ShadeNetDownloader.Services.Browser.CaptureInfo> Captures { get; } = new();

    public Task ShowCaptureAsync(ShadeNetDownloader.Services.Browser.CaptureInfo capture)
    {
        Captures.Add(capture);
        return Task.CompletedTask;
    }

    public Task ShowAddDownloadAsync(IReadOnlyList<string>? prefill = null)
    {
        Opened.Add(prefill);
        return Task.CompletedTask;
    }
}

public class ViewModelTests
{
    static NavItem Item(NavKind kind, string title = "x", string? category = null) =>
        new(kind, title, Wpf.Ui.Controls.SymbolRegular.Home24, category);

    static (DownloadManager Manager, SettingsService Settings, TempDir Dir) Env()
    {
        var dir = new TempDir();
        var settings = new SettingsService(dir.Path);
        settings.Load();
        settings.Current.Engine.DownloadRoot = dir.Path;
        settings.Current.Engine.UseCategoryFolders = false;
        return (new DownloadManager(settings.Current.Engine), settings, dir);
    }

    static void AddHeld(DownloadManager m, params string[] names)
    {
        foreach (var name in names)
            m.Add(new AddDownloadRequest { Url = "http://127.0.0.1:9/" + name, FileName = name, Mode = AddMode.Hold });
    }

    static JobSnapshot Snapshot(DownloadJob job, DownloadState state, long downloaded, long? total, double speed,
        string? error = null, JobErrorKind kind = JobErrorKind.None, TimeSpan? eta = null, int connections = 1) =>
        new(job.Id, state, job.FileName, job.SaveDirectory, downloaded, total, speed, eta, connections,
            Array.Empty<SegmentInfo>(), true, error, kind);

    [Fact]
    public void Empty_state_text_depends_on_the_filter()
    {
        var (manager, _, dir) = Env();
        using var d = dir;
        using var m = manager;
        var vm = new DownloadsViewModel(manager, new ToastService());

        vm.SetFilter(Item(NavKind.All, "All downloads"));
        Assert.True(vm.IsEmpty);
        Assert.Equal("All downloads", vm.Title);
        Assert.Contains("Paste a link", vm.EmptyHint);

        vm.SetFilter(Item(NavKind.Failed, "Failed"));
        Assert.Contains("failed", vm.EmptyTitle, StringComparison.OrdinalIgnoreCase);

        vm.SetFilter(Item(NavKind.Category, "Video", "Video"));
        Assert.Equal("Nothing in Video", vm.EmptyTitle);
    }

    [Fact]
    public void The_list_filters_by_status_category_and_search()
    {
        var (manager, _, dir) = Env();
        using var d = dir;
        using var m = manager;
        AddHeld(manager, "holiday.mp4", "lecture.mkv", "report.pdf", "Holiday-photos.zip");
        var vm = new DownloadsViewModel(manager, new ToastService());

        Assert.Equal(4, vm.View.Count);
        Assert.Equal("4 downloads", vm.Subtitle);
        Assert.False(vm.IsEmpty);

        vm.SetFilter(Item(NavKind.Category, "Video", "Video"));
        Assert.Equal(2, vm.View.Count);

        vm.SetFilter(Item(NavKind.Paused, "Paused"));        // everything held is paused
        Assert.Equal(4, vm.View.Count);
        vm.SetFilter(Item(NavKind.Completed, "Completed"));  // nothing finished
        Assert.Equal(0, vm.View.Count);
        Assert.True(vm.IsEmpty);

        vm.SetFilter(Item(NavKind.All, "All downloads"));
        vm.SearchText = "holiday";                           // case-insensitive, matches two names
        Assert.Equal(2, vm.View.Count);
        vm.SearchText = "holiday zip";                       // every word must match
        Assert.Equal(1, vm.View.Count);
        vm.SearchText = "127.0.0.1";                         // matches the host too
        Assert.Equal(4, vm.View.Count);
        vm.SearchText = "nothing-matches";
        Assert.Equal(0, vm.View.Count);
        Assert.True(vm.IsEmpty);
        Assert.Contains("No results", vm.EmptyTitle);

        vm.SearchText = "";
        Assert.Equal(4, vm.View.Count);
    }

    [Fact]
    public void Newest_downloads_are_listed_first()
    {
        var (manager, _, dir) = Env();
        using var d = dir;
        using var m = manager;
        AddHeld(manager, "first.bin");
        Thread.Sleep(5);
        AddHeld(manager, "second.bin");
        var vm = new DownloadsViewModel(manager, new ToastService());

        Assert.Equal("second.bin", ((DownloadItemViewModel)vm.View.GetItemAt(0)).FileName);
    }

    [Fact]
    public void Removing_hides_at_once_and_undo_brings_it_back()
    {
        var (manager, _, dir) = Env();
        using var d = dir;
        using var m = manager;
        AddHeld(manager, "a.bin", "b.bin");
        var toasts = new ToastService();
        var vm = new DownloadsViewModel(manager, toasts);
        var target = vm.Items.Single(i => i.FileName == "a.bin");

        vm.RemoveSelectionOf(target, deleteFiles: false);
        Assert.Equal(1, vm.View.Count);
        Assert.Equal(2, manager.Jobs.Count);                 // the engine still has it during the undo window
        Assert.NotNull(toasts.Current);
        Assert.Equal("Undo", toasts.Current!.ActionText);

        toasts.Current.Action!.Invoke();                     // press Undo
        Assert.Equal(2, vm.View.Count);
        Assert.False(target.IsPendingRemoval);
    }

    [Fact]
    public void Actions_apply_to_the_whole_selection_when_the_clicked_card_is_part_of_it()
    {
        var (manager, _, dir) = Env();
        using var d = dir;
        using var m = manager;
        AddHeld(manager, "a.bin", "b.bin", "c.bin");
        var toasts = new ToastService();
        var vm = new DownloadsViewModel(manager, toasts);
        var a = vm.Items.Single(i => i.FileName == "a.bin");
        var b = vm.Items.Single(i => i.FileName == "b.bin");

        vm.SetSelection(new[] { a, b });
        vm.RemoveSelectionOf(a, deleteFiles: false);         // a is selected -> removes a and b
        Assert.Equal(1, vm.View.Count);

        toasts.Current!.Action!.Invoke();                    // undo
        vm.SetSelection(new[] { a });
        vm.RemoveSelectionOf(b, deleteFiles: false);         // b is NOT selected -> only b
        Assert.Equal(2, vm.View.Count);
        Assert.True(b.IsPendingRemoval);
        Assert.False(a.IsPendingRemoval);
    }

    [Theory]
    [InlineData(DownloadState.Downloading, "Pause")]
    [InlineData(DownloadState.Paused, "Resume")]
    [InlineData(DownloadState.Failed, "Try again")]
    [InlineData(DownloadState.Completed, "Open file")]
    public void The_primary_button_matches_the_state(DownloadState state, string tooltip)
    {
        var (manager, _, dir) = Env();
        using var d = dir;
        using var m = manager;
        AddHeld(manager, "x.bin");
        var vm = new DownloadsViewModel(manager, new ToastService());
        var item = vm.Items.Single();

        item.Update(Snapshot(item.Job, state, downloaded: 50, total: 100, speed: 10));
        Assert.Equal(tooltip, item.PrimaryTooltip);
        Assert.True(item.HasPrimaryAction);
    }

    [Fact]
    public void A_card_describes_what_is_happening()
    {
        var (manager, _, dir) = Env();
        using var d = dir;
        using var m = manager;
        AddHeld(manager, "movie.mp4");
        var vm = new DownloadsViewModel(manager, new ToastService());
        var item = vm.Items.Single();

        item.Update(Snapshot(item.Job, DownloadState.Downloading, 50 * 1024 * 1024, 100 * 1024 * 1024, 5 * 1024 * 1024,
            eta: TimeSpan.FromSeconds(10), connections: 4));
        Assert.Equal("Downloading", item.StatusText);
        Assert.Equal(0.5, item.Fraction, 3);
        Assert.Equal("50%", item.PercentText);
        Assert.Contains("50.0 MB of 100 MB", item.DetailText);
        Assert.Contains("5.00 MB/s", item.DetailText);
        Assert.Contains("10 s left", item.DetailText);
        Assert.Contains("4 connections", item.DetailText);

        item.Update(Snapshot(item.Job, DownloadState.Downloading, 1000, null, 100));
        Assert.True(item.IsIndeterminate);                   // unknown size -> sweeping bar, no percentage
        Assert.Equal("", item.PercentText);

        item.Update(Snapshot(item.Job, DownloadState.Failed, 10, 100, 0, "The server answered 404 Not Found.", JobErrorKind.AddressExpired));
        Assert.Equal("Failed", item.StatusText);
        Assert.Contains("404", item.DetailText);
        Assert.Contains("expired", item.HintText);

        item.Update(Snapshot(item.Job, DownloadState.Completed, 100, 100, 0));
        Assert.True(item.IsCompleted);
        Assert.Equal(1.0, item.Fraction);
        Assert.Equal("", item.HintText);
    }

    [Fact]
    public void The_list_sorts_by_name_file_type_size_and_date_and_remembers_it()
    {
        var (manager, settings, dir) = Env();
        using var d = dir;
        using var m = manager;
        AddHeld(manager, "a1.pdf", "b10.zip", "B2.mp4");
        var vm = new DownloadsViewModel(manager, new ToastService(), settings: settings);
        string[] Order()
        {
            vm.View.Refresh();
            return vm.View.Cast<DownloadItemViewModel>().Select(i => i.FileName).ToArray();
        }

        Assert.Equal(new[] { "B2.mp4", "b10.zip", "a1.pdf" }, Order());   // date added, newest first (as before)
        Assert.Equal("Newest first", vm.DescendingLabel);

        vm.SortByKeyCommand.Execute(DownloadSortKey.Name);
        Assert.Equal(new[] { "a1.pdf", "B2.mp4", "b10.zip" }, Order());   // A to Z, 2 before 10, case ignored
        Assert.Equal("Name", vm.SortLabel);
        Assert.True(vm.SortAscending);

        vm.SortByKeyCommand.Execute(DownloadSortKey.FileType);
        Assert.Equal(new[] { "B2.mp4", "a1.pdf", "b10.zip" }, Order());

        foreach (var item in vm.Items) item.TotalBytes = item.FileName switch { "a1.pdf" => 300, "b10.zip" => 100, _ => null };
        vm.SortByKeyCommand.Execute(DownloadSortKey.Size);
        Assert.Equal(new[] { "a1.pdf", "b10.zip", "B2.mp4" }, Order());   // largest first, an unknown size last
        vm.SetAscendingCommand.Execute(null);
        Assert.Equal(new[] { "B2.mp4", "b10.zip", "a1.pdf" }, Order());
        Assert.Equal("Smallest first", vm.AscendingLabel);

        var reloaded = new SettingsService(settings.DataDir);
        reloaded.Load();
        Assert.Equal(DownloadSortKey.Size, reloaded.Current.Sort.By);
        Assert.False(reloaded.Current.Sort.Descending);
    }

    [Theory]
    [InlineData("part 2", "part 10", -1)]
    [InlineData("Part 2", "part 2", 0)]
    [InlineData("file007", "file7", 0)]
    [InlineData("a", "b", -1)]
    [InlineData("ab", "a", 1)]
    [InlineData("v1.9", "v1.10", -1)]
    public void Names_are_compared_with_their_numbers_as_numbers(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(DownloadSortComparer.NaturalCompare(a, b)));

    [Fact]
    public void The_sidebar_shows_icons_only_when_asked_and_remembers_it()
    {
        var (manager, settings, dir) = Env();
        using var d = dir;
        using var m = manager;
        var toasts = new ToastService();
        MainViewModel Main() => new(manager, settings, new FakeDialogs(), toasts, new DownloadsViewModel(manager, toasts),
            new SettingsViewModel(settings, new AppThemeService()));

        var main = Main();
        Assert.True(main.IsSidebarExpanded);
        Assert.Equal(MainViewModel.SidebarExpandedWidth, main.SidebarWidth.Value);

        main.ToggleSidebarCommand.Execute(null);
        Assert.False(main.IsSidebarExpanded);
        Assert.Equal(MainViewModel.SidebarCollapsedWidth, main.SidebarWidth.Value);
        Assert.Equal(MainViewModel.SidebarCollapsedWidth, main.DropMargin.Left);
        Assert.True(settings.Current.SidebarCollapsed);

        var reloaded = new SettingsService(settings.DataDir);
        reloaded.Load();
        Assert.True(reloaded.Current.SidebarCollapsed);
        Assert.False(Main().IsSidebarExpanded);   // the next start shows it as it was left
    }

    [Fact]
    public void Sidebar_counts_ignore_downloads_waiting_out_their_undo_window()
    {
        var (manager, settings, dir) = Env();
        using var d = dir;
        using var m = manager;
        AddHeld(manager, "a.mp4", "b.mp4", "c.pdf");
        var toasts = new ToastService();
        var downloads = new DownloadsViewModel(manager, toasts);
        var main = new MainViewModel(manager, settings, new FakeDialogs(), toasts, downloads,
            new SettingsViewModel(settings, new AppThemeService()));
        Assert.Equal(3, main.Primary[0].Count);
        Assert.Equal(2, main.Categories.Single(c => c.Category == "Video").Count);

        // Remove one video: the badges drop immediately, without waiting for the next refresh tick.
        var video = downloads.Items.First(i => i.FileName.EndsWith(".mp4"));
        downloads.RemoveSelectionOf(video, deleteFiles: false);
        Assert.Equal(2, main.Primary[0].Count);
        Assert.Equal(2, main.Primary[3].Count);            // paused
        Assert.Equal(1, main.Categories.Single(c => c.Category == "Video").Count);
        Assert.Contains("0 completed", main.StatusText);
        Assert.Equal(3, manager.Jobs.Count);               // the engine still holds it until the window ends

        // Undo: they come back at once.
        toasts.Current!.Action!.Invoke();
        Assert.Equal(3, main.Primary[0].Count);
        Assert.Equal(2, main.Categories.Single(c => c.Category == "Video").Count);
    }

    [Fact]
    public void Open_with_only_acts_on_finished_files_that_still_exist()
    {
        var (manager, _, dir) = Env();
        using var d = dir;
        using var m = manager;
        AddHeld(manager, "x.bin");
        var toasts = new ToastService();
        var vm = new DownloadsViewModel(manager, toasts);
        var item = vm.Items.Single();

        // Not finished: nothing happens, no error.
        vm.OpenWith(item);
        Assert.Null(toasts.Current);

        // Finished but the file is gone: a clear message instead of a crash.
        item.Update(Snapshot(item.Job, DownloadState.Completed, 100, 100, 0));
        item.Job.State = DownloadState.Completed;
        vm.OpenWith(item);
        Assert.NotNull(toasts.Current);
        Assert.Contains("no longer", toasts.Current!.Message);
        Assert.Equal(ToastKind.Error, toasts.Current.Kind);
    }

    [Fact]
    public void Five_hundred_downloads_with_live_updates_and_filtering_stay_fast()
    {
        var (manager, _, dir) = Env();
        using var d = dir;
        using var m = manager;
        for (var i = 0; i < 500; i++)
            AddHeld(manager, $"file-{i:000}." + (i % 3 == 0 ? "mp4" : i % 3 == 1 ? "zip" : "pdf"));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var vm = new DownloadsViewModel(manager, new ToastService());
        var build = sw.ElapsedMilliseconds;
        Assert.Equal(500, vm.View.Count);

        // 20 rounds of "every card receives a progress update"
        sw.Restart();
        for (var round = 0; round < 20; round++)
            foreach (var item in vm.Items)
                item.Update(Snapshot(item.Job, DownloadState.Downloading, round * 1000, 100_000, 50_000, eta: TimeSpan.FromSeconds(5)));
        var updates = sw.ElapsedMilliseconds;

        sw.Restart();
        vm.SetFilter(Item(NavKind.Category, "Video", "Video"));
        vm.SearchText = "file-1";
        vm.SearchText = "";
        vm.SetFilter(Item(NavKind.All, "All"));
        var filtering = sw.ElapsedMilliseconds;

        Assert.True(build < 1500, $"building 500 cards took {build} ms");
        Assert.True(updates < 1500, $"10 000 card updates took {updates} ms");
        Assert.True(filtering < 500, $"filter/search changes took {filtering} ms");
    }

    [Fact]
    public async Task Shell_selection_is_exclusive_and_counts_follow_the_engine()
    {
        var (manager, settings, dir) = Env();
        using var d = dir;
        using var m = manager;
        var toasts = new ToastService();
        var downloads = new DownloadsViewModel(manager, toasts);
        var main = new MainViewModel(manager, settings, new FakeDialogs(), toasts, downloads,
            new SettingsViewModel(settings, new AppThemeService()));

        Assert.True(main.Primary[0].IsSelected);
        Assert.Same(main.Downloads, main.CurrentPage);

        AddHeld(manager, "a.mp4", "b.mkv", "c.pdf");
        main.Refresh();

        Assert.Equal(3, main.Primary[0].Count);
        Assert.Equal(3, main.Primary[3].Count);
        Assert.Equal(0, main.Primary[1].Count);
        Assert.Equal(2, main.Categories.Single(c => c.Category == "Video").Count);
        Assert.Equal(1, main.Categories.Single(c => c.Category == "Documents").Count);

        main.SelectCommand.Execute(main.Categories.Single(c => c.Category == "Video"));
        Assert.False(main.Primary[0].IsSelected);
        Assert.Equal(1, main.Categories.Count(c => c.IsSelected) + main.Primary.Count(p => p.IsSelected));
        Assert.Equal("Video", main.Downloads.Title);

        main.SelectCommand.Execute(main.SettingsItem);
        Assert.Same(main.SettingsPage, main.CurrentPage);
        Assert.DoesNotContain(main.Categories, c => c.IsSelected);

        main.SelectCommand.Execute(main.Primary[0]);
        Assert.Same(main.Downloads, main.CurrentPage);

        await manager.StopAllAsync();
    }

    [Fact]
    public async Task Typing_in_the_title_bar_search_filters_the_list_and_add_opens_the_dialog()
    {
        var (manager, settings, dir) = Env();
        using var d = dir;
        using var m = manager;
        AddHeld(manager, "alpha.bin", "beta.bin");
        var dialogs = new FakeDialogs();
        var toasts = new ToastService();
        var downloads = new DownloadsViewModel(manager, toasts);
        var main = new MainViewModel(manager, settings, dialogs, toasts, downloads,
            new SettingsViewModel(settings, new AppThemeService()));

        main.SearchText = "alp";
        Assert.Equal(1, downloads.View.Count);

        await main.AddDownloadCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Opened);
    }

    [Fact]
    public void The_sidebar_lists_every_status_and_every_category()
    {
        var (manager, settings, dir) = Env();
        using var d = dir;
        using var m = manager;
        var toasts = new ToastService();
        var main = new MainViewModel(manager, settings, new FakeDialogs(), toasts, new DownloadsViewModel(manager, toasts),
            new SettingsViewModel(settings, new AppThemeService()));

        Assert.Equal(6, main.Primary.Count);
        Assert.Equal(ShadeNet.Core.Util.CategoryResolver.Categories.Count, main.Categories.Count);
        Assert.All(main.Primary.Concat(main.Categories), n => Assert.False(string.IsNullOrWhiteSpace(n.Title)));
    }
}

public class UrlParserTests
{
    [Fact]
    public void Finds_every_link_in_messy_text_without_duplicates_or_trailing_punctuation()
    {
        var links = UrlParser.Extract(
            "Get it at https://example.com/a.zip, or (https://example.com/b.iso).\n" +
            "Mirror: HTTP://MIRROR.example/c.bin!  Again https://example.com/a.zip\n" +
            "<a href=\"https://example.com/d.pdf\">doc</a> ftp://nope.example/x  javascript:alert(1)");

        Assert.Equal(new[]
        {
            "https://example.com/a.zip", "https://example.com/b.iso", "HTTP://MIRROR.example/c.bin", "https://example.com/d.pdf",
        }, links);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no links here")]
    public void Nothing_to_find(string? text) => Assert.Empty(UrlParser.Extract(text));

    [Theory]
    [InlineData("https://example.com/file.zip", true)]
    [InlineData("  http://example.com/x  ", true)]
    [InlineData("https://example.com/a https://example.com/b", false)]
    [InlineData("ftp://example.com/x", false)]
    [InlineData("example.com/x", false)]
    [InlineData("", false)]
    public void Single_url_detection(string text, bool expected) => Assert.Equal(expected, UrlParser.IsSingleUrl(text));
}

public class ToastServiceTests
{
    [Fact]
    public void A_new_toast_replaces_the_old_one_and_dismiss_clears_it()
    {
        var toasts = new ToastService();
        toasts.Show("one");
        Assert.Equal("one", toasts.Current!.Message);
        toasts.Show("two", ToastKind.Success, "Open", () => { });
        Assert.Equal("two", toasts.Current!.Message);
        Assert.True(toasts.Current.HasAction);
        toasts.Dismiss();
        Assert.Null(toasts.Current);
    }

    [Fact]
    public void Pressing_the_action_runs_it_once_and_closes_the_toast()
    {
        var toasts = new ToastService();
        var runs = 0;
        toasts.Show("removed", ToastKind.Info, "Undo", () => runs++);
        toasts.RunActionCommand.Execute(null);
        toasts.RunActionCommand.Execute(null);
        Assert.Equal(1, runs);
        Assert.Null(toasts.Current);
    }
}
