using ShadeNet.Core.Models;

namespace ShadeNet.Core.Tests;

/// <summary>What the details drawer shows has to come from the engine: which connection works where, and how fast.</summary>
public class DetailsTelemetryTests
{
    [Fact]
    [Trait("Category", "Slow")]
    public async Task Snapshots_say_which_connection_works_on_which_part_and_how_fast()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: 4);
        var snapshots = new List<JobSnapshot>();
        m.JobUpdated += s => { lock (snapshots) snapshots.Add(s); };

        var job = await Harness.Download(h, m, h.Server.FileUrl(8 * 1024 * 1024, kbps: 1024), connections: 4);
        Harness.AssertByteExact(job, 8 * 1024 * 1024);

        JobSnapshot[] all;
        lock (snapshots) all = snapshots.ToArray();

        // while it ran, there was a moment with two or more connections working on different parts, each with a speed
        var busy = all.Where(s => s.State == DownloadState.Downloading)
            .Select(s => s.Segments.Where(x => x.State == SegmentState.Active && x.Connection >= 0).ToArray())
            .FirstOrDefault(active => active.Select(x => x.Connection).Distinct().Count() >= 2);
        Assert.NotNull(busy);
        Assert.All(busy!, x => Assert.True(x.Speed >= 0));
        Assert.Contains(busy!, x => x.Speed > 0);

        // once everything is done nobody owns a part any more
        var last = all.Last(s => s.State == DownloadState.Completed);
        Assert.All(last.Segments, x => Assert.Equal(-1, x.Connection));
        Assert.True(last.PeakConnections >= 2, $"peak {last.PeakConnections}");
        Assert.True(last.Splits >= 1);
    }

    [Fact]
    public void A_segment_nobody_works_on_reports_no_connection()
    {
        var info = new Segment { Start = 10, End = 99, Downloaded = 5, State = SegmentState.Pending }.ToInfo();
        Assert.Equal(-1, info.Connection);
        Assert.Equal(0, info.Speed);
        Assert.Equal((10L, 99L, 5L), (info.Start, info.End, info.Downloaded));
    }

    [Fact]
    public async Task The_global_limit_chosen_last_time_applies_at_start_and_is_remembered_when_changed()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.GlobalSpeedLimit = 123_456);
        Assert.Equal(123_456, m.GlobalLimiter.BytesPerSecond);

        m.SetGlobalSpeedLimit(5_000, enabled: true);
        Assert.Equal(5_000, m.GlobalLimiter.BytesPerSecond);
        Assert.Equal(5_000, m.Settings.GlobalSpeedLimit);

        m.SetGlobalSpeedLimit(5_000, enabled: false);
        Assert.Equal(0, m.GlobalLimiter.BytesPerSecond);
        Assert.Equal(0, m.Settings.GlobalSpeedLimit);

        m.SetGlobalSpeedLimit(-7, enabled: true);   // nonsense never becomes a limit
        Assert.Equal(0, m.Settings.GlobalSpeedLimit);
    }
}
