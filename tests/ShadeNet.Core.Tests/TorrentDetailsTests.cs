using System.Net;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Torrents;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

/// <summary>
/// Torrents, the second part: what the details drawer reads (peers, files, trackers, pieces; live and from what was saved), choosing the
/// files again while it runs and after it finished, fetching in order, and a .torrent file that only comes with the browser's sign-in.
/// </summary>
public class TorrentDetailsTests
{
    static string Why(DownloadJob job) =>
        $"state {job.State} ({job.ErrorKind}) {job.Error}; done {job.Torrent?.BytesDone:N0} of {job.TotalSize:N0}";

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

    [Fact]
    public async Task The_details_show_the_peer_the_files_the_tracker_and_the_pieces_live_and_after_a_pause()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), new[] { ("a.bin", 2_000_000), ("b.bin", 1_000_000) }, pieceLength: 32 * 1024);
        var m = h.NewManager();
        var job = m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice(swarm.Metadata), SpeedLimit = 512 * 1024 });
        await WaitUntil(() => job.State == DownloadState.Downloading && job.Torrent!.BytesDone > 300_000, 30, "a part of it", () => Why(job));

        TorrentDetails live = TorrentDetails.Empty;
        await WaitUntil(() =>
        {
            live = m.GetTorrentDetailsAsync(job.Id).GetAwaiter().GetResult();
            return live.Peers.Count > 0;
        }, 15, "the seeder among the peers");

        Assert.True(live.Running);
        var peer = Assert.Single(live.Peers);
        Assert.True(peer.IsSeeder);
        Assert.Equal(1.0, peer.Progress, 2);
        Assert.StartsWith("127.0.0.1:", peer.Address);
        Assert.False(string.IsNullOrWhiteSpace(peer.Client));
        Assert.Equal(new[] { "a.bin", "b.bin" }, live.Files.Select(f => f.Path).OrderBy(p => p));
        Assert.Contains(live.Files, f => f.Progress is > 0);
        Assert.Contains(live.Trackers, t => t.Address.StartsWith(swarm.Announce.TrimEnd('/'), StringComparison.Ordinal));
        Assert.Contains(live.Trackers, t => t.Address == "DHT");
        Assert.Equal(swarm.Torrent.PieceCount, live.Pieces.Count);
        Assert.Contains(true, live.Pieces);
        Assert.Equal(swarm.Torrent.Size, live.Size);

        await PauseAsync(m, job);
        var saved = await m.GetTorrentDetailsAsync(job.Id);
        Assert.False(saved.Running);
        Assert.Empty(saved.Peers);
        Assert.Equal(swarm.Torrent.PieceCount, saved.Pieces.Count);
        Assert.True(saved.Pieces.Count(p => p) * 32 * 1024 >= 300_000 * 0.8, "the pieces on the disk were not read back from the resume data");
        Assert.Contains(saved.Trackers, t => t.Status.Contains("not running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_left_out_while_it_runs_is_not_downloaded_and_one_added_after_it_finished_is()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), new[] { ("1 small.bin", 400_000), ("2 big.bin", 4_000_000), ("3 small.bin", 400_000) },
            pieceLength: 32 * 1024);
        var m = h.NewManager();
        var order = swarm.Torrent.Files.Select(f => f.Path).ToList();
        var job = m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice(swarm.Metadata), SpeedLimit = 1024 * 1024 });
        await WaitUntil(() => job.State == DownloadState.Downloading, 30, "it to run", () => Why(job));

        // the big one is left out while it runs; the first small one goes first
        var priorities = order.Select(p => p == "2 big.bin" ? TorrentFilePriority.Skip : p == "1 small.bin" ? TorrentFilePriority.High : TorrentFilePriority.Normal).ToList();
        await m.SetTorrentFilesAsync(job.Id, priorities);
        Assert.Equal(800_000, job.TotalSize);
        await Done(job);
        Assert.Equal(800_000, job.TotalSize);
        var big = Path.Combine(job.TargetPath, "2 big.bin");
        Assert.False(File.Exists(big) && swarm.Expected("2 big.bin").AsSpan().SequenceEqual(ReadShared(big)), "the file left out was downloaded anyway");
        var details = await m.GetTorrentDetailsAsync(job.Id);
        Assert.Equal(TorrentFilePriority.High, details.Files.Single(f => f.Path == "1 small.bin").Priority);
        Assert.Equal(TorrentFilePriority.Skip, details.Files.Single(f => f.Path == "2 big.bin").Priority);

        // finished: the big one is asked for after all, so it downloads again (what it has stays)
        await m.SetTorrentFilesAsync(job.Id, order.Select(_ => TorrentFilePriority.Normal).ToList());
        Assert.NotEqual(DownloadState.Completed, job.State);
        m.SetJobSpeedLimit(job.Id, null);
        await Done(job);
        Assert.Equal(swarm.Torrent.Size, job.TotalSize);
        foreach (var f in order) Assert.True(swarm.Expected(f).AsSpan().SequenceEqual(ReadShared(Path.Combine(job.TargetPath, f))), f + " differs");

        await Assert.ThrowsAsync<ArgumentException>(() => m.SetTorrentFilesAsync(job.Id, order.Select(_ => TorrentFilePriority.Skip).ToList()));
    }

    [Fact]
    public async Task In_order_the_pieces_arrive_from_the_start()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), new[] { ("film.mkv", 6_000_000) }, pieceLength: 32 * 1024);
        var m = h.NewManager();
        var job = m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice(swarm.Metadata, Sequential: true), SpeedLimit = 1024 * 1024 });
        Assert.True(job.Torrent!.Sequential);
        await WaitUntil(() => job.Torrent!.BytesDone > 2_000_000, 30, "a third of it", () => Why(job));

        var pieces = (await m.GetTorrentDetailsAsync(job.Id)).Pieces;
        var have = pieces.Count(p => p);
        var prefix = pieces.TakeWhile(p => p).Count();
        // in order, nearly everything that is in is the start of the file (a piece or two still on their way leave a small gap)
        Assert.True(prefix >= have * 0.85, $"{have} pieces in, but only the first {prefix} without a gap");
        Assert.True((await m.GetTorrentDetailsAsync(job.Id)).Sequential);

        // and back to the usual way while it runs
        await m.SetTorrentSequentialAsync(job.Id, false);
        Assert.False(job.Torrent.Sequential);
        m.SetJobSpeedLimit(job.Id, null);
        await Done(job);
    }

    [Fact]
    public async Task A_torrent_file_that_needs_the_browsers_sign_in_is_fetched_with_it()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), new[] { ("x.bin", 700_000) });
        using var site = new HlsServer();
        site.Files["/dl/x.torrent"] = swarm.Metadata;
        site.Intercept = ctx =>
        {
            // a tracker site: the .torrent file only for a signed-in member
            if (ctx.Request.Headers["Cookie"]?.Contains("member=yes", StringComparison.Ordinal) == true) return false;
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            return true;
        };
        var m = h.NewManager();
        var url = site.Url("/dl/x.torrent");

        await Assert.ThrowsAnyAsync<Exception>(() => m.DescribeTorrentAsync(url));
        var info = await m.DescribeTorrentAsync(url, new RequestSpec(url, "https://tracker.example/details/1", "member=yes"));
        Assert.Equal(swarm.Torrent.Size, info.Size);

        // added before it was read (what the browser hands over when it starts at once): the download fetches it with the sign-in
        var job = await Done(m.Add(new AddDownloadRequest { Url = url, Cookies = "member=yes", Referer = "https://tracker.example/details/1", Torrent = new TorrentChoice() }));
        Assert.Equal(DownloadKind.Torrent, job.Kind);
        Assert.True(swarm.Expected("x.bin").AsSpan().SequenceEqual(ReadShared(job.TargetPath)));
        Assert.Contains(site.Requests, r => r.Path == "/dl/x.torrent" && r.Cookie?.Contains("member=yes") == true && r.Referer == "https://tracker.example/details/1");
    }
}
