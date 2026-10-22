using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ShadeNet.Bridge;

namespace ShadeNet.Bridge.Tests;

public class FramingTests
{
    static byte[] Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        return BitConverter.GetBytes((uint)payload.Length).Concat(payload).ToArray();
    }

    [Fact]
    public async Task A_frame_survives_a_round_trip_including_non_ascii_text()
    {
        using var ms = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"name\":\"naïve – 日本語\"}");
        await Framing.WriteAsync(ms, payload);
        ms.Position = 0;
        Assert.Equal(payload, await Framing.ReadAsync(ms, 1024));
        Assert.Null(await Framing.ReadAsync(ms, 1024));   // clean end of stream
    }

    [Fact]
    public async Task The_length_prefix_is_four_bytes_little_endian()
    {
        using var ms = new MemoryStream();
        await Framing.WriteAsync(ms, new byte[258]);
        Assert.Equal(new byte[] { 2, 1, 0, 0 }, ms.ToArray()[..4]);
    }

    [Fact]
    public async Task An_empty_frame_is_valid()
    {
        using var ms = new MemoryStream(Frame(""));
        Assert.Empty((await Framing.ReadAsync(ms, 10))!);
    }

    [Fact]
    public async Task A_frame_over_the_limit_is_refused_without_allocating_it()
    {
        using var ms = new MemoryStream(BitConverter.GetBytes(uint.MaxValue).Concat(new byte[10]).ToArray());
        await Assert.ThrowsAsync<BridgeProtocolException>(() => Framing.ReadAsync(ms, 1024));
    }

    [Fact]
    public async Task A_truncated_header_or_payload_is_a_protocol_error()
    {
        await Assert.ThrowsAsync<BridgeProtocolException>(() => Framing.ReadAsync(new MemoryStream(new byte[] { 5, 0 }), 100));
        await Assert.ThrowsAsync<BridgeProtocolException>(() => Framing.ReadAsync(new MemoryStream(Frame("{\"type\":\"x\"}")[..8]), 100));
    }

    sealed class OneByteStream(Stream inner) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(1, count));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, Math.Min(1, count), ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Frames_are_reassembled_when_the_stream_delivers_one_byte_at_a_time()
    {
        var json = "{\"type\":\"capture\",\"id\":\"1\"}";
        using var slow = new OneByteStream(new MemoryStream(Frame(json)));
        Assert.Equal(json, Encoding.UTF8.GetString((await Framing.ReadAsync(slow, 1024))!));
    }

    [Theory]
    [InlineData("{\"type\":\"hello\"}", "hello")]
    [InlineData("{\"Type\":\"x\"}", null)]          // member names are exact for "type"
    [InlineData("[1,2]", null)]
    [InlineData("{\"id\":1}", null)]
    [InlineData("{\"type\":5}", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    public void Messages_need_a_string_type(string json, string? expected) =>
        Assert.Equal(expected, BridgeMessage.Parse(Encoding.UTF8.GetBytes(json))?.Type);

    [Fact]
    public void A_capture_request_deserialises_from_the_extensions_camelCase_json()
    {
        var json = """
            {"type":"capture","id":"a1","url":"https://x.example/f.zip","referrer":"https://x.example/page",
             "cookies":"a=1; b=2","userAgent":"UA","fileName":"f.zip","mimeType":"application/zip","size":123,
             "headers":{"Authorization":"Bearer t"},"source":"download","unknownFutureField":true}
            """;
        var request = BridgeMessage.Parse(Encoding.UTF8.GetBytes(json))!.As<CaptureRequest>()!;

        Assert.Equal("a1", request.Id);
        Assert.Equal("https://x.example/f.zip", request.Url);
        Assert.Equal("a=1; b=2", request.Cookies);
        Assert.Equal(123, request.Size);
        Assert.Equal("Bearer t", request.Headers!["Authorization"]);
    }

    [Fact]
    public void Replies_are_written_in_camelCase_without_null_members()
    {
        var json = JsonSerializer.Serialize(new CaptureResult { Id = "a1", Accepted = true, Reason = CaptureReasons.Added }, BridgeProtocol.Json);
        Assert.Equal("{\"type\":\"capture-result\",\"id\":\"a1\",\"accepted\":true,\"reason\":\"added\"}", json);
    }
}

public class PipeTests
{
    static string UniquePipe() => "ShadeNetTest." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_client_and_the_server_exchange_messages_in_both_directions()
    {
        var name = UniquePipe();
        await using var server = new BridgeServer(name, async connection =>
        {
            while (await connection.ReadAsync() is { } message)
                await connection.SendAsync(new CaptureResult { Id = message.As<CaptureRequest>()!.Id, Accepted = true, Reason = "echo" });
        });
        Assert.True(server.Start());

        await using var client = await BridgeClient.TryConnectAsync(name, TimeSpan.FromSeconds(5));
        Assert.NotNull(client);
        await client!.SendAsync(new CaptureRequest { Id = "42", Url = "https://x/y.zip" });
        var reply = await client.ReadAsync();

        Assert.Equal(MessageTypes.CaptureResult, reply!.Type);
        Assert.Equal("42", reply.As<CaptureResult>()!.Id);
    }

    [Fact]
    public async Task Several_clients_can_be_connected_at_once()
    {
        var name = UniquePipe();
        await using var server = new BridgeServer(name, async c =>
        {
            while (await c.ReadAsync() is { } m) await c.SendAsync(new CaptureResult { Id = m.As<CaptureRequest>()!.Id, Accepted = true });
        });
        server.Start();

        var clients = new List<BridgeConnection>();
        for (var i = 0; i < 5; i++) clients.Add((await BridgeClient.TryConnectAsync(name, TimeSpan.FromSeconds(5)))!);
        for (var i = 0; i < clients.Count; i++) await clients[i].SendAsync(new CaptureRequest { Id = i.ToString() });
        for (var i = 0; i < clients.Count; i++) Assert.Equal(i.ToString(), (await clients[i].ReadAsync())!.As<CaptureResult>()!.Id);
        foreach (var c in clients) await c.DisposeAsync();
    }

    [Fact]
    public async Task A_second_server_for_the_same_pipe_is_told_it_is_second()
    {
        var name = UniquePipe();
        await using var first = new BridgeServer(name, _ => Task.CompletedTask);
        await using var second = new BridgeServer(name, _ => Task.CompletedTask);
        Assert.True(first.Start());
        Assert.False(second.Start());
    }

    [Fact]
    public async Task Nobody_listening_means_no_connection_not_an_exception()
    {
        Assert.Null(await BridgeClient.TryConnectAsync(UniquePipe(), TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Garbage_from_a_client_does_not_stop_the_server_serving_the_next_one()
    {
        var name = UniquePipe();
        var seen = new List<string>();
        await using var server = new BridgeServer(name, async c =>
        {
            while (await c.ReadAsync() is { } m) lock (seen) seen.Add(m.Type);
        });
        server.Start();

        // a frame announcing 3 GB, then silence
        await using (var bad = await BridgeClient.TryConnectAsync(name, TimeSpan.FromSeconds(5)))
            await bad!.Stream.WriteAsync(BitConverter.GetBytes(3_000_000_000u));
        // a frame that is not JSON is skipped, the next good one still arrives
        await using var good = await BridgeClient.TryConnectAsync(name, TimeSpan.FromSeconds(5));
        await good!.SendRawAsync(Encoding.UTF8.GetBytes("nope"));
        await good.SendAsync(new CaptureRequest { Id = "ok" });

        for (var i = 0; i < 100 && seen.Count == 0; i++) await Task.Delay(50);
        Assert.Equal(new[] { "capture" }, seen);
    }

    [Fact]
    public async Task Disposing_the_server_while_a_client_is_connected_returns_promptly()
    {
        var name = UniquePipe();
        var server = new BridgeServer(name, async c => { while (await c.ReadAsync() != null) { } });
        server.Start();
        await using var client = await BridgeClient.TryConnectAsync(name, TimeSpan.FromSeconds(5));

        var sw = Stopwatch.StartNew();
        await server.DisposeAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Disposing_the_server_twice_is_harmless()
    {
        // Regression: the program's shutdown reaches DisposeAsync from two places; the second call threw
        // ObjectDisposedException and the process never ended (found by asking a running program to quit).
        var server = new BridgeServer(UniquePipe(), c => Task.CompletedTask);
        server.Start();

        await server.DisposeAsync();
        await server.DisposeAsync();
    }
}

/// <summary>The real host executable, driven exactly like a browser drives it: framed JSON on stdin/stdout.</summary>
public class NativeHostProcessTests
{
    static string FindHost()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            foreach (var cfg in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(dir.FullName, "src", "ShadeNet.NativeHost", "bin", cfg, "net10.0", "ShadeNet.NativeHost.exe");
                if (File.Exists(candidate)) return candidate;
            }

        throw new FileNotFoundException("ShadeNet.NativeHost.exe was not built.");
    }

    static Process StartHost(string pipe, params (string Key, string Value)[] env)
    {
        var info = new ProcessStartInfo(FindHost(), "chrome-extension://abcdefghijklmnopabcdefghijklmnop/")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["SHADENET_PIPE"] = pipe;
        info.Environment["SHADENET_DATA_DIR"] = Path.Combine(Path.GetTempPath(), "shadenet-host-test");
        // the host is framework-dependent: point it at the runtime this test is running on
        info.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        foreach (var (k, v) in env) info.Environment[k] = v;
        return Process.Start(info)!;
    }

    static async Task SendToHost(Process host, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, BridgeProtocol.Json);
        await Framing.WriteAsync(host.StandardInput.BaseStream, bytes);
    }

    [Fact]
    public async Task The_host_relays_frames_both_ways_and_introduces_itself_to_the_app()
    {
        var pipe = "ShadeNetTest." + Guid.NewGuid().ToString("N");
        var received = new List<BridgeMessage>();
        await using var server = new BridgeServer(pipe, async c =>
        {
            while (await c.ReadAsync() is { } m)
            {
                lock (received) received.Add(m);
                if (m.Type == MessageTypes.Capture)
                    await c.SendAsync(new CaptureResult { Id = m.As<CaptureRequest>()!.Id, Accepted = true, Reason = CaptureReasons.Added });
            }
        });
        Assert.True(server.Start());

        using var host = StartHost(pipe);
        await SendToHost(host, new CaptureRequest { Id = "7", Url = "https://example.com/a.zip", Cookies = "s=1" });

        var reply = await Framing.ReadAsync(host.StandardOutput.BaseStream, 1 << 20).WaitAsync(TimeSpan.FromSeconds(20));
        var result = BridgeMessage.Parse(reply!)!.As<CaptureResult>()!;
        Assert.Equal("7", result.Id);
        Assert.True(result.Accepted);

        lock (received)
        {
            Assert.Equal(MessageTypes.HostHello, received[0].Type);
            Assert.Contains("chrome-extension://", received[0].As<HostHello>()!.Origin);
            Assert.Equal("s=1", received[1].As<CaptureRequest>()!.Cookies);
        }

        host.StandardInput.Close();   // the browser went away
        Assert.True(host.WaitForExit(10_000), "the host must exit when the browser closes its end");
        Assert.Equal(0, host.ExitCode);
    }

    [Fact]
    public async Task The_host_exits_when_the_app_goes_away()
    {
        var pipe = "ShadeNetTest." + Guid.NewGuid().ToString("N");
        var server = new BridgeServer(pipe, async c => { while (await c.ReadAsync() != null) { } });
        server.Start();
        using var host = StartHost(pipe);
        await SendToHost(host, new CaptureRequest { Id = "1", Url = "https://e/x.zip" });
        await Task.Delay(500);

        await server.DisposeAsync();
        Assert.True(host.WaitForExit(15_000), "the host must not outlive the app");
    }

    [Fact]
    public async Task Without_an_app_the_host_tells_the_browser_instead_of_hanging()
    {
        using var host = StartHost("ShadeNetTest." + Guid.NewGuid().ToString("N"), ("SHADENET_HOST_NO_LAUNCH", "1"));
        await SendToHost(host, new { type = "hello" });

        var frame = await Framing.ReadAsync(host.StandardOutput.BaseStream, 1 << 20).WaitAsync(TimeSpan.FromSeconds(20));
        var error = BridgeMessage.Parse(frame!)!.As<HostError>()!;

        Assert.Equal(MessageTypes.HostError, BridgeMessage.Parse(frame!)!.Type);
        Assert.Equal("app-unavailable", error.Code);
        Assert.True(host.WaitForExit(10_000));
        Assert.Equal(2, host.ExitCode);
    }

    [Fact]
    public async Task A_hello_that_says_launch_false_asks_whether_the_app_is_there_without_starting_it()
    {
        // the toolbar popup does this on every open: a user who closed ShadeNet must not get it back for looking
        var data = Path.Combine(Path.GetTempPath(), "shadenet-host-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "host.json"), "{\"appPath\":\"" + FindHost().Replace("\\", "\\\\") + "\"}");   // would start (the wrong program) if it tried

            using var host = StartHost("ShadeNetTest." + Guid.NewGuid().ToString("N"), ("SHADENET_DATA_DIR", data));
            await SendToHost(host, new { type = "hello", launch = false });

            var frame = await Framing.ReadAsync(host.StandardOutput.BaseStream, 1 << 20).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("app-not-running", BridgeMessage.Parse(frame!)!.As<HostError>()!.Code);
            Assert.True(host.WaitForExit(10_000));

            var log = File.ReadAllText(Path.Combine(data, "host.log"));
            Assert.DoesNotContain("launched", log);
        }
        finally
        {
            try { Directory.Delete(data, true); } catch { /* the log may still be open for a moment */ }
        }
    }

    [Fact]
    public async Task The_first_frame_reaches_the_app_after_the_introduction()
    {
        var pipe = "ShadeNetTest." + Guid.NewGuid().ToString("N");
        var received = new List<BridgeMessage>();
        await using var server = new BridgeServer(pipe, async c =>
        {
            while (await c.ReadAsync() is { } m) lock (received) received.Add(m);
        });
        server.Start();

        using var host = StartHost(pipe);
        await SendToHost(host, new { type = "hello", browser = "chromium", launch = false });
        await Task.Delay(1000);

        lock (received)
        {
            Assert.Equal(new[] { MessageTypes.HostHello, MessageTypes.Hello }, received.Select(m => m.Type).ToArray());
        }

        host.StandardInput.Close();
        host.WaitForExit(10_000);
    }

    [Fact]
    public async Task Large_frames_pass_through_unchanged()
    {
        var pipe = "ShadeNetTest." + Guid.NewGuid().ToString("N");
        string? seen = null;
        await using var server = new BridgeServer(pipe, async c =>
        {
            while (await c.ReadAsync() is { } m)
                if (m.Type == MessageTypes.Capture)
                {
                    seen = m.As<CaptureRequest>()!.Cookies;
                    await c.SendAsync(new CaptureResult { Id = "big", Accepted = true });
                }
        });
        server.Start();

        using var host = StartHost(pipe);
        var cookies = new string('c', 300_000);
        await SendToHost(host, new CaptureRequest { Id = "big", Url = "https://e/x.zip", Cookies = cookies });
        await Framing.ReadAsync(host.StandardOutput.BaseStream, 1 << 20).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(cookies, seen);
        host.StandardInput.Close();
        host.WaitForExit(10_000);
    }
}
