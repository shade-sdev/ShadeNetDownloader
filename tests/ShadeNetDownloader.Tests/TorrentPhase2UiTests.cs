using System.IO;
using Microsoft.Win32;
using MonoTorrent;
using ShadeNet.Bridge;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Torrents;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Services.Browser;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

/// <summary>
/// Torrents, the second part, in the app: opening magnet links and .torrent files from Windows (registered on a throw-away registry key),
/// links handed to a running ShadeNet, magnet links and .torrent files from the browser, and the details drawer's Peers / Files / Trackers.
/// </summary>
public class TorrentPhase2UiTests
{
    static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "Timed out waiting for: " + what);
    }

    const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Debian";

    static EngineSettings Hermetic(string dir) => new()
    {
        DownloadRoot = Path.Combine(dir, "dl"),
        UseCategoryFolders = false,
        TorrentFolder = Path.Combine(dir, "torrents"),   // never the user's own
        TorrentListenAddress = System.Net.IPAddress.Loopback,
        TorrentUseDht = false,
        TorrentPortForwarding = false,
        TorrentLocalPeers = false,
    };

    // ---- Windows: magnet links and .torrent files ------------------------------------------------------------------

    sealed class RegistrySandbox : IDisposable
    {
        public readonly string Root = @"Software\ShadeNetTests\" + Guid.NewGuid().ToString("N");

        public RegistryKey? Open(string path) => Registry.CurrentUser.OpenSubKey($@"{Root}\{path}");

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\ShadeNetTests");
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\ShadeNetTests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Where_nothing_opens_them_ShadeNet_becomes_the_program_for_magnet_links_and_torrent_files()
    {
        using var reg = new RegistrySandbox();
        var exe = @"C:\Program Files\ShadeNet\ShadeNetDownloader.exe";
        var association = new TorrentAssociation(exe, reg.Root, _ => null);   // nothing handles them yet

        Assert.False(association.GetStatus().Registered);
        association.Register();

        Assert.True(association.GetStatus().Registered);
        Assert.Equal($"\"{exe}\" \"%1\"", reg.Open(@"Classes\magnet\shell\open\command")!.GetValue(null));
        Assert.Equal("", reg.Open(@"Classes\magnet")!.GetValue("URL Protocol"));
        Assert.Equal(TorrentAssociation.TorrentProgId, reg.Open(@"Classes\.torrent")!.GetValue(null));
        Assert.Equal(TorrentAssociation.MagnetProgId, reg.Open(@"ShadeNet\Capabilities\URLAssociations")!.GetValue("magnet"));
        Assert.Equal($@"{reg.Root}\ShadeNet\Capabilities", reg.Open("RegisteredApplications")!.GetValue(TorrentAssociation.AppName));

        association.Unregister();
        Assert.False(association.GetStatus().Registered);
        Assert.Null(reg.Open(@"Classes\magnet"));
        Assert.Null(reg.Open(@"Classes\.torrent")?.GetValue(null));
        Assert.Null(reg.Open(@"Classes\" + TorrentAssociation.TorrentProgId));
        Assert.Null(reg.Open("RegisteredApplications")?.GetValue(TorrentAssociation.AppName));
    }

    [Fact]
    public void Another_torrent_program_keeps_what_it_opens_and_Default_apps_is_offered()
    {
        using var reg = new RegistrySandbox();
        var exe = @"C:\Program Files\ShadeNet\ShadeNetDownloader.exe";
        // another program opens both now
        var association = new TorrentAssociation(exe, reg.Root, _ => "\"C:\\Program Files\\qBittorrent\\qbittorrent.exe\" \"%1\"");
        var settings = new SettingsService(Path.Combine(Path.GetTempPath(), "sn-assoc-" + Guid.NewGuid().ToString("N")[..6]));
        var page = new DownloadSettingsViewModel(settings, () => { }, () => { }, association);

        Assert.False(page.OpenTorrentsWithShadeNet);
        page.OpenTorrentsWithShadeNet = true;

        Assert.True(page.OpenTorrentsWithShadeNet);
        Assert.True(page.NeedsDefaultApps, "the user is shown the way to choose ShadeNet");
        Assert.Contains("Default apps", page.AssociationText);
        Assert.Null(reg.Open(@"Classes\magnet"));                         // not taken from the other program
        Assert.Null(reg.Open(@"Classes\.torrent")?.GetValue(null));
        Assert.NotNull(reg.Open(@"Classes\.torrent\OpenWithProgids")!.GetValue(TorrentAssociation.TorrentProgId, null, RegistryValueOptions.DoNotExpandEnvironmentNames));

        page.OpenTorrentsWithShadeNet = false;
        Assert.Null(reg.Open(@"Classes\" + TorrentAssociation.MagnetProgId));
    }

    [Fact]
    public void Removing_another_installations_registration_leaves_it_alone()
    {
        using var reg = new RegistrySandbox();
        new TorrentAssociation(@"C:\Users\me\AppData\Local\Programs\ShadeNet\ShadeNetDownloader.exe", reg.Root, _ => null).Register();

        new TorrentAssociation(@"D:\Test\ShadeNet\ShadeNetDownloader.exe", reg.Root, _ => null).Unregister();

        Assert.NotNull(reg.Open(@"Classes\" + TorrentAssociation.TorrentProgId));
        Assert.NotNull(reg.Open(@"Classes\magnet"));
    }

    [Fact]
    public void Links_given_on_the_command_line_or_by_a_second_launch_are_the_links_only()
    {
        var args = new[] { "--minimized", Magnet, @"C:\Users\me\Downloads\debian.torrent", "garbage", "https://example.com/a.zip" };
        Assert.Equal(new[] { Magnet, @"C:\Users\me\Downloads\debian.torrent", "https://example.com/a.zip" }, App.LinksFromArguments(args));

        var message = BridgeMessage.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = MessageTypes.Activate, links = new object[] { Magnet, 42, "not a link", "magnet:?dn=nothing" },
        }))!;
        Assert.Equal(new[] { Magnet }, BrowserBridgeService.LinksOf(message));
        Assert.Empty(BrowserBridgeService.LinksOf(BridgeMessage.Parse("{\"type\":\"activate\"}"u8.ToArray())!));
    }

    // ---- the browser ----------------------------------------------------------------------------------------------

    sealed class CaptureEnv : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly DownloadManager Manager;
        public readonly FakeCaptureUi Ui = new();
        public readonly BrowserSettings Settings = new();
        public readonly CaptureProcessor Processor;

        public CaptureEnv()
        {
            Manager = new DownloadManager(Hermetic(Dir.Path));
            Processor = new CaptureProcessor(Manager, () => Settings, Ui);
        }

        public void Dispose()
        {
            Manager.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task A_magnet_link_clicked_in_the_browser_opens_the_dialog_unless_the_user_turned_that_off()
    {
        using var env = new CaptureEnv();

        var result = await env.Processor.HandleAsync(new CaptureRequest { Id = "1", Url = Magnet, Referrer = "https://tracker.example/t/1", Source = "magnet" });
        Assert.True(result.Accepted);
        var shown = Assert.Single(env.Ui.Shown);
        Assert.Equal("magnet", shown.Source);
        Assert.True(shown.IsTorrent);
        Assert.Equal("https://tracker.example/t/1", shown.Referrer);

        env.Settings.MagnetLinks = false;
        var refused = await env.Processor.HandleAsync(new CaptureRequest { Id = "2", Url = Magnet, Source = "magnet" });
        Assert.False(refused.Accepted);
        Assert.Equal(CaptureReasons.Disabled, refused.Reason);

        // "Download with ShadeNet" on the link names ShadeNet: it counts even with the setting off
        Assert.True((await env.Processor.HandleAsync(new CaptureRequest { Id = "3", Url = Magnet, Source = "magnet-menu" })).Accepted);

        // what is not a magnet link is not taken for one
        Assert.False((await env.Processor.HandleAsync(new CaptureRequest { Id = "4", Url = "magnet:?dn=no-identity", Source = "magnet" })).Accepted);
        Assert.False((await env.Processor.HandleAsync(new CaptureRequest { Id = "5", Url = "javascript:alert(1)", Source = "magnet-menu" })).Accepted);
    }

    [Fact]
    public async Task A_torrent_file_the_browser_downloads_becomes_a_torrent_with_the_browsers_sign_in()
    {
        using var env = new CaptureEnv();
        env.Settings.Mode = CaptureMode.StartImmediately;

        var result = await env.Processor.HandleAsync(new CaptureRequest
        {
            Id = "1", Url = "https://tracker.example/download.php?id=7", FileName = "Debian.torrent", MimeType = "application/x-bittorrent",
            Cookies = "member=yes", Referrer = "https://tracker.example/details/7", Source = "download",
        });

        Assert.True(result.Accepted);
        var job = Assert.Single(env.Manager.Jobs);
        Assert.Equal(DownloadKind.Torrent, job.Kind);
        Assert.Equal("https://tracker.example/download.php?id=7", job.Url);
        Assert.Equal("member=yes", job.Cookies);
        Assert.Equal("https://tracker.example/details/7", job.Referer);
        env.Manager.Pause(job.Id);
    }

    // ---- the details drawer -----------------------------------------------------------------------------------------

    static async Task<string> MakeTorrentAsync(string dir)
    {
        var source = Path.Combine(dir, "src", "Pack");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "a.bin"), new byte[200_000]);
        File.WriteAllBytes(Path.Combine(source, "b.bin"), new byte[100_000]);
        var dict = await new TorrentCreator(TorrentType.V1Only).CreateAsync(new TorrentFileSource(source));
        var path = Path.Combine(dir, "pack.torrent");
        File.WriteAllBytes(path, dict.Encode());
        return path;
    }

    [Fact]
    public async Task The_drawer_lists_peers_files_and_trackers_and_a_file_set_to_skip_is_left_out()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(Hermetic(dir.Path));
        var job = manager.Add(new AddDownloadRequest { Url = await MakeTorrentAsync(dir.Path), Mode = AddMode.Hold });
        var item = new DownloadItemViewModel(job, manager.Snapshot(job), null!);
        using var details = new DownloadDetailsViewModel(item, manager, new ToastService());

        Assert.True(details.IsTorrent);
        Assert.Equal("Peers", details.ConnectionsTabTitle);
        Assert.Equal("Pieces", details.MapTitle);
        await WaitFor(() => details.TorrentFiles.Count == 2 && details.PiecesText.Contains(" of "), "the files and pieces from what was saved");
        await Task.Delay(200);   // (no UI thread here: that first refresh ends on another thread)
        Assert.Equal(new[] { "a.bin", "b.bin" }, details.TorrentFiles.Select(f => f.Path).OrderBy(p => p));

        // live numbers, as the engine gives them
        details.ApplyTorrent(new TorrentDetails(true,
            new[] { new TorrentPeer("10.0.0.5:51413", "Transmission 4.0", 1, 300_000, 0, true, true, false) },
            new[] { new TorrentFileProgress(0, "a.bin", 200_000, 0.5, TorrentFilePriority.High), new TorrentFileProgress(1, "b.bin", 100_000, 0, TorrentFilePriority.Normal) },
            new[] { new TorrentTracker("http://tracker.example/announce", "Working", null, 1), new TorrentTracker("DHT", "Working (120 nodes)", null, 0) },
            new[] { true, true, false, true }, 100_000, 350_000, false));
        var peer = Assert.Single(details.Peers);
        Assert.Equal("Transmission 4.0", peer.Client);
        Assert.Equal("has all of it", peer.ProgressText);
        Assert.Contains("encrypted", peer.FlagsText);
        Assert.Equal(2, details.Trackers.Count);
        Assert.Equal("tier 1", details.Trackers[0].TierText);
        Assert.Equal("3 of 4", details.PiecesText);
        Assert.Equal(new[] { SegmentState.Done, SegmentState.Pending, SegmentState.Done }, details.MapSegments.Select(s => s.State));
        Assert.Equal(349_999, details.MapSegments[^1].End);

        // the user skips b.bin in the Files tab
        details.TorrentFiles.Single(f => f.Path == "b.bin").Priority = TorrentFilePriority.Skip;
        await WaitFor(() => job.Torrent!.Files!.Single(f => f.Path == "b.bin").Selected == false, "the choice to reach the download");
        Assert.Equal(200_000, job.TotalSize);
        Assert.True(job.Torrent!.Files!.Single(f => f.Path == "a.bin").Selected);

        // and asks for the pieces in order
        details.Sequential = true;
        await WaitFor(() => job.Torrent!.Sequential, "in order to reach the download");
    }

    [Fact]
    public void Pieces_become_runs_for_the_map()
    {
        var runs = DownloadDetailsViewModel.PieceRuns(new[] { false, false, true, true, true, false }, 10, 55);
        Assert.Equal(new[] { (0L, 19L, 0L), (20L, 49L, 30L), (50L, 54L, 0L) }, runs.Select(r => (r.Start, r.End, r.Downloaded)));
        Assert.Empty(DownloadDetailsViewModel.PieceRuns(Array.Empty<bool>(), 10, 0));
    }
}
