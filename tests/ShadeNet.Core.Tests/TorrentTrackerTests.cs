using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.BEncoding;
using ShadeNet.Core.Models;
using ShadeNet.Core.Torrents;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

/// <summary>
/// Trackers that are long gone, as magnet links found on the web are full of, must not hold a torrent up: MonoTorrent asks the trackers
/// of one tier one at a time (about 45 seconds for each that does not answer), so they are spread out and asked at once. And the DHT's
/// saved nodes are read back the way they were saved.
/// </summary>
public class TorrentTrackerTests
{
    /// <summary>Trackers that take a request and never answer (a UDP port that swallows packets, an HTTP port that accepts and stays silent).</summary>
    sealed class DeadTrackers : IDisposable
    {
        readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        readonly TcpListener _http = new(IPAddress.Loopback, 0);
        readonly List<TcpClient> _held = new();
        readonly CancellationTokenSource _stop = new();

        public DeadTrackers()
        {
            _http.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested) lock (_held) _held.Add(_http.AcceptTcpClientAsync(_stop.Token).AsTask().GetAwaiter().GetResult());
                }
                catch
                {
                    // stopped
                }
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested) await _udp.ReceiveAsync(_stop.Token);   // swallowed
                }
                catch
                {
                    // stopped
                }
            });
        }

        public string Udp => $"udp://127.0.0.1:{((IPEndPoint)_udp.Client.LocalEndPoint!).Port}/announce";
        public string Http => $"http://127.0.0.1:{((IPEndPoint)_http.LocalEndpoint).Port}/announce";

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
            _http.Stop();
            lock (_held) foreach (var c in _held) c.Dispose();
        }
    }

    [Fact]
    public async Task A_magnet_link_whose_first_trackers_are_dead_gets_its_file_list_from_the_one_that_answers()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), new[] { ("a.bin", 600_000), ("b.bin", 300_000) });
        using var dead = new DeadTrackers();
        // the dead ones first, the live one last (as on the web: most listed trackers are gone)
        var magnet = $"magnet:?xt=urn:btih:{swarm.Torrent.InfoHashes.V1!.ToHex()}&dn=Pack"
                     + $"&tr={Uri.EscapeDataString(dead.Udp)}&tr={Uri.EscapeDataString(dead.Http)}&tr={Uri.EscapeDataString(dead.Udp.Replace("/announce", "/x"))}"
                     + $"&tr={Uri.EscapeDataString(swarm.Announce)}";
        var m = h.NewManager();
        var sw = Stopwatch.StartNew();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var info = await m.DescribeTorrentAsync(magnet, cts.Token);

        Assert.Equal(swarm.Torrent.Size, info.Size);
        Assert.True(sw.Elapsed.TotalSeconds < 15, $"the file list took {sw.Elapsed.TotalSeconds:0.0} s: the dead trackers were waited for");

        // and the download that follows asks all of them at once too (the file list keeps a tier per tracker)
        var saved = Torrent.Load(info.Metadata);
        Assert.True(saved.AnnounceUrls.Count >= 4, $"{saved.AnnounceUrls.Count} tier(s): {string.Join(" | ", saved.AnnounceUrls.Select(t => string.Join(", ", t)))}");
        Assert.All(saved.AnnounceUrls, tier => Assert.Single(tier));
    }

    [Fact]
    public async Task A_torrent_whose_one_tier_starts_with_dead_trackers_still_downloads_at_once()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), new[] { ("x.bin", 900_000) });
        using var dead = new DeadTrackers();
        // the same torrent, with its trackers in ONE tier: two dead ones before the live one
        var dict = BEncodedDictionary.Decode<BEncodedDictionary>(swarm.Metadata);
        dict.Remove("announce");
        dict["announce-list"] = new BEncodedList { new BEncodedList { new BEncodedString(dead.Udp), new BEncodedString(dead.Http), new BEncodedString(swarm.Announce) } };
        var oneTier = dict.Encode();
        var m = h.NewManager();
        var sw = Stopwatch.StartNew();

        var job = m.Add(new AddDownloadRequest { Url = "one-tier.torrent", Torrent = new TorrentChoice(oneTier) });
        await WaitUntil(() => job.State is DownloadState.Completed or DownloadState.Failed, 60, "it to finish", () => $"{job.State} {job.Error}");

        Assert.Equal(DownloadState.Completed, job.State);
        Assert.True(sw.Elapsed.TotalSeconds < 15, $"it took {sw.Elapsed.TotalSeconds:0.0} s: the dead trackers were waited for");
    }

    [Fact]
    public void The_DHTs_saved_nodes_are_read_as_MonoTorrent_saved_them()
    {
        var a = Enumerable.Range(0, 26).Select(i => (byte)i).ToArray();
        var b = Enumerable.Range(100, 26).Select(i => (byte)i).ToArray();

        // MonoTorrent saves a bencoded list of 26-byte records ...
        var list = new BEncodedList { new BEncodedString(a), new BEncodedString(b) }.Encode();
        Assert.Equal(new[] { a, b }, TorrentSession.CompactNodesOf(list));

        // ... and what ShadeNet writes back is the plain records it reads
        Assert.Equal(new[] { a, b }, TorrentSession.CompactNodesOf(a.Concat(b).ToArray()));
        Assert.Empty(TorrentSession.CompactNodesOf(Array.Empty<byte>()));
    }
}
