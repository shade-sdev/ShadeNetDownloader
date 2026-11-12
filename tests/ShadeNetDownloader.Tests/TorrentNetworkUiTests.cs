using System.IO;
using System.Net;
using MonoTorrent;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Torrents;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

/// <summary>
/// Where torrents go and what the user does by hand, in the app: Settings > Torrents (encryption, the network adapter, the proxy, banned
/// peers), the drawer's Peers and Trackers (add, ban, take off, the swarm's size) and the card's words for the swarm.
/// </summary>
public class TorrentNetworkUiTests
{
    static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "Timed out waiting for: " + what);
    }

    static EngineSettings Hermetic(string dir) => new()
    {
        DownloadRoot = Path.Combine(dir, "dl"),
        UseCategoryFolders = false,
        TorrentFolder = Path.Combine(dir, "torrents"),
        TorrentListenAddress = IPAddress.Loopback,
        TorrentUseDht = false,
        TorrentPortForwarding = false,
        TorrentLocalPeers = false,
    };

    static SettingsService NewSettings(string dir)
    {
        var settings = new SettingsService(dir);
        var e = settings.Current.Engine;
        e.TorrentFolder = Path.Combine(dir, "torrents");
        e.TorrentUseDht = false;
        e.TorrentPortForwarding = false;
        e.TorrentLocalPeers = false;
        return settings;
    }

    static readonly NetworkAdapterInfo Vpn = new("{11111111-0000-4000-8000-000000000001}", "Proton VPN", "WireGuard Tunnel", true, new[] { IPAddress.Parse("10.2.0.2") });
    static readonly NetworkAdapterInfo Wifi = new("{11111111-0000-4000-8000-000000000002}", "Wi-Fi", "Intel Wi-Fi", false, Array.Empty<IPAddress>());

    [Fact]
    public void Settings_bind_torrents_to_an_adapter_and_keep_it_listed_while_it_is_gone()
    {
        using var dir = new TempDir();
        var settings = NewSettings(dir.Path);
        var adapters = new List<NetworkAdapterInfo> { Vpn, Wifi };
        using var manager = new DownloadManager(settings.Current.Engine);
        var page = new DownloadSettingsViewModel(settings, () => manager.ApplySettings(), () => { }, manager: manager, adapters: () => adapters);

        Assert.Equal(new[] { "Any network (usual)", "Proton VPN  ·  10.2.0.2", "Wi-Fi (not connected)" }, page.AdapterChoices.Select(c => c.Label));
        Assert.Null(page.SelectedAdapter!.Id);
        Assert.Equal("Right now torrents go directly.", page.TorrentNetworkText);

        page.SelectedAdapter = page.AdapterChoices[1];
        Assert.Equal(Vpn.Id, settings.Current.Engine.TorrentNetworkAdapter);
        Assert.Equal("Proton VPN", settings.Current.Engine.TorrentNetworkAdapterName);

        // the VPN is disconnected and Windows drops its adapter: it stays the choice, and says so
        adapters.Remove(Vpn);
        page.RefreshAdapters();
        Assert.Equal(Vpn.Id, page.SelectedAdapter!.Id);
        Assert.Equal("Proton VPN (not there now)", page.SelectedAdapter.Label);
        Assert.Contains("Proton VPN, which is not connected", page.TorrentNetworkText);
        Assert.Equal(Vpn.Id, settings.Current.Engine.TorrentNetworkAdapter);   // (listing again changes nothing)

        page.SelectedAdapter = page.AdapterChoices[0];
        Assert.Null(settings.Current.Engine.TorrentNetworkAdapter);
    }

    [Fact]
    public void Settings_choose_the_encryption_and_whether_torrents_use_the_proxy()
    {
        using var dir = new TempDir();
        var settings = NewSettings(dir.Path);
        settings.Current.Engine.Proxy = ProxyMode.Manual;
        settings.Current.Engine.ProxyAddress = "socks5://127.0.0.1:1080";
        using var manager = new DownloadManager(settings.Current.Engine);
        var page = new DownloadSettingsViewModel(settings, () => manager.ApplySettings(), () => { }, manager: manager, adapters: () => Array.Empty<NetworkAdapterInfo>());

        Assert.Equal(TorrentEncryption.Prefer, page.SelectedEncryption.Value);
        page.SelectedEncryption = page.EncryptionChoices.Single(c => c.Value == TorrentEncryption.Require);
        Assert.Equal(TorrentEncryption.Require, settings.Current.Engine.TorrentEncryption);
        Assert.Equal(TorrentEncryption.Require, new SettingsService(dir.Path).Load().Engine.TorrentEncryption);   // saved

        Assert.True(page.TorrentUseProxy);
        Assert.Contains("through the proxy 127.0.0.1:1080", page.TorrentNetworkText);
        Assert.Contains("UDP trackers", page.TorrentNetworkText);
        page.TorrentUseProxy = false;
        Assert.False(settings.Current.Engine.TorrentUseProxy);
        Assert.Equal("Right now torrents go directly.", page.TorrentNetworkText);
    }

    [Fact]
    public async Task Banned_peers_are_listed_in_Settings_and_can_be_unbanned()
    {
        using var dir = new TempDir();
        var settings = NewSettings(dir.Path);
        using var manager = new DownloadManager(settings.Current.Engine);
        await manager.BanTorrentPeerAsync("203.0.113.7:51413");
        var page = new DownloadSettingsViewModel(settings, () => manager.ApplySettings(), () => { }, manager: manager, adapters: () => Array.Empty<NetworkAdapterInfo>());

        Assert.True(page.HasBanned);
        Assert.Contains("203.0.113.7", page.BannedText);
        page.UnbanPeersCommand.Execute(null);
        Assert.False(page.HasBanned);
        Assert.Empty(manager.BannedTorrentPeers);
    }

    static async Task<string> MakeTorrentAsync(string dir)
    {
        var source = Path.Combine(dir, "src", "Pack");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "a.bin"), new byte[200_000]);
        var creator = new TorrentCreator(TorrentType.V1Only) { Announces = { new List<string> { "http://tracker.example/announce" } } };
        var dict = await creator.CreateAsync(new TorrentFileSource(source));
        var path = Path.Combine(dir, "pack.torrent");
        File.WriteAllBytes(path, dict.Encode());
        return path;
    }

    [Fact]
    public async Task The_drawer_adds_and_takes_off_trackers_and_shows_the_swarm()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(Hermetic(dir.Path));
        var job = manager.Add(new AddDownloadRequest { Url = await MakeTorrentAsync(dir.Path), Mode = AddMode.Hold });
        var item = new DownloadItemViewModel(job, manager.Snapshot(job), null!);
        using var details = new DownloadDetailsViewModel(item, manager, new ToastService());
        await WaitFor(() => details.Trackers.Any(t => t.Address.StartsWith("http://tracker.example")), "the torrent's tracker");

        details.NewTrackers = "udp://tracker.one.example:1337/announce\nudp://tracker.two.example:6969/announce";
        await details.AddTrackersCommand.ExecuteAsync(null);
        Assert.Equal("", details.NewTrackers);
        Assert.Equal(2, job.Torrent!.AddedTrackers!.Count);
        await WaitFor(() => details.Trackers.Count(t => t.Address.StartsWith("udp://")) == 2, "the added trackers listed");

        var own = details.Trackers.First(t => t.Address.StartsWith("http://tracker.example"));
        await details.RemoveTrackerCommand.ExecuteAsync(own);
        Assert.Single(job.Torrent.RemovedTrackers!);
        Assert.DoesNotContain(details.Trackers, t => t.Address.StartsWith("http://tracker.example"));

        // not a tracker: nothing is added
        details.NewTrackers = "hello";
        await details.AddTrackersCommand.ExecuteAsync(null);
        Assert.Equal(2, job.Torrent.AddedTrackers.Count);
        Assert.Equal("hello", details.NewTrackers);

        // the engine's numbers: each tracker's counts, the whole swarm's, and the DHT cannot be taken off
        details.ApplyTorrent(new TorrentDetails(true, Array.Empty<TorrentPeer>(), Array.Empty<TorrentFileProgress>(),
            new[] { new TorrentTracker("udp://tracker.one.example:1337/announce", "Working", null, 1, 120, 30, 5000), new TorrentTracker("DHT", "Working (80 nodes)", null, 0) },
            Array.Empty<bool>(), 0, 0, false, SwarmSeeds: 120, SwarmLeechers: 30));
        Assert.Equal("120 sharing all of it  ·  30 downloading  ·  5,000 finished", details.Trackers[0].CountsText);
        Assert.False(details.Trackers.Single(t => t.Address == "DHT").CanRemove);
        Assert.Equal("The whole swarm, as the trackers count it: 120 sharing all of it  ·  30 downloading.", details.SwarmText);
        Assert.True(details.TorrentRunning);
        Assert.False(details.IsPrivateTorrent);
    }

    [Fact]
    public async Task The_drawer_bans_a_peer_and_refuses_a_peer_that_is_not_an_address()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(Hermetic(dir.Path));
        var job = manager.Add(new AddDownloadRequest { Url = await MakeTorrentAsync(dir.Path), Mode = AddMode.Hold });
        var item = new DownloadItemViewModel(job, manager.Snapshot(job), null!);
        var toasts = new ToastService();
        using var details = new DownloadDetailsViewModel(item, manager, toasts);

        await details.BanPeerCommand.ExecuteAsync(new TorrentPeerRow("[2001:db8::7]:51413"));
        Assert.Contains("2001:db8::7", manager.BannedTorrentPeers);
        Assert.Contains("Banned [2001:db8::7]", toasts.Current!.Message);

        details.NewPeer = "nonsense";
        await details.AddPeerCommand.ExecuteAsync(null);
        Assert.Equal(ToastKind.Error, toasts.Current!.Kind);
        details.NewPeer = "203.0.113.7:51413";
        await details.AddPeerCommand.ExecuteAsync(null);   // (it is paused: peers are only connected to while it runs)
        Assert.Contains("Start the torrent first", toasts.Current!.Message);
    }

    [Fact]
    public void The_card_tells_how_big_the_swarm_is()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(Hermetic(dir.Path));
        var job = manager.Add(new AddDownloadRequest { Url = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Debian", Mode = AddMode.Hold });
        var item = new DownloadItemViewModel(job, manager.Snapshot(job), null!);

        var s = manager.Snapshot(job) with
        {
            State = DownloadState.Downloading,
            Downloaded = 10,
            Total = 100,
            Torrent = new TorrentStatus(3, 1, 0, 0, 0, false, null, 1200, 340),
        };
        item.Update(s);
        Assert.Contains("swarm: 1,200 sharing, 340 downloading", item.DetailText);
        Assert.False(item.CanRecheck);   // (its files are not known yet)
    }
}
