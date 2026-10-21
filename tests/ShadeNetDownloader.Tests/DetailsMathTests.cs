using ShadeNet.Core.Models;
using ShadeNetDownloader.Util;

namespace ShadeNetDownloader.Tests;

public class BlockMapTests
{
    static SegmentInfo Seg(long start, long end, long downloaded, SegmentState state, int connection = -1) =>
        new(start, end, downloaded, state, connection);

    [Fact]
    public void Nothing_downloaded_gives_empty_cells()
    {
        var cells = BlockMap.Compute(new[] { Seg(0, 999, 0, SegmentState.Pending) }, 1000, 10);
        Assert.Equal(10, cells.Length);
        Assert.All(cells, c => Assert.Equal(0f, c.Done));
        Assert.All(cells, c => Assert.Equal(-1, c.ActiveConnection));
    }

    [Fact]
    public void A_finished_file_fills_every_cell()
    {
        var cells = BlockMap.Compute(new[] { Seg(0, 999, 1000, SegmentState.Done) }, 1000, 10);
        Assert.All(cells, c => Assert.Equal(1f, c.Done));
    }

    [Fact]
    public void Half_the_file_fills_the_first_half_of_the_cells()
    {
        var cells = BlockMap.Compute(new[] { Seg(0, 999, 500, SegmentState.Active, 0) }, 1000, 10);
        Assert.Equal(new[] { 1f, 1f, 1f, 1f, 1f, 0f, 0f, 0f, 0f, 0f }, cells.Select(c => c.Done).ToArray());
    }

    [Fact]
    public void A_partly_filled_cell_shows_its_share()
    {
        var cells = BlockMap.Compute(new[] { Seg(0, 999, 250, SegmentState.Active, 0) }, 1000, 10);   // cell width 100
        Assert.Equal(1f, cells[1].Done);
        Assert.Equal(0.5f, cells[2].Done, 3);
        Assert.Equal(0f, cells[3].Done);
    }

    [Fact]
    public void Each_connection_is_marked_where_it_is_writing()
    {
        var segments = new[]
        {
            Seg(0, 499, 150, SegmentState.Active, connection: 0),      // edge at 150 -> cell 1
            Seg(500, 999, 300, SegmentState.Active, connection: 3),    // edge at 800 -> cell 8
        };
        var cells = BlockMap.Compute(segments, 1000, 10);
        Assert.Equal(0, cells[1].ActiveConnection);
        Assert.Equal(3, cells[8].ActiveConnection);
        Assert.Equal(2, cells.Count(c => c.ActiveConnection >= 0));
    }

    [Fact]
    public void Several_segments_each_contribute_to_their_own_cells()
    {
        var segments = new[]
        {
            Seg(0, 299, 300, SegmentState.Done),
            Seg(300, 699, 0, SegmentState.Pending),
            Seg(700, 999, 100, SegmentState.Active, 1),
        };
        var cells = BlockMap.Compute(segments, 1000, 10);
        Assert.Equal(new[] { 1f, 1f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f }, cells.Select(c => c.Done).ToArray());
    }

    [Fact]
    public void An_unknown_size_gives_empty_cells_not_an_error()
    {
        var cells = BlockMap.Compute(new[] { Seg(0, Segment.Unbounded, 12345, SegmentState.Active, 0) }, 0, 8);
        Assert.Equal(8, cells.Length);
        Assert.All(cells, c => Assert.Equal(0f, c.Done));
    }

    [Fact]
    public void Zero_cells_or_no_segments_are_fine()
    {
        Assert.Empty(BlockMap.Compute(new[] { Seg(0, 9, 5, SegmentState.Active) }, 10, 0));
        Assert.All(BlockMap.Compute(Array.Empty<SegmentInfo>(), 100, 5), c => Assert.Equal(0f, c.Done));
    }

    [Fact]
    public void Odd_sizes_never_put_a_cell_outside_zero_to_one()
    {
        var rng = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var total = rng.NextInt64(1, 10_000_000_000);
            var cuts = Enumerable.Range(0, rng.Next(1, 20)).Select(_ => rng.NextInt64(1, total)).Append(0).Append(total).Distinct().OrderBy(x => x).ToArray();
            var segments = new List<SegmentInfo>();
            for (var i = 0; i + 1 < cuts.Length; i++)
            {
                var length = cuts[i + 1] - cuts[i];
                segments.Add(Seg(cuts[i], cuts[i + 1] - 1, rng.NextInt64(0, length + 1), SegmentState.Active, i));
            }

            var cells = BlockMap.Compute(segments, total, rng.Next(1, 300));
            Assert.All(cells, c => Assert.InRange(c.Done, 0f, 1f));

            // the picture agrees with the bytes: summed cell fill ~ downloaded share of the file
            var fraction = segments.Sum(s => (double)s.Downloaded) / total;
            Assert.Equal(fraction, cells.Average(c => c.Done), 3);
        }
    }

    [Fact]
    public void Downloaded_bytes_beyond_the_end_are_clipped()
    {
        var cells = BlockMap.Compute(new[] { Seg(900, 999, 500, SegmentState.Active, 0) }, 1000, 10);
        Assert.Equal(1f, cells[9].Done);
        Assert.All(cells.Take(9), c => Assert.Equal(0f, c.Done));
        Assert.Equal(-1, cells[9].ActiveConnection);   // the edge is past the end: nobody is writing inside the file
    }
}

public class SegmentSummaryTests
{
    [Fact]
    public void Segments_are_counted_by_state_and_the_working_ones_are_listed_by_connection()
    {
        var summary = SegmentSummary.Of(new[]
        {
            new SegmentInfo(0, 99, 100, SegmentState.Done),
            new SegmentInfo(100, 199, 40, SegmentState.Active, 2, 1000),
            new SegmentInfo(200, 299, 10, SegmentState.Active, 1, 2000),
            new SegmentInfo(300, 399, 0, SegmentState.Pending),
            new SegmentInfo(400, 499, 0, SegmentState.Pending),
        });

        Assert.Equal((5, 1, 2, 2), (summary.Total, summary.Done, summary.Active, summary.Pending));
        Assert.Equal(new[] { 1, 2 }, summary.Working.Select(w => w.Connection).ToArray());
        Assert.Equal(0.4, summary.Working[1].Fraction, 3);
        Assert.Equal(100, summary.Working[1].Length);
    }

    [Fact]
    public void An_unbounded_segment_is_counted_but_not_listed_as_a_range()
    {
        var summary = SegmentSummary.Of(new[] { new SegmentInfo(0, Segment.Unbounded, 5, SegmentState.Active, 0, 10) });
        Assert.Equal(1, summary.Active);
        Assert.Empty(summary.Working);
    }

    [Fact]
    public void Nothing_at_all_is_an_empty_summary()
    {
        var summary = SegmentSummary.Of(Array.Empty<SegmentInfo>());
        Assert.Equal((0, 0, 0, 0), (summary.Total, summary.Done, summary.Active, summary.Pending));
    }
}

public class SpeedHistoryTests
{
    [Fact]
    public void One_sample_per_second_at_most()
    {
        var h = new SpeedHistory();
        Assert.True(h.TryAdd(100, 5_000));
        Assert.False(h.TryAdd(200, 5_400));
        Assert.False(h.TryAdd(300, 5_999));
        Assert.True(h.TryAdd(400, 6_000));
        Assert.Equal(new double[] { 100, 400 }, h.ToArray());
    }

    [Fact]
    public void The_oldest_samples_drop_out_after_two_minutes()
    {
        var h = new SpeedHistory();
        for (var i = 0; i < 130; i++) h.TryAdd(i, i * 1000L);
        var samples = h.ToArray();
        Assert.Equal(SpeedHistory.Capacity, samples.Length);
        Assert.Equal(10, samples[0]);
        Assert.Equal(129, samples[^1]);
        Assert.Equal(samples.OrderBy(x => x), samples);   // still oldest first after wrapping
    }

    [Fact]
    public void The_peak_covers_what_is_still_remembered()
    {
        var h = new SpeedHistory();
        h.TryAdd(500, 0);
        h.TryAdd(9_000, 1000);
        h.TryAdd(300, 2000);
        Assert.Equal(9_000, h.Peak);
        for (var i = 0; i < SpeedHistory.Capacity; i++) h.TryAdd(10, 10_000 + i * 1000L);
        Assert.Equal(10, h.Peak);   // the spike scrolled out of the window
    }

    [Fact]
    public void Negative_speeds_are_recorded_as_zero()
    {
        var h = new SpeedHistory();
        h.TryAdd(-5, 0);
        Assert.Equal(new double[] { 0 }, h.ToArray());
    }

    [Fact]
    public void The_graph_scale_has_headroom_and_a_calm_floor()
    {
        Assert.Equal(64 * 1024, GraphScale.Ceiling(0));
        Assert.Equal(64 * 1024, GraphScale.Ceiling(1000));
        Assert.Equal(10_000_000 * 1.15, GraphScale.Ceiling(10_000_000), 3);
    }
}
