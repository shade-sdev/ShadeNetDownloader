using ShadeNet.Core.Models;

namespace ShadeNetDownloader.Util;

/// <summary>One cell of the block map: how much of that slice of the file is on disk, and whether a connection is at its edge.</summary>
public readonly record struct BlockCell(float Done, int ActiveConnection);

/// <summary>
/// Turns the engine's segment list into a grid of cells for the "blocks" picture (like the block map of a torrent client):
/// each cell is an equal slice of the file; its fill is how much of the slice is already downloaded, and the cell where a
/// connection is currently writing is marked with that connection's id.
/// </summary>
public static class BlockMap
{
    public static BlockCell[] Compute(IReadOnlyList<SegmentInfo> segments, long total, int cells)
    {
        if (cells <= 0) return Array.Empty<BlockCell>();

        var done = new double[cells];
        var active = new int[cells];
        Array.Fill(active, -1);
        if (total <= 0) return Build(done, active);

        double CellStart(int i) => (double)total * i / cells;

        foreach (var s in segments)
        {
            // the downloaded part of a segment is [Start, Start + Downloaded), clipped to the file
            var a = Math.Max(0L, s.Start);
            var b = Math.Min(total, s.Start + Math.Max(0L, s.Downloaded));
            if (b > a)
            {
                var first = Math.Clamp((int)(a * (double)cells / total), 0, cells - 1);
                var last = Math.Clamp((int)((b - 1) * (double)cells / total), 0, cells - 1);
                for (var i = first; i <= last; i++)
                {
                    var from = Math.Max(a, CellStart(i));
                    var to = Math.Min(b, CellStart(i + 1));
                    if (to > from) done[i] += (to - from) / (CellStart(i + 1) - CellStart(i));
                }
            }

            if (s.State == SegmentState.Active)
            {
                var edge = s.Start + Math.Max(0L, s.Downloaded);
                if (edge < total) active[Math.Clamp((int)(edge * (double)cells / total), 0, cells - 1)] = s.Connection;
            }
        }

        return Build(done, active);
    }

    static BlockCell[] Build(double[] done, int[] active)
    {
        var result = new BlockCell[done.Length];
        for (var i = 0; i < result.Length; i++) result[i] = new BlockCell((float)Math.Clamp(done[i], 0, 1), active[i]);
        return result;
    }
}

/// <summary>A segment a connection is working on right now.</summary>
public sealed record WorkingSegment(int Connection, long Start, long End, long Downloaded, double Speed)
{
    public long Length => End - Start + 1;
    public double Fraction => Length > 0 ? Math.Clamp((double)Downloaded / Length, 0, 1) : 0;
}

/// <summary>Counts and the working connections, for the "connections" tab and the block counter.</summary>
public sealed record SegmentSummary(int Total, int Done, int Active, int Pending, IReadOnlyList<WorkingSegment> Working)
{
    public static SegmentSummary Of(IReadOnlyList<SegmentInfo> segments)
    {
        int done = 0, active = 0, pending = 0;
        var working = new List<WorkingSegment>();
        foreach (var s in segments)
        {
            switch (s.State)
            {
                case SegmentState.Done:
                    done++;
                    break;
                case SegmentState.Active:
                    active++;
                    if (s.Connection >= 0 && s.End != Segment.Unbounded)
                        working.Add(new WorkingSegment(s.Connection, s.Start, s.End, s.Downloaded, s.Speed));
                    break;
                default:
                    pending++;
                    break;
            }
        }

        working.Sort((x, y) => x.Connection.CompareTo(y.Connection));
        return new SegmentSummary(segments.Count, done, active, pending, working);
    }
}

/// <summary>
/// The last two minutes of a download's speed, one sample per second, for the graph. A plain ring: fixed memory per
/// download however long it runs.
/// </summary>
public sealed class SpeedHistory
{
    public const int Capacity = 120;
    public const long SampleIntervalMs = 1000;

    readonly double[] _ring = new double[Capacity];
    int _next;
    int _count;
    long _lastSampleMs = long.MinValue;

    public int Count => _count;

    /// <summary>Records <paramref name="bytesPerSecond"/> if a second has passed since the last sample.</summary>
    /// <returns><c>true</c> when a sample was taken.</returns>
    public bool TryAdd(double bytesPerSecond, long nowMs)
    {
        if (_lastSampleMs != long.MinValue && nowMs - _lastSampleMs < SampleIntervalMs) return false;
        _lastSampleMs = nowMs;
        _ring[_next] = Math.Max(0, bytesPerSecond);
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
        return true;
    }

    /// <summary>Oldest first.</summary>
    public double[] ToArray()
    {
        var result = new double[_count];
        var start = _count < Capacity ? 0 : _next;
        for (var i = 0; i < _count; i++) result[i] = _ring[(start + i) % Capacity];
        return result;
    }

    public double Peak
    {
        get
        {
            double peak = 0;
            for (var i = 0; i < _count; i++) peak = Math.Max(peak, _ring[i]);
            return peak;
        }
    }
}

public static class GraphScale
{
    /// <summary>The value the top of the graph stands for: the peak plus headroom, never less than 64 KiB/s so an idle graph is calm.</summary>
    public static double Ceiling(double peak) => Math.Max(peak * 1.15, 64 * 1024);
}
