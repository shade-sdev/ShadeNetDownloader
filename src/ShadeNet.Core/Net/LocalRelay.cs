using System.Net;
using System.Net.Sockets;
using System.Text;
using ShadeNet.Core.Engine;

namespace ShadeNet.Core.Net;

/// <summary>
/// An HTTP proxy on 127.0.0.1 for a helper program that cannot be bound to an adapter itself (ffmpeg, recording a live stream):
/// it is given this proxy, and every connection it asks for is made through <see cref="NetworkLock.ConnectAsync"/>, from the
/// chosen adapter's address or not at all. HTTPS goes through as a CONNECT tunnel; a plain request is sent on with its path only,
/// one request per connection.
/// </summary>
public sealed class LocalRelay : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly EngineSettings _settings;
    readonly List<Socket> _open = new();

    LocalRelay(EngineSettings settings)
    {
        _settings = settings;
        _listener.Start();
        _ = AcceptAsync();
    }

    /// <summary>A relay for the settings' adapter, listening until disposed.</summary>
    public static LocalRelay Start(EngineSettings settings) => new(settings);

    /// <summary>"http://127.0.0.1:port": what the helper is given as its proxy.</summary>
    public string Address => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>Connections refused because the adapter was not connected.</summary>
    public int Refused => Volatile.Read(ref _refused);

    int _refused;

    async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
                lock (_open) _open.Add(client);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // stopped
        }
    }

    async Task ServeAsync(Socket client)
    {
        Socket? upstream = null;
        try
        {
            var head = await ReadHeadAsync(client, _stop.Token).ConfigureAwait(false);
            if (head == null) return;
            var text = Encoding.Latin1.GetString(head);
            var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            var first = text[..lineEnd].Split(' ');
            if (first.Length < 3) return;

            string host;
            int port;
            byte[]? forward = null;
            if (first[0] == "CONNECT")
            {
                var colon = first[1].LastIndexOf(':');
                host = first[1][..colon].Trim('[', ']');
                port = int.Parse(first[1][(colon + 1)..]);
            }
            else
            {
                if (!Uri.TryCreate(first[1], UriKind.Absolute, out var uri) || uri.Scheme != "http") return;
                host = uri.IdnHost;
                port = uri.Port;
                // the request with its path only, and one request per connection (the next may be for another host)
                var headers = text[(lineEnd + 2)..].Split("\r\n").Where(h => h.Length > 0
                    && !h.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) && !h.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase));
                forward = Encoding.Latin1.GetBytes($"{first[0]} {uri.PathAndQuery} {first[2]}\r\n{string.Join("\r\n", headers)}\r\nConnection: close\r\n\r\n");
            }

            try
            {
                upstream = await NetworkLock.ConnectAsync(host, port, AddressFamily.Unspecified, NetworkLock.Resolve(_settings), _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or NetworkAdapterOfflineException)
            {
                if (ex is NetworkAdapterOfflineException) Interlocked.Increment(ref _refused);
                await SendAsync(client, Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n")).ConfigureAwait(false);
                return;
            }

            lock (_open) _open.Add(upstream);
            if (forward == null) await SendAsync(client, Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n")).ConfigureAwait(false);
            else await SendAsync(upstream, forward).ConfigureAwait(false);

            using var a = new NetworkStream(client, ownsSocket: false);
            using var b = new NetworkStream(upstream, ownsSocket: false);
            var up = a.CopyToAsync(b, _stop.Token);
            var down = b.CopyToAsync(a, _stop.Token);
            // one side has finished sending: the other is told (a graceful end, after all that was sent; closing at once would
            // reset the connection and could lose the last bytes), and both are given a moment to finish
            var ended = await Task.WhenAny(up, down).ConfigureAwait(false);
            try
            {
                (ended == down ? client : upstream).Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            {
                // already gone
            }

            await Task.WhenAny(Task.WhenAll(up, down), Task.Delay(TimeSpan.FromSeconds(10), _stop.Token)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or FormatException
                                       or ArgumentOutOfRangeException)
        {
            // the connection ended
        }
        finally
        {
            Close(client);
            if (upstream != null) Close(upstream);
        }
    }

    void Close(Socket socket)
    {
        lock (_open) _open.Remove(socket);
        socket.Dispose();
    }

    /// <summary>The request's head, byte by byte up to its blank line (what follows belongs to the tunnel); null when it ends first.</summary>
    static async Task<byte[]?> ReadHeadAsync(Socket socket, CancellationToken ct)
    {
        var head = new List<byte>(512);
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (head.Count > 32 * 1024) return null;
            if (await socket.ReceiveAsync(one, SocketFlags.None, ct).ConfigureAwait(false) == 0) return null;
            head.Add(one[0]);
        }

        return head.ToArray();
    }

    static async Task SendAsync(Socket socket, byte[] bytes)
    {
        var sent = 0;
        while (sent < bytes.Length) sent += await socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        lock (_open)
        {
            foreach (var s in _open) s.Dispose();
            _open.Clear();
        }

        return ValueTask.CompletedTask;
    }
}
