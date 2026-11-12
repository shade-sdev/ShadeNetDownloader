using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ShadeNet.Core.Tests;

/// <summary>
/// A proxy on 127.0.0.1 that writes down where it was asked to connect: SOCKS5 (optionally with a user name and password) or HTTP
/// (CONNECT tunnels, and plain requests with a full address). It passes the bytes both ways, like a real one.
/// </summary>
public sealed class TestProxy : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly bool _http;
    readonly string? _user;
    readonly string? _password;
    readonly List<TcpClient> _open = new();

    TestProxy(bool http, string? user, string? password)
    {
        _http = http;
        _user = user;
        _password = password;
        _listener.Start();
        _ = AcceptAsync();
    }

    public static TestProxy Socks5(string? user = null, string? password = null) => new(false, user, password);

    public static TestProxy Http() => new(true, null, null);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The address for Settings (with the sign-in for a SOCKS5 proxy that wants one).</summary>
    public string Address => _http
        ? $"http://127.0.0.1:{Port}"
        : $"socks5://{(_user != null ? $"{Uri.EscapeDataString(_user)}:{Uri.EscapeDataString(_password ?? "")}@" : "")}127.0.0.1:{Port}";

    /// <summary>"host:port" of every connection it made for a client.</summary>
    public ConcurrentQueue<string> Targets { get; } = new();

    /// <summary>Sign-ins that were refused.</summary>
    public int Refused;

    async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_open) _open.Add(client);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (Exception)
        {
            // stopped
        }
    }

    async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var s = client.GetStream();
            var (target, head) = _http ? await HttpAsync(s) : (await Socks5Async(s), null);
            if (target == null) return;
            Targets.Enqueue(target);
            using var upstream = new TcpClient();
            var colon = target.LastIndexOf(':');
            await upstream.ConnectAsync(target[..colon].Trim('[', ']'), int.Parse(target[(colon + 1)..]), _stop.Token);
            lock (_open) _open.Add(upstream);
            var u = upstream.GetStream();
            if (head != null) await u.WriteAsync(head);
            var a = s.CopyToAsync(u, _stop.Token);
            var b = u.CopyToAsync(s, _stop.Token);
            await Task.WhenAny(a, b);
        }
        catch (Exception)
        {
            // a connection that ended
        }
    }

    async Task<string?> Socks5Async(NetworkStream s)
    {
        var greeting = await ReadAsync(s, 2);
        var methods = await ReadAsync(s, greeting[1]);
        if (_user != null)
        {
            if (!methods.Contains((byte)2))
            {
                await s.WriteAsync(new byte[] { 5, 0xFF });
                Interlocked.Increment(ref Refused);
                return null;
            }

            await s.WriteAsync(new byte[] { 5, 2 });
            var ver = await ReadAsync(s, 2);
            var user = Encoding.UTF8.GetString(await ReadAsync(s, ver[1]));
            var passLength = await ReadAsync(s, 1);
            var pass = Encoding.UTF8.GetString(await ReadAsync(s, passLength[0]));
            var ok = user == _user && pass == _password;
            await s.WriteAsync(new byte[] { 1, (byte)(ok ? 0 : 1) });
            if (!ok)
            {
                Interlocked.Increment(ref Refused);
                return null;
            }
        }
        else
        {
            await s.WriteAsync(new byte[] { 5, 0 });
        }

        var request = await ReadAsync(s, 4);
        string host = request[3] switch
        {
            1 => new IPAddress(await ReadAsync(s, 4)).ToString(),
            4 => "[" + new IPAddress(await ReadAsync(s, 16)) + "]",
            3 => Encoding.ASCII.GetString(await ReadAsync(s, (await ReadAsync(s, 1))[0])),
            _ => throw new InvalidDataException(),
        };
        var port = await ReadAsync(s, 2);
        await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        return $"{host}:{(port[0] << 8) | port[1]}";
    }

    /// <summary>The target of an HTTP proxy request, and (for a plain request) the request to send it, with its path only.</summary>
    async Task<(string?, byte[]?)> HttpAsync(NetworkStream s)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (await s.ReadAsync(one) == 0) return (null, null);
            head.Add(one[0]);
        }

        var text = Encoding.ASCII.GetString(head.ToArray());
        var first = text[..text.IndexOf("\r\n", StringComparison.Ordinal)].Split(' ');
        if (first[0] == "CONNECT")
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"));
            return (first[1], null);
        }

        var uri = new Uri(first[1]);
        var rest = text[(text.IndexOf("\r\n", StringComparison.Ordinal) + 2)..];
        var forwarded = $"{first[0]} {uri.PathAndQuery} {first[2]}\r\n{rest}";
        return ($"{uri.Host}:{uri.Port}", Encoding.ASCII.GetBytes(forwarded));
    }

    static async Task<byte[]> ReadAsync(Stream s, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read));
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }

        return buffer;
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        lock (_open) foreach (var c in _open) c.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A UDP port that counts what it receives and never answers (a tracker that must not be asked).</summary>
public sealed class UdpSink : IDisposable
{
    readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    readonly CancellationTokenSource _stop = new();
    int _packets;

    public UdpSink() => _ = Task.Run(async () =>
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _udp.ReceiveAsync(_stop.Token);
                Interlocked.Increment(ref _packets);
            }
        }
        catch (Exception)
        {
            // stopped
        }
    });

    public int Packets => Volatile.Read(ref _packets);

    public string Announce => $"udp://127.0.0.1:{((IPEndPoint)_udp.Client.LocalEndPoint!).Port}/announce";

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
    }
}
