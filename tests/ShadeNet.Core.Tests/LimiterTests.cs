using System.Diagnostics;
using ShadeNet.Core.Models;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

/// <summary>
/// Measures what the speed limiter really does, against an unthrottled server (so any rate is the limiter's doing).
/// </summary>
[Trait(Cat.Category, Cat.Slow)]
public class LimiterTests
{
    const long Big = 1024 * MiB;

    static async Task<double> RateOf(Func<long> bytes, double warmup = 1.5, double window = 3.0)
    {
        await Task.Delay(TimeSpan.FromSeconds(warmup));
        var b0 = bytes();
        var sw = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(window));
        return (bytes() - b0) / sw.Elapsed.TotalSeconds;
    }

    static DownloadJob Start(Harness h, DownloadManager m, string name, int conns, long? limit = null, long size = Big, int? kbps = null) =>
        h.Add(m, h.Server.FileUrl(size, name: name, kbps: kbps), conns, limit, fileName: name);

    [Theory]
    [InlineData(256 * KiB)]
    [InlineData(2 * MiB)]
    [InlineData(8 * MiB)]
    public async Task The_global_limit_is_accurate(long target)
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        m.SetGlobalSpeedLimit(target, true);
        var job = Start(h, m, "g.bin", 4);

        var measured = await RateOf(() => job.DownloadedBytes);
        Assert.InRange(measured, target * 0.92, target * 1.08);
    }

    [Fact]
    public async Task A_per_job_limit_holds_while_another_job_runs_free()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var capped = Start(h, m, "a.bin", 2, 512 * KiB);
        var free = Start(h, m, "b.bin", 2);

        var ra = RateOf(() => capped.DownloadedBytes);
        var rb = RateOf(() => free.DownloadedBytes);
        var (a, b) = (await ra, await rb);

        Assert.InRange(a, 512 * KiB * 0.90, 512 * KiB * 1.08);
        Assert.True(b > 20 * a, $"the unlimited job only reached {b / MiB:0.0} MiB/s");
    }

    [Theory]
    [InlineData(2 * MiB, 512 * KiB)]
    [InlineData(512 * KiB, 2 * MiB)]
    public async Task The_stricter_of_global_and_per_job_limits_wins(long global, long perJob)
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        m.SetGlobalSpeedLimit(global, true);
        var job = Start(h, m, "w.bin", 2, perJob);

        var expected = Math.Min(global, perJob);
        var measured = await RateOf(() => job.DownloadedBytes);
        Assert.InRange(measured, expected * 0.92, expected * 1.08);
    }

    [Fact]
    public async Task Changing_the_limit_takes_effect_within_about_a_second_in_both_directions()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var job = Start(h, m, "live.bin", 4, size: 8 * Big);
        await Task.Delay(1000);

        m.SetGlobalSpeedLimit(300 * KiB, true);
        var slow = await RateOf(() => job.DownloadedBytes, warmup: 1.0, window: 3.0);
        Assert.InRange(slow, 300 * KiB * 0.90, 300 * KiB * 1.10);

        m.SetGlobalSpeedLimit(3 * MiB, true);
        var mid = await RateOf(() => job.DownloadedBytes, warmup: 1.0, window: 3.0);
        Assert.InRange(mid, 3 * MiB * 0.92, 3 * MiB * 1.08);

        m.SetGlobalSpeedLimit(0, false);
        var free = await RateOf(() => job.DownloadedBytes, warmup: 0.5, window: 0.5);
        Assert.True(free > 30 * MiB, $"after removing the limit only {free / MiB:0.0} MiB/s");
    }

    [Fact]
    public async Task A_per_job_limit_can_be_changed_while_running()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var job = Start(h, m, "pj.bin", 2, 400 * KiB, size: 4 * Big);
        var before = await RateOf(() => job.DownloadedBytes);
        m.SetJobSpeedLimit(job.Id, 1600 * KiB);
        var after = await RateOf(() => job.DownloadedBytes, warmup: 1.0);

        Assert.InRange(before, 400 * KiB * 0.9, 400 * KiB * 1.1);
        Assert.InRange(after, 1600 * KiB * 0.9, 1600 * KiB * 1.1);
    }

    [Theory]
    [InlineData(20 * KiB)]
    [InlineData(2 * KiB)]
    public async Task Pause_stays_instant_even_when_heavily_throttled(long limit)
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        m.SetGlobalSpeedLimit(limit, true);
        var job = Start(h, m, "slowpause.bin", 2);
        await Task.Delay(1500);

        var sw = Stopwatch.StartNew();
        m.Pause(job.Id);
        await WaitUntil(() => job.State == DownloadState.Paused, 10, "pause");
        Assert.True(sw.ElapsedMilliseconds < 500, $"pause took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_tiny_limit_keeps_the_job_to_two_connections_and_the_rate_exact()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: 8, tweak: s => s.RampUpInterval = TimeSpan.FromMilliseconds(300));
        m.SetGlobalSpeedLimit(8 * KiB, true);
        var job = Start(h, m, "tiny.bin", 8);

        var measured = await RateOf(() => job.DownloadedBytes, warmup: 2.0, window: 6.0);
        Assert.InRange(measured, 8 * KiB * 0.80, 8 * KiB * 1.20);
        Assert.InRange(job.Stats.PeakConnections, 1, 2);
    }

    [Fact]
    public async Task Ramp_up_stops_once_the_limit_is_saturated()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: 8, tweak: s => s.RampUpInterval = TimeSpan.FromMilliseconds(300));
        m.SetGlobalSpeedLimit(2 * MiB, true);
        var job = Start(h, m, "sat.bin", 8);
        await Task.Delay(5000);
        Assert.True(job.Stats.PeakConnections <= 2, $"opened {job.Stats.PeakConnections} connections for a saturated limit");
    }

    [Fact]
    public async Task Several_connections_share_one_global_limit()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: 8, tweak: s => s.RampUpInterval = TimeSpan.FromMilliseconds(500));
        m.SetGlobalSpeedLimit(3 * MiB / 2, true);
        // The server caps each connection at 512 KiB/s, so the engine needs ~3-4 connections to approach the limit.
        var job = Start(h, m, "shared.bin", 8, kbps: 512);

        var measured = await RateOf(() => job.DownloadedBytes, warmup: 6.0, window: 4.0);
        Assert.True(job.Stats.PeakConnections >= 3, $"peak connections {job.Stats.PeakConnections}");
        Assert.InRange(measured, 1.5 * MiB * 0.92, 1.5 * MiB * 1.08);
    }

    [Fact]
    public async Task A_limited_download_is_byte_exact_and_takes_about_size_over_limit()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        m.SetGlobalSpeedLimit(2 * MiB, true);
        var sw = Stopwatch.StartNew();
        var job = Start(h, m, "timed.bin", 4, size: 3 * MiB);
        await Finished(job, 30);

        AssertByteExact(job, 3 * MiB);
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.1, 2.4);
    }
}
