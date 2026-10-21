using System.Diagnostics;
using ShadeNet.Core.Models;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

/// <summary>Multi-connection behaviour against a server that caps each connection (so more connections = faster).</summary>
[Trait(Cat.Category, Cat.Slow)]
public class SegmentationTests
{
    static void Quick(Core.Engine.EngineSettings s) => s.RampUpInterval = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Several_connections_beat_one_when_the_server_caps_each_connection()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        var sw = Stopwatch.StartNew();
        var job = await Download(h, m, h.Server.FileUrl(6 * MiB, name: "fast.bin", kbps: 1024), 8, 60);

        AssertByteExact(job, 6 * MiB);
        Assert.True(job.Stats.PeakConnections >= 2, $"peak connections {job.Stats.PeakConnections}");
        Assert.True(job.Stats.Splits >= 1);
        Assert.True(sw.Elapsed.TotalSeconds < 6.0 * 0.75, $"took {sw.Elapsed.TotalSeconds:0.0}s; one connection needs 6 s");
    }

    [Fact]
    public async Task A_finished_warm_connection_takes_over_ranges_whose_connection_is_still_handshaking()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        // Every NEW tcp connection costs 2.5 s, reused ones are free: the cheap way to finish is to reuse.
        var job = await Download(h, m, h.Server.FileUrl(8 * MiB, name: "takeover.bin", kbps: 2048, connLatencyMs: 2500), 8, 90);

        AssertByteExact(job, 8 * MiB);
        Assert.True(job.Stats.Takeovers >= 1, $"takeovers {job.Stats.Takeovers}");
    }

    [Fact]
    public async Task Corrupt_data_on_a_second_connection_is_caught_by_the_overlap_check()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        var job = await Download(h, m, h.Server.FileUrl(8 * MiB, name: "corrupt.bin", kbps: 2048, corrupt: true), 8, 90);

        AssertByteExact(job, 8 * MiB);
        Assert.Equal(1, job.Stats.Fallbacks);
        Assert.Equal(1, job.MaxConnections);
    }

    [Fact]
    public async Task A_server_that_lies_about_ranges_is_detected_and_downgraded()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        var job = await Download(h, m, h.Server.FileUrl(8 * MiB, name: "lie.bin", kbps: 2048, lie: true), 8, 90);

        AssertByteExact(job, 8 * MiB);
        Assert.Equal(1, job.Stats.Fallbacks);
        Assert.False(job.AcceptsRanges);
    }

    [Fact]
    public async Task Small_files_are_not_split()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        var job = await Download(h, m, h.Server.FileUrl(400 * KiB, name: "small.bin", kbps: 1024), 8);
        AssertByteExact(job, 400 * KiB);
        Assert.Equal(0, job.Stats.Splits);
        Assert.Equal(1, job.Stats.PeakConnections);
    }

    [Fact]
    public async Task The_finished_segment_list_covers_the_file_exactly()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: Quick);
        var job = await Download(h, m, h.Server.FileUrl(6 * MiB, name: "cover.bin", kbps: 1024), 8, 60);

        AssertByteExact(job, 6 * MiB);
        Assert.True(job.Segments.Count >= 2);
        Assert.Equal(0, job.Segments[0].Start);
        for (var i = 0; i < job.Segments.Count; i++)
        {
            var s = job.Segments[i];
            Assert.Equal(SegmentState.Done, s.State);
            Assert.Equal(s.Length, s.Downloaded);
            if (i > 0) Assert.Equal(job.Segments[i - 1].End + 1, s.Start);
        }

        Assert.Equal(6 * MiB - 1, job.Segments[^1].End);
    }

    /// <summary>
    /// Worst case for the bookkeeping: tiny segments, no lookahead, instant ramp-up, local-speed transfers - the
    /// engine splits and takes over constantly. The file must still be exactly right.
    /// </summary>
    [Theory]
    [InlineData(16, 64)]
    [InlineData(8, 32)]
    [InlineData(32, 128)]
    public async Task Constant_splitting_and_takeover_at_full_speed_stays_byte_exact(int connections, int minSegmentKiB)
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: connections, tweak: s =>
        {
            s.MinSegmentBytes = minSegmentKiB * 1024;
            s.LookaheadSeconds = 0;
            s.RampUpInterval = TimeSpan.FromMilliseconds(10);
            s.TickInterval = TimeSpan.FromMilliseconds(20);
            s.TakeoverThresholdBytes = 64 * 1024; // make takeovers common
        });

        long size = 96 * MiB + 12345;
        var job = await Download(h, m, h.Server.FileUrl(size, name: "stress.bin", kbps: 16384), connections, 40);

        AssertByteExact(job, size);
        Assert.True(job.Stats.Splits >= 3, $"splits {job.Stats.Splits}");
        Assert.True(job.Stats.PeakConnections >= 3, $"peak {job.Stats.PeakConnections}");
    }
}
