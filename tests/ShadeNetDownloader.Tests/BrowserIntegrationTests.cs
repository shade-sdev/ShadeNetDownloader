using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using ShadeNet.Bridge;
using ShadeNet.Core;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Services.Browser;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

sealed class FakeCaptureUi : ICaptureUi
{
    public List<CaptureInfo> Shown { get; } = new();
    public List<string> Notices { get; } = new();
    public int BroughtToFront;

    public void ShowCapture(CaptureInfo capture) => Shown.Add(capture);
    public void Notify(string message, ToastKind kind) => Notices.Add(message);
    public void BringToFront() => BroughtToFront++;
}

public class CaptureProcessorTests
{
    sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly DownloadManager Manager;
        public readonly FakeCaptureUi Ui = new();
        public readonly BrowserSettings Settings = new();
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public readonly CaptureProcessor Processor;

        public Env()
        {
            Manager = new DownloadManager(new ShadeNet.Core.Engine.EngineSettings { DownloadRoot = Dir.Path, UseCategoryFolders = false });
            Processor = new CaptureProcessor(Manager, () => Settings, Ui, () => Now);
        }

        public DownloadJob Held(string url, string name = "a.zip", long? size = null)
        {
            var job = Manager.Add(new AddDownloadRequest { Url = url, FileName = name, Mode = AddMode.Hold });
            job.TotalSize = size;
            return job;
        }

        public void Dispose()
        {
            Manager.StopAllAsync().Wait(TimeSpan.FromSeconds(3));
            Manager.Dispose();
            Dir.Dispose();
        }
    }

    static CaptureRequest Request(string url = "http://127.0.0.1:9/files/a.zip", Action<Dictionary<string, string>>? headers = null,
        string? cookies = "a=1; b=2", long? size = null, string? fileName = null)
    {
        var h = new Dictionary<string, string>();
        headers?.Invoke(h);
        return new CaptureRequest
        {
            Id = "r1", Url = url, Referrer = "http://127.0.0.1:9/page", Cookies = cookies, UserAgent = "Browser-UA/1",
            Headers = h, Size = size, FileName = fileName,
        };
    }

    [Fact]
    public async Task Switched_off_means_the_browser_keeps_the_download()
    {
        using var e = new Env();
        e.Settings.Enabled = false;
        var result = await e.Processor.HandleAsync(Request());
        Assert.False(result.Accepted);
        Assert.Equal(CaptureReasons.Disabled, result.Reason);
        Assert.Empty(e.Manager.Jobs);
        Assert.Empty(e.Ui.Shown);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/system.ini")]
    [InlineData("ftp://example.com/a.zip")]
    [InlineData("blob:http://a/1")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Anything_but_a_web_address_is_refused(string url)
    {
        using var e = new Env();
        var result = await e.Processor.HandleAsync(Request(url));
        Assert.False(result.Accepted);
        Assert.Equal(CaptureReasons.Invalid, result.Reason);
        Assert.Empty(e.Manager.Jobs);
    }

    [Fact]
    public async Task An_oversized_address_is_refused()
    {
        using var e = new Env();
        var result = await e.Processor.HandleAsync(Request("http://127.0.0.1:9/" + new string('a', 9000)));
        Assert.Equal(CaptureReasons.Invalid, result.Reason);
    }

    [Fact]
    public async Task Excluded_hosts_and_their_subdomains_stay_with_the_browser()
    {
        using var e = new Env();
        e.Settings.ExcludedHosts.Add("example.com");

        Assert.Equal(CaptureReasons.Excluded, (await e.Processor.HandleAsync(Request("http://example.com/a.zip"))).Reason);
        Assert.Equal(CaptureReasons.Excluded, (await e.Processor.HandleAsync(Request("http://cdn.example.com/a.zip"))).Reason);
        Assert.NotEqual(CaptureReasons.Excluded, (await e.Processor.HandleAsync(Request("http://notexample.com/a.zip"))).Reason);
    }

    [Fact]
    public async Task Downloads_known_to_be_small_stay_with_the_browser()
    {
        using var e = new Env();
        e.Settings.MinSizeBytes = 1_000_000;
        Assert.False((await e.Processor.HandleAsync(Request(size: 5_000))).Accepted);
        Assert.True((await e.Processor.HandleAsync(Request("http://127.0.0.1:9/big.zip", size: 5_000_000))).Accepted);
        Assert.True((await e.Processor.HandleAsync(Request("http://127.0.0.1:9/unknown.zip"))).Accepted, "unknown size is taken");
    }

    [Fact]
    public async Task In_dialog_mode_the_capture_is_shown_with_a_cleaned_session()
    {
        using var e = new Env();
        var result = await e.Processor.HandleAsync(Request(headers: h =>
        {
            h["Authorization"] = "Bearer t";
            h["Host"] = "evil";
            h["Origin"] = "http://127.0.0.1:9";
            h["Range"] = "bytes=0-";
            h["X-Injected"] = "1";
        }, cookies: "Cookie: a=1; b=2"));

        Assert.True(result.Accepted);
        Assert.Equal(CaptureReasons.Added, result.Reason);
        Assert.Equal("r1", result.Id);
        Assert.Empty(e.Manager.Jobs);   // nothing is added until the user decides

        var shown = Assert.Single(e.Ui.Shown);
        Assert.Equal("a=1; b=2", shown.Cookies);
        Assert.Equal("http://127.0.0.1:9/page", shown.Referrer);
        Assert.Equal("Browser-UA/1", shown.UserAgent);
        Assert.Equal(new[] { "Authorization", "Origin" }, shown.Headers.Keys.OrderBy(k => k).ToArray());
        Assert.True(e.Ui.BroughtToFront > 0);
    }

    [Fact]
    public async Task Hostile_values_are_dropped_rather_than_passed_on()
    {
        using var e = new Env();
        var request = Request(headers: h => h["Authorization"] = "x\r\nX-Evil: 1") with
        {
            Referrer = "javascript:alert(1)",
            UserAgent = "UA\r\nX-Evil: 1",
            Cookies = new string('c', 40_000),
        };
        await e.Processor.HandleAsync(request);

        var shown = Assert.Single(e.Ui.Shown);
        Assert.Null(shown.Referrer);
        Assert.Null(shown.UserAgent);
        Assert.Null(shown.Cookies);
        Assert.Empty(shown.Headers);
    }

    [Fact]
    public async Task In_immediate_mode_the_download_starts_with_the_browsers_session()
    {
        using var e = new Env();
        e.Settings.Mode = CaptureMode.StartImmediately;

        var result = await e.Processor.HandleAsync(Request(headers: h => h["Authorization"] = "Bearer t"));

        Assert.True(result.Accepted);
        var job = Assert.Single(e.Manager.Jobs);
        Assert.Equal(job.Id.ToString(), result.JobId);
        Assert.Equal("a=1; b=2", job.Cookies);
        Assert.Equal("http://127.0.0.1:9/page", job.Referer);
        Assert.Equal("Browser-UA/1", job.UserAgent);
        Assert.Equal("Bearer t", job.Headers["Authorization"]);
        Assert.Empty(e.Ui.Shown);
    }

    [Fact]
    public async Task The_same_address_already_downloading_is_not_added_twice()
    {
        using var e = new Env();
        e.Settings.Mode = CaptureMode.StartImmediately;
        var paused = e.Held("http://127.0.0.1:9/files/a.zip");

        var result = await e.Processor.HandleAsync(Request());

        Assert.True(result.Accepted);
        Assert.Equal(CaptureReasons.Duplicate, result.Reason);
        Assert.Single(e.Manager.Jobs);
        Assert.Equal(paused.Id.ToString(), result.JobId);
    }

    [Fact]
    public async Task Retrying_a_failed_download_from_the_browser_gives_it_the_new_session()
    {
        using var e = new Env();
        var failed = e.Held("http://127.0.0.1:9/files/a.zip");
        failed.State = DownloadState.Failed;
        failed.ErrorKind = JobErrorKind.WebPage;
        failed.Cookies = "stale=1";

        var result = await e.Processor.HandleAsync(Request());

        Assert.True(result.Accepted);
        Assert.Equal(CaptureReasons.Refreshed, result.Reason);
        Assert.Single(e.Manager.Jobs);
        Assert.Equal("a=1; b=2", failed.Cookies);
        Assert.Equal("Browser-UA/1", failed.UserAgent);
        Assert.Empty(e.Ui.Shown);
    }

    [Fact]
    public async Task A_finished_download_can_be_downloaded_again()
    {
        using var e = new Env();
        e.Settings.Mode = CaptureMode.StartImmediately;
        e.Held("http://127.0.0.1:9/files/a.zip").State = DownloadState.Completed;

        var result = await e.Processor.HandleAsync(Request());
        Assert.Equal(CaptureReasons.Added, result.Reason);
        Assert.Equal(2, e.Manager.Jobs.Count);
    }

    [Fact]
    public async Task A_download_waiting_for_a_new_link_takes_the_next_capture_of_the_same_file()
    {
        using var e = new Env();
        var job = e.Held("http://127.0.0.1:9/old/a.zip", size: 1234);
        job.State = DownloadState.Failed;
        e.Processor.ExpectRefresh(job.Id);
        Assert.True(e.Processor.IsWaitingForRefresh(job.Id));

        var result = await e.Processor.HandleAsync(Request("http://127.0.0.1:9/new/token-xyz/a.zip", size: 1234));

        Assert.Equal(CaptureReasons.Refreshed, result.Reason);
        Assert.Equal(job.Id.ToString(), result.JobId);
        Assert.Equal("http://127.0.0.1:9/new/token-xyz/a.zip", job.Url);
        Assert.Equal("a=1; b=2", job.Cookies);
        Assert.False(e.Processor.IsWaitingForRefresh(job.Id), "the waiting is over");
        Assert.Single(e.Manager.Jobs);
    }

    [Fact]
    public async Task A_capture_of_a_different_file_does_not_hijack_the_waiting_download()
    {
        using var e = new Env();
        e.Settings.Mode = CaptureMode.StartImmediately;
        var job = e.Held("http://127.0.0.1:9/old/a.zip", size: 1234);
        job.State = DownloadState.Failed;
        e.Processor.ExpectRefresh(job.Id);

        var result = await e.Processor.HandleAsync(Request("http://127.0.0.1:9/other/b.zip", size: 999));

        Assert.Equal(CaptureReasons.Added, result.Reason);
        Assert.Equal("http://127.0.0.1:9/old/a.zip", job.Url);
        Assert.True(e.Processor.IsWaitingForRefresh(job.Id));
    }

    [Fact]
    public async Task Without_a_known_size_the_name_decides()
    {
        using var e = new Env();
        var job = e.Held("http://127.0.0.1:9/old/archive.zip", name: "archive.zip");
        job.State = DownloadState.Failed;
        e.Processor.ExpectRefresh(job.Id);

        var result = await e.Processor.HandleAsync(Request("http://127.0.0.1:9/new/x", fileName: "archive.zip"));
        Assert.Equal(CaptureReasons.Refreshed, result.Reason);
    }

    [Fact]
    public async Task Waiting_for_a_new_link_ends_after_ten_minutes_and_only_one_download_waits_at_a_time()
    {
        using var e = new Env();
        e.Settings.Mode = CaptureMode.StartImmediately;
        var first = e.Held("http://127.0.0.1:9/old/a.zip", size: 10);
        var second = e.Held("http://127.0.0.1:9/old/b.zip", "b.zip", size: 10);
        first.State = second.State = DownloadState.Failed;

        e.Processor.ExpectRefresh(first.Id);
        e.Processor.ExpectRefresh(second.Id);
        Assert.False(e.Processor.IsWaitingForRefresh(first.Id), "IDM: only one expired download at a time");
        Assert.True(e.Processor.IsWaitingForRefresh(second.Id));

        e.Now += CaptureProcessor.RefreshWindow + TimeSpan.FromSeconds(1);
        Assert.False(e.Processor.IsWaitingForRefresh(second.Id));
        var result = await e.Processor.HandleAsync(Request("http://127.0.0.1:9/new/b.zip", size: 10));
        Assert.Equal(CaptureReasons.Added, result.Reason);
    }

    [Fact]
    public async Task A_failure_inside_the_processor_is_reported_not_thrown()
    {
        using var e = new Env();
        e.Settings.Mode = CaptureMode.StartImmediately;
        e.Manager.Dispose();   // everything the processor touches now misbehaves

        var result = await e.Processor.HandleAsync(Request("http://127.0.0.1:9/files/zzz.zip"));
        // either it still worked or it reported an error: it must never throw
        Assert.NotNull(result);
        Assert.Equal("r1", result.Id);
    }
}

public class BrowserBridgeServiceTests
{
    static string Pipe() => "ShadeNetTest." + Guid.NewGuid().ToString("N");

    sealed class Env : IAsyncDisposable
    {
        public readonly TempDir Dir = new();
        public readonly DownloadManager Manager;
        public readonly FakeCaptureUi Ui = new();
        public readonly SettingsService Settings;
        public readonly BrowserBridgeService Service;
        public readonly string PipeName = Pipe();

        public Env()
        {
            Settings = new SettingsService(Dir.Path);
            Settings.Load();
            Manager = new DownloadManager(new ShadeNet.Core.Engine.EngineSettings { DownloadRoot = Dir.Path, UseCategoryFolders = false });
            Service = new BrowserBridgeService(new CaptureProcessor(Manager, () => Settings.Current.Browser, Ui), Settings, Ui, "7.7.7");
            Assert.True(Service.Start(PipeName));
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            Manager.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task The_extensions_hello_is_answered_with_the_apps_settings()
    {
        await using var e = new Env();
        e.Settings.Current.Browser.ExcludedHosts.Add("bank.example");
        e.Settings.Current.Browser.MinSizeBytes = 4096;

        await using var client = await BridgeClient.TryConnectAsync(e.PipeName, TimeSpan.FromSeconds(5));
        await client!.SendAsync(new { type = MessageTypes.Hello, browser = "chromium" });
        var reply = (await client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!;

        Assert.Equal(MessageTypes.HelloAck, reply.Type);
        var settings = reply.As<BridgeSettings>()!;
        Assert.Equal("7.7.7", settings.AppVersion);
        Assert.True(settings.Enabled);
        Assert.Equal(new[] { "bank.example" }, settings.ExcludedHosts);
        Assert.Equal(4096, settings.MinSizeBytes);
    }

    [Fact]
    public async Task A_capture_is_processed_and_answered_with_the_same_id()
    {
        await using var e = new Env();
        await using var client = await BridgeClient.TryConnectAsync(e.PipeName, TimeSpan.FromSeconds(5));
        await client!.SendAsync(new CaptureRequest { Id = "cap-9", Url = "http://127.0.0.1:9/f.zip", Cookies = "k=v", Referrer = "http://127.0.0.1:9/p" });
        var reply = (await client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.As<CaptureResult>()!;

        Assert.Equal("cap-9", reply.Id);
        Assert.True(reply.Accepted);
        var shown = Assert.Single(e.Ui.Shown);
        Assert.Equal("k=v", shown.Cookies);
    }

    [Fact]
    public async Task A_connected_browser_is_noticed_and_forgotten_when_it_leaves()
    {
        await using var e = new Env();
        var changes = 0;
        e.Service.StatusChanged += () => Interlocked.Increment(ref changes);
        Assert.False(e.Service.IsBrowserConnected);

        var client = await BridgeClient.TryConnectAsync(e.PipeName, TimeSpan.FromSeconds(5));
        await client!.SendAsync(new HostHello { Origin = "chrome-extension://x/" });
        await Until(() => e.Service.IsBrowserConnected);

        await client.DisposeAsync();
        await Until(() => !e.Service.IsBrowserConnected);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task Changed_settings_are_pushed_to_connected_extensions()
    {
        await using var e = new Env();
        await using var client = await BridgeClient.TryConnectAsync(e.PipeName, TimeSpan.FromSeconds(5));
        await client!.SendAsync(new { type = MessageTypes.Hello });
        Assert.Equal(MessageTypes.HelloAck, (await client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Type);
        await Until(() => e.Service.IsBrowserConnected);

        e.Settings.Current.Browser.Enabled = false;
        await e.Service.PushSettingsAsync();
        var pushed = (await client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!;

        Assert.Equal(MessageTypes.Settings, pushed.Type);
        Assert.False(pushed.As<BridgeSettings>()!.Enabled);
    }

    [Fact]
    public async Task A_second_instance_brings_the_first_to_the_front()
    {
        await using var e = new Env();
        Assert.True(await BrowserBridgeService.ActivateRunningInstanceAsync(e.PipeName));
        await Until(() => e.Ui.BroughtToFront > 0);
    }

    [Fact]
    public async Task With_no_instance_running_there_is_nothing_to_activate()
    {
        Assert.False(await BrowserBridgeService.ActivateRunningInstanceAsync(Pipe()));
    }

    [Fact]
    public async Task Only_one_instance_can_own_the_pipe()
    {
        await using var e = new Env();
        using var settings = new TempDir();
        await using var second = new BrowserBridgeService(
            new CaptureProcessor(e.Manager, () => new BrowserSettings(), e.Ui), e.Settings, e.Ui, "1");
        Assert.False(second.Start(e.PipeName));
    }

    [Fact]
    public async Task Ping_is_answered()
    {
        await using var e = new Env();
        await using var client = await BridgeClient.TryConnectAsync(e.PipeName, TimeSpan.FromSeconds(5));
        await client!.SendAsync(new { type = MessageTypes.Ping });
        Assert.Equal(MessageTypes.Pong, (await client.ReadAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.Type);
    }

    static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "timed out waiting for the condition");
    }
}

public class BrowserIntegrationInstallerTests
{
    sealed class Env : IDisposable
    {
        public readonly TempDir Data = new();
        public readonly TempDir App = new();
        public readonly string RegistryRoot = @"Software\ShadeNetTests\" + Guid.NewGuid().ToString("N");
        public readonly BrowserIntegrationInstaller Installer;

        public Env(bool withHost = true)
        {
            if (withHost)
            {
                Directory.CreateDirectory(Path.Combine(App.Path, "NativeHost"));
                File.WriteAllText(Path.Combine(App.Path, "NativeHost", "ShadeNet.NativeHost.exe"), "stub");
            }

            Installer = new BrowserIntegrationInstaller(Data.Path, App.Path, RegistryRoot);
        }

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\ShadeNetTests\" + RegistryRoot.Split('\\').Last(), throwOnMissingSubKey: false);
            try
            {
                // the shared parent goes too once the last test has cleaned up after itself
                Registry.CurrentUser.DeleteSubKey(@"Software\ShadeNetTests", throwOnMissingSubKey: false);
            }
            catch (InvalidOperationException)
            {
                // another test is still using it
            }

            Data.Dispose();
            App.Dispose();
        }
    }

    [Fact]
    public void Nothing_is_registered_until_the_user_sets_it_up()
    {
        using var e = new Env();
        var status = e.Installer.GetStatus();
        Assert.False(status.IsInstalled);
        Assert.True(status.HostFilesPresent);
        Assert.Empty(status.RegisteredBrowsers);
    }

    [Fact]
    public void Install_registers_the_host_for_every_supported_browser_and_writes_a_manifest_for_our_extension_only()
    {
        using var e = new Env();
        e.Installer.Install();

        var status = e.Installer.GetStatus();
        Assert.Equal(BrowserIntegrationInstaller.Browsers.Select(b => b.Name).OrderBy(n => n), status.RegisteredBrowsers.OrderBy(n => n));

        foreach (var browser in BrowserIntegrationInstaller.Browsers)
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{e.RegistryRoot}\{browser.VendorPath}\NativeMessagingHosts\{BridgeProtocol.HostName}");
            Assert.Equal(e.Installer.ManifestPath, key!.GetValue(null));
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(e.Installer.ManifestPath));
        var root = manifest.RootElement;
        Assert.Equal(BridgeProtocol.HostName, root.GetProperty("name").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());
        Assert.Equal(new[] { $"chrome-extension://{BrowserIntegrationInstaller.ExtensionId}/" },
            root.GetProperty("allowed_origins").EnumerateArray().Select(x => x.GetString()).ToArray());

        var path = root.GetProperty("path").GetString()!;
        Assert.True(Path.IsPathRooted(path), "browsers need an absolute path");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void The_host_can_find_the_app_again_from_the_config_written_at_install()
    {
        using var e = new Env();
        e.Installer.Install();
        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(e.Data.Path, "host.json"))).RootElement;
        Assert.False(string.IsNullOrEmpty(config.GetProperty("appPath").GetString()));
    }

    [Fact]
    public void A_host_outside_the_machine_wide_runtime_gets_a_launcher_that_sets_DOTNET_ROOT()
    {
        using var e = new Env();
        e.Installer.Install();
        var path = JsonDocument.Parse(File.ReadAllText(e.Installer.ManifestPath)).RootElement.GetProperty("path").GetString()!;

        if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var script = File.ReadAllText(path);
            Assert.Contains("set \"DOTNET_ROOT=", script);
            Assert.Contains(e.Installer.HostExecutable, script);
            Assert.Contains("%*", script);   // the origin argument the browser passes must reach the host
        }
        else
        {
            Assert.Equal(e.Installer.HostExecutable, path);
        }
    }

    [Fact]
    public void Install_can_be_repeated_and_Uninstall_removes_everything()
    {
        using var e = new Env();
        e.Installer.Install();
        e.Installer.Install();
        Assert.True(e.Installer.GetStatus().IsInstalled);

        e.Installer.Uninstall();
        Assert.False(e.Installer.GetStatus().IsInstalled);
        Assert.False(File.Exists(e.Installer.ManifestPath));
        Assert.False(File.Exists(Path.Combine(e.Data.Path, "host.json")));
        using var key = Registry.CurrentUser.OpenSubKey($@"{e.RegistryRoot}\Google\Chrome\NativeMessagingHosts\{BridgeProtocol.HostName}");
        Assert.Null(key);

        e.Installer.Uninstall();   // nothing left: still fine
    }

    [Fact]
    public void Setting_up_without_the_host_in_the_installation_explains_itself()
    {
        using var e = new Env(withHost: false);
        Assert.False(e.Installer.GetStatus().HostFilesPresent);
        Assert.Throws<FileNotFoundException>(() => e.Installer.Install());
        Assert.False(e.Installer.GetStatus().IsInstalled);
    }

    [Fact]
    public void The_extension_id_matches_the_key_in_the_extensions_manifest()
    {
        var dir = AppContext.BaseDirectory;
        string? manifestPath = null;
        for (var d = new DirectoryInfo(dir); d != null && manifestPath == null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "extension", "chromium", "manifest.json");
            if (File.Exists(candidate)) manifestPath = candidate;
        }

        // Chrome derives the ID from the key: first 16 bytes of SHA-256 of the DER public key, each nibble as a letter a-p.
        var key = JsonDocument.Parse(File.ReadAllText(manifestPath!)).RootElement.GetProperty("key").GetString()!;
        var hash = System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(key));
        var id = string.Concat(hash.Take(16).Select(b => $"{(char)('a' + (b >> 4))}{(char)('a' + (b & 15))}"));

        Assert.Equal(id, BrowserIntegrationInstaller.ExtensionId);
    }
}

public class SettingsBrowserTests
{
    sealed class FakeBridge : IBrowserBridge
    {
        public bool IsBrowserConnected { get; set; }
        public int Pushes;
        public List<Guid> Expected { get; } = new();
        public event Action? StatusChanged;
        public void ExpectRefresh(Guid jobId) => Expected.Add(jobId);
        public Task PushSettingsAsync() { Interlocked.Increment(ref Pushes); return Task.CompletedTask; }
        public void Raise() => StatusChanged?.Invoke();
    }

    static (SettingsViewModel Vm, SettingsService Settings, FakeBridge Bridge, TempDir Dir) Make()
    {
        var dir = new TempDir();
        var settings = new SettingsService(dir.Path);
        settings.Load();
        var bridge = new FakeBridge();
        var installer = new BrowserIntegrationInstaller(dir.Path, dir.Path, @"Software\ShadeNetTests\" + Guid.NewGuid().ToString("N"));
        return (new SettingsViewModel(settings, new AppThemeService(), installer, bridge), settings, bridge, dir);
    }

    [Fact]
    public void Turning_capture_off_is_saved_and_pushed_to_the_extension()
    {
        var (vm, settings, bridge, dir) = Make();
        using var _ = dir;
        vm.BrowserEnabled = false;
        Assert.False(settings.Current.Browser.Enabled);
        Assert.Equal(1, bridge.Pushes);
        Assert.Contains("\"Enabled\": false", File.ReadAllText(settings.SettingsPath));

        vm.BrowserEnabled = false;   // no change, no noise
        Assert.Equal(1, bridge.Pushes);
    }

    [Fact]
    public void The_capture_mode_choice_is_saved()
    {
        var (vm, settings, bridge, dir) = Make();
        using var _ = dir;
        Assert.True(vm.CaptureShowsDialog);
        vm.CaptureStartsImmediately = true;
        Assert.Equal(CaptureMode.StartImmediately, settings.Current.Browser.Mode);
        Assert.False(vm.CaptureShowsDialog);
        Assert.Equal(1, bridge.Pushes);
    }

    [Fact]
    public void Excluded_sites_are_read_from_free_text_and_normalised()
    {
        var (vm, settings, bridge, dir) = Make();
        using var _ = dir;
        vm.ExcludedHostsText = "Bank.Example.com\n*.paypal.com, .Docs.example.org  bank.example.com";
        Assert.Equal(new[] { "bank.example.com", "paypal.com", "docs.example.org" }, settings.Current.Browser.ExcludedHosts);
        Assert.Equal(1, bridge.Pushes);

        vm.ExcludedHostsText = "";
        Assert.Empty(settings.Current.Browser.ExcludedHosts);
    }

    [Fact]
    public void The_status_follows_the_installation_and_the_connection()
    {
        var (vm, _, bridge, dir) = Make();
        using var _ = dir;
        Assert.False(vm.IsIntegrationInstalled);
        Assert.Contains("missing", vm.IntegrationStatus);   // this test's app folder has no host

        vm.InstallIntegrationCommand.Execute(null);
        Assert.False(string.IsNullOrEmpty(vm.IntegrationError), "setting up without the host must say why");
    }
}

public class AddDialogCaptureTests
{
    [Fact]
    public async Task A_captured_link_opens_with_the_browsers_session_and_probes_with_it()
    {
        using var dir = new TempDir();
        using var server = new AddDialogTests.GatedServer();
        using var manager = new DownloadManager(new ShadeNet.Core.Engine.EngineSettings { DownloadRoot = dir.Path });

        var capture = new CaptureInfo(server.BaseUrl + "/files/archive.zip", server.BaseUrl + "/page", "session=ok", "Browser-UA/1",
            new Dictionary<string, string> { ["Authorization"] = "Bearer t" }, null, null, "download");
        using var vm = new AddDownloadViewModel(manager, null, capture);

        Assert.Equal("session=ok", vm.Cookies);
        Assert.Equal(server.BaseUrl + "/page", vm.Referer);
        Assert.Contains("browser", vm.SessionNote!, StringComparison.OrdinalIgnoreCase);

        for (var i = 0; i < 100 && vm.ProbeState != ProbeState.Ready; i++) await Task.Delay(50);
        Assert.Equal(ProbeState.Ready, vm.ProbeState);   // the gated server only answers with the cookie
        Assert.Equal("archive.zip", vm.FileName);

        var (request, _) = Assert.Single(vm.Build(AddMode.Start));
        Assert.Equal("session=ok", request.Cookies);
        Assert.Equal("Browser-UA/1", request.UserAgent);
        Assert.Equal("Bearer t", request.Headers!["Authorization"]);
    }

    [Fact]
    public void A_dialog_opened_by_hand_has_no_session_note()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(new ShadeNet.Core.Engine.EngineSettings { DownloadRoot = dir.Path });
        using var vm = new AddDownloadViewModel(manager, null);
        Assert.Null(vm.SessionNote);
    }
}

/// <summary>
/// The whole chain except the browser itself: the real host executable, driven over stdin/stdout the way Chrome drives
/// it, talking through the real pipe to the app's bridge service, which really adds a download.
/// </summary>
public class FullChainTests
{
    static string FindHost()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            foreach (var cfg in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(dir.FullName, "src", "ShadeNet.NativeHost", "bin", cfg, "net10.0", "ShadeNet.NativeHost.exe");
                if (File.Exists(candidate)) return candidate;
            }

        throw new FileNotFoundException("ShadeNet.NativeHost.exe was not built.");
    }

    static async Task<BridgeMessage> Read(System.Diagnostics.Process host) =>
        BridgeMessage.Parse((await Framing.ReadAsync(host.StandardOutput.BaseStream, 1 << 20).WaitAsync(TimeSpan.FromSeconds(30)))!)!;

    [Fact]
    public async Task A_capture_typed_into_the_hosts_stdin_becomes_a_download_with_the_browsers_session()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.Path);
        settings.Load();
        settings.Current.Browser.Mode = CaptureMode.StartImmediately;
        using var manager = new DownloadManager(new ShadeNet.Core.Engine.EngineSettings { DownloadRoot = dir.Path, UseCategoryFolders = false });
        var ui = new FakeCaptureUi();
        var pipe = "ShadeNetTest." + Guid.NewGuid().ToString("N");
        await using var service = new BrowserBridgeService(new CaptureProcessor(manager, () => settings.Current.Browser, ui), settings, ui, "1.2.3");
        Assert.True(service.Start(pipe));

        var info = new System.Diagnostics.ProcessStartInfo(FindHost(), "chrome-extension://" + BrowserIntegrationInstaller.ExtensionId + "/")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["SHADENET_PIPE"] = pipe;
        info.Environment["SHADENET_DATA_DIR"] = dir.Path;
        info.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        using var host = System.Diagnostics.Process.Start(info)!;

        async Task Send(object message) =>
            await Framing.WriteAsync(host.StandardInput.BaseStream, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, BridgeProtocol.Json));

        // exactly what the extension sends
        await Send(new { type = "hello", browser = "chromium", extensionVersion = "1.0.0" });
        var ack = await Read(host);
        Assert.Equal(MessageTypes.HelloAck, ack.Type);
        Assert.Equal("1.2.3", ack.As<BridgeSettings>()!.AppVersion);
        Assert.True(service.IsBrowserConnected);

        await Send(new
        {
            type = "capture",
            id = "chain-1",
            url = "http://127.0.0.1:9/download/web/abc/The%20Clumsy%20Witch%20Seris.zip",
            referrer = "https://gofile.io/d/abc",
            cookies = "accountToken=GAdjl4qbces5",
            userAgent = "Mozilla/5.0 Test",
            headers = new Dictionary<string, string> { ["Authorization"] = "Bearer x" },
            mimeType = "application/zip",
            size = 2546126931L,
            source = "download",
        });
        var result = (await Read(host)).As<CaptureResult>()!;

        Assert.Equal("chain-1", result.Id);
        Assert.True(result.Accepted);
        Assert.Equal(CaptureReasons.Added, result.Reason);

        var job = Assert.Single(manager.Jobs);
        Assert.Equal("accountToken=GAdjl4qbces5", job.Cookies);
        Assert.Equal("https://gofile.io/d/abc", job.Referer);
        Assert.Equal("Mozilla/5.0 Test", job.UserAgent);
        Assert.Equal("Bearer x", job.Headers["Authorization"]);

        // changing a setting in the app reaches the extension through the same chain
        settings.Current.Browser.Enabled = false;
        await service.PushSettingsAsync();
        var pushed = await Read(host);
        Assert.Equal(MessageTypes.Settings, pushed.Type);
        Assert.False(pushed.As<BridgeSettings>()!.Enabled);

        host.StandardInput.Close();
        Assert.True(host.WaitForExit(10_000));
        for (var i = 0; i < 100 && service.IsBrowserConnected; i++) await Task.Delay(50);
        Assert.False(service.IsBrowserConnected);
    }
}
