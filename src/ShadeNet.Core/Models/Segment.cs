using ShadeNet.Core.Engine;

namespace ShadeNet.Core.Models;

/// <summary>
/// A contiguous byte range of the target file. Invariant: the segments of a job are disjoint, ordered by
/// <see cref="Start"/> and cover <c>[0, TotalSize)</c>. <see cref="End"/> is inclusive and shrinks when a
/// segment is split; <see cref="long.MaxValue"/> means "until EOF" (size unknown).
/// </summary>
public sealed class Segment
{
    public const long Unbounded = long.MaxValue;

    public int Id { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public long Downloaded { get; set; }
    public SegmentState State { get; set; }

    /// <summary>Connection currently responsible for this segment (runtime only, guarded by the job lock).</summary>
    internal ConnectionSlot? Owner { get; set; }

    public long Position => Start + Downloaded;
    public bool IsBounded => End != Unbounded;
    public long Length => IsBounded ? End - Start + 1 : Unbounded;
    public long Remaining => IsBounded ? End - Position + 1 : Unbounded;

    public SegmentInfo ToInfo() => new(Start, End, Downloaded, State, Owner?.Id ?? -1, Owner?.Speed ?? 0);
}

/// <summary>Immutable copy of a segment for UI consumption.</summary>
/// <param name="Connection">Id of the connection working on it right now, -1 when nobody is.</param>
/// <param name="Speed">That connection's current speed in bytes per second.</param>
public readonly record struct SegmentInfo(long Start, long End, long Downloaded, SegmentState State, int Connection = -1, double Speed = 0);
