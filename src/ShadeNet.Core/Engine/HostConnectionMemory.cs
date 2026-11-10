using System.Collections.Concurrent;

namespace ShadeNet.Core.Engine;

/// <summary>
/// How many connections a site was seen to allow. A download that a site turned extra connections away from (429/503 while
/// others worked) records the most that worked at the same time, and the next downloads from that site open no more than
/// that from the start, instead of being refused again. A cap is forgotten after <see cref="Lifetime"/> (sites change, and a
/// busy moment should not cost speed for good); a tighter cap learned later replaces a looser one. Kept while the program runs.
/// </summary>
public sealed class HostConnectionMemory
{
    readonly ConcurrentDictionary<string, (int Cap, DateTimeOffset At)> _caps = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<DateTimeOffset> _now;

    public HostConnectionMemory(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>The connections <paramref name="host"/> allows, when it was seen to limit them; else null.</summary>
    public int? CapFor(string host)
    {
        if (string.IsNullOrEmpty(host) || !_caps.TryGetValue(host, out var known)) return null;
        if (_now() - known.At <= Lifetime) return known.Cap;
        _caps.TryRemove(new KeyValuePair<string, (int, DateTimeOffset)>(host, known));
        return null;
    }

    /// <summary><paramref name="host"/> turned connections away while <paramref name="working"/> of them worked.</summary>
    public void Learn(string host, int working)
    {
        if (string.IsNullOrEmpty(host) || working < 1) return;
        var now = _now();
        _caps.AddOrUpdate(host, (working, now), (_, old) =>
            now - old.At > Lifetime || working < old.Cap ? (working, now) : (old.Cap, now));
    }
}
