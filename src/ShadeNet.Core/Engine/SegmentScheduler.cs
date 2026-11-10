using ShadeNet.Core.Models;

namespace ShadeNet.Core.Engine;

/// <summary>
/// Dynamic segmentation (IDM <c>ChunksList</c>): instead of pre-splitting the file into N equal parts, the largest
/// amount of remaining work - weighted by each connection's live speed - is bisected whenever a connection is free.
/// Any part with room for two halves of the minimum size is split; the speed only decides which one.
/// Bytes a connection has received but not yet written are claimed (<see cref="Reserve"/>): a split starts after them.
/// All state lives in <see cref="DownloadJob.Segments"/> and is guarded by <see cref="DownloadJob.Sync"/>.
/// </summary>
internal sealed class SegmentScheduler
{
    /// <summary>A connection under 1/this of the fastest one's speed is crawling (see <see cref="SlowOwnerMinSegment"/>).</summary>
    const int SlowOwnerRatio = 4;

    /// <summary>How small a crawling connection's part may be cut (others' parts keep the minimum the job was given).</summary>
    const int SlowOwnerMinSegment = 64 * 1024;

    readonly DownloadJob _job;
    readonly int _minSegment;
    readonly double _lookahead;
    readonly int _takeoverThreshold;

    /// <summary>The best speed one answer of this download reached (the runner keeps it up to date); 0 = not known yet.</summary>
    public double BestConnectionSpeed { get; set; }

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
                        && next.Downloaded + next.InFlight <= _takeoverThreshold && last.IsBounded && last.End + 1 == next.Start)
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
            if (allowSplit && FindChunkToSplit() is { } victim)
                return Split(victim, slot);

            return null;
        }
    }

    static void Assign(Segment s, ConnectionSlot slot)
    {
        s.Owner = slot;
        s.State = SegmentState.Active;
        s.InFlight = 0;   // whatever a previous owner had on its way is not counted: the new owner fetches it again
    }

    /// <summary>Bytes of <paramref name="s"/> nobody has received yet (past what is written and what is on its way).</summary>
    static long Unclaimed(Segment s) => s.IsBounded ? Math.Max(0, s.Remaining - s.InFlight) : long.MaxValue;

    /// <summary>The new slot takes the upper half of the victim's unclaimed range.</summary>
    Segment Split(Segment victim, ConnectionSlot slot)
    {
        var position = victim.Position + victim.InFlight;
        var mid = position + Unclaimed(victim) / 2;
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
    /// The part to split for a free connection: of those whose unclaimed bytes make two halves of at least the minimum
    /// size, the one with the most left after <see cref="_lookahead"/> more seconds at its connection's speed
    /// (<c>unclaimed − lookahead·speed</c>), so a slow connection's part is helped first. Null when none can be split.
    /// <para>
    /// The speed no longer decides WHETHER to split: with fast connections every part would "finish within the lookahead"
    /// and no connection would be added, although more connections would still finish the file sooner.
    /// </para>
    /// </summary>
    Segment? FindChunkToSplit()
    {
        // A connection that has not received anything yet has no speed to judge: it counts as an average one, not as the
        // slowest (else, with every connection opened at once, the new ones would split each other's parts while the warm
        // first connection kept the biggest part to itself).
        double sum = 0;
        var measured = 0;
        foreach (var s in _job.Segments)
            if (s.State == SegmentState.Active && s.Owner is { Speed: > 0 } o)
            {
                sum += o.Speed;
                measured++;
            }

        var average = measured > 0 ? sum / measured : 0;
        // The yardstick for "crawling": the fastest connection at work, or the best one answer of this download reached
        // shared among the connections at work (fast parts may have finished long before anything sampled their speed).
        var fastest = 0.0;
        foreach (var s in _job.Segments)
            if (s.State == SegmentState.Active && s.Owner is { } o)
                fastest = Math.Max(fastest, o.Speed);
        fastest = Math.Max(fastest, BestConnectionSpeed / Math.Max(1, measured));

        Segment? best = null;
        var bestScore = long.MinValue;
        foreach (var s in _job.Segments)
        {
            if (s.State != SegmentState.Active || !s.IsBounded) continue;

            // The part of a connection far slower than the fastest one may be cut finer: a free fast connection then takes
            // nearly all of it, instead of leaving the crawling one a whole minimum-size piece to finish at its pace.
            var crawling = s.Owner is { Speed: > 0 } owner && owner.Speed * SlowOwnerRatio < fastest;
            var min = crawling ? Math.Min(_minSegment, SlowOwnerMinSegment) : _minSegment;
            var unclaimed = Unclaimed(s);
            if (unclaimed < 2L * min + 3) continue;

            var speed = s.Owner?.Speed is > 0 and var known ? known : average;
            var score = unclaimed - (long)(speed * _lookahead);
            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }

        return best;
    }

    /// <summary>True when there is unowned work or a part that can be bisected into two halves ≥ <c>MinSegment</c>.</summary>
    public bool NeedNewConnection()
    {
        lock (_job.Sync)
        {
            foreach (var s in _job.Segments)
                if (s.State == SegmentState.Pending) return true;

            return FindChunkToSplit() != null;
        }
    }

    /// <summary>Bytes the owner may still receive for <paramref name="seg"/> (past what it has on its way); -1 when the segment was taken away.</summary>
    public long Allowance(Segment seg, ConnectionSlot slot)
    {
        lock (_job.Sync)
        {
            if (!ReferenceEquals(seg.Owner, slot)) return -1;
            // "nothing left" is 0, never negative (negative is reserved for "ownership lost")
            return Unclaimed(seg);
        }
    }

    /// <summary>
    /// Claims up to <paramref name="n"/> received bytes for the owner before they are written; returns how many are its to
    /// write (fewer when the part ends there), -1 when the segment was taken away.
    /// </summary>
    public int Reserve(Segment seg, ConnectionSlot slot, int n)
    {
        lock (_job.Sync)
        {
            if (!ReferenceEquals(seg.Owner, slot)) return -1;
            var granted = (int)Math.Min(n, Unclaimed(seg));
            seg.InFlight += granted;
            return granted;
        }
    }

    /// <summary>Records <paramref name="n"/> reserved bytes as written. Returns false if ownership was lost meanwhile.</summary>
    public bool Commit(Segment seg, ConnectionSlot slot, int n)
    {
        lock (_job.Sync)
        {
            if (!ReferenceEquals(seg.Owner, slot)) return false;

            seg.InFlight = Math.Max(0, seg.InFlight - n);
            // (a split starts after the claimed bytes, so they are all still ours; the clamp is only a guard)
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
            seg.InFlight = 0;
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
            seg.InFlight = 0;
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
            seg.InFlight = 0;
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
                s.InFlight = 0;
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
