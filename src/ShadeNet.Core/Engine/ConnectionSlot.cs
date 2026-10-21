using ShadeNet.Core.Models;

namespace ShadeNet.Core.Engine;

/// <summary>
/// One logical connection: a long-lived worker identity that repeatedly asks the scheduler for work. Because the same
/// slot (and, through <see cref="HttpClient"/> pooling, the same warm TCP/TLS connection) moves on to the next segment,
/// finishing a segment costs no new handshake and no TCP slow start.
/// </summary>
internal sealed class ConnectionSlot
{
    readonly object _gate = new();
    CancellationTokenSource? _cts;
    Segment? _current;

    public ConnectionSlot(int id) => Id = id;

    public int Id { get; }
    public SpeedMeter Meter { get; } = new();
    public volatile bool Alive = true;

    /// <summary>Last segment this slot finished - used to find the adjacent segment for chunk takeover.</summary>
    public Segment? Last { get; set; }

    public double Speed => Meter.BytesPerSecond;
    public bool IsReceiving => Alive && Meter.TotalBytes > 0 && _current != null;

    /// <summary>Starts working on <paramref name="seg"/>; the returned token is cancelled on revoke or when <paramref name="parent"/> is.</summary>
    public CancellationToken Begin(Segment seg, CancellationToken parent)
    {
        lock (_gate)
        {
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
            _current = seg;
            return _cts.Token;
        }
    }

    public void End()
    {
        lock (_gate)
        {
            _current = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>Aborts the in-flight request for <paramref name="seg"/> (the segment was reassigned to another slot).</summary>
    public void Revoke(Segment seg)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, seg)) _cts?.Cancel();
        }
    }
}
