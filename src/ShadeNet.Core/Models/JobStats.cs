namespace ShadeNet.Core.Models;

/// <summary>Counters describing what the engine did for a job (runtime only; handy for diagnostics and tests).</summary>
public sealed class JobStats
{
    int _splits, _takeovers, _retries, _peakConnections, _fallbacks;

    public int Splits => _splits;
    public int Takeovers => _takeovers;
    public int Retries => _retries;
    public int Fallbacks => _fallbacks;
    public int PeakConnections => _peakConnections;

    internal void AddSplit() => Interlocked.Increment(ref _splits);
    internal void AddTakeover() => Interlocked.Increment(ref _takeovers);
    internal void AddFallback() => Interlocked.Increment(ref _fallbacks);
    internal void AddRetry() => Interlocked.Increment(ref _retries);

    internal void NotePeak(int alive)
    {
        int cur;
        while (alive > (cur = Volatile.Read(ref _peakConnections)))
            if (Interlocked.CompareExchange(ref _peakConnections, alive, cur) == cur) break;
    }

    internal void Reset()
    {
        _splits = _takeovers = _retries = _peakConnections = _fallbacks = 0;
    }
}
