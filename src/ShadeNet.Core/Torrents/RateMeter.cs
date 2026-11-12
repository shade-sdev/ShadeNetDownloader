using System.Diagnostics;

namespace ShadeNet.Core.Torrents;

/// <summary>
/// Bytes per second from a growing total. MonoTorrent's own rates count the protocol's messages too (a peer that has everything still
/// gets a few KB/s of "have" and keep-alive messages), which would show as sending while nobody downloads anything; its totals of file
/// data do not, so the speeds are measured from those.
/// </summary>
internal sealed class RateMeter
{
    long _lastTotal = -1;
    long _lastTicks;
    double _rate;

    public double Rate => _rate;

    public double Sample(long total)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastTotal < 0 || total < _lastTotal)
        {
            _lastTotal = total;
            _lastTicks = now;
            _rate = 0;
            return 0;
        }

        var span = (now - _lastTicks) / (double)Stopwatch.Frequency;
        if (span < 0.45) return _rate;   // too short to measure: the last value stands
        var rate = (total - _lastTotal) / span;
        _rate = _rate <= 0 || rate <= 0 ? rate : _rate * 0.5 + rate * 0.5;
        _lastTotal = total;
        _lastTicks = now;
        return _rate;
    }
}
