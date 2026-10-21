using System.Collections.Concurrent;
using System.Diagnostics;
using ShadeNet.Core.Models;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

public class ManagerTests
{
    [Fact]
    public async Task Same_file_name_gets_a_counter_even_before_the_first_download_starts()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var a = h.Add(m, h.Server.FileUrl(1000, name: "same.bin"), fileName: "same.bin", mode: AddMode.Hold);
        var b = h.Add(m, h.Server.FileUrl(1000, name: "same.bin"), fileName: "same.bin", mode: AddMode.Hold);
        var c = h.Add(m, h.Server.FileUrl(1000, name: "same.bin"), fileName: "same.bin", mode: AddMode.Hold);

        Assert.Equal(new[] { "same.bin", "same (1).bin", "same (2).bin" }, new[] { a.FileName, b.FileName, c.FileName });
    }

    [Fact]
    public async Task An_existing_file_is_never_overwritten()
    {
        await using var h = await Harness.CreateAsync();
        var dir = h.Dir();
        File.WriteAllText(Path.Combine(dir, "keep.bin"), "precious");
        var job = h.Add(h.NewManager(dir), h.Server.FileUrl(500, name: "keep.bin"), fileName: "keep.bin");
        await Finished(job);

        Assert.Equal("keep (1).bin", job.FileName);
        Assert.Equal("precious", File.ReadAllText(Path.Combine(dir, "keep.bin")));
        AssertByteExact(job, 500);
    }

    [Fact]
    public async Task Duplicate_urls_can_be_detected()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var url = h.Server.FileUrl(1000, name: "d.bin");
        Assert.Null(m.FindDuplicate(url));
        var job = h.Add(m, url, mode: AddMode.Hold);
        Assert.Same(job, m.FindDuplicate(url));
        Assert.Same(job, m.FindDuplicate(url.ToUpperInvariant()));
        Assert.Null(m.FindDuplicate(url + "&other=1"));
    }

    [Fact]
    public async Task Hold_mode_adds_a_paused_job_that_does_not_start_by_itself()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var job = h.Add(m, h.Server.FileUrl(2000, name: "hold.bin"), mode: AddMode.Hold);
        await Task.Delay(500);
        Assert.Equal(DownloadState.Paused, job.State);
        Assert.Equal(0, h.Server.RequestCount);

        m.Start(job.Id);
        await Finished(job);
        AssertByteExact(job, 2000);
    }

    [Fact]
    public async Task The_concurrent_job_limit_is_respected_by_the_queue()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.MaxConcurrentJobs = 2);
        var jobs = Enumerable.Range(0, 5)
            .Select(i => h.Add(m, h.Server.FileUrl(1 * MiB, name: $"q{i}.bin", kbps: 1024), 1, mode: AddMode.Queue))
            .ToList();

        var peak = 0;
        var sw = Stopwatch.StartNew();
        while (jobs.Any(j => j.State is not (DownloadState.Completed or DownloadState.Failed)) && sw.Elapsed.TotalSeconds < 60)
        {
            peak = Math.Max(peak, jobs.Count(j => j.State is DownloadState.Probing or DownloadState.Downloading));
            await Task.Delay(10);
        }

        Assert.All(jobs, j => AssertByteExact(j, 1 * MiB));
        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task Queued_jobs_start_in_priority_order()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.MaxConcurrentJobs = 1);
        var order = new ConcurrentQueue<string>();
        m.JobCompleted += j => order.Enqueue(j.FileName);

        var blocker = h.Add(m, h.Server.FileUrl(1 * MiB, name: "blocker.bin", kbps: 1024), 1, mode: AddMode.Queue);
        await WaitUntil(() => blocker.State == DownloadState.Downloading);

        var low = h.Add(m, h.Server.FileUrl(50_000, name: "low.bin"), mode: AddMode.Hold);
        var mid = h.Add(m, h.Server.FileUrl(50_000, name: "mid.bin"), mode: AddMode.Hold);
        var high = h.Add(m, h.Server.FileUrl(50_000, name: "high.bin"), mode: AddMode.Hold);
        low.Priority = 0;
        mid.Priority = 3;
        high.Priority = 9;
        m.Enqueue(low.Id);
        m.Enqueue(mid.Id);
        m.Enqueue(high.Id);

        await Finished(low);
        await Finished(mid);
        await Finished(high);
        Assert.Equal(new[] { "blocker.bin", "high.bin", "mid.bin", "low.bin" }, order.ToArray());
    }

    [Fact]
    public async Task Pause_all_and_resume_all_cover_every_job()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.MaxConcurrentJobs = 3);
        var jobs = Enumerable.Range(0, 3)
            .Select(i => h.Add(m, h.Server.FileUrl(3 * MiB, name: $"p{i}.bin", kbps: 1024), 1))
            .ToList();
        await Task.WhenAll(jobs.Select(j => ReachAsync(j, 0.1)));

        m.PauseAll();
        await WaitUntil(() => jobs.All(j => j.State == DownloadState.Paused), 15, "all paused");

        m.ResumeAll();
        foreach (var j in jobs) await Finished(j);
        Assert.All(jobs, j => AssertByteExact(j, 3 * MiB));
    }

    [Fact]
    public async Task Starting_twice_does_not_run_the_job_twice()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var job = h.Add(m, h.Server.FileUrl(2 * MiB, name: "twice.bin", kbps: 2048), 1, mode: AddMode.Hold);
        m.Start(job.Id);
        m.Start(job.Id);
        m.Start(job.Id);
        await Finished(job);

        AssertByteExact(job, 2 * MiB);
        Assert.True(h.Server.BytesServed < 2 * MiB + 64 * KiB, $"served {h.Server.BytesServed} bytes for a 2 MiB file");
    }

    [Fact]
    public async Task Removing_a_running_job_stops_it_and_deletes_the_partial_file()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var removed = new TaskCompletionSource<Guid>();
        m.JobRemoved += id => removed.TrySetResult(id);

        var job = h.Add(m, h.Server.FileUrl(50 * MiB, name: "big.bin", kbps: 1024), 2);
        await ReachAsync(job, 0.01);
        Assert.True(File.Exists(job.PartPath));

        var sw = Stopwatch.StartNew();
        await m.RemoveAsync(job.Id, deleteFiles: true);

        Assert.True(sw.Elapsed.TotalSeconds < 3, $"removal took {sw.Elapsed.TotalSeconds:0.0}s");
        Assert.Equal(job.Id, await removed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(File.Exists(job.PartPath));
        Assert.Empty(m.Jobs);
    }

    [Fact]
    public async Task Removing_an_unfinished_job_never_leaves_its_partial_file_behind()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var job = h.Add(m, h.Server.FileUrl(20 * MiB, name: "half.bin", kbps: 1024), 2);
        await ReachAsync(job, 0.05);
        await PauseAsync(m, job);
        Assert.True(File.Exists(job.PartPath));

        await m.RemoveAsync(job.Id, deleteFiles: false); // "remove from list" only
        Assert.False(File.Exists(job.PartPath));
    }

    [Fact]
    public async Task Removing_a_completed_job_can_keep_or_delete_the_file()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var keep = await Download(h, m, h.Server.FileUrl(1000, name: "keep.bin"));
        var drop = await Download(h, m, h.Server.FileUrl(1000, name: "drop.bin"));

        await m.RemoveAsync(keep.Id, deleteFiles: false);
        await m.RemoveAsync(drop.Id, deleteFiles: true);

        Assert.True(File.Exists(keep.TargetPath));
        Assert.False(File.Exists(drop.TargetPath));
    }

    [Fact]
    public async Task Progress_snapshots_are_monotonic_and_end_complete()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var snaps = new ConcurrentQueue<JobSnapshot>();
        m.JobUpdated += snaps.Enqueue;

        var job = h.Add(m, h.Server.FileUrl(4 * MiB, name: "snap.bin", kbps: 2048), 1);
        await Finished(job);
        await Task.Delay(100);

        var list = snaps.Where(s => s.Id == job.Id).ToList();
        Assert.NotEmpty(list);
        long last = 0;
        foreach (var s in list.Where(s => s.State == DownloadState.Downloading))
        {
            Assert.True(s.Downloaded >= last, "progress went backwards");
            Assert.True(s.Downloaded <= 4 * MiB);
            last = s.Downloaded;
        }

        var final = list[^1];
        Assert.Equal(DownloadState.Completed, final.State);
        Assert.Equal(4 * MiB, final.Downloaded);
        Assert.Equal(1.0, final.Fraction);
        Assert.Contains(list, s => s.State == DownloadState.Downloading && s.Speed > 0);
    }

    [Fact]
    public async Task A_failed_job_can_be_retried_and_the_error_is_cleared()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var url = h.Server.FileUrl(100_000, name: "flaky.bin", token: "t1");
        var job = h.Add(m, url, mode: AddMode.Hold);
        h.Server.ValidTokens.Remove("t1");
        m.Start(job.Id);
        await Finished(job);
        Assert.Equal(DownloadState.Failed, job.State);
        Assert.NotNull(job.Error);

        h.Server.ValidTokens.Add("t1");
        m.Enqueue(job.Id);
        await Finished(job);
        Assert.Equal(DownloadState.Completed, job.State);
        Assert.Null(job.Error);
        AssertByteExact(job, 100_000);
    }

    [Fact]
    [Trait(Cat.Category, Cat.Slow)]
    public async Task The_per_job_connection_cap_is_honoured()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.RampUpInterval = TimeSpan.FromMilliseconds(300));
        var job = h.Add(m, h.Server.FileUrl(12 * MiB, name: "cap.bin", kbps: 1024), connections: 2);
        await Finished(job, 90);
        AssertByteExact(job, 12 * MiB);
        Assert.InRange(job.Stats.PeakConnections, 1, 2);
    }

    [Fact]
    [Trait(Cat.Category, Cat.Slow)]
    public async Task A_host_exception_limits_connections_below_what_the_job_asked_for()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s =>
        {
            s.RampUpInterval = TimeSpan.FromMilliseconds(300);
            s.HostConnectionLimits["127.0.0.1"] = 2;
        });
        var job = h.Add(m, h.Server.FileUrl(12 * MiB, name: "host.bin", kbps: 1024), connections: 8);
        await Finished(job, 90);
        AssertByteExact(job, 12 * MiB);
        Assert.InRange(job.Stats.PeakConnections, 1, 2);
    }

    [Fact]
    public async Task Max_connections_is_clamped_to_a_sane_range()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var job = h.Add(m, h.Server.FileUrl(100, name: "c.bin"), mode: AddMode.Hold);
        m.SetMaxConnections(job.Id, 0);
        Assert.Equal(1, job.MaxConnections);
        m.SetMaxConnections(job.Id, 500);
        Assert.Equal(32, job.MaxConnections);
        m.SetMaxConnections(job.Id, 6);
        Assert.Equal(6, job.MaxConnections);
    }
}
