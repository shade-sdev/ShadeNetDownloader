using System.Net;
using System.Net.NetworkInformation;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Connections;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Torrents;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

/// <summary>
/// Where torrents go: through ShadeNet's proxy (and nowhere else), bound to one network adapter (and nothing while it is gone), with
/// the encryption chosen, over IPv6 too; and what the user does by hand: trackers added and taken off, asking for peers now, checking
/// the files again, banning and adding peers, the swarm's size as the trackers count it.
/// </summary>
public class TorrentNetworkTests
{
    static readonly (string, int)[] OneFile = { ("data.bin", 2_000_000) };

    static string Why(DownloadJob job) =>
        $"state {job.State} ({job.ErrorKind}) {job.Error}; done {job.Torrent?.BytesDone:N0} of {job.TotalSize:N0}; seeding {job.Torrent?.Seeding}";

    static async Task<DownloadJob> Done(DownloadJob job, double seconds = 60)
    {
        await WaitUntil(() => job.State is DownloadState.Completed or DownloadState.Failed, seconds, $"torrent '{job.FileName}' to finish", () => Why(job));
        Assert.True(job.State == DownloadState.Completed, Why(job));
        return job;
    }

    static byte[] ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        s.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The same torrent (the same identity) with other trackers, a tier each.</summary>
    static byte[] WithTrackers(byte[] metadata, params string[] trackers)
    {
        var dict = BEncodedDictionary.Decode<BEncodedDictionary>(metadata);
        dict.Remove("announce");
        dict.Remove("announce-list");
        if (trackers.Length > 0)
        {
            var list = new BEncodedList();
            foreach (var t in trackers) list.Add(new BEncodedList { new BEncodedString(t) });
            dict["announce-list"] = list;
        }

        return dict.Encode();
    }

    static string LoopbackAdapter() =>
        NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback).Id;

    static async Task SeesNothingFor(DownloadJob job, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            Assert.True((job.Torrent?.BytesDone ?? 0) == 0 && job.State != DownloadState.Completed, "it downloaded: " + Why(job));
            await Task.Delay(100);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // The proxy
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Proxy_addresses_can_be_socks_with_a_sign_in()
    {
        Assert.Equal("socks5", HttpFetcher.TryParseProxy("socks5://127.0.0.1:1080")!.Scheme);
        Assert.Equal("socks5h", HttpFetcher.TryParseProxy("socks5h://proxy.example:9050")!.Scheme);
        Assert.Equal("http", HttpFetcher.TryParseProxy("proxy.example:8080")!.Scheme);
        Assert.Null(HttpFetcher.TryParseProxy("socks5://proxy.example"));   // a SOCKS proxy has no usual port
        Assert.Null(HttpFetcher.TryParseProxy("ftp://proxy.example:21"));

        var signed = HttpFetcher.TryParseProxy("socks5://me:p%40ss@127.0.0.1:1080")!;
        Assert.Equal(("me", "p@ss"), (HttpFetcher.CredentialsOf(signed)!.UserName, HttpFetcher.CredentialsOf(signed)!.Password));
        var web = HttpFetcher.WebProxyOf(HttpFetcher.TryParseProxy("socks5h://me:x@127.0.0.1:1080")!);
        Assert.Equal("socks5://127.0.0.1:1080/", web.Address!.ToString());   // the sign-in is the credentials, not the address
        Assert.NotNull(web.Credentials);
    }

    [Fact]
    public async Task Through_a_SOCKS5_proxy_peers_and_trackers_go_through_it_and_UDP_trackers_are_not_asked()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile);
        await using var proxy = TestProxy.Socks5("shade", "s3cret:x");
        using var udp = new UdpSink();
        var m = h.NewManager(tweak: s =>
        {
            s.Proxy = ProxyMode.Manual;
            s.ProxyAddress = proxy.Address;
            s.TorrentUseDht = true;   // (and the DHT is not used either: it cannot pass a proxy)
        });

        var job = await Done(m.Add(new AddDownloadRequest
        {
            Url = "proxied.torrent",
            Torrent = new TorrentChoice(WithTrackers(swarm.Metadata, udp.Announce, swarm.Announce)),
        }));

        Assert.Equal(swarm.Expected("data.bin"), ReadShared(job.TargetPath));
        var trackerPort = new Uri(swarm.Announce).Port;
        Assert.Contains($"127.0.0.1:{swarm.SeederPort}", proxy.Targets);   // the peer, through the proxy
        Assert.Contains($"127.0.0.1:{trackerPort}", proxy.Targets);        // the tracker, through the proxy
        Assert.Equal(0, proxy.Refused);
        Assert.Equal(0, udp.Packets);

        var details = await m.GetTorrentDetailsAsync(job.Id);
        Assert.True(details.Running);
        var udpRow = details.Trackers.Single(t => t.Address.StartsWith("udp:"));
        Assert.Equal("Not used", udpRow.Status);
        Assert.Contains("proxy", udpRow.Message);
        Assert.Contains("proxy", details.Trackers.Single(t => t.Address == "DHT").Status);
        Assert.Contains("through the proxy 127.0.0.1", m.DescribeTorrentNetwork());
        Assert.Contains("UDP trackers", m.DescribeTorrentNetwork());
    }

    [Fact]
    public async Task Through_an_HTTP_proxy_peers_get_a_CONNECT_tunnel_and_tracker_requests_go_through_it()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile);
        await using var proxy = TestProxy.Http();
        var m = h.NewManager(tweak: s =>
        {
            s.Proxy = ProxyMode.Manual;
            s.ProxyAddress = proxy.Address;
        });

        var job = await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice() }));

        Assert.Equal(swarm.Expected("data.bin"), ReadShared(job.TargetPath));
        Assert.Contains($"127.0.0.1:{swarm.SeederPort}", proxy.Targets);
        Assert.Contains($"127.0.0.1:{new Uri(swarm.Announce).Port}", proxy.Targets);
    }

    [Fact]
    public async Task With_the_proxy_off_for_torrents_they_connect_directly()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile);
        await using var proxy = TestProxy.Socks5();
        var m = h.NewManager(tweak: s =>
        {
            s.Proxy = ProxyMode.Manual;
            s.ProxyAddress = proxy.Address;
            s.TorrentUseProxy = false;
        });

        await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice() }));

        Assert.Empty(proxy.Targets);
        Assert.Equal("directly", m.DescribeTorrentNetwork());
    }

    // ------------------------------------------------------------------------------------------------------------
    // One network adapter
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Bound_to_an_adapter_that_is_not_there_nothing_goes_out_until_it_is()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile);
        var m = h.NewManager(tweak: s =>
        {
            s.TorrentNetworkAdapter = "{0BADC0DE-0000-4000-8000-000000000000}";
            s.TorrentNetworkAdapterName = "Test VPN";
        });

        JobSnapshot? last = null;
        m.JobUpdated += s => last = s;
        var job = m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice() });
        await WaitUntil(() => job.State == DownloadState.Downloading, 20, "it to start", () => Why(job));
        await SeesNothingFor(job, 5);
        Assert.Equal(0, swarm.ClientAnnounces);   // the tracker never heard of it
        Assert.Contains("Waiting for Test VPN", last?.Torrent?.Activity);
        Assert.Contains("Test VPN, which is not connected", m.DescribeTorrentNetwork());

        // the adapter "connects" (here: the loopback one, which is what the swarm is on)
        m.Settings.TorrentNetworkAdapter = LoopbackAdapter();
        m.ApplySettings();
        await Done(job);
        Assert.Equal(swarm.Expected("data.bin"), ReadShared(job.TargetPath));
        Assert.Contains("only through", m.DescribeTorrentNetwork());
        Assert.Contains("127.0.0.1", m.DescribeTorrentNetwork());
    }

    // ------------------------------------------------------------------------------------------------------------
    // Encryption
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Required_encryption_leaves_out_plain_peers_and_off_uses_only_plain_ones()
    {
        await using var h = await Harness.CreateAsync();
        await using var plain = await TorrentSwarm.CreateAsync(h.Dir("plain"), OneFile, seederEncryption: new[] { EncryptionType.PlainText });
        await using var any = await TorrentSwarm.CreateAsync(h.Dir("any"), OneFile, seederUploadLimit: 400_000);

        // required: the peer that cannot encrypt is not used
        var strict = h.NewManager(h.Dir("strict"), tweak: s => s.TorrentEncryption = TorrentEncryption.Require);
        var refused = strict.Add(new AddDownloadRequest { Url = plain.TorrentFile, Torrent = new TorrentChoice() });
        await WaitUntil(() => refused.State == DownloadState.Downloading, 20, "it to start", () => Why(refused));
        await SeesNothingFor(refused, 6);

        // required, with a peer that can: it is encrypted
        var encrypted = strict.Add(new AddDownloadRequest { Url = any.TorrentFile, Torrent = new TorrentChoice() });
        await WaitUntil(() => strict.GetTorrentDetailsAsync(encrypted.Id).Result.Peers.Count > 0, 30, "a peer", () => Why(encrypted));
        Assert.All((await strict.GetTorrentDetailsAsync(encrypted.Id)).Peers, p => Assert.True(p.Encrypted));
        await Done(encrypted);

        // off: plain only, which the plain peer is
        var open = h.NewManager(h.Dir("open"), tweak: s => s.TorrentEncryption = TorrentEncryption.Off);
        var job = await Done(open.Add(new AddDownloadRequest { Url = plain.TorrentFile, Torrent = new TorrentChoice() }));
        Assert.Equal(plain.Expected("data.bin"), ReadShared(job.TargetPath));
    }

    // ------------------------------------------------------------------------------------------------------------
    // IPv6, and a peer added by hand
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_peer_added_by_hand_over_IPv6_sends_the_torrent_and_peers_reach_ShadeNet_over_IPv6()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile, seederAddress: IPAddress.IPv6Loopback);
        var m = h.NewManager();

        // no tracker and no DHT: the only way to the seeder is the address given by hand
        var job = m.Add(new AddDownloadRequest { Url = "no-trackers.torrent", Torrent = new TorrentChoice(WithTrackers(swarm.Metadata)) });
        await WaitUntil(() => job.State == DownloadState.Downloading, 20, "it to start", () => Why(job));
        await Assert.ThrowsAsync<ArgumentException>(() => m.AddTorrentPeerAsync(job.Id, "not a peer"));
        await m.AddTorrentPeerAsync(job.Id, $"[::1]:{swarm.SeederPort}");
        await Done(job);
        Assert.Equal(swarm.Expected("data.bin"), ReadShared(job.TargetPath));

        // ShadeNet listens on IPv6 too: a client that only speaks IPv6 gets the torrent from it (the seeder is gone)
        await swarm.StopSeederAsync();
        Assert.True(m.IsSeeding(job.Id));
        var engine = swarm.NewEngine(h.Dir("v6-cache"), address: IPAddress.IPv6Loopback);
        var leecher = await engine.AddAsync(Torrent.Load(WithTrackers(swarm.Metadata)), h.Dir("v6-leecher"));
        await leecher.StartAsync();
        await leecher.AddPeerAsync(new PeerInfo(new Uri($"ipv6://[::1]:{m.TorrentListenPort}")));
        await WaitUntil(() => leecher.Complete, 60, "the IPv6 client to get it from ShadeNet", () => $"{leecher.State} {leecher.Progress:0}%");
        Assert.Equal(swarm.Expected("data.bin"), File.ReadAllBytes(Path.Combine(h.Dir("v6-leecher"), "data.bin")));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Trackers by hand, and the swarm's size
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Trackers_added_and_taken_off_are_kept_and_a_tracker_added_while_running_is_asked_at_once()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile);
        var m = h.NewManager();
        var job = m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice(), Mode = AddMode.Hold });

        await Assert.ThrowsAsync<ArgumentException>(() => m.AddTorrentTrackersAsync(job.Id, "nonsense"));
        Assert.Equal(1, await m.AddTorrentTrackersAsync(job.Id, "udp://127.0.0.1:9/announce"));
        Assert.Equal(0, await m.AddTorrentTrackersAsync(job.Id, "udp://127.0.0.1:9/announce"));   // known already
        await m.RemoveTorrentTrackerAsync(job.Id, swarm.Announce);

        var offline = await m.GetTorrentDetailsAsync(job.Id);
        Assert.Contains(offline.Trackers, t => t.Address == "udp://127.0.0.1:9/announce");
        Assert.DoesNotContain(offline.Trackers, t => t.Address == swarm.Announce);
        var saved = TorrentJobData.FromJson(job.Torrent!.ToJson());   // (as it is stored)
        Assert.Contains("udp://127.0.0.1:9/announce", saved.AddedTrackers!);
        Assert.Contains(saved.RemovedTrackers!, r => r.TrimEnd('/') == swarm.Announce.TrimEnd('/'));

        // without its tracker it finds no one
        m.Start(job.Id);
        await WaitUntil(() => job.State == DownloadState.Downloading, 20, "it to start", () => Why(job));
        await SeesNothingFor(job, 3);
        Assert.Equal(0, swarm.ClientAnnounces);

        // given back while it runs: asked at once
        Assert.Equal(1, await m.AddTorrentTrackersAsync(job.Id, swarm.Announce));
        await Done(job, 30);

        // "ask for peers now" asks again (the tracker would otherwise wait half an hour)
        var before = swarm.Announces;
        Assert.True(m.ReannounceTorrent(job.Id));
        await WaitUntil(() => swarm.Announces > before, 20, "the tracker to be asked again");
    }

    [Fact]
    public async Task A_private_torrent_takes_no_other_trackers()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile, seed: false);
        var dict = BEncodedDictionary.Decode<BEncodedDictionary>(swarm.Metadata);
        ((BEncodedDictionary)dict["info"])["private"] = new BEncodedNumber(1);
        var m = h.NewManager();
        var job = m.Add(new AddDownloadRequest { Url = "private.torrent", Torrent = new TorrentChoice(dict.Encode()), Mode = AddMode.Hold });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => m.AddTorrentTrackersAsync(job.Id, "udp://127.0.0.1:9/announce"));
        Assert.Contains("private", ex.Message);
    }

    [Fact]
    public async Task A_UDP_tracker_finds_the_peers_and_tells_how_big_the_swarm_is()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile, udpTracker: true);
        var m = h.NewManager();

        // before it is added: the trackers are asked how many share it
        var info = await m.DescribeTorrentAsync(swarm.TorrentFile);
        var count = await m.CountTorrentSwarmAsync(info);
        Assert.Equal((1, 1), (count.Answered, count.Asked));
        Assert.NotNull(count.Seeds);   // (MonoTorrent's tracker answers a UDP scrape with zeros; real trackers count)

        var job = await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice(info.Metadata) }));
        Assert.Equal(swarm.Expected("data.bin"), ReadShared(job.TargetPath));

        // while it is shared: the tracker's counts, on the card and in the drawer
        await WaitUntil(() => m.GetTorrentDetailsAsync(job.Id).Result.SwarmSeeds >= 1, 20, "the swarm's count");
        var tracker = (await m.GetTorrentDetailsAsync(job.Id)).Trackers.Single(t => t.Tier > 0);
        Assert.Equal("Working", tracker.Status);
        Assert.True(tracker.Seeds >= 1);
        Assert.True(m.Snapshot(job).Torrent!.SwarmSeeds >= 1);
    }

    [Fact]
    public async Task An_HTTP_tracker_tells_how_big_the_swarm_is_in_its_answers()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile);
        var m = h.NewManager();

        var job = await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice() }));

        m.ReannounceTorrent(job.Id);   // (the first answer came before ShadeNet had all of it)
        await WaitUntil(() => m.GetTorrentDetailsAsync(job.Id).Result.SwarmSeeds >= 1, 20, "the swarm's count");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Checking the files again
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Checking_a_finished_torrent_again_downloads_what_was_damaged()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile);
        var m = h.NewManager(tweak: s => s.TorrentSeedRatio = 0);   // (not shared after: nothing holds the file open)
        var job = await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice() }));

        // a few bytes on the disk go bad
        using (var f = new FileStream(job.TargetPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            f.Position = 1000;
            f.Write(new byte[4000]);
        }

        Assert.NotEqual(swarm.Expected("data.bin"), ReadShared(job.TargetPath));
        Assert.Equal(TorrentRecheck.Restarted, await m.RecheckTorrentAsync(job.Id));
        await WaitUntil(() => job.State != DownloadState.Completed, 5, "it to start");
        await Done(job);
        Assert.Equal(swarm.Expected("data.bin"), ReadShared(job.TargetPath));
    }

    [Fact]
    public async Task Checking_a_paused_torrent_again_waits_for_it_to_start()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile);
        var m = h.NewManager();
        var job = m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice(), Mode = AddMode.Hold });

        Assert.Equal(TorrentRecheck.OnNextStart, await m.RecheckTorrentAsync(job.Id));
        Assert.Equal(DownloadState.Paused, job.State);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Banned peers
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_banned_address_is_never_connected_to_until_it_is_unbanned()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile);
        var m = h.NewManager();
        await m.BanTorrentPeerAsync("127.0.0.1");
        Assert.Contains("127.0.0.1", m.BannedTorrentPeers);

        var job = m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice() });
        await WaitUntil(() => job.State == DownloadState.Downloading, 20, "it to start", () => Why(job));
        await WaitUntil(() => swarm.ClientAnnounces > 0, 20, "the tracker to be asked");
        await SeesNothingFor(job, 4);   // the tracker named the seeder, and the seeder came: neither way is it let in

        m.UnbanTorrentPeers();
        Assert.Empty(m.BannedTorrentPeers);
        m.ReannounceTorrent(job.Id);
        await Done(job);
    }

    [Fact]
    public async Task Banning_a_connected_peer_disconnects_it()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), new[] { ("slow.bin", 6_000_000) }, seederUploadLimit: 150_000);
        var m = h.NewManager();
        var job = m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice() });
        await WaitUntil(() => m.GetTorrentDetailsAsync(job.Id).Result.Peers.Count > 0, 30, "a peer", () => Why(job));

        var peer = (await m.GetTorrentDetailsAsync(job.Id)).Peers[0];
        await m.BanTorrentPeerAsync(peer.Address);
        await WaitUntil(() => m.GetTorrentDetailsAsync(job.Id).Result.Peers.Count == 0, 10, "the peer to be gone");
        await Task.Delay(2000);
        Assert.Empty((await m.GetTorrentDetailsAsync(job.Id)).Peers);   // and it does not come back
    }

    // ------------------------------------------------------------------------------------------------------------
    // What was shared stays counted across a restart of the torrent
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task What_a_shared_torrent_sent_stays_counted_when_it_is_restarted()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), OneFile);
        var m = h.NewManager(tweak: s => s.TorrentSeedRatio = 10);
        var job = await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice() }));

        // someone gets it from ShadeNet alone
        await swarm.StopSeederAsync();
        var leecher = await swarm.StartLeecherAsync(h.Dir("leecher"));
        await WaitUntil(() => leecher.Complete, 60, "the other client to get it", () => $"{leecher.State} {leecher.Progress:0}%");
        await WaitUntil(() => job.Torrent!.Uploaded >= 2_000_000, 10, "it to count what it sent", () => $"{job.Torrent!.Uploaded:N0}");
        var sent = job.Torrent!.Uploaded;

        // (switching to in-order fetching stops and starts it in the engine)
        await m.SetTorrentSequentialAsync(job.Id, true);
        await Task.Delay(2500);
        Assert.True(job.Torrent.Uploaded >= sent, $"{job.Torrent.Uploaded:N0} after, {sent:N0} before");
    }
}
