using System.Diagnostics;

namespace ShadeNet.Core.Engine;

/// <summary>
/// Smooth byte-rate limiter (token bucket that allows "debt"). Workers reserve bytes after every read and sleep for
/// the returned delay; because they stop reading while sleeping, the socket buffer fills and TCP flow control
/// throttles the sender - the same effect IDM gets from sleeping off the overshoot.
/// The rate can be changed at any time and applies to the very next reservation. All waits are cancellable.
/// </summary>
public sealed class SpeedLimiter
{
    readonly object _gate = new();
    double _rate;           // bytes/s, 0 = unlimited
    double _tokens;
    long _lastTicks = Stopwatch.GetTimestamp();

    public SpeedLimiter(long bytesPerSecond = 0) => BytesPerSecond = bytesPerSecond;

    /// <summary>0 means unlimited.</summary>
    public long BytesPerSecond
    {
        get { lock (_gate) return (long)_rate; }
        set
        {
            lock (_gate)
            {
                _rate = Math.Max(0, value);
                _tokens = Math.Min(_tokens, Capacity);
                _lastTicks = Stopwatch.GetTimestamp();
            }
        }
    }

    public bool IsEnabled => BytesPerSecond > 0;

    // Bucket holds 200 ms of budget (but at least one network-sized packet burst).
    double Capacity => Math.Max(_rate * 0.2, 8 * 1024);

    /// <summary>Reserves <paramref name="bytes"/> and returns how long the caller must wait before using them.</summary>
    public TimeSpan Reserve(int bytes)
    {
        lock (_gate)
        {
            if (_rate <= 0) return TimeSpan.Zero;

            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = now;
            _tokens = Math.Min(Capacity, _tokens + elapsed * _rate);
            _tokens -= bytes;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _rate);
        }
    }

    /// <summary>Gives back a reservation that was not used (cancelled wait).</summary>
    public void Refund(int bytes)
    {
        lock (_gate)
        {
            if (_rate > 0) _tokens = Math.Min(Capacity, _tokens + bytes);
        }
    }

    /// <summary>
    /// Reserves from every non-null limiter (the strictest one wins) and waits once for the longest delay.
    /// </summary>
    public static async ValueTask AcquireAsync(int bytes, CancellationToken ct, SpeedLimiter? a, SpeedLimiter? b = null)
    {
        var wa = a?.Reserve(bytes) ?? TimeSpan.Zero;
        var wb = b?.Reserve(bytes) ?? TimeSpan.Zero;
        var wait = wa > wb ? wa : wb;
        if (wait <= TimeSpan.FromMilliseconds(1)) return;

        try
        {
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            a?.Refund(bytes);
            b?.Refund(bytes);
            throw;
        }
    }

    /// <summary>The read size that keeps one reservation under ~200 ms of budget for the strictest active limiter.</summary>
    public static int PreferredReadSize(int max, SpeedLimiter? a, SpeedLimiter? b = null)
    {
        long rate = long.MaxValue;
        if (a is { IsEnabled: true }) rate = Math.Min(rate, a.BytesPerSecond);
        if (b is { IsEnabled: true }) rate = Math.Min(rate, b.BytesPerSecond);
        if (rate == long.MaxValue) return max;
        return (int)Math.Clamp(rate / 5, 1024, max);
    }
}
