using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;

namespace ShadeNet.Core.Tests;

public class SchedulerTests
{
    const long MiB = 1024 * 1024;
    const int Min = 256 * 1024;

    static (DownloadJob Job, SegmentScheduler Sched) NewJob(long size, int takeover = 64)
    {
        var job = new DownloadJob { TotalSize = size, AcceptsRanges = true };
        var sched = new SegmentScheduler(job, Min, 3.0, takeover);
        sched.ResetToSingleSegment();
        return (job, sched);
    }

    static Segment? Acquire(SegmentScheduler s, ConnectionSlot slot, bool split = true) =>
        s.Acquire(slot, split, out _, out _);

    /// <summary>Segments must stay ordered, disjoint, contiguous and cover [0,total).</summary>
    static void AssertInvariants(DownloadJob job, long total)
    {
        lock (job.Sync)
        {
            Assert.NotEmpty(job.Segments);
            Assert.Equal(0, job.Segments[0].Start);
            for (var i = 0; i < job.Segments.Count; i++)
            {
                var s = job.Segments[i];
                Assert.True(s.End >= s.Start - 1, $"segment {i} has End < Start - 1");
                Assert.InRange(s.Downloaded, 0, s.Length);
                if (i > 0) Assert.Equal(job.Segments[i - 1].End + 1, s.Start);
            }

            Assert.Equal(total - 1, job.Segments[^1].End);
        }
    }

    [Fact]
    public void A_fresh_job_hands_out_the_whole_file_to_the_first_connection()
    {
        var (job, sched) = NewJob(9 * MiB);
        var slot = new ConnectionSlot(0);
        var seg = Acquire(sched, slot);

        Assert.NotNull(seg);
        Assert.Equal(0, seg!.Start);
        Assert.Equal(9 * MiB - 1, seg.End);
        Assert.Equal(SegmentState.Active, seg.State);
        Assert.Same(slot, seg.Owner);
        Assert.Single(job.Segments);
    }

    [Fact]
    public void A_second_connection_takes_the_upper_half_of_the_remaining_range()
    {
        var (job, sched) = NewJob(8 * MiB);
        var first = Acquire(sched, new ConnectionSlot(0))!;
        first.Downloaded = 2 * MiB; // 6 MiB left → split at 2 + 3 = 5 MiB

        var second = Acquire(sched, new ConnectionSlot(1))!;

        Assert.Equal(5 * MiB, second.Start);
        Assert.Equal(8 * MiB - 1, second.End);
        Assert.Equal(5 * MiB - 1, first.End);
        Assert.Equal(2, job.Segments.Count);
        Assert.Equal(1, job.Stats.Splits);
        AssertInvariants(job, 8 * MiB);
    }

    [Fact]
    public void Ranges_that_cannot_be_bisected_into_two_useful_halves_are_not_split()
    {
        var (job, sched) = NewJob(2L * Min + 2); // free < 2·Min + 3
        Acquire(sched, new ConnectionSlot(0));
        Assert.Null(Acquire(sched, new ConnectionSlot(1)));
        Assert.False(sched.NeedNewConnection());

        var (job2, sched2) = NewJob(2L * Min + 3); // exactly the threshold
        Acquire(sched2, new ConnectionSlot(0));
        Assert.True(sched2.NeedNewConnection());
        Assert.NotNull(Acquire(sched2, new ConnectionSlot(1)));
        AssertInvariants(job2, 2L * Min + 3);
    }

    [Fact]
    public void Splitting_can_be_disabled_for_servers_without_ranges()
    {
        var (_, sched) = NewJob(100 * MiB);
        Acquire(sched, new ConnectionSlot(0));
        Assert.Null(Acquire(sched, new ConnectionSlot(1), split: false));
    }

    [Fact]
    public void The_slowest_connection_is_split_first()
    {
        var (job, sched) = NewJob(8 * MiB);
        var fast = new ConnectionSlot(0);
        var slow = new ConnectionSlot(1);
        var segFast = Acquire(sched, fast)!;
        // Manually build two equal active chunks: [0,4M) fast and [4M,8M) slow.
        lock (job.Sync)
        {
            segFast.End = 4 * MiB - 1;
            job.Segments.Add(new Segment { Id = 1, Start = 4 * MiB, End = 8 * MiB - 1, State = SegmentState.Active, Owner = slow });
        }

        fast.Meter.SetSpeed(2 * MiB); // will finish its 4 MiB in 2 s → "free" = 4 − 3·2 < 0
        slow.Meter.SetSpeed(100 * 1024);

        var created = Acquire(sched, new ConnectionSlot(2))!;

        Assert.True(created.Start > 4 * MiB, "the new connection must take part of the slow connection's chunk");
        Assert.Equal(created.Start - 1, job.Segments[1].End);
        Assert.Equal(4 * MiB - 1, segFast.End);
        AssertInvariants(job, 8 * MiB);
    }

    [Fact]
    public void A_connection_that_will_finish_within_the_lookahead_is_left_alone()
    {
        var (job, sched) = NewJob(8 * MiB);
        var slot = new ConnectionSlot(0);
        Acquire(sched, slot);
        slot.Meter.SetSpeed(3 * MiB); // 8 MiB at 3 MiB/s: free = 8 − 9 < 0

        Assert.False(sched.NeedNewConnection());
        Assert.Null(Acquire(sched, new ConnectionSlot(1)));
    }

    [Fact]
    public void Unowned_pending_work_is_always_given_out_before_splitting()
    {
        var (job, sched) = NewJob(8 * MiB);
        var a = Acquire(sched, new ConnectionSlot(0))!;
        lock (job.Sync)
        {
            a.End = 4 * MiB - 1;
            job.Segments.Add(new Segment { Id = 1, Start = 4 * MiB, End = 8 * MiB - 1, State = SegmentState.Pending });
        }

        Assert.True(sched.NeedNewConnection());
        var slot = new ConnectionSlot(1);
        var got = Acquire(sched, slot)!;
        Assert.Equal(4 * MiB, got.Start);
        Assert.Equal(2, job.Segments.Count); // no split happened
        Assert.Same(slot, got.Owner);
    }

    [Fact]
    public void A_finished_connection_takes_over_a_neighbour_that_has_barely_started()
    {
        var (job, sched) = NewJob(8 * MiB);
        var warm = new ConnectionSlot(0);
        var cold = new ConnectionSlot(1);
        var first = Acquire(sched, warm)!;
        first.Downloaded = 2 * MiB;
        var second = Acquire(sched, cold)!;     // split: second now belongs to `cold`, 0 bytes received
        second.Downloaded = 10;                 // ≤ 64 bytes

        sched.MarkDone(first, warm); // simulate: warm connection finished its range
        // (first was cut at the split point; pretend it completed)
        warm.Last = first;

        var taken = sched.Acquire(warm, true, out var revokedFrom, out var revokedSeg);

        Assert.Same(second, taken);
        Assert.Same(cold, revokedFrom);
        Assert.Same(second, revokedSeg);
        Assert.Same(warm, second.Owner);
        Assert.Equal(1, job.Stats.Takeovers);
        Assert.Equal(-1, sched.Allowance(second, cold));          // the old owner is cut off...
        Assert.False(sched.Commit(second, cold, 100));            // ...and cannot record progress any more
        Assert.True(sched.Commit(second, warm, 100));
    }

    [Fact]
    public void No_takeover_when_the_neighbour_is_already_making_progress()
    {
        var (job, sched) = NewJob(8 * MiB);
        var warm = new ConnectionSlot(0);
        var cold = new ConnectionSlot(1);
        var first = Acquire(sched, warm)!;
        first.Downloaded = 2 * MiB;
        var second = Acquire(sched, cold)!;
        second.Downloaded = 500_000; // clearly under way

        sched.MarkDone(first, warm);
        warm.Last = first;
        sched.Acquire(warm, true, out var revokedFrom, out _);

        Assert.Null(revokedFrom);
        Assert.Equal(0, job.Stats.Takeovers);
        Assert.Same(cold, second.Owner);
    }

    [Fact]
    public void Ownership_rules_protect_against_stale_workers()
    {
        var (_, sched) = NewJob(4 * MiB);
        var a = new ConnectionSlot(0);
        var b = new ConnectionSlot(1);
        var seg = Acquire(sched, a)!;

        Assert.False(sched.Commit(seg, b, 10));
        Assert.False(sched.MarkDone(seg, b));
        Assert.Equal(-1, sched.Allowance(seg, b));
        Assert.Equal(seg.Remaining, sched.Allowance(seg, a));

        Assert.True(sched.Commit(seg, a, 4096));
        Assert.Equal(4096, seg.Downloaded);

        sched.Release(seg, b); // not the owner: no effect
        Assert.Equal(SegmentState.Active, seg.State);
        sched.Release(seg, a);
        Assert.Equal(SegmentState.Pending, seg.State);
        Assert.Null(seg.Owner);
        Assert.Equal(4096, seg.Downloaded); // progress is kept for whoever picks it up
    }

    [Fact]
    public void A_chunk_that_was_in_flight_during_a_split_only_counts_up_to_the_new_end()
    {
        var (job, sched) = NewJob(8 * MiB);
        var owner = new ConnectionSlot(0);
        var seg = Acquire(sched, owner)!;
        seg.Downloaded = 1 * MiB;

        // The owner has read 128 KiB but not recorded it yet; meanwhile another connection splits the range.
        Acquire(sched, new ConnectionSlot(1));
        var allowedNow = sched.Allowance(seg, owner);
        Assert.True(allowedNow > 0 && allowedNow < 8 * MiB);

        // Pretend the split left only 10 000 bytes in this segment.
        lock (job.Sync) seg.End = seg.Position + 10_000 - 1;
        Assert.True(sched.Commit(seg, owner, 128 * 1024));

        Assert.Equal(seg.Length, seg.Downloaded);       // exactly full, not overshooting
        Assert.Equal(0, sched.Allowance(seg, owner));   // "nothing left" is 0 ...
        Assert.True(sched.MarkDone(seg, owner));        // ... and the segment can be completed
    }

    [Fact]
    public void A_range_trimmed_below_the_received_data_reports_zero_not_lost()
    {
        var (job, sched) = NewJob(8 * MiB);
        var owner = new ConnectionSlot(0);
        var seg = Acquire(sched, owner)!;
        seg.Downloaded = 2 * MiB;
        lock (job.Sync) seg.End = 1 * MiB; // trimmed below what is already there

        Assert.Equal(0, sched.Allowance(seg, owner));
        Assert.Equal(-1, sched.Allowance(seg, new ConnectionSlot(9)));
    }

    [Fact]
    public void AllDone_only_when_every_segment_is_done()
    {
        var (job, sched) = NewJob(8 * MiB);
        var a = new ConnectionSlot(0);
        var first = Acquire(sched, a)!;
        var second = Acquire(sched, new ConnectionSlot(1))!;
        Assert.False(sched.AllDone());
        sched.MarkDone(first, a);
        Assert.False(sched.AllDone());
        sched.MarkDone(second, second.Owner!);
        Assert.True(sched.AllDone());
    }

    [Fact]
    public void Unknown_size_is_a_single_unbounded_segment_that_is_never_split()
    {
        var job = new DownloadJob { TotalSize = null };
        var sched = new SegmentScheduler(job, Min, 3.0, 64);
        sched.ResetToSingleSegment();

        var slot = new ConnectionSlot(0);
        var seg = Acquire(sched, slot)!;
        Assert.False(seg.IsBounded);
        Assert.Null(Acquire(sched, new ConnectionSlot(1)));
        Assert.False(sched.NeedNewConnection());

        seg.Downloaded = 12345;
        sched.CompleteUnbounded(seg, slot);
        Assert.Equal(12345, job.TotalSize);
        Assert.Equal(12344, seg.End);
        Assert.True(sched.AllDone());
    }

    [Fact]
    public void ReleaseAll_returns_work_to_the_pool_on_stop()
    {
        var (job, sched) = NewJob(8 * MiB);
        Acquire(sched, new ConnectionSlot(0));
        Acquire(sched, new ConnectionSlot(1));
        sched.ReleaseAll();
        Assert.All(job.Segments, s =>
        {
            Assert.Equal(SegmentState.Pending, s.State);
            Assert.Null(s.Owner);
        });
    }

    [Fact]
    public void Random_operations_never_break_the_segment_invariants()
    {
        for (var seed = 0; seed < 40; seed++)
        {
            var rng = new Random(seed);
            var total = (long)(rng.Next(1, 64) * MiB + rng.Next(0, 100_000));
            var (job, sched) = NewJob(total);
            var slots = Enumerable.Range(0, 6).Select(i => new ConnectionSlot(i)).ToList();
            var current = new Segment?[slots.Count];

            for (var step = 0; step < 400; step++)
            {
                var i = rng.Next(slots.Count);
                var slot = slots[i];
                switch (rng.Next(5))
                {
                    case 0 or 1 when current[i] == null:
                        var seg = sched.Acquire(slot, true, out var from, out var fromSeg);
                        if (from != null)
                            for (var k = 0; k < slots.Count; k++)
                                if (ReferenceEquals(slots[k], from) && ReferenceEquals(current[k], fromSeg)) current[k] = null;
                        current[i] = seg;
                        if (seg != null) slot.Meter.SetSpeed(rng.Next(0, 5) * MiB);
                        break;
                    case 2 when current[i] != null:
                        var allowance = sched.Allowance(current[i]!, slot);
                        if (allowance < 0) current[i] = null;
                        else if (allowance > 0) sched.Commit(current[i]!, slot, (int)Math.Min(allowance, rng.Next(1, 300_000)));
                        break;
                    case 3 when current[i] != null:
                        if (sched.Allowance(current[i]!, slot) == 0)
                        {
                            sched.MarkDone(current[i]!, slot);
                            slot.Last = current[i];
                        }

                        current[i] = null;
                        break;
                    case 4 when current[i] != null:
                        sched.Release(current[i]!, slot);
                        current[i] = null;
                        break;
                }

                AssertInvariants(job, total);
            }
        }
    }

    [Fact]
    public void ResetToSingleSegment_covers_the_whole_file_again()
    {
        var (job, sched) = NewJob(8 * MiB);
        Acquire(sched, new ConnectionSlot(0));
        Acquire(sched, new ConnectionSlot(1));
        Assert.True(job.Segments.Count > 1);

        sched.ResetToSingleSegment();
        var only = Assert.Single(job.Segments);
        Assert.Equal(0, only.Start);
        Assert.Equal(8 * MiB - 1, only.End);
        Assert.Equal(0, only.Downloaded);
        Assert.Equal(SegmentState.Pending, only.State);
    }
}
