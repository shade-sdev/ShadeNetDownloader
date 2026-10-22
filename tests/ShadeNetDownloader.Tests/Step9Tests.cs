using System.Net;
using Microsoft.Win32;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Models;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Services.Tray;
using ShadeNetDownloader.Util;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

/// <summary>A server for small files: <c>/f/{name}</c> serves 3000 bytes with range support, anything else is a 404.</summary>
sealed class TinyServer : IDisposable
{
    public const int Size = 3000;

    readonly HttpListener _listener = new();
    readonly CancellationTokenSource _stop = new();

    public TinyServer()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    public string BaseUrl { get; }

    public string Url(string name) => $"{BaseUrl}/f/{name}";

    async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            try
            {
                if (!ctx.Request.Url!.AbsolutePath.StartsWith("/f/"))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    continue;
                }

                var data = new byte[Size];
                ContentGen.Fill(data, 0);
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.AddHeader("Accept-Ranges", "bytes");

                long from = 0, to = Size - 1;
                var range = ctx.Request.Headers["Range"];
                if (range != null && range.StartsWith("bytes="))
                {
                    var parts = range[6..].Split('-');
                    from = long.Parse(parts[0]);
                    if (parts[1].Length > 0) to = Math.Min(long.Parse(parts[1]), Size - 1);
                    ctx.Response.StatusCode = 206;
                    ctx.Response.AddHeader("Content-Range", $"bytes {from}-{to}/{Size}");
                }

                var length = (int)(to - from + 1);
                ctx.Response.ContentLength64 = length;
                if (ctx.Request.HttpMethod != "HEAD") await ctx.Response.OutputStream.WriteAsync(data.AsMemory((int)from, length));
                ctx.Response.Close();
            }
            catch
            {
                // a client that went away: ignore
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }
}

static class ContentGen
{
    public static void Fill(Span<byte> buffer, long offset)
    {
        for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)((offset + i) * 31 + 7);
    }
}

sealed class FakePower : IPowerService
{
    public List<FinishAction> Performed { get; } = new();

    public void Perform(FinishAction action)
    {
        lock (Performed) Performed.Add(action);
    }
}

sealed class FakeNotifier : ITrayNotifier
{
    public record Balloon(string Title, string Text, bool Silent, object? Tag);

    public List<Balloon> Shown { get; } = new();

    public event Action<object?>? BalloonClicked;

    public void ShowBalloon(string title, string text, bool silent, object? tag) => Shown.Add(new Balloon(title, text, silent, tag));

    public void Click(object? tag) => BalloonClicked?.Invoke(tag);
}

sealed class FakeShell : IAppShell
{
    public bool IsForeground { get; set; }
    public int BroughtToFront { get; private set; }
    public List<string> Shown { get; } = new();

    public void BringToFront() => BroughtToFront++;
    public int Exits;
    public void Exit() => Exits++;

    public void ShowInFolder(string path) => Shown.Add(path);
}

static class Wait
{
    public static async Task Until(Func<bool> condition, string what, int seconds = 15)
    {
        for (var i = 0; i < seconds * 50 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "Timed out waiting for: " + what);
    }
}

// ================================================================================================================
// the schedule
// ================================================================================================================

public class ScheduleMathTests
{
    static readonly DayOfWeek[] Everyday = Enum.GetValues<DayOfWeek>();

    // 2026-10-19 is a Monday
    static DateTime Monday(int hour, int minute = 0) => new(2026, 10, 19, hour, minute, 0);

    [Fact]
    public void The_fixture_date_is_a_monday() => Assert.Equal(DayOfWeek.Monday, Monday(0).DayOfWeek);

    [Theory]
    [InlineData(0, 59, false)]
    [InlineData(1, 0, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]    // the end is exclusive
    [InlineData(15, 0, false)]
    public void A_daytime_window_includes_its_start_and_excludes_its_end(int hour, int minute, bool expected) =>
        Assert.Equal(expected, ScheduleMath.IsInWindow(Monday(hour, minute), new TimeOnly(1, 0), new TimeOnly(7, 0), Everyday));

    [Fact]
    public void An_overnight_window_belongs_to_the_day_it_starts_on()
    {
        var start = new TimeOnly(23, 0);
        var stop = new TimeOnly(6, 0);
        var mondayOnly = new[] { DayOfWeek.Monday };

        Assert.True(ScheduleMath.IsInWindow(Monday(23, 30), start, stop, mondayOnly));
        Assert.True(ScheduleMath.IsInWindow(Monday(23, 30).AddHours(3), start, stop, mondayOnly));   // Tuesday 02:30, window began Monday
        Assert.False(ScheduleMath.IsInWindow(Monday(2), start, stop, mondayOnly));                    // Monday 02:00 began on Sunday
        Assert.False(ScheduleMath.IsInWindow(Monday(23, 30).AddDays(1), start, stop, mondayOnly));   // Tuesday 23:30: Tuesday is not selected
        Assert.False(ScheduleMath.IsInWindow(Monday(12), start, stop, mondayOnly));
    }

    [Fact]
    public void Equal_start_and_stop_mean_the_whole_selected_day()
    {
        Assert.True(ScheduleMath.IsInWindow(Monday(13), new TimeOnly(5, 0), new TimeOnly(5, 0), new[] { DayOfWeek.Monday }));
        Assert.False(ScheduleMath.IsInWindow(Monday(13).AddDays(1), new TimeOnly(5, 0), new TimeOnly(5, 0), new[] { DayOfWeek.Monday }));
    }

    [Fact]
    public void No_selected_day_means_never() =>
        Assert.False(ScheduleMath.IsInWindow(Monday(3), new TimeOnly(1, 0), new TimeOnly(7, 0), Array.Empty<DayOfWeek>()));
}

public class QueueSchedulerTests
{
    static (DownloadManager Manager, TempDir Dir) Env()
    {
        var dir = new TempDir();
        return (new DownloadManager(new EngineSettings { DownloadRoot = dir.Path, UseCategoryFolders = false }), dir);
    }

    [Fact]
    public void The_queue_follows_the_edges_of_the_window_and_a_hand_start_is_not_overruled()
    {
        var (manager, dir) = Env();
        using var d = dir;
        using var m = manager;
        var now = new DateTime(2026, 10, 19, 3, 0, 0);
        var schedule = new ScheduleSettings { Enabled = true, Start = new TimeOnly(1, 0), Stop = new TimeOnly(7, 0), Days = Enum.GetValues<DayOfWeek>().ToList() };
        var scheduler = new QueueScheduler(manager, () => schedule, () => now);

        scheduler.Evaluate();
        Assert.True(manager.QueueRunning);                 // inside the window

        now = now.AddHours(5);                             // 08:00
        scheduler.Evaluate();
        Assert.False(manager.QueueRunning);                // left it: stopped

        manager.SetQueueRunning(true);                     // the user starts it by hand...
        now = now.AddHours(1);
        scheduler.Evaluate();
        Assert.True(manager.QueueRunning);                 // ...and the next tick does not undo that

        scheduler.Reset();                                 // schedule edited: apply it again
        scheduler.Evaluate();
        Assert.False(manager.QueueRunning);

        now = now.AddHours(17);                            // 02:00 the next night
        scheduler.Evaluate();
        Assert.True(manager.QueueRunning);
    }

    [Fact]
    public void A_disabled_schedule_never_touches_the_queue()
    {
        var (manager, dir) = Env();
        using var d = dir;
        using var m = manager;
        var schedule = new ScheduleSettings { Enabled = false, Start = new TimeOnly(1, 0), Stop = new TimeOnly(2, 0) };
        var scheduler = new QueueScheduler(manager, () => schedule, () => new DateTime(2026, 10, 19, 12, 0, 0));

        scheduler.Evaluate();
        Assert.True(manager.QueueRunning);
    }
}

// ================================================================================================================
// "when finished"
// ================================================================================================================

public class FinishWatcherTests
{
    static (DownloadManager Manager, TempDir Dir) Env()
    {
        var dir = new TempDir();
        return (new DownloadManager(new EngineSettings { DownloadRoot = dir.Path, UseCategoryFolders = false }), dir);
    }

    static Func<TimeSpan, CancellationToken, Task> NoWait => (_, _) => Task.CompletedTask;

    /// <summary>A wait that never ends by itself: the countdown stays where it is until it is cancelled.</summary>
    static Func<TimeSpan, CancellationToken, Task> Frozen => (_, ct) => Task.Delay(Timeout.Infinite, ct);

    static async Task<DownloadJob> Download(DownloadManager manager, TinyServer server, string name)
    {
        var done = new TaskCompletionSource<DownloadJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.JobCompleted += j => { if (j.FileName == name) done.TrySetResult(j); };
        manager.Add(new AddDownloadRequest { Url = server.Url(name), FileName = name, Mode = AddMode.Start });
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task It_acts_after_the_countdown_once_the_chosen_download_has_finished()
    {
        var (manager, dir) = Env();
        using var d = dir;
        using var m = manager;
        using var server = new TinyServer();
        var power = new FakePower();
        var watcher = new FinishWatcher(manager, power, new ToastService(), countdownSeconds: 3, delay: NoWait);

        watcher.Action = FinishAction.Sleep;
        Assert.True(watcher.IsArmed);
        await Download(manager, server, "one.bin");

        await Wait.Until(() => power.Performed.Count == 1, "the power action");
        Assert.Equal(FinishAction.Sleep, power.Performed.Single());
        Assert.Equal(FinishAction.None, watcher.Action);   // one shot: a forgotten choice must not fire twice
    }

    [Fact]
    public async Task Choosing_it_on_an_idle_list_does_nothing_by_itself()
    {
        var (manager, dir) = Env();
        using var d = dir;
        using var m = manager;
        using var server = new TinyServer();
        var power = new FakePower();
        var watcher = new FinishWatcher(manager, power, new ToastService(), countdownSeconds: 3, delay: NoWait);

        await Download(manager, server, "before.bin");     // finished before the choice was made
        watcher.Action = FinishAction.Shutdown;
        await Task.Delay(300);

        Assert.False(watcher.IsCountingDown);
        Assert.Empty(power.Performed);
    }

    [Fact]
    public async Task Cancel_stops_the_countdown_and_the_plan()
    {
        var (manager, dir) = Env();
        using var d = dir;
        using var m = manager;
        using var server = new TinyServer();
        var power = new FakePower();
        var watcher = new FinishWatcher(manager, power, new ToastService(), countdownSeconds: 60, delay: Frozen);

        watcher.Action = FinishAction.Shutdown;
        await Download(manager, server, "two.bin");
        await Wait.Until(() => watcher.IsCountingDown, "the countdown to begin");
        Assert.Equal(60, watcher.SecondsLeft);

        watcher.Cancel();
        await Wait.Until(() => !watcher.IsCountingDown, "the countdown to stop");
        Assert.Equal(FinishAction.None, watcher.Action);
        await Task.Delay(200);
        Assert.Empty(power.Performed);
    }

    [Fact]
    public async Task A_new_download_during_the_countdown_calls_it_off()
    {
        var (manager, dir) = Env();
        using var d = dir;
        using var m = manager;
        using var server = new TinyServer();
        var power = new FakePower();
        var watcher = new FinishWatcher(manager, power, new ToastService(), countdownSeconds: 60, delay: Frozen);

        watcher.Action = FinishAction.CloseApp;
        await Download(manager, server, "three.bin");
        await Wait.Until(() => watcher.IsCountingDown, "the countdown to begin");

        manager.Add(new AddDownloadRequest { Url = server.Url("four.bin"), FileName = "four.bin", Mode = AddMode.Hold });
        await Wait.Until(() => !watcher.IsCountingDown, "the countdown to stop");
        Assert.Empty(power.Performed);
    }

    [Theory]
    [InlineData(FinishAction.None, "nothing")]
    [InlineData(FinishAction.Shutdown, "shut down")]
    [InlineData(FinishAction.CloseApp, "close ShadeNet")]
    public void Short_names_read_naturally(FinishAction action, string expected) => Assert.Equal(expected, FinishWatcher.ShortName(action));
}

// ================================================================================================================
// notifications
// ================================================================================================================

public class NotificationServiceTests
{
    sealed class Rig : IDisposable
    {
        public TempDir Dir = new();
        public DownloadManager Manager;
        public TinyServer Server = new();
        public FakeNotifier Notifier = new();
        public FakeShell Shell = new() { IsForeground = false };
        public GeneralSettings Settings = new();
        public NotificationService Service;

        public Rig()
        {
            Manager = new DownloadManager(new EngineSettings { DownloadRoot = Dir.Path, UseCategoryFolders = false });
            Service = new NotificationService(Manager, () => Settings, Notifier, Shell);
        }

        public async Task Finish(params string[] names)
        {
            var pending = names.ToHashSet();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Manager.JobCompleted += j =>
            {
                lock (pending)
                {
                    pending.Remove(j.FileName);
                    if (pending.Count == 0) done.TrySetResult();
                }
            };
            foreach (var n in names) Manager.Add(new AddDownloadRequest { Url = Server.Url(n), FileName = n, Mode = AddMode.Start });
            await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }

        public void Dispose()
        {
            Manager.Dispose();
            Server.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task One_finished_download_makes_one_balloon_that_opens_its_folder()
    {
        using var rig = new Rig();
        await rig.Finish("report.bin");
        rig.Service.Flush();

        var balloon = Assert.Single(rig.Notifier.Shown);
        Assert.Equal("Download finished", balloon.Title);
        Assert.Contains("report.bin", balloon.Text);
        Assert.False(balloon.Silent);
        var path = Assert.IsType<string>(balloon.Tag);
        Assert.EndsWith("report.bin", path);

        rig.Notifier.Click(balloon.Tag);
        Assert.Equal(new[] { path }, rig.Shell.Shown);    // shows the file; never runs it
        Assert.Equal(0, rig.Shell.BroughtToFront);

        rig.Service.Flush();
        Assert.Single(rig.Notifier.Shown);                // nothing is told twice
    }

    [Fact]
    public async Task Several_finished_downloads_are_one_summary()
    {
        using var rig = new Rig();
        await rig.Finish("a.bin", "b.bin", "c.bin", "d.bin");
        rig.Service.Flush();

        var balloon = Assert.Single(rig.Notifier.Shown);
        Assert.Equal("4 downloads finished", balloon.Title);
        Assert.Contains("and 1 more", balloon.Text);
        Assert.Null(balloon.Tag);

        rig.Notifier.Click(balloon.Tag);
        Assert.Equal(1, rig.Shell.BroughtToFront);        // no single file to show: the window opens
    }

    [Fact]
    public async Task Nothing_is_shown_while_the_window_is_in_front_unless_asked()
    {
        using var rig = new Rig();
        rig.Shell.IsForeground = true;
        await rig.Finish("x.bin");
        rig.Service.Flush();
        Assert.Empty(rig.Notifier.Shown);

        rig.Settings.NotifyOnlyInBackground = false;
        await rig.Finish("y.bin");
        rig.Service.Flush();
        Assert.Single(rig.Notifier.Shown);
    }

    [Fact]
    public async Task The_settings_switch_each_kind_and_the_sound_off()
    {
        using var rig = new Rig();
        rig.Settings.NotifyOnComplete = false;
        await rig.Finish("quiet.bin");
        rig.Service.Flush();
        Assert.Empty(rig.Notifier.Shown);

        rig.Settings.NotifyOnComplete = true;
        rig.Settings.NotificationSound = false;
        await rig.Finish("hush.bin");
        rig.Service.Flush();
        Assert.True(Assert.Single(rig.Notifier.Shown).Silent);
    }

    [Fact]
    public async Task A_failed_download_is_reported_once()
    {
        using var rig = new Rig();
        rig.Manager.Settings.MaxRetries = 0;
        var job = rig.Manager.Add(new AddDownloadRequest { Url = rig.Server.BaseUrl + "/gone/x.bin", FileName = "x.bin", Mode = AddMode.Start });
        await Wait.Until(() => job.State == DownloadState.Failed, "the download to fail");
        await Task.Delay(100);

        rig.Service.Flush();
        var balloon = Assert.Single(rig.Notifier.Shown);
        Assert.Equal("Download failed", balloon.Title);
        Assert.Contains("x.bin", balloon.Text);

        rig.Service.Flush();
        Assert.Single(rig.Notifier.Shown);

        rig.Settings.NotifyOnFailure = false;
        Assert.Single(rig.Notifier.Shown);
    }
}

// ================================================================================================================
// start with Windows
// ================================================================================================================

public class StartupRegistrationTests : IDisposable
{
    readonly string _parent = @"Software\ShadeNetTests";
    readonly string _key;

    public StartupRegistrationTests() => _key = _parent + @"\Run-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(_parent);
        if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(_parent, throwOnMissingSubKey: false);
    }

    [Fact]
    public void Enabling_writes_one_quoted_value_that_starts_minimised_and_disabling_removes_it()
    {
        var reg = new StartupRegistration(@"C:\Program Files\ShadeNet\ShadeNetDownloader.exe", _key);
        Assert.False(reg.IsEnabled);

        reg.SetEnabled(true);
        Assert.True(reg.IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(_key))
            Assert.Equal("\"C:\\Program Files\\ShadeNet\\ShadeNetDownloader.exe\" --minimized", key!.GetValue(StartupRegistration.ValueName));

        reg.SetEnabled(false);
        Assert.False(reg.IsEnabled);
        reg.SetEnabled(false);   // twice is fine
    }

    [Fact]
    public void An_entry_that_points_to_another_copy_of_the_program_does_not_count_as_enabled()
    {
        new StartupRegistration(@"C:\Old\ShadeNetDownloader.exe", _key).SetEnabled(true);
        Assert.False(new StartupRegistration(@"C:\New\ShadeNetDownloader.exe", _key).IsEnabled);
    }
}

// ================================================================================================================
// the settings pages
// ================================================================================================================

public class SettingsPageTests
{
    sealed class Rig : IDisposable
    {
        public TempDir Dir = new();
        public SettingsService Settings;
        public int Applied;
        public int CategoriesChanged;
        public int TrayChanged;
        public int ScheduleChanged;
        public string RunKey = @"Software\ShadeNetTests\Page-" + Guid.NewGuid().ToString("N");

        public Rig()
        {
            Settings = new SettingsService(Dir.Path);
            Settings.Load();
            Settings.Current.Engine.DownloadRoot = Dir.Path;
        }

        public EngineSettings Engine => Settings.Current.Engine;

        public GeneralSettingsViewModel General() => new(Settings, new StartupRegistration(@"C:\x\ShadeNetDownloader.exe", RunKey), () => TrayChanged++);
        public DownloadSettingsViewModel Downloads() => new(Settings, () => Applied++, () => CategoriesChanged++);
        public NetworkSettingsViewModel Network() => new(Settings, () => Applied++);
        public QueueSettingsViewModel Queue(FinishWatcher? finish = null) => new(Settings, () => Applied++, () => ScheduleChanged++, finish);

        /// <summary>What a restart would read back.</summary>
        public AppSettings Reloaded()
        {
            var again = new SettingsService(Dir.Path);
            again.Load();
            return again.Current;
        }

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(RunKey, throwOnMissingSubKey: false);
            using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\ShadeNetTests"))
                if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\ShadeNetTests", throwOnMissingSubKey: false);
            Dir.Dispose();
        }
    }

    // ---- general ----

    [Fact]
    public void The_tray_switch_saves_and_tells_the_app()
    {
        using var rig = new Rig();
        var vm = rig.General();
        Assert.True(vm.ShowTrayIcon);
        Assert.True(vm.CanCloseToTray);

        vm.ShowTrayIcon = false;
        Assert.Equal(1, rig.TrayChanged);
        Assert.False(vm.CanCloseToTray);                      // no icon to come back from: closing really closes
        Assert.False(rig.Reloaded().General.ShowTrayIcon);
    }

    [Fact]
    public void Start_with_windows_writes_and_removes_the_run_entry()
    {
        using var rig = new Rig();
        var vm = rig.General();
        Assert.False(vm.StartWithWindows);

        vm.StartWithWindows = true;
        Assert.True(new StartupRegistration(@"C:\x\ShadeNetDownloader.exe", rig.RunKey).IsEnabled);
        Assert.True(rig.Reloaded().General.StartWithWindows);

        vm.StartWithWindows = false;
        Assert.False(new StartupRegistration(@"C:\x\ShadeNetDownloader.exe", rig.RunKey).IsEnabled);
    }

    [Fact]
    public void Notification_switches_are_saved()
    {
        using var rig = new Rig();
        var vm = rig.General();
        vm.NotifyOnComplete = false;
        vm.NotificationSound = false;
        vm.NotifyOnlyInBackground = false;
        vm.NotifyOnFailure = false;

        var back = rig.Reloaded().General;
        Assert.False(back.NotifyOnComplete);
        Assert.False(back.NotificationSound);
        Assert.False(back.NotifyOnlyInBackground);
        Assert.False(back.NotifyOnFailure);
    }

    // ---- downloads ----

    [Fact]
    public void The_download_folder_must_be_a_full_path_and_is_applied_when_it_is()
    {
        using var rig = new Rig();
        var vm = rig.Downloads();

        vm.DownloadRootText = "downloads";
        Assert.NotEqual("", vm.DownloadRootError);
        Assert.Equal(rig.Dir.Path, rig.Engine.DownloadRoot);   // the bad value did not get in

        var good = System.IO.Path.Combine(rig.Dir.Path, "elsewhere");
        vm.DownloadRootText = good;
        Assert.Equal("", vm.DownloadRootError);
        Assert.Equal(good, rig.Engine.DownloadRoot);
        Assert.Equal(good, rig.Reloaded().Engine.DownloadRoot);
        Assert.True(rig.Applied > 0);
    }

    [Fact]
    public void Connections_are_clamped_to_the_allowed_range()
    {
        using var rig = new Rig();
        var vm = rig.Downloads();
        vm.DefaultConnections = 500;
        Assert.Equal(32, rig.Engine.DefaultConnections);
        vm.DefaultConnections = 0;
        Assert.Equal(1, rig.Engine.DefaultConnections);
    }

    [Fact]
    public void Editing_the_file_types_of_a_category_moves_files_between_categories_and_tells_the_sidebar()
    {
        using var rig = new Rig();
        var vm = rig.Downloads();
        Assert.Equal("Compressed", rig.Engine.CategoryOf("pack.zip"));

        var video = vm.Categories.Single(c => c.Name == "Video");
        video.ExtensionsText = "mp4, .ZIP  mkv";
        Assert.Equal("Video", rig.Engine.CategoryOf("pack.zip"));
        Assert.Equal("Video", rig.Engine.CategoryOf("movie.mkv"));
        Assert.Equal("mp4 zip mkv", video.ExtensionsText);     // normalised: no dots, lower case, space separated
        Assert.True(rig.CategoriesChanged > 0);
        Assert.Equal("Video", rig.Reloaded().Engine.CategoryOf("pack.zip"));

        video.ResetCommand.Execute(null);
        Assert.Equal("Compressed", rig.Engine.CategoryOf("pack.zip"));
    }

    [Fact]
    public void A_category_folder_needs_a_full_path_and_empty_means_the_default()
    {
        using var rig = new Rig();
        var vm = rig.Downloads();
        var music = vm.Categories.Single(c => c.Name == "Music");

        music.FolderText = "music";
        Assert.NotEqual("", music.Error);
        Assert.False(rig.Engine.CategoryFolders.ContainsKey("Music"));

        var folder = System.IO.Path.Combine(rig.Dir.Path, "tunes");
        music.FolderText = folder;
        Assert.Equal("", music.Error);
        Assert.Equal(folder, rig.Engine.CategoryFolders["Music"]);

        music.FolderText = "";
        Assert.False(rig.Engine.CategoryFolders.ContainsKey("Music"));
    }

    [Fact]
    public void Custom_categories_can_be_added_validated_and_removed()
    {
        using var rig = new Rig();
        var vm = rig.Downloads();
        var before = vm.Categories.Count;

        vm.NewCategoryName = "";
        vm.AddCategoryCommand.Execute(null);
        Assert.NotEqual("", vm.NewCategoryError);

        vm.NewCategoryName = "video";                         // taken (any case)
        vm.AddCategoryCommand.Execute(null);
        Assert.NotEqual("", vm.NewCategoryError);

        vm.NewCategoryName = "bad/name";
        vm.AddCategoryCommand.Execute(null);
        Assert.NotEqual("", vm.NewCategoryError);
        Assert.Equal(before, vm.Categories.Count);

        vm.NewCategoryName = "Design";
        vm.NewCategoryExtensions = ".psd .ai sketch";
        vm.AddCategoryCommand.Execute(null);
        Assert.Equal("", vm.NewCategoryError);
        Assert.Equal(before + 1, vm.Categories.Count);
        Assert.Equal("Design", rig.Engine.CategoryOf("logo.PSD"));
        Assert.Contains("Design", rig.Engine.CategoryNames);
        Assert.Equal("Design", rig.Reloaded().Engine.CategoryOf("logo.psd"));

        vm.Categories.Single(c => c.Name == "Design").RemoveCommand.Execute(null);
        Assert.Equal(before, vm.Categories.Count);
        Assert.NotEqual("Design", rig.Engine.CategoryOf("logo.psd"));
    }

    // ---- network ----

    [Fact]
    public void A_manual_proxy_needs_an_address_and_says_so()
    {
        using var rig = new Rig();
        var vm = rig.Network();
        Assert.True(vm.ProxySystem);

        vm.ProxyManual = true;
        Assert.Equal(ProxyMode.Manual, rig.Engine.Proxy);
        Assert.NotEqual("", vm.ProxyError);

        vm.ProxyAddress = "proxy.example.com:8080";
        Assert.Equal("", vm.ProxyError);
        Assert.Equal("proxy.example.com:8080", rig.Reloaded().Engine.ProxyAddress);

        vm.ProxyNone = true;
        Assert.Equal("", vm.ProxyError);
        Assert.False(vm.ProxyManual);
    }

    [Fact]
    public void Time_outs_and_retries_are_clamped_and_the_user_agent_cannot_be_blank()
    {
        using var rig = new Rig();
        var vm = rig.Network();

        vm.ConnectTimeoutSeconds = 1;
        Assert.Equal(3, vm.ConnectTimeoutSeconds);
        vm.ConnectTimeoutSeconds = 9999;
        Assert.Equal(120, vm.ConnectTimeoutSeconds);
        vm.MaxRetries = -4;
        Assert.Equal(0, vm.MaxRetries);

        var original = vm.UserAgent;
        vm.UserAgent = "   ";
        Assert.Equal(original, vm.UserAgent);
        vm.UserAgent = "Line\r\nBreak";
        Assert.Equal(original, vm.UserAgent);
        vm.UserAgent = "MyAgent/1.0";
        Assert.Equal("MyAgent/1.0", rig.Reloaded().Engine.UserAgent);
        vm.ResetUserAgentCommand.Execute(null);
        Assert.Equal(original, vm.UserAgent);
    }

    [Fact]
    public void Per_site_connection_limits_are_added_listed_and_removed()
    {
        using var rig = new Rig();
        var vm = rig.Network();

        vm.NewHost = "https://files.example.com/path";        // an address, not a site name
        vm.AddHostLimitCommand.Execute(null);
        Assert.NotEqual("", vm.HostError);
        Assert.Empty(vm.HostLimits);

        vm.NewHost = "Files.Example.com";
        vm.NewHostLimit = 3;
        vm.AddHostLimitCommand.Execute(null);
        Assert.Equal("", vm.HostError);
        var row = Assert.Single(vm.HostLimits);
        Assert.Equal("files.example.com", row.Host);
        Assert.Equal(3, rig.Reloaded().Engine.HostConnectionLimits["files.example.com"]);
        Assert.Equal("", vm.NewHost);

        vm.RemoveHostLimitCommand.Execute(row);
        Assert.Empty(vm.HostLimits);
        Assert.Empty(rig.Engine.HostConnectionLimits);
    }

    // ---- queue ----

    [Fact]
    public void The_number_of_simultaneous_downloads_is_clamped_and_applied_at_once()
    {
        using var rig = new Rig();
        var vm = rig.Queue();
        var applied = rig.Applied;

        vm.MaxConcurrentJobs = 6;
        Assert.Equal(6, rig.Engine.MaxConcurrentJobs);
        Assert.True(rig.Applied > applied);
        vm.MaxConcurrentJobs = 0;
        Assert.Equal(1, rig.Engine.MaxConcurrentJobs);
        vm.MaxConcurrentJobs = 99;
        Assert.Equal(32, rig.Engine.MaxConcurrentJobs);
        Assert.Equal(32, rig.Reloaded().Engine.MaxConcurrentJobs);
    }

    [Fact]
    public void The_schedule_accepts_times_and_days_and_describes_itself()
    {
        using var rig = new Rig();
        var vm = rig.Queue();
        Assert.Equal("The queue runs whenever it is started.", vm.ScheduleSummary);

        vm.ScheduleEnabled = true;
        Assert.True(rig.ScheduleChanged > 0);
        Assert.Contains("01:00 until 07:00", vm.ScheduleSummary);

        vm.StartText = "23:15";
        vm.StopText = "6:30";
        Assert.Equal("", vm.TimeError);
        Assert.Equal(new TimeOnly(23, 15), rig.Settings.Current.Schedule.Start);
        Assert.Equal(new TimeOnly(6, 30), rig.Settings.Current.Schedule.Stop);
        Assert.Contains("the next morning", vm.ScheduleSummary);

        vm.StopText = "25:99";
        Assert.NotEqual("", vm.TimeError);
        Assert.Equal(new TimeOnly(6, 30), rig.Settings.Current.Schedule.Stop);   // unchanged

        var sunday = vm.Days.Single(x => x.Day == DayOfWeek.Sunday);
        Assert.Equal(DayOfWeek.Monday, vm.Days[0].Day);
        sunday.IsOn = false;
        Assert.DoesNotContain(DayOfWeek.Sunday, rig.Settings.Current.Schedule.Days);
        sunday.IsOn = true;
        Assert.Contains(DayOfWeek.Sunday, rig.Settings.Current.Schedule.Days);

        var back = rig.Reloaded().Schedule;
        Assert.True(back.Enabled);
        Assert.Equal(new TimeOnly(23, 15), back.Start);
    }

    [Fact]
    public void The_finish_choice_in_the_page_and_the_status_bar_stay_in_step()
    {
        using var rig = new Rig();
        using var manager = new DownloadManager(rig.Engine);
        var watcher = new FinishWatcher(manager, new FakePower(), new ToastService(), delay: (_, _) => Task.CompletedTask);
        var vm = rig.Queue(watcher);
        Assert.Equal(FinishAction.None, vm.SelectedFinish.Action);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedFinish = vm.FinishChoices.Single(c => c.Action == FinishAction.Shutdown);
        Assert.Equal(FinishAction.Shutdown, watcher.Action);

        watcher.Action = FinishAction.Sleep;                   // chosen from the status-bar menu
        Assert.Equal(FinishAction.Sleep, vm.SelectedFinish.Action);
        Assert.Contains(nameof(QueueSettingsViewModel.SelectedFinish), changed);

        // never saved: a restart starts with nothing armed
        rig.Settings.Save();
        var saved = string.Concat(System.IO.Directory.GetFiles(rig.Dir.Path, "*.json").Select(System.IO.File.ReadAllText));
        Assert.NotEmpty(saved);
        Assert.DoesNotContain("Sleep", saved);
    }

    [Fact]
    public void Switching_sections_flips_exactly_one_flag()
    {
        using var rig = new Rig();
        var vm = new SettingsViewModel(rig.Settings, new AppThemeService(), startup: new StartupRegistration(@"C:\x.exe", rig.RunKey));
        Assert.True(vm.IsGeneral);

        foreach (var section in Enum.GetValues<SettingsSection>())
        {
            vm.Section = section;
            var flags = new[] { vm.IsGeneral, vm.IsDownloads, vm.IsCategories, vm.IsQueue, vm.IsNetwork, vm.IsBrowser, vm.IsAppearance, vm.IsAbout };
            Assert.Single(flags, f => f);
            Assert.True(flags[(int)section]);
        }

        vm.IsNetwork = true;
        Assert.Equal(SettingsSection.Network, vm.Section);
    }
}

// ================================================================================================================
// the shell: categories in the sidebar, the queue, the taskbar
// ================================================================================================================

public class ShellStep9Tests
{
    static (DownloadManager Manager, SettingsService Settings, TempDir Dir) Env()
    {
        var dir = new TempDir();
        var settings = new SettingsService(dir.Path);
        settings.Load();
        settings.Current.Engine.DownloadRoot = dir.Path;
        settings.Current.Engine.UseCategoryFolders = false;
        return (new DownloadManager(settings.Current.Engine), settings, dir);
    }

    static MainViewModel MakeMain(DownloadManager manager, SettingsService settings, out DownloadsViewModel downloads, out SettingsViewModel page)
    {
        var toasts = new ToastService();
        downloads = new DownloadsViewModel(manager, toasts);
        page = new SettingsViewModel(settings, new AppThemeService(), manager: manager);
        return new MainViewModel(manager, settings, new FakeDialogs(), toasts, downloads, page);
    }

    [Fact]
    public void The_sidebar_follows_added_and_removed_categories_and_keeps_its_counts()
    {
        var (manager, settings, dir) = Env();
        using var d = dir;
        using var m = manager;
        var main = MakeMain(manager, settings, out _, out var page);
        var videoBefore = main.Categories.Single(c => c.Category == "Video");

        page.Downloads.NewCategoryName = "Design";
        page.Downloads.NewCategoryExtensions = "psd";
        page.Downloads.AddCategoryCommand.Execute(null);
        Assert.Contains(main.Categories, c => c.Category == "Design");
        Assert.Same(videoBefore, main.Categories.Single(c => c.Category == "Video"));   // reused, not rebuilt
        Assert.Equal("General", main.Categories.Last().Category);

        main.SelectCommand.Execute(main.Categories.Single(c => c.Category == "Design"));
        page.Downloads.RemoveCustomCategory("Design");
        Assert.DoesNotContain(main.Categories, c => c.Category == "Design");
        Assert.True(main.Primary[0].IsSelected);                                         // the removed page falls back to All
    }

    [Fact]
    public void A_stopped_queue_is_shown_and_the_toggle_works()
    {
        var (manager, settings, dir) = Env();
        using var d = dir;
        using var m = manager;
        var main = MakeMain(manager, settings, out _, out _);
        Assert.True(main.QueueRunning);
        Assert.Equal("", main.QueueText);

        main.ToggleQueueCommand.Execute(null);
        main.Refresh();
        Assert.False(manager.QueueRunning);
        Assert.False(main.QueueRunning);
        Assert.Equal("Queue stopped", main.QueueText);

        main.ToggleQueueCommand.Execute(null);
        main.Refresh();
        Assert.True(main.QueueRunning);
    }

    [Fact]
    public void The_taskbar_and_tray_text_follow_the_downloads()
    {
        var (manager, settings, dir) = Env();
        using var d = dir;
        using var m = manager;
        var main = MakeMain(manager, settings, out _, out _);

        main.Refresh();
        Assert.Equal(System.Windows.Shell.TaskbarItemProgressState.None, main.TaskbarState);
        Assert.Equal("ShadeNet Downloader", main.TrayTooltip);

        manager.SetQueueRunning(false);
        manager.Add(new AddDownloadRequest { Url = "http://127.0.0.1:9/a.bin", FileName = "a.bin", Mode = AddMode.Queue });
        main.Refresh();
        Assert.Equal("ShadeNet · 1 queued", main.TrayTooltip);
        Assert.Contains("queue stopped", main.StatusText);
    }

    [Fact]
    public void Queued_downloads_move_within_the_queue_and_paused_ones_can_join_it()
    {
        var (manager, settings, dir) = Env();
        using var d = dir;
        using var m = manager;
        manager.SetQueueRunning(false);                          // so they stay queued
        foreach (var n in new[] { "one.bin", "two.bin", "three.bin" })
        {
            manager.Add(new AddDownloadRequest { Url = "http://127.0.0.1:9/" + n, FileName = n, Mode = AddMode.Queue });
            Thread.Sleep(5);
        }

        var downloads = new DownloadsViewModel(manager, new ToastService());
        downloads.SetFilter(new NavItem(NavKind.Queued, "Queued", Wpf.Ui.Controls.SymbolRegular.Clock24));
        Assert.True(downloads.IsQueueView);

        string[] Order() => downloads.View.Cast<DownloadItemViewModel>().Select(i => i.FileName).ToArray();
        Assert.Equal(new[] { "one.bin", "two.bin", "three.bin" }, Order());       // oldest first: the first in is the first out
        Assert.Equal(new[] { "one.bin", "two.bin", "three.bin" }, manager.QueuedInOrder().Select(j => j.FileName).ToArray());

        var three = downloads.View.Cast<DownloadItemViewModel>().Single(i => i.FileName == "three.bin");
        downloads.MoveInQueue(three, QueueMove.Top);
        Assert.Equal(new[] { "three.bin", "one.bin", "two.bin" }, manager.QueuedInOrder().Select(j => j.FileName).ToArray());
        downloads.MoveInQueue(three, QueueMove.Down);
        Assert.Equal(new[] { "one.bin", "three.bin", "two.bin" }, manager.QueuedInOrder().Select(j => j.FileName).ToArray());
        downloads.MoveInQueue(three, QueueMove.Bottom);
        Assert.Equal(new[] { "one.bin", "two.bin", "three.bin" }, manager.QueuedInOrder().Select(j => j.FileName).ToArray());

        // a paused download joins the end of the list of queued ones
        var held = manager.Add(new AddDownloadRequest { Url = "http://127.0.0.1:9/held.bin", FileName = "held.bin", Mode = AddMode.Hold });
        var heldVm = new DownloadsViewModel(manager, new ToastService()).View.Cast<DownloadItemViewModel>().Single(i => i.FileName == "held.bin");
        Assert.True(heldVm.CanQueue);
        Assert.False(heldVm.CanMoveInQueue);
        new DownloadsViewModel(manager, new ToastService()).EnqueueSelectionOf(heldVm);
        Assert.Equal(DownloadState.Queued, held.State);
    }
}

// ================================================================================================================
// the tray menu
// ================================================================================================================

public class TrayMenuTests
{
    [Fact]
    public void The_finish_choices_cover_every_action_once()
    {
        Assert.Equal(Enum.GetValues<FinishAction>().Length, TrayMenu.FinishChoices.Length);
        Assert.Equal(Enum.GetValues<FinishAction>().OrderBy(a => a), TrayMenu.FinishChoices.Select(c => c.Action).OrderBy(a => a));
        Assert.All(TrayMenu.FinishChoices, c => Assert.False(string.IsNullOrWhiteSpace(c.Label)));
    }
}
