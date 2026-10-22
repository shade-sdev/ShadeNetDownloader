using System.IO.Pipes;
using System.Text.Json;

namespace ShadeNet.Bridge;

/// <summary>One connected peer on the bridge pipe (a native host, or a second app instance asking to be activated).</summary>
public sealed class BridgeConnection : IAsyncDisposable
{
    readonly Stream _stream;
    readonly SemaphoreSlim _writeGate = new(1, 1);
    readonly int _maxIncoming;

    public BridgeConnection(Stream stream, int maxIncoming = BridgeProtocol.MaxFromBrowserBytes)
    {
        _stream = stream;
        _maxIncoming = maxIncoming;
    }

    public Stream Stream => _stream;

    /// <summary>Reads the next frame; <c>null</c> when the peer went away. Frames that are not valid JSON objects are skipped.</summary>
    public async Task<BridgeMessage?> ReadAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var frame = await Framing.ReadAsync(_stream, _maxIncoming, ct).ConfigureAwait(false);
            if (frame == null) return null;
            var message = BridgeMessage.Parse(frame);
            if (message != null) return message;
        }
    }

    public async Task SendAsync<T>(T message, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, BridgeProtocol.Json);
        await SendRawAsync(bytes, ct).ConfigureAwait(false);
    }

    public async Task SendRawAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Framing.WriteAsync(_stream, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return _stream.DisposeAsync();
    }
}

/// <summary>
/// The app's end of the pipe. Only the current Windows user can connect (<see cref="PipeOptions.CurrentUserOnly"/>), and
/// the extension's identity is enforced one step earlier by the native messaging manifest's <c>allowed_origins</c>.
/// </summary>
public sealed class BridgeServer : IAsyncDisposable
{
    readonly string _pipeName;
    readonly Func<BridgeConnection, Task> _onConnection;
    readonly CancellationTokenSource _stop = new();
    readonly List<Task> _clients = new();
    readonly List<BridgeConnection> _connections = new();
    Task? _acceptLoop;

    public BridgeServer(string pipeName, Func<BridgeConnection, Task> onConnection)
    {
        _pipeName = pipeName;
        _onConnection = onConnection;
    }

    /// <summary>
    /// Starts listening. Returns <c>false</c> when another instance already owns the pipe name (this process is then
    /// the second instance and should hand over to the first).
    /// </summary>
    public bool Start()
    {
        NamedPipeServerStream first;
        try
        {
            first = Create(firstInstance: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the name is taken (Windows reports that as "access denied" for a FirstPipeInstance)
            return false;
        }

        _acceptLoop = Task.Run(() => AcceptLoopAsync(first));
        return true;
    }

    // With the default zero-size buffer a write only completes when the peer reads it, so one slow reader would stall
    // whoever writes to it (settings pushes, replies). A real buffer decouples them.
    const int PipeBufferBytes = 64 * 1024;

    NamedPipeServerStream Create(bool firstInstance) =>
        new(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            PipeBufferBytes, PipeBufferBytes);

    async Task AcceptLoopAsync(NamedPipeServerStream pipe)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                pipe = Create(firstInstance: false);
                continue;
            }

            var connected = pipe;
            pipe = Create(firstInstance: false);   // the next client can connect while this one is served

            var task = Task.Run(async () =>
            {
                await using var connection = new BridgeConnection(connected);
                lock (_connections) _connections.Add(connection);
                try
                {
                    await _onConnection(connection).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or BridgeProtocolException or OperationCanceledException or ObjectDisposedException)
                {
                    // a misbehaving or vanished peer must never take the app down
                }
                finally
                {
                    lock (_connections) _connections.Remove(connection);
                }
            });

            lock (_clients)
            {
                _clients.RemoveAll(t => t.IsCompleted);
                _clients.Add(task);
            }
        }

        await pipe.DisposeAsync().ConfigureAwait(false);
    }

    int _disposed;

    public async ValueTask DisposeAsync()
    {
        // Shutdown reaches this from more than one place (the host's service container and the app's own exit); the second
        // call must be a no-op, not an ObjectDisposedException that keeps the process from ending.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop != null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // shutting down
            }
        }

        // Closing the pipes ends every peer's read loop: a host must not outlive the app that it was talking to.
        BridgeConnection[] open;
        lock (_connections) open = _connections.ToArray();
        foreach (var connection in open)
        {
            try
            {
                await connection.Stream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // already gone
            }
        }

        Task[] clients;
        lock (_clients) clients = _clients.ToArray();
        try
        {
            await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // peers that do not leave are abandoned with the process
        }

        _stop.Dispose();
    }
}

/// <summary>Connects to the app's pipe (host side, or a second app instance).</summary>
public static class BridgeClient
{
    public static async Task<BridgeConnection?> TryConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout, ct).ConfigureAwait(false);
            return new BridgeConnection(pipe, BridgeProtocol.MaxToBrowserBytes);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }
}
