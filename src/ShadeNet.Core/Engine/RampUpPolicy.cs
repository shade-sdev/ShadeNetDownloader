using System.Diagnostics;

namespace ShadeNet.Core.Engine;

/// <summary>
/// Slow-start for connections (IDM report §3.5): begin with one connection and, while the active ones are actually
/// receiving data, double every <c>interval</c> up to the cap. Pointless connections are not added once a speed limit
/// is nearly saturated, and a very low limit caps the job at two connections.
/// </summary>
internal sealed class RampUpPolicy
{
    const double LimiterSaturation = 0.85;
    const long LowLimitBytes = 10_241;

    readonly TimeSpan _interval;
    long _lastTicks = Stopwatch.GetTimestamp();

    public RampUpPolicy(TimeSpan interval) => _interval = interval;

    /// <returns>How many connections to add right now (0 = none).</returns>
    public int Plan(int active, int receiving, double speed, long limit, int max)
    {
        if (limit > 0 && limit < LowLimitBytes) max = Math.Min(max, 2);
        if (active >= max) return 0;
        if (limit > 0 && speed >= LimiterSaturation * limit) return 0;
        if (receiving == 0) return 0;
        if (receiving * 2 <= active) return 0;

        var now = Stopwatch.GetTimestamp();
        if ((now - _lastTicks) / (double)Stopwatch.Frequency < _interval.TotalSeconds) return 0;
        _lastTicks = now;

        var target = Math.Min(active * 2, max);
        return Math.Max(0, target - active);
    }
}
