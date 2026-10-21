using System.Diagnostics;
using ShadeNet.Core.Models;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

/// <summary>Pause, resume, restart, crash - and not stitching together two versions of a file.</summary>
[Trait(Cat.Category, Cat.Slow)]
public class ResumeTests
{
    static void Quick(Core.Engine.EngineSettings s) => s.RampUpInterval = TimeSpan.FromMilliseconds(300);

    static async Task<DownloadJob> StartAt(Harness h, DownloadManager m, string url, int conns, double fraction)
    {
        var probe = await m.ProbeAsync(new Net.RequestSpec(url));
        var job = m.Add(new AddDownloadRequest { Url = url, MaxConnections = conns }, probe);
        await ReachAsync(job, fraction);
        return job;
    }

    static async Task ResumeAndFinish(DownloadManager m, DownloadJob job)
    {
        m.Start(job.Id);
        await Finished(job, 90);
    }

    [Fact]
    public async Task Pause_then_resume_downloads_only_what_is_missing()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        const long size = 6 * MiB;
        var job = await StartAt(h, m, h.Server.FileUrl(size, name: "r1.bin", etag: "r1", kbps: 1024), 4, 0.35);
        await PauseAsync(m, job);

        var atPause = job.DownloadedBytes;
        var served = h.Server.BytesServed;
        Assert.InRange(atPause, 1, size - 1);
        Assert.True(job.Segments.Count >= 2, "the interesting case is a paused multi-segment download");

        await Task.Delay(800);
        Assert.True(h.Server.BytesServed - served < 64 * KiB, "the server kept sending while paused");

        await ResumeAndFinish(m, job);
        AssertByteExact(job, size);

        var overhead = h.Server.BytesServed - served - (size - atPause);
        Assert.True(overhead < size * 0.10, $"re-downloaded {overhead} bytes that were already on disk");
    }

    [Fact]
    public async Task Pausing_and_resuming_many_times_never_corrupts_the_file()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        const long size = 6 * MiB;
        var job = h.Add(m, h.Server.FileUrl(size, name: "yoyo.bin", etag: "y", kbps: 2048), 6);

        for (var i = 0; i < 5 && job.State != DownloadState.Completed; i++)
        {
            await WaitUntil(() => job.State is DownloadState.Downloading or DownloadState.Completed, 30, "downloading");
            await Task.Delay(300 + i * 100);
            if (job.State == DownloadState.Completed) break;
            await PauseAsync(m, job);
            m.Start(job.Id);
        }

        await Finished(job, 90);
        AssertByteExact(job, size);
    }

    [Fact]
    public async Task A_restarted_application_picks_up_where_it_left_off()
    {
        await using var h = await Harness.CreateAsync();
        var dir = h.Dir();
        var db = Path.Combine(dir, "jobs.db");
        const long size = 6 * MiB;

        var m1 = h.NewManager(dir, tweak: Quick, db: db);
        var job1 = await StartAt(h, m1, h.Server.FileUrl(size, name: "r2.bin", etag: "r2", kbps: 1024), 4, 0.40);
        await m1.StopAllAsync();
        m1.Dispose();

        var m2 = h.NewManager(dir, tweak: Quick, db: db);
        m2.Load();
        var job = Assert.Single(m2.Jobs);
        Assert.Equal(DownloadState.Paused, job.State);
        Assert.True(job.DownloadedBytes > 0 && job.DownloadedBytes < size);
        Assert.Equal(job1.FileName, job.FileName);

        var served = h.Server.BytesServed;
        var before = job.DownloadedBytes;
        await ResumeAndFinish(m2, job);
        AssertByteExact(job, size);
        Assert.True(h.Server.BytesServed - served - (size - before) < size * 0.10);
    }

    [Fact]
    public async Task Auto_resume_setting_restarts_interrupted_jobs_on_load()
    {
        await using var h = await Harness.CreateAsync();
        var dir = h.Dir();
        var db = Path.Combine(dir, "jobs.db");
        const long size = 4 * MiB;

        var m1 = h.NewManager(dir, tweak: Quick, db: db);
        await StartAt(h, m1, h.Server.FileUrl(size, name: "auto.bin", etag: "a", kbps: 1024), 2, 0.30);
        await m1.StopAllAsync();
        m1.Dispose();

        // A graceful stop records "Paused". A crash leaves "Downloading" behind - put the database in that state.
        using (var store = new Storage.JobStore(db))
        {
            var rec = Assert.Single(store.LoadAll());
            rec.State = DownloadState.Downloading;
            store.SaveJob(rec);
        }

        var m2 = h.NewManager(dir, tweak: s => { Quick(s); s.AutoResumeOnStartup = true; }, db: db);
        m2.Load();
        var job = Assert.Single(m2.Jobs);
        await Finished(job, 90);
        AssertByteExact(job, size);
    }

    [Fact]
    public async Task A_hard_killed_process_leaves_a_job_that_resumes_correctly()
    {
        await using var h = await Harness.CreateAsync();
        var dir = h.Dir();
        var db = Path.Combine(dir, "jobs.db");
        const long size = 12 * MiB;
        var url = h.Server.FileUrl(size, name: "r3.bin", etag: "r3", kbps: 1024);

        var cli = FindCli();
        var baseline = h.Server.BytesServed;
        var child = Process.Start(new ProcessStartInfo("dotnet", $"\"{cli}\" get \"{url}\" -o \"{dir}\" -c 4 --db \"{db}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        _ = child.StandardOutput.ReadToEndAsync();

        await WaitUntil(() => h.Server.BytesServed - baseline >= size * 0.45, 60, "child to download 45 %");
        await Task.Delay(500);
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();

        var m = h.NewManager(dir, tweak: Quick, db: db);
        m.Load();
        var job = Assert.Single(m.Jobs);
        Assert.Equal(DownloadState.Paused, job.State);
        Assert.True(File.Exists(job.PartPath));
        var onRecord = job.DownloadedBytes;
        Assert.True(onRecord > 0, "nothing was checkpointed before the kill");

        var served = h.Server.BytesServed;
        await ResumeAndFinish(m, job);
        AssertByteExact(job, size);
        Assert.True(h.Server.BytesServed - served < size - onRecord + size * 0.25);
    }

    static string FindCli()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            foreach (var cfg in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(dir.FullName, "tools", "ShadeNet.Cli", "bin", cfg, "net10.0", "ShadeNet.Cli.dll");
                if (File.Exists(candidate)) return candidate;
            }
        }

        throw new FileNotFoundException("ShadeNet.Cli.dll not found - build the whole solution first.");
    }

    [Fact]
    public async Task A_changed_remote_file_stops_the_resume_and_keeps_the_partial_data()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        var job = await StartAt(h, m, h.Server.FileUrl(5 * MiB, name: "r4.bin", etag: "r4", kbps: 1024), 2, 0.35);
        await PauseAsync(m, job);

        h.Server.Epoch = 1; // the file on the server is now a different version
        m.Start(job.Id);
        await Finished(job, 30);

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal(JobErrorKind.FileChanged, job.ErrorKind);
        Assert.True(File.Exists(job.PartPath));
    }

    [Fact]
    public async Task A_changed_file_without_an_etag_is_caught_by_the_size_check_on_refresh()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        var job = await StartAt(h, m, h.Server.FileUrl(5 * MiB, name: "size.bin", kbps: 1024, token: "a"), 2, 0.30);
        await PauseAsync(m, job);

        // "Refresh address" points at a file of a different size: must be refused.
        m.RefreshAddress(job.Id, h.Server.FileUrl(5 * MiB + 1, name: "size.bin", token: "b"));
        await Finished(job, 30);
        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal(JobErrorKind.FileChanged, job.ErrorKind);
    }

    [Fact]
    public async Task A_deleted_partial_file_means_start_over_but_stay_correct()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        const long size = 4 * MiB;
        var job = await StartAt(h, m, h.Server.FileUrl(size, name: "r5.bin", etag: "r5", kbps: 1024), 2, 0.35);
        await PauseAsync(m, job);

        File.Delete(job.PartPath);
        await ResumeAndFinish(m, job);
        AssertByteExact(job, size);
    }

    [Fact]
    public async Task Servers_without_range_support_restart_from_zero_on_resume()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        const long size = 4 * MiB;
        var job = await StartAt(h, m, h.Server.FileUrl(size, name: "r6.bin", ranges: false, kbps: 2048), 4, 0.40);
        await PauseAsync(m, job);

        var served = h.Server.BytesServed;
        await ResumeAndFinish(m, job);
        AssertByteExact(job, size);
        Assert.True(h.Server.BytesServed - served >= size, "without ranges the whole file has to come again");
    }

    [Fact]
    public async Task An_expired_link_fails_clearly_and_refresh_address_continues_the_same_file()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        const long size = 5 * MiB;
        var job = await StartAt(h, m, h.Server.FileUrl(size, name: "r7.bin", etag: "r7", kbps: 1024, token: "old"), 2, 0.35);
        await PauseAsync(m, job);
        var before = job.DownloadedBytes;

        h.Server.ValidTokens.Remove("old");
        m.Start(job.Id);
        await Finished(job, 30);
        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal(JobErrorKind.AddressExpired, job.ErrorKind);

        var served = h.Server.BytesServed;
        m.RefreshAddress(job.Id, h.Server.FileUrl(size, name: "r7.bin", etag: "r7", kbps: 1024, token: "new"));
        await Finished(job, 90);
        AssertByteExact(job, size);
        Assert.True(h.Server.BytesServed - served - (size - before) < size * 0.10, "refresh re-downloaded finished parts");
    }

    [Fact]
    public async Task Pausing_a_job_that_is_still_probing_is_safe()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        var job = h.Add(m, h.Server.FileUrl(2 * MiB, name: "early.bin", connLatencyMs: 1500), 2);
        await Task.Delay(300); // the probe is waiting on the slow first connection
        await PauseAsync(m, job);

        m.Start(job.Id);
        await Finished(job, 30);
        AssertByteExact(job, 2 * MiB);
    }
}
