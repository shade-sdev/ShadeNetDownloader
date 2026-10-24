using System.IO;
using ShadeNet.Bridge;

namespace ShadeNetDownloader.Services.Browser;

/// <summary>What the rest of the app may ask of the browser link.</summary>
public interface IBrowserBridge
{
    /// <summary>At least one browser extension is connected right now.</summary>
    bool IsBrowserConnected { get; }

    /// <summary>The next capture that is the same file as this download refreshes its address.</summary>
    void ExpectRefresh(Guid jobId);

    /// <summary>Tell connected extensions that the app's browser settings changed.</summary>
    Task PushSettingsAsync();

    event Action? StatusChanged;
}

/// <summary>
/// The app's end of the browser integration: owns the named pipe the native host connects to, answers the extension's
/// hello with the app's settings, and passes captures to the <see cref="CaptureProcessor"/>. A second app instance
/// finds the pipe taken, asks this one to come to the front, and exits (single instance, like IDM's).
/// </summary>
public sealed class BrowserBridgeService : IBrowserBridge, IAsyncDisposable
{
    readonly CaptureProcessor _processor;
    readonly SettingsService _settings;
    readonly ICaptureUi _ui;
    readonly string _appVersion;
    readonly object _gate = new();
    readonly List<BridgeConnection> _browsers = new();
    BridgeServer? _server;

    public BrowserBridgeService(CaptureProcessor processor, SettingsService settings, ICaptureUi ui, string appVersion)
    {
        _processor = processor;
        _settings = settings;
        _ui = ui;
        _appVersion = appVersion;
    }

    public event Action? StatusChanged;

    public bool IsBrowserConnected
    {
        get
        {
            lock (_gate) return _browsers.Count > 0;
        }
    }

    public void ExpectRefresh(Guid jobId) => _processor.ExpectRefresh(jobId);

    /// <returns><c>false</c> when another instance already owns the pipe.</returns>
    public bool Start(string? pipeName = null)
    {
        _server = new BridgeServer(pipeName ?? BridgeProtocol.PipeName(), OnConnectionAsync);
        return _server.Start();
    }

    /// <summary>Tells a running instance to come to the front. Used by the second instance before it exits.</summary>
    public static async Task<bool> ActivateRunningInstanceAsync(string? pipeName = null)
    {
        pipeName ??= BridgeProtocol.PipeName();

        // Connecting to a pipe that does not exist waits out the whole timeout; checking first keeps normal starts instant.
        if (!File.Exists(@"\\.\pipe\" + pipeName)) return false;

        await using var connection = await BridgeClient.TryConnectAsync(pipeName, TimeSpan.FromMilliseconds(800));
        if (connection == null) return false;
        await connection.SendAsync(new { type = MessageTypes.Activate });
        return true;
    }

    /// <summary>Asks a running instance to quit for real. <c>false</c> when none is running.</summary>
    public static async Task<bool> QuitRunningInstanceAsync(string? pipeName = null)
    {
        pipeName ??= BridgeProtocol.PipeName();
        if (!File.Exists(@"\\.\pipe\" + pipeName)) return false;

        await using var connection = await BridgeClient.TryConnectAsync(pipeName, TimeSpan.FromMilliseconds(800));
        if (connection == null) return false;
        await connection.SendAsync(new { type = MessageTypes.Quit });
        return true;
    }

    /// <summary>Settings changed in the app: tell every connected extension.</summary>
    public async Task PushSettingsAsync()
    {
        BridgeConnection[] browsers;
        lock (_gate) browsers = _browsers.ToArray();

        var message = CurrentSettings(MessageTypes.Settings);
        foreach (var browser in browsers)
        {
            try
            {
                await browser.SendAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // that browser is gone; its connection loop cleans up
            }
        }
    }

    BridgeSettings CurrentSettings(string type)
    {
        var browser = _settings.Current.Browser;
        return new BridgeSettings
        {
            Type = type,
            AppVersion = _appVersion,
            Enabled = browser.Enabled,
            ExcludedHosts = browser.ExcludedHosts.ToArray(),
            MinSizeBytes = browser.MinSizeBytes,
            PageSignIn = browser.PageSignIn,
        };
    }

    async Task OnConnectionAsync(BridgeConnection connection)
    {
        var isBrowser = false;
        try
        {
            while (await connection.ReadAsync().ConfigureAwait(false) is { } message)
            {
                switch (message.Type)
                {
                    case MessageTypes.HostHello:
                    case MessageTypes.Hello:
                        if (!isBrowser)
                        {
                            isBrowser = true;
                            lock (_gate) _browsers.Add(connection);
                            StatusChanged?.Invoke();
                        }

                        if (message.Type == MessageTypes.Hello)
                            await connection.SendAsync(CurrentSettings(MessageTypes.HelloAck)).ConfigureAwait(false);
                        break;

                    case MessageTypes.Capture:
                        if (message.As<CaptureRequest>() is { } request)
                            await connection.SendAsync(await _processor.HandleAsync(request).ConfigureAwait(false)).ConfigureAwait(false);
                        break;

                    case MessageTypes.Ping:
                        await connection.SendAsync(new { type = MessageTypes.Pong }).ConfigureAwait(false);
                        break;

                    case MessageTypes.Activate:
                        _ui.BringToFront();
                        break;

                    case MessageTypes.Quit:
                        _ui.Quit();
                        break;
                }
            }
        }
        finally
        {
            if (isBrowser)
            {
                lock (_gate) _browsers.Remove(connection);
                StatusChanged?.Invoke();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server != null) await _server.DisposeAsync().ConfigureAwait(false);
    }
}
