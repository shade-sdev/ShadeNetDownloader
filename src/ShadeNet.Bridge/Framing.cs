namespace ShadeNet.Bridge;

public sealed class BridgeProtocolException : Exception
{
    public BridgeProtocolException(string message) : base(message) { }
}

/// <summary>
/// Chrome's native messaging framing: a 4-byte little-endian length followed by that many bytes of UTF-8 JSON. The same
/// framing is used on the pipe between the host and the app, so the host can relay frames without understanding them
/// (IDM's <c>IDMMsgHost.exe</c> does the same, report §5).
/// </summary>
public static class Framing
{
    /// <returns>The payload, or <c>null</c> when the stream ended cleanly before a new frame started.</returns>
    public static async Task<byte[]?> ReadAsync(Stream stream, int maxBytes, CancellationToken ct = default)
    {
        var header = new byte[4];
        var got = await ReadFullyAsync(stream, header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 4) throw new BridgeProtocolException("The stream ended inside a frame header.");

        var length = BitConverter.ToUInt32(header, 0);
        if (length > (uint)maxBytes) throw new BridgeProtocolException($"Frame of {length:N0} bytes exceeds the limit of {maxBytes:N0}.");

        var payload = new byte[length];
        if (length > 0 && await ReadFullyAsync(stream, payload, ct).ConfigureAwait(false) < length)
            throw new BridgeProtocolException("The stream ended inside a frame.");
        return payload;
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        // One buffer, one write: frames from different threads never interleave when the caller serialises writes.
        var buffer = new byte[4 + payload.Length];
        BitConverter.TryWriteBytes(buffer, (uint)payload.Length);
        payload.CopyTo(buffer.AsMemory(4));
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }

        return total;
    }
}
