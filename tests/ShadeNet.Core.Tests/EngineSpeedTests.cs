using System.Diagnostics;
using ShadeNet.Core.Models;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

/// <summary>
/// What makes a download finish sooner: every connection at once, a dropped connection asked again at once, a far too slow
/// one replaced, a site's connection limit remembered, and no wait for the next tick at the end. Each test would fail by a
/// wide margin with the slow-start engine (one connection, doubling every 2 s; the first retry after 1 s).
/// </summary>
public class EngineSpeedTests
{
    const long MiB = 1024 * 1024;

    [Fact]
    public async Task Every_connection_opens_at_once_when_the_server_caps_each_one()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: 8);
        var sw = Stopwatch.StartNew();

        // 1 MiB/s per connection: 16 MiB takes 2 s on 8 connections, 16 s on one (about 6 s with the old slow-start)
        var job = await Download(h, m, h.Server.FileUrl(16 * MiB, name: "capped.bin", kbps: 1024), 8, 60);

        AssertByteExact(job, 16 * MiB);
        Assert.Equal(8, job.Stats.PeakConnections);
        Assert.True(sw.Elapsed.TotalSeconds < 4.5, $"took {sw.Elapsed.TotalSeconds:0.0} s; 2 s is the ideal. {Describe(job)}");
    }

    [Fact]
    public async Task A_small_file_does_not_cut_itself_into_slivers()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: 8);
        var job = await Download(h, m, h.Server.FileUrl(300 * 1024, name: "small.bin"), 8, 30);

        AssertByteExact(job, 300 * 1024);
        Assert.Equal(1, job.Stats.PeakConnections);
        Assert.Equal(0, job.Stats.Splits);
    }

    [Fact]
    public async Task A_download_finishes_without_waiting_for_the_next_tick()
    {
        await using var h = await Harness.CreateAsync();
        // a tick every 3 s: only a wake-up when the last connection ends can finish this sooner
        var m = h.NewManager(connections: 8, tweak: s => s.TickInterval = TimeSpan.FromSeconds(3));
        var sw = Stopwatch.StartNew();
        var job = await Download(h, m, h.Server.FileUrl(256 * 1024, name: "quick.bin"), 8, 30);

        AssertByteExact(job, 256 * 1024);
        Assert.True(sw.Elapsed.TotalSeconds < 2, $"took {sw.Elapsed.TotalSeconds:0.0} s: it waited for the tick");
    }

    [Fact]
    public async Task A_dropped_connection_is_asked_again_at_once()
    {
        await using var h = await Harness.CreateAsync();
        // a long wait between tries, so only an immediate first retry keeps this quick
        var m = h.NewManager(connections: 8, tweak: s => s.RetryBaseDelay = TimeSpan.FromSeconds(5));
        var sw = Stopwatch.StartNew();
        var job = await Download(h, m, h.Server.FileUrl(16 * MiB, name: "drops.bin", kbps: 2048, dropAfter: 1 * MiB, drops: 4), 8, 60);

        AssertByteExact(job, 16 * MiB);
        Assert.True(job.Stats.Retries >= 1, Describe(job));
        Assert.True(sw.Elapsed.TotalSeconds < 4, $"took {sw.Elapsed.TotalSeconds:0.0} s; a 5 s wait per drop was not needed. {Describe(job)}");
    }

    [Fact]
    public async Task A_connection_far_slower_than_the_others_asks_again_on_a_new_one()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: 8);
        var sw = Stopwatch.StartNew();
        // every third request crawls at 32 KiB/s: kept, the last slow part alone would take many seconds
        var job = await Download(h, m, h.Server.FileUrl(24 * MiB, name: "slowroute.bin", kbps: 2048, slowEvery: 3, slowKbps: 32), 8, 90);

        AssertByteExact(job, 24 * MiB);
        Assert.True(job.Stats.ReplacedConnections >= 1, Describe(job));
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"took {sw.Elapsed.TotalSeconds:0.0} s. {Describe(job)}");
    }

    [Fact]
    public async Task Free_fast_connections_take_nearly_all_of_a_crawling_ones_part()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: 8);
        var sw = Stopwatch.StartNew();
        // the other connections are as fast as this computer: everything but the crawling parts is done at once. A crawling
        // part may be cut finer than the usual minimum, so it keeps little (measured: 4.3 s with whole 256 KiB pieces, ~2 s now)
        var job = await Download(h, m, h.Server.FileUrl(32 * MiB, name: "crawl.bin", slowEvery: 3, slowKbps: 125), 8, 60);

        AssertByteExact(job, 32 * MiB);
        Assert.True(sw.Elapsed.TotalSeconds < 3.5, $"took {sw.Elapsed.TotalSeconds:0.0} s. {Describe(job)}");
    }

    [Fact]
    public async Task Under_a_speed_limit_no_connection_is_replaced_for_being_slow()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: 8, tweak: s => s.RampUpInterval = TimeSpan.FromMilliseconds(200));
        // slow on purpose: the limit, not the route
        var job = h.Add(m, h.Server.FileUrl(6 * MiB, name: "limited.bin", kbps: 2048, slowEvery: 3, slowKbps: 64), 8, limit: 3 * MiB);
        await Finished(job, 90);

        AssertByteExact(job, 6 * MiB);
        Assert.Equal(0, job.Stats.ReplacedConnections);
    }

    [Fact]
    public async Task A_server_that_allows_two_connections_keeps_both_busy()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: 8);
        var sw = Stopwatch.StartNew();
        // 16 MiB at 2 MiB/s per connection: 4 s on the two the server allows, 8 s if the refusals had talked it down to one
        // (a connection moving on to its next part is refused for a moment while the server still counts its last request)
        var job = await Download(h, m, h.Server.FileUrl(16 * MiB, name: "two.bin", kbps: 2048, maxConns: 2), 8, 60);

        AssertByteExact(job, 16 * MiB);
        Assert.True(sw.Elapsed.TotalSeconds < 6.5, $"took {sw.Elapsed.TotalSeconds:0.0} s; 4 s is the ideal. {Describe(job)}");
    }

    [Fact]
    public async Task On_a_two_connection_server_any_connection_turned_away_gives_its_part_back()
    {
        await using var h = await Harness.CreateAsync();
        // Which connections are turned away is chance; three downloads (each with a manager that has learned nothing) make it
        // most unlikely that the first connection is never among them.
        for (var round = 0; round < 3; round++)
        {
            var m = h.NewManager(connections: 8);
            // probed apart (not kept): no connection is warm, so the first one may be among those turned away
            var url = h.Server.FileUrl(16 * MiB, name: $"cold{round}.bin", kbps: 2048, maxConns: 2);
            var probe = await m.ProbeAsync(new Net.RequestSpec(url));
            var sw = Stopwatch.StartNew();
            var job = m.Add(new AddDownloadRequest { Url = url, MaxConnections = 8, Mode = AddMode.Start }, probe);
            await Finished(job, 90);

            AssertByteExact(job, 16 * MiB);
            Assert.True(sw.Elapsed.TotalSeconds < 6.5, $"round {round} took {sw.Elapsed.TotalSeconds:0.0} s; 4 s is the ideal. {Describe(job)}");
        }
    }

    [Fact]
    public async Task A_site_that_turns_extra_connections_away_is_remembered_for_the_next_download()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: 8);

        var first = await Download(h, m, h.Server.FileUrl(8 * MiB, name: "strict1.bin", kbps: 2048, maxConns: 2), 8, 60);
        AssertByteExact(first, 8 * MiB);
        Assert.Equal(2, m.HostLimits.CapFor(new Uri(h.Server.BaseUrl).Host));

        var second = await Download(h, m, h.Server.FileUrl(8 * MiB, name: "strict2.bin", kbps: 2048, maxConns: 2), 8, 60);
        AssertByteExact(second, 8 * MiB);
        Assert.True(second.Stats.PeakConnections <= 2, Describe(second));
        Assert.Equal(0, second.Stats.Retries);      // not turned away again
    }
}
