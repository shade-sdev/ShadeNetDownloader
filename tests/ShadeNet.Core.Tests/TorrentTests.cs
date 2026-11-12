using System.Diagnostics;
using ShadeNet.Core.Models;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

/// <summary>
/// Torrents against a swarm on this machine (a tracker and a seeder on 127.0.0.1): magnet links and .torrent files, the files the user
/// chose, pause and resume across a restart, sharing until the ratio limit, and torrents that name paths outside their folder.
/// </summary>
public class TorrentTests
{
    static readonly (string, int)[] ThreeFiles = { ("a.bin", 3_000_000), ("sub\\b.bin", 1_500_000), ("c.txt", 1_000) };

    static string Why(DownloadJob job) =>
        $"state {job.State} ({job.ErrorKind}) {job.Error}; done {job.Torrent?.BytesDone:N0} of {job.TotalSize:N0}; seeding {job.Torrent?.Seeding}; uploaded {job.Torrent?.Uploaded:N0}";

    static async Task<DownloadJob> Done(DownloadJob job, double seconds = 60)
    {
        await WaitUntil(() => job.State is DownloadState.Completed or DownloadState.Failed, seconds, $"torrent '{job.FileName}' to finish", () => Why(job));
        Assert.True(job.State == DownloadState.Completed, Why(job));
        return job;
    }

    /// <summary>A file the torrent engine still has open (it is being shared).</summary>
    static byte[] ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        s.CopyTo(copy);
        return copy.ToArray();
    }

    static void AssertSameFiles(TorrentSwarm swarm, DownloadJob job, IEnumerable<string>? only = null)
    {
        foreach (var f in swarm.Torrent.Files.Where(f => only == null || only.Contains(f.Path)))
        {
            var path = job.Torrent!.IsFolder ? Path.Combine(job.TargetPath, f.Path) : job.TargetPath;
            Assert.True(File.Exists(path), $"{f.Path} is missing ({path})");
            Assert.True(swarm.Expected(f.Path).AsSpan().SequenceEqual(ReadShared(path)), $"{f.Path} differs from what was shared");
        }
    }

    [Fact]
    public async Task A_torrent_file_of_several_files_is_saved_as_a_folder_byte_exact()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), ThreeFiles);
        var m = h.NewManager();

        var job = await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice() }));

        Assert.Equal(DownloadKind.Torrent, job.Kind);
        Assert.Equal("Pack", job.FileName);
        Assert.True(job.Torrent!.IsFolder);
        Assert.Equal(swarm.Torrent.Size, job.TotalSize);
        AssertSameFiles(swarm, job);
        var snapshot = m.Snapshot(job);
        Assert.Equal(job.TotalSize, snapshot.Downloaded);
        Assert.NotNull(snapshot.Torrent);
    }

    [Fact]
    public async Task A_magnet_link_gets_its_file_list_from_the_peers()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), ThreeFiles, name: "Holiday photos");
        var m = h.NewManager();

        var job = m.Add(new AddDownloadRequest { Url = swarm.Magnet });
        Assert.Null(job.TotalSize);   // nothing is known until a peer sends the file list
        await Done(job);

        Assert.Equal("Holiday photos", job.FileName);
        Assert.Equal(3, job.Torrent!.Files!.Count);
        Assert.Equal(swarm.Torrent.Size, job.TotalSize);
        AssertSameFiles(swarm, job);
    }

    [Fact]
    public async Task Describing_a_magnet_link_and_a_torrent_file_lists_the_files()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), ThreeFiles);
        var m = h.NewManager();

        var fromFile = await m.DescribeTorrentAsync(swarm.TorrentFile);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var fromMagnet = await m.DescribeTorrentAsync(swarm.Magnet, cts.Token);

        foreach (var info in new[] { fromFile, fromMagnet })
        {
            Assert.Equal("Pack", info.Name);
            Assert.Equal(swarm.Torrent.Size, info.Size);
            Assert.Equal(swarm.Torrent.Files.Select(f => f.Path).OrderBy(p => p), info.Files.Select(f => f.Path).OrderBy(p => p));
            Assert.Equal(swarm.Torrent.InfoHashes.V1!.ToHex(), info.InfoHash, ignoreCase: true);
        }

        Assert.True(DownloadManager.IsTorrentAddress(swarm.Magnet));
        Assert.True(DownloadManager.IsTorrentAddress(swarm.TorrentFile));
        Assert.True(DownloadManager.IsTorrentAddress("https://example.org/linux.iso.torrent?x=1"));
        Assert.True(DownloadManager.IsTorrentAddress("https://example.org/get?id=4", "application/x-bittorrent"));
        Assert.False(DownloadManager.IsTorrentAddress("https://example.org/file.zip"));
    }

    [Fact]
    public async Task Only_the_chosen_files_are_downloaded()
    {
        await using var h = await Harness.CreateAsync();
        // a large file in the middle that is left out
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), new[] { ("1 first.bin", 600_000), ("2 big.bin", 6_000_000), ("3 last.bin", 700_000) });
        var m = h.NewManager();
        var order = swarm.Torrent.Files.Select(f => f.Path).ToList();
        var wanted = new[] { order.IndexOf("1 first.bin"), order.IndexOf("3 last.bin") };

        var job = await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice(swarm.Metadata, wanted) }));

        Assert.Equal(1_300_000, job.TotalSize);
        AssertSameFiles(swarm, job, new[] { "1 first.bin", "3 last.bin" });
        var big = Path.Combine(job.TargetPath, "2 big.bin");
        Assert.False(File.Exists(big) && swarm.Expected("2 big.bin").AsSpan().SequenceEqual(ReadShared(big)), "the file left out was downloaded anyway");
    }

    [Fact]
    public async Task A_torrent_of_one_file_is_saved_as_that_file()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), new[] { ("talk.mp4", 2_500_000) });
        var m = h.NewManager();

        var job = await Done(m.Add(new AddDownloadRequest { Url = swarm.Magnet, FileName = "my talk.mp4" }));

        Assert.False(job.Torrent!.IsFolder);
        Assert.Equal(Path.Combine(job.SaveDirectory, "my talk.mp4"), job.TargetPath);
        AssertSameFiles(swarm, job);
    }

    [Fact]
    public async Task A_paused_torrent_carries_on_after_a_restart()
    {
        await using var h = await Harness.CreateAsync();
        // 6 MB at 1 MB/s (its own limit): time to pause part-way
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), ThreeFiles.Append(("d.bin", 1_500_000)).ToArray(), pieceLength: 64 * 1024);
        var db = Path.Combine(h.Root, "jobs.db");
        var m1 = h.NewManager(db: db);

        var job = m1.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice(swarm.Metadata), SpeedLimit = 1024 * 1024 });
        await WaitUntil(() => job.Torrent!.BytesDone > 1_500_000, 30, "a part of it", () => Why(job));
        await PauseAsync(m1, job);
        var before = job.Torrent!.BytesDone;
        await m1.StopAllAsync();
        m1.Dispose();

        var m2 = h.NewManager(db: db);
        m2.Load();
        var again = m2.Find(job.Id)!;
        Assert.Equal(DownloadState.Paused, again.State);
        Assert.Equal(DownloadKind.Torrent, again.Kind);
        Assert.True(again.Torrent!.BytesDone >= before * 0.9, $"progress was not kept: {again.Torrent.BytesDone:N0} of {before:N0}");
        Assert.Equal(swarm.Torrent.Files.Count, again.Torrent.Files!.Count);

        m2.Start(again.Id);
        await Done(again);
        AssertSameFiles(swarm, again);
    }

    [Fact]
    public async Task A_finished_torrent_is_shared_until_the_ratio_limit()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), ThreeFiles);
        var m = h.NewManager(tweak: s =>
        {
            s.TorrentSeedRatio = 0.5;
            s.TorrentSeedMinutes = 0;
        });

        var job = await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice(swarm.Metadata) }));
        Assert.True(job.Torrent!.Seeding, Why(job));
        Assert.True(m.IsSeeding(job.Id));

        // the original seeder leaves: a newcomer can only get the pieces from ShadeNet
        await swarm.StopSeederAsync();
        var newcomer = await swarm.StartLeecherAsync(h.Dir("newcomer"));
        await WaitUntil(() => !job.Torrent.Seeding, 60, "sharing to stop at ratio 0.5", () => Why(job) + $"; newcomer {newcomer.Progress:0}% {newcomer.State}");

        Assert.False(m.IsSeeding(job.Id));
        Assert.True(job.Torrent.Uploaded >= swarm.Torrent.Size / 2, Why(job));
        Assert.Equal(DownloadState.Completed, job.State);
    }

    [Fact]
    public async Task With_sharing_off_a_finished_torrent_is_not_shared()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), ThreeFiles);
        var m = h.NewManager(tweak: s => s.TorrentSeedRatio = 0);

        var job = await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile }));

        Assert.False(job.Torrent!.Seeding);
        Assert.False(m.IsSeeding(job.Id));
    }

    [Fact]
    public async Task Stop_sharing_and_share_again()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), ThreeFiles);
        var m = h.NewManager();
        var job = await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile }));
        Assert.True(m.IsSeeding(job.Id));

        await m.StopSeedingAsync(job.Id);
        Assert.False(m.IsSeeding(job.Id));
        Assert.False(job.Torrent!.Seeding);

        Assert.True(await m.StartSeedingAsync(job.Id));
        Assert.True(m.IsSeeding(job.Id));

        // its files gone: it cannot be shared
        await m.StopSeedingAsync(job.Id);
        File.Delete(Path.Combine(job.TargetPath, "a.bin"));
        Assert.False(await m.StartSeedingAsync(job.Id));
    }

    [Fact]
    public async Task A_torrent_naming_a_path_outside_its_folder_writes_nothing_outside_it()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), ThreeFiles, seed: false);
        var hostile = TorrentSwarm.WithHostilePath(swarm.Metadata, "..", "..", "escaped.bin");
        var save = h.Dir("save");
        var m = h.NewManager(save);

        DownloadJob? job = null;
        try
        {
            job = m.Add(new AddDownloadRequest { Url = "evil.torrent", Torrent = new TorrentChoice(hostile) });
            await WaitUntil(() => job.State is DownloadState.Failed or DownloadState.Completed or DownloadState.Downloading, 30, "it to start or be refused", () => Why(job));
        }
        catch (Exception ex) when (ex is ArgumentException or ShadeNet.Core.Engine.DownloadException)
        {
            // refused when it was added: fine too
        }

        await Task.Delay(500);
        Assert.DoesNotContain(Directory.GetFiles(h.Root, "escaped.bin", SearchOption.AllDirectories),
            p => !p.StartsWith(Path.Combine(save, job?.FileName ?? "\0"), StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(save)!, "escaped.bin")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(save)!)!, "escaped.bin")));
        if (job != null && job.State == DownloadState.Failed) Assert.Contains("outside", job.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_same_torrent_is_not_added_twice()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), ThreeFiles, seed: false);
        var m = h.NewManager();
        var first = m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice(swarm.Metadata), Mode = AddMode.Hold });

        Assert.Throws<ArgumentException>(() => m.Add(new AddDownloadRequest { Url = swarm.Magnet, Mode = AddMode.Hold }));
        Assert.Same(first, m.FindDuplicate(swarm.Magnet + "&tr=udp%3A%2F%2Fother.example%3A80"));
    }

    [Fact]
    public async Task Removing_an_unfinished_torrent_deletes_its_files_and_what_the_engine_kept()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), ThreeFiles, pieceLength: 32 * 1024);
        var m = h.NewManager();
        var job = m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, Torrent = new TorrentChoice(swarm.Metadata), SpeedLimit = 512 * 1024 });
        await WaitUntil(() => job.Torrent!.BytesDone > 300_000, 30, "a part of it", () => Why(job));
        await PauseAsync(m, job);
        Assert.True(Directory.Exists(job.TargetPath));

        await m.RemoveAsync(job.Id, deleteFiles: false);

        Assert.False(Directory.Exists(job.TargetPath), "the unfinished files stayed");
        var cache = Path.Combine(h.Root, "torrent-cache");
        Assert.Empty(Directory.GetFiles(cache, job.Torrent!.InfoHash + ".*", SearchOption.AllDirectories));
    }

    [Fact]
    [Trait(Cat.Category, Cat.Slow)]
    public async Task A_speed_limit_holds_a_torrent_back()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), new[] { ("x.bin", 1_536_000) });
        var m = h.NewManager();
        var sw = Stopwatch.StartNew();

        await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile, SpeedLimit = 256 * 1024 }));

        Assert.True(sw.Elapsed.TotalSeconds >= 4, $"1.5 MB at 256 KB/s took only {sw.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    [Trait(Cat.Category, Cat.Slow)]
    public async Task A_torrent_paused_under_a_limit_does_not_hold_back_the_next_one()
    {
        // MonoTorrent serves the transfers that wait for a limit in one queue for the whole program: one left behind by a stopped
        // torrent used to block it, and every later torrent with a limit stalled at a few hundred kilobytes
        await using var h = await Harness.CreateAsync();
        await using var first = await TorrentSwarm.CreateAsync(h.Dir("swarm1"), new[] { ("a.bin", 3_000_000) });
        await using var second = await TorrentSwarm.CreateAsync(h.Dir("swarm2"), new[] { ("b.bin", 1_536_000) });
        var m = h.NewManager();

        var paused = m.Add(new AddDownloadRequest { Url = first.TorrentFile, SpeedLimit = 1024 * 1024 });
        await WaitUntil(() => paused.Torrent!.BytesDone > 1_000_000, 30, "a part of the first", () => Why(paused));
        await PauseAsync(m, paused);

        var next = await Done(m.Add(new AddDownloadRequest { Url = second.TorrentFile, SpeedLimit = 512 * 1024 }), 30);
        AssertSameFiles(second, next);
    }

    [Fact]
    [Trait(Cat.Category, Cat.Slow)]
    public async Task The_limit_of_all_downloads_holds_torrents_back_too()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), new[] { ("x.bin", 1_536_000) });
        var m = h.NewManager();
        m.SetGlobalSpeedLimit(256 * 1024, enabled: true);
        var sw = Stopwatch.StartNew();

        await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile }));

        Assert.True(sw.Elapsed.TotalSeconds >= 4, $"1.5 MB under a 256 KB/s total took only {sw.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    [Trait(Cat.Category, Cat.Slow)]
    public async Task The_upload_limit_holds_sharing_back()
    {
        await using var h = await Harness.CreateAsync();
        await using var swarm = await TorrentSwarm.CreateAsync(h.Dir("swarm"), new[] { ("x.bin", 1_536_000) });
        var m = h.NewManager(tweak: s =>
        {
            s.TorrentUploadLimit = 256 * 1024;
            s.TorrentSeedRatio = 5;
        });
        var job = await Done(m.Add(new AddDownloadRequest { Url = swarm.TorrentFile }));
        await swarm.StopSeederAsync();

        var sw = Stopwatch.StartNew();
        var newcomer = await swarm.StartLeecherAsync(h.Dir("newcomer"));
        await WaitUntil(() => newcomer.Complete, 60, "the newcomer to get it from ShadeNet", () => Why(job));

        Assert.True(sw.Elapsed.TotalSeconds >= 4, $"1.5 MB shared at 256 KB/s took only {sw.Elapsed.TotalSeconds:0.0} s");
    }
}
