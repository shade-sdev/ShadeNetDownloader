using ShadeNet.Core.Models;

namespace ShadeNet.Core.Engine;

/// <summary>
/// Dynamic segmentation (IDM <c>ChunksList</c>): instead of pre-splitting the file into N equal parts, the largest
/// amount of remaining work - weighted by each connection's live speed - is bisected whenever a connection is free.
/// All state lives in <see cref="DownloadJob.Segments"/> and is guarded by <see cref="DownloadJob.Sync"/>.
/// </summary>
internal sealed class SegmentScheduler
{
    readonly DownloadJob _job;
    readonly int _minSegment;
    readonly double _lookahead;
    readonly int _takeoverThreshold;

    public SegmentScheduler(DownloadJob job, int minSegment, double lookaheadSeconds, int takeoverThreshold)
    {
        _job = job;
        _minSegment = minSegment;
        _lookahead = lookaheadSeconds;
        _takeoverThreshold = takeoverThreshold;
    }

    /// <summary>
    /// Hands <paramref name="slot"/> its next piece of work, or null if there is nothing left to do.
    /// Order: pending segment → takeover of the adjacent not-yet-started segment → split of the largest free chunk.
    /// </summary>
    /// <param name="revokedFrom">If a segment was taken from another slot, that slot (cancel it after calling).</param>
    public Segment? Acquire(ConnectionSlot slot, bool allowSplit, out ConnectionSlot? revokedFrom, out Segment? revokedSegment)
    {
        revokedFrom = null;
        revokedSegment = null;

        lock (_job.Sync)
        {
            // 1. Unowned work (fresh job, resumed job, or a segment released by a failed/trimmed slot).
            foreach (var s in _job.Segments)
            {
                if (s.State == SegmentState.Pending)
                {
                    Assign(s, slot);
                    return s;
                }
            }

            // 2. Chunk takeover: the next segment is adjacent and its connection has barely started - the warm
            //    connection that just finished continues there instead of waiting for a fresh handshake.
            var last = slot.Last;
            if (last != null)
            {
                var i = _job.Segments.IndexOf(last);
                if (i >= 0 && i + 1 < _job.Segments.Count)
                {
                    var next = _job.Segments[i + 1];
                    if (next.State == SegmentState.Active && !ReferenceEquals(next.Owner, slot)
                        && next.Downloaded <= _takeoverThreshold && last.IsBounded && last.End + 1 == next.Start)
                    {
                        _job.Stats.AddTakeover();
                        revokedFrom = next.Owner;
                        revokedSegment = next;
                        Assign(next, slot);
                        return next;
                    }
                }
            }

            // 3. Split the chunk with the most work left.
            if (allowSplit)
            {
                var (victim, free) = FindMaxFreeChunk();
                if (victim != null && free >= 2L * _minSegment + 3)
                    return Split(victim, slot);
            }

            return null;
        }
    }

    static void Assign(Segment s, ConnectionSlot slot)
    {
        s.Owner = slot;
        s.State = SegmentState.Active;
    }

    /// <summary>The new slot takes the upper half of the victim's remaining range.</summary>
    Segment Split(Segment victim, ConnectionSlot slot)
    {
        var position = victim.Position;
        var mid = position + victim.Remaining / 2;
        var created = new Segment
        {
            Id = ++_job.NextSegmentId,
            Start = mid,
            End = victim.End,
            Downloaded = 0,
            State = SegmentState.Active,
            Owner = slot,
        };

        _job.Stats.AddSplit();
        victim.End = mid - 1;
        _job.Segments.Insert(_job.Segments.IndexOf(victim) + 1, created);
        return created;
    }

    /// <summary>
    /// <c>free = (end − lookahead·speed − received − start) + 1</c>: the bytes a connection will NOT have fetched after
    /// <see cref="_lookahead"/> more seconds. Slow connections therefore look "bigger" and are split first.
    /// </summary>
    (Segment? Segment, long Free) FindMaxFreeChunk()
    {
        Segment? best = null;
        long bestFree = -1;
        foreach (var s in _job.Segments)
        {
            if (s.State != SegmentState.Active || !s.IsBounded) continue;

            var remaining = s.Remaining;
            if (remaining <= 0) continue;

            var speed = s.Owner?.Speed ?? 0;
            var free = remaining - (long)(speed * _lookahead);
            if (free > bestFree)
            {
                bestFree = free;
                best = s;
            }
        }

        return (best, bestFree);
    }

    /// <summary>True when there is unowned work or the biggest chunk can be bisected into two halves ≥ <c>MinSegment</c>.</summary>
    public bool NeedNewConnection()
    {
        lock (_job.Sync)
        {
            foreach (var s in _job.Segments)
                if (s.State == SegmentState.Pending) return true;

            var (victim, free) = FindMaxFreeChunk();
            return victim != null && free >= 2L * _minSegment + 3;
        }
    }

    /// <summary>Bytes the owner may still write into <paramref name="seg"/>; -1 when the segment was taken away.</summary>
    public long Allowance(Segment seg, ConnectionSlot slot)
    {
        lock (_job.Sync)
        {
            if (!ReferenceEquals(seg.Owner, slot)) return -1;
            // A split may have trimmed the range below what was already received; "nothing left" is 0, never negative
            // (negative is reserved for "ownership lost").
            return Math.Max(0, seg.Remaining);
        }
    }

    /// <summary>Records <paramref name="n"/> written bytes. Returns false if ownership was lost meanwhile.</summary>
    public bool Commit(Segment seg, ConnectionSlot slot, int n)
    {
        lock (_job.Sync)
        {
            if (!ReferenceEquals(seg.Owner, slot)) return false;

            // The range may have been trimmed by a split while this chunk was in flight: bytes past the new end now
            // belong to (and are written by) the neighbour, so only count what is still ours.
            var mine = seg.IsBounded ? (int)Math.Min(n, Math.Max(0, seg.Remaining)) : n;
            seg.Downloaded += mine;
            return true;
        }
    }

    public bool MarkDone(Segment seg, ConnectionSlot slot)
    {
        lock (_job.Sync)
        {
            if (!ReferenceEquals(seg.Owner, slot)) return false;
            seg.State = SegmentState.Done;
            seg.Owner = null;
            return true;
        }
    }

    /// <summary>Gives the segment back so any slot can pick it up (graceful stop, connection-cap trim).</summary>
    public void Release(Segment seg, ConnectionSlot slot)
    {
        lock (_job.Sync)
        {
            if (!ReferenceEquals(seg.Owner, slot) || seg.State == SegmentState.Done) return;
            seg.Owner = null;
            seg.State = SegmentState.Pending;
        }
    }

    /// <summary>Unbounded segment hit EOF: now the size is known.</summary>
    public void CompleteUnbounded(Segment seg, ConnectionSlot slot)
    {
        lock (_job.Sync)
        {
            if (!ReferenceEquals(seg.Owner, slot)) return;
            seg.End = seg.Start + seg.Downloaded - 1;
            seg.State = SegmentState.Done;
            seg.Owner = null;
            _job.TotalSize = seg.Start + seg.Downloaded;
        }
    }

    public bool IsDone(Segment seg)
    {
        lock (_job.Sync) return seg.State == SegmentState.Done;
    }

    public bool AllDone()
    {
        lock (_job.Sync)
        {
            foreach (var s in _job.Segments)
                if (s.State != SegmentState.Done) return false;
            return _job.Segments.Count > 0;
        }
    }

    /// <summary>Returns every segment owned by a stopped runner to <see cref="SegmentState.Pending"/>.</summary>
    public void ReleaseAll()
    {
        lock (_job.Sync)
        {
            foreach (var s in _job.Segments)
            {
                s.Owner = null;
                if (s.State == SegmentState.Active) s.State = SegmentState.Pending;
            }
        }
    }

    /// <summary>The neighbour that follows <paramref name="seg"/> in the file, if contiguous.</summary>
    public Segment? NextOf(Segment seg)
    {
        lock (_job.Sync)
        {
            var i = _job.Segments.IndexOf(seg);
            if (i < 0 || i + 1 >= _job.Segments.Count) return null;
            var n = _job.Segments[i + 1];
            return seg.IsBounded && seg.End + 1 == n.Start ? n : null;
        }
    }

    /// <summary>Resets to a single segment covering the whole file (fallback when multi-connection is unsafe).</summary>
    public void ResetToSingleSegment()
    {
        lock (_job.Sync)
        {
            _job.Segments.Clear();
            _job.NextSegmentId = 0;
            _job.Segments.Add(new Segment
            {
                Id = _job.NextSegmentId++,
                Start = 0,
                End = _job.TotalSize is > 0 ? _job.TotalSize.Value - 1 : Segment.Unbounded,
                State = SegmentState.Pending,
            });
        }
    }
}
