using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Tests;

public class CategoryRuleTests
{
    [Fact]
    public void A_type_the_user_gave_to_one_built_in_is_not_taken_back_by_another_built_ins_default()
    {
        var s = new EngineSettings();
        Assert.Equal("Compressed", s.CategoryOf("a.zip"));

        s.CategoryExtensions["Video"] = new List<string> { "mp4", "zip" };   // Compressed still lists zip by default
        s.InvalidateCategories();
        Assert.Equal("Video", s.CategoryOf("a.zip"));
        Assert.Equal("Compressed", s.CategoryOf("a.rar"));
    }

    [Theory]
    [InlineData("movie.mp4", "Video")]
    [InlineData("song.MP3", "Music")]
    [InlineData("report.pdf", "Documents")]
    [InlineData("setup.exe", "Programs")]
    [InlineData("backup.7z", "Compressed")]
    [InlineData("photo.jpeg", "Images")]
    [InlineData("noextension", "General")]
    [InlineData("weird.xyz", "General")]
    public void The_built_in_rules_work_without_any_customisation(string file, string expected) =>
        Assert.Equal(expected, new EngineSettings().CategoryOf(file));

    [Fact]
    public void A_category_can_be_given_other_file_types_than_the_default()
    {
        var s = new EngineSettings();
        s.CategoryExtensions["Video"] = new() { ".MKV", "*.webm", "m4v" };
        s.InvalidateCategories();

        Assert.Equal("Video", s.CategoryOf("a.mkv"));
        Assert.Equal("Video", s.CategoryOf("a.webm"));
        Assert.Equal("General", s.CategoryOf("a.mp4"));   // no longer claimed
        Assert.Equal(new[] { "mkv", "webm", "m4v" }, s.ExtensionsOf("Video").ToArray());
    }

    [Fact]
    public void A_custom_category_takes_its_file_types_even_from_a_built_in_one()
    {
        var s = new EngineSettings();
        s.CustomCategories.Add(new CustomCategory { Name = "Books", Extensions = { "epub", "mobi", "azw3" } });
        s.InvalidateCategories();

        Assert.Equal("Books", s.CategoryOf("novel.epub"));    // epub is a Documents default; the user's category wins
        Assert.Equal("Documents", s.CategoryOf("sheet.xlsx"));
        Assert.Equal("Books", s.CategoryOf("x.AZW3"));
    }

    [Fact]
    public void Category_names_list_built_ins_then_custom_then_general()
    {
        var s = new EngineSettings();
        s.CustomCategories.Add(new CustomCategory { Name = "Books" });
        s.CustomCategories.Add(new CustomCategory { Name = "Games" });
        Assert.Equal(new[] { "Video", "Music", "Documents", "Programs", "Compressed", "Images", "Books", "Games", "General" },
            s.CategoryNames.ToArray());
    }

    [Theory]
    [InlineData("Books", true)]
    [InlineData("  Game Saves ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("video", false)]          // taken by a built-in, whatever the case
    [InlineData("General", false)]
    [InlineData("a/b", false)]
    [InlineData("a:b", false)]
    [InlineData(".hidden", false)]
    [InlineData("ends.", false)]
    [InlineData("this name is much too long to be a sensible folder name", false)]
    public void A_new_category_name_must_be_usable_as_a_folder_name(string name, bool valid) =>
        Assert.Equal(valid, new EngineSettings().IsValidNewCategoryName(name));

    [Fact]
    public void A_category_may_keep_its_own_name_when_it_is_edited()
    {
        var s = new EngineSettings();
        s.CustomCategories.Add(new CustomCategory { Name = "Books" });
        Assert.False(s.IsValidNewCategoryName("books"));
        Assert.True(s.IsValidNewCategoryName("books", except: "Books"));
    }

    [Theory]
    [InlineData("MP4", "mp4")]
    [InlineData(".mp4", "mp4")]
    [InlineData("*.mp4", "mp4")]
    [InlineData(" mp4 ", "mp4")]
    public void Extensions_are_written_however_people_write_them(string raw, string expected) =>
        Assert.Equal(new[] { expected }, EngineSettings.NormalizeExtensions(new[] { raw }).ToArray());

    [Fact]
    public void Things_that_are_not_extensions_are_dropped_and_duplicates_merged()
    {
        var result = EngineSettings.NormalizeExtensions(new[] { "zip", "ZIP", ".zip", "", "  ", "a b", "bad/ext", "waytoolongextensionname", "tar.gz" });
        Assert.Equal(new[] { "zip" }, result.ToArray());
    }

    [Fact]
    public void Normalize_clamps_limits_and_restores_case_insensitive_lookups()
    {
        var s = new EngineSettings { MaxConcurrentJobs = 0, DefaultConnections = 500 };
        s.CategoryFolders = new Dictionary<string, string> { ["Video"] = @"D:\v" };   // as JSON loading leaves it
        s.CustomCategories.Add(new CustomCategory { Name = "" });
        s.Normalize();

        Assert.Equal(1, s.MaxConcurrentJobs);
        Assert.Equal(32, s.DefaultConnections);
        Assert.Equal(@"D:\v", s.ResolveCategoryFolder("VIDEO"));
        Assert.Empty(s.CustomCategories);
    }

    [Fact]
    public async Task New_downloads_are_filed_by_the_users_rules()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s =>
        {
            s.UseCategoryFolders = true;
            s.CustomCategories.Add(new CustomCategory { Name = "Disk images", Extensions = { "bin" } });
        });
        var job = m.Add(new AddDownloadRequest { Url = h.Server.FileUrl(100, name: "game.bin"), FileName = "game.bin", Mode = AddMode.Hold });
        Assert.Equal("Disk images", job.Category);
        Assert.EndsWith("Disk images", job.SaveDirectory);
    }
}

public class ProxyTests
{
    [Fact]
    public void The_system_proxy_is_the_default()
    {
        using var handler = HttpFetcher.CreateHandler(4, new EngineSettings());
        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void None_means_a_direct_connection()
    {
        using var handler = HttpFetcher.CreateHandler(4, new EngineSettings { Proxy = ProxyMode.None });
        Assert.False(handler.UseProxy);
    }

    [Theory]
    [InlineData("proxy.local:3128", "proxy.local", 3128)]
    [InlineData("http://10.0.0.5:8080", "10.0.0.5", 8080)]
    [InlineData("  corp-proxy:80 ", "corp-proxy", 80)]
    public void A_manual_proxy_is_used(string address, string host, int port)
    {
        using var handler = HttpFetcher.CreateHandler(4, new EngineSettings { Proxy = ProxyMode.Manual, ProxyAddress = address });
        Assert.True(handler.UseProxy);
        var proxy = Assert.IsType<System.Net.WebProxy>(handler.Proxy);
        var uri = proxy.Address!;
        Assert.Equal((host, port), (uri.Host, uri.Port));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://x:21")]
    [InlineData("http://")]
    [InlineData("a b c")]
    public void An_unusable_manual_proxy_falls_back_to_the_system_setting_instead_of_breaking_downloads(string address)
    {
        using var handler = HttpFetcher.CreateHandler(4, new EngineSettings { Proxy = ProxyMode.Manual, ProxyAddress = address });
        Assert.Null(handler.Proxy);
        Assert.False(EngineSettings.IsUsableProxyAddress(address));
    }

    [Fact]
    public async Task Downloads_go_through_the_manual_proxy()
    {
        // The test server doubles as the proxy: an HTTP proxy receives the full address as the request target.
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s =>
        {
            s.Proxy = ProxyMode.Manual;
            s.ProxyAddress = $"127.0.0.1:{h.Server.Port}";
        });

        // http://files.invalid/... can only be reached through the proxy; the server answers /file/... on any host
        var url = "http://files.invalid:81/file/50000";
        var probe = await m.ProbeAsync(new RequestSpec(url));

        Assert.Equal(50000, probe.TotalSize);
        Assert.Contains(h.Server.Requests, r => r.PathAndQuery.StartsWith("/file/50000"));
    }
}

public class QueueTests
{
    static DownloadJob Queued(Harness h, DownloadManager m, string name, int kbps = 256)
    {
        var job = m.Add(new AddDownloadRequest { Url = h.Server.FileUrl(3 * 1024 * 1024, name: name, kbps: kbps), FileName = name, Mode = AddMode.Hold });
        m.Enqueue(job.Id);
        return job;
    }

    static async Task StopEverything(DownloadManager m)
    {
        m.SetQueueRunning(false);
        m.PauseAll();
        await Harness.WaitUntil(() => m.Jobs.All(j => !j.IsActive), 20, "everything to stop");
    }

    [Fact]
    public async Task A_stopped_queue_starts_nothing_until_it_is_started()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.MaxConcurrentJobs = 2);
        m.SetQueueRunning(false);

        var a = Queued(h, m, "a.bin");
        var b = Queued(h, m, "b.bin");
        await Task.Delay(600);
        Assert.Equal(DownloadState.Queued, a.State);
        Assert.Equal(DownloadState.Queued, b.State);

        m.SetQueueRunning(true);
        await Harness.WaitUntil(() => a.IsActive && b.IsActive, 15, "both to start");
        await StopEverything(m);
    }

    [Fact]
    public async Task At_most_the_allowed_number_run_together_and_a_higher_limit_takes_effect_at_once()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.MaxConcurrentJobs = 1);
        var jobs = new[] { Queued(h, m, "a.bin"), Queued(h, m, "b.bin"), Queued(h, m, "c.bin") };

        await Harness.WaitUntil(() => jobs.Count(j => j.IsActive) == 1, 15, "one to run");
        await Task.Delay(500);
        Assert.Equal(1, jobs.Count(j => j.IsActive));
        Assert.Equal(2, jobs.Count(j => j.State == DownloadState.Queued));

        m.Settings.MaxConcurrentJobs = 3;
        m.ApplySettings();
        await Harness.WaitUntil(() => jobs.Count(j => j.IsActive) == 3, 15, "all three to run");
        await StopEverything(m);
    }

    [Fact]
    public async Task Moving_a_download_in_the_queue_changes_the_order_it_starts_in()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.MaxConcurrentJobs = 1);
        m.SetQueueRunning(false);
        var a = Queued(h, m, "a.bin");
        var b = Queued(h, m, "b.bin");
        var c = Queued(h, m, "c.bin");
        var d = Queued(h, m, "d.bin");
        Assert.Equal(new[] { "a.bin", "b.bin", "c.bin", "d.bin" }, m.QueuedInOrder().Select(j => j.FileName).ToArray());

        Assert.True(m.MoveInQueue(d.Id, QueueMove.Top));
        Assert.Equal(new[] { "d.bin", "a.bin", "b.bin", "c.bin" }, m.QueuedInOrder().Select(j => j.FileName).ToArray());
        Assert.True(m.MoveInQueue(a.Id, QueueMove.Down));
        Assert.Equal(new[] { "d.bin", "b.bin", "a.bin", "c.bin" }, m.QueuedInOrder().Select(j => j.FileName).ToArray());
        Assert.True(m.MoveInQueue(c.Id, QueueMove.Up));
        Assert.Equal(new[] { "d.bin", "b.bin", "c.bin", "a.bin" }, m.QueuedInOrder().Select(j => j.FileName).ToArray());
        Assert.True(m.MoveInQueue(d.Id, QueueMove.Bottom));
        Assert.Equal(new[] { "b.bin", "c.bin", "a.bin", "d.bin" }, m.QueuedInOrder().Select(j => j.FileName).ToArray());

        var started = new List<Guid>();
        m.JobUpdated += s => { lock (started) if (s.State == DownloadState.Probing && !started.Contains(s.Id)) started.Add(s.Id); };
        m.SetQueueRunning(true);
        await Harness.WaitUntil(() => { lock (started) return started.Count >= 1; }, 15, "the first to start");
        lock (started) Assert.Equal(b.Id, started[0]);
        await StopEverything(m);
    }

    [Fact]
    public async Task Moving_to_where_it_already_is_changes_nothing_and_only_queued_downloads_move()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        m.SetQueueRunning(false);
        var a = Queued(h, m, "a.bin");
        var b = Queued(h, m, "b.bin");
        var changed = 0;
        m.QueueOrderChanged += () => changed++;

        Assert.False(m.MoveInQueue(a.Id, QueueMove.Top));     // already first
        Assert.False(m.MoveInQueue(b.Id, QueueMove.Bottom));  // already last
        Assert.False(m.MoveInQueue(b.Id, QueueMove.Down));
        Assert.Equal(0, changed);

        var paused = m.Add(new AddDownloadRequest { Url = h.Server.FileUrl(100), FileName = "p.bin", Mode = AddMode.Hold });
        Assert.False(m.MoveInQueue(paused.Id, QueueMove.Top));   // not queued
        Assert.False(m.MoveInQueue(Guid.NewGuid(), QueueMove.Top));

        Assert.True(m.MoveInQueue(b.Id, QueueMove.Top));
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task The_queue_order_survives_a_restart()
    {
        await using var h = await Harness.CreateAsync();
        var db = Path.Combine(h.Dir(), "jobs.db");
        var dir = h.Dir();

        var m1 = h.NewManager(dir, db: db);
        m1.SetQueueRunning(false);
        var a = Queued(h, m1, "a.bin");
        var b = Queued(h, m1, "b.bin");
        var c = Queued(h, m1, "c.bin");
        m1.MoveInQueue(c.Id, QueueMove.Top);
        await m1.StopAllAsync();
        m1.Dispose();

        var m2 = h.NewManager(dir, db: db);
        m2.SetQueueRunning(false);
        m2.Load();
        var order = m2.Jobs.Where(j => j.State == DownloadState.Queued || j.State == DownloadState.Paused)
            .OrderByDescending(j => j.Priority).ThenBy(j => j.CreatedAt).Select(j => j.FileName).ToArray();
        Assert.Equal("c.bin", order[0]);
    }

    [Fact]
    public async Task Stopping_the_queue_pauses_what_the_queue_started_but_not_what_the_user_started()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.MaxConcurrentJobs = 1);
        var fromQueue = Queued(h, m, "q.bin");
        await Harness.WaitUntil(() => fromQueue.IsActive, 15, "the queued one to start");

        var byHand = m.Add(new AddDownloadRequest { Url = h.Server.FileUrl(3 * 1024 * 1024, name: "h.bin", kbps: 256), FileName = "h.bin", Mode = AddMode.Start });
        await Harness.WaitUntil(() => byHand.IsActive, 15, "the manual one to start");

        m.SetQueueRunning(false, pauseQueueDownloads: true);

        await Harness.WaitUntil(() => fromQueue.State == DownloadState.Paused, 15, "the queue's download to pause");
        await Task.Delay(500);
        Assert.True(byHand.IsActive, "a download the user started stays running");
        await StopEverything(m);
    }

    [Fact]
    public async Task The_queue_state_is_reported_once_per_change()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var changes = 0;
        m.QueueStateChanged += () => changes++;
        Assert.True(m.QueueRunning);

        m.SetQueueRunning(true);    // already running
        m.SetQueueRunning(false);
        m.SetQueueRunning(false);   // already stopped
        m.SetQueueRunning(true);

        Assert.Equal(2, changes);
    }
}
