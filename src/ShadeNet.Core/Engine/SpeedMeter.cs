using System.Diagnostics;

namespace ShadeNet.Core.Engine;

/// <summary>Thread-safe byte counter with an exponentially smoothed bytes/second estimate, sampled by the scheduler tick.</summary>
public sealed class SpeedMeter
{
    const double Alpha = 0.3;

    long _total;
    long _lastTotal;
    long _lastTicks = Stopwatch.GetTimestamp();
    double _ema;

    public long TotalBytes => Interlocked.Read(ref _total);

    /// <summary>Last smoothed value (bytes/s).</summary>
    public double BytesPerSecond => Volatile.Read(ref _ema);

    public void Add(long bytes) => Interlocked.Add(ref _total, bytes);

    /// <summary>Called by one thread (the scheduler tick). Returns the updated smoothed speed.</summary>
    public double Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        if (dt < 0.05) return _ema;

        var total = TotalBytes;
        var instant = (total - _lastTotal) / dt;
        _lastTotal = total;
        _lastTicks = now;

        var next = _ema <= 0 ? instant : _ema + Alpha * (instant - _ema);
        if (next < 1) next = 0;
        Volatile.Write(ref _ema, next);
        return next;
    }

    /// <summary>Test hook: pretend the connection is currently running at this speed.</summary>
    internal void SetSpeed(double bytesPerSecond) => Volatile.Write(ref _ema, bytesPerSecond);

    public void Reset()
    {
        _lastTotal = TotalBytes;
        _lastTicks = Stopwatch.GetTimestamp();
        Volatile.Write(ref _ema, 0);
    }
}
