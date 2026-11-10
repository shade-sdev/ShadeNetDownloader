using System.Diagnostics;

namespace ShadeNet.Core.Engine;

/// <summary>
/// How many connections a download opens, and when.
/// <para>
/// Without a speed limit every allowed connection opens at once (one per <c>pieceBytes</c> of a small file, so it is not
/// cut into slivers), and one that ends is replaced at once while there is work to split. Waiting for proof that more
/// connections help cost seconds on every download whose server caps each connection, which is the usual reason to use
/// several; a server that allows fewer says so (429/503), and the download, and the next one, keep to what it allows.
/// </para>
/// <para>
/// Under a speed limit the connections are pointless past the limit, so the old slow-start applies: begin with one and,
/// while the active ones are receiving, double every <c>interval</c> up to the cap; none is added once the limit is
/// nearly saturated, and a very low limit caps the job at two connections.
/// </para>
/// </summary>
internal sealed class RampUpPolicy
{
    const double LimiterSaturation = 0.85;
    const long LowLimitBytes = 10_241;

    readonly TimeSpan _interval;
    long _lastTicks = Stopwatch.GetTimestamp();

    public RampUpPolicy(TimeSpan interval) => _interval = interval;

    /// <summary>The connections to open when the download starts.</summary>
    /// <param name="size">The file's size, when known.</param>
    /// <param name="pieceBytes">A small file gets one connection per this many bytes.</param>
    public static int Initial(int max, long? size, long limit, int pieceBytes)
    {
        if (max <= 1) return 1;
        if (limit > 0) return 1;   // under a limit connections are added while the limit leaves room (Plan)
        if (size is not > 0 || pieceBytes <= 0) return 1;
        var bySize = (size.Value + pieceBytes - 1) / pieceBytes;
        return (int)Math.Clamp(bySize, 1, max);
    }

    /// <returns>How many connections to add right now (0 = none).</returns>
    public int Plan(int active, int receiving, double speed, long limit, int max)
    {
        if (limit > 0 && limit < LowLimitBytes) max = Math.Min(max, 2);
        if (active >= max) return 0;
        if (limit <= 0) return max - active;   // no limit: what is missing comes back at once
        if (speed >= LimiterSaturation * limit) return 0;
        if (receiving == 0) return 0;
        if (receiving * 2 <= active) return 0;

        var now = Stopwatch.GetTimestamp();
        if ((now - _lastTicks) / (double)Stopwatch.Frequency < _interval.TotalSeconds) return 0;
        _lastTicks = now;

        var target = Math.Min(active * 2, max);
        return Math.Max(0, target - active);
    }
}
