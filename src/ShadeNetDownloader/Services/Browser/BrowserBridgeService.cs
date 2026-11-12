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

    /// <summary>
    /// Tells a running instance to come to the front, with the links this launch was given (a magnet link or a .torrent file Windows
    /// opened with ShadeNet): it shows them in its Add dialog. Used by the second instance before it exits.
    /// </summary>
    public static async Task<bool> ActivateRunningInstanceAsync(string? pipeName = null, IReadOnlyList<string>? links = null)
    {
        pipeName ??= BridgeProtocol.PipeName();

        // Connecting to a pipe that does not exist waits out the whole timeout; checking first keeps normal starts instant.
        if (!PipeExists(pipeName)) return false;

        await using var connection = await BridgeClient.TryConnectAsync(pipeName, ConnectTimeout);
        if (connection == null) return false;
        if (links is { Count: > 0 }) await connection.SendAsync(new { type = MessageTypes.Activate, links });
        else await connection.SendAsync(new { type = MessageTypes.Activate });
        return true;
    }

    /// <summary>The links an activation carries: only what reads as a link (another program on this computer may write to the pipe too).</summary>
    internal static IReadOnlyList<string> LinksOf(BridgeMessage message)
    {
        if (!message.Body.TryGetProperty("links", out var list) || list.ValueKind != System.Text.Json.JsonValueKind.Array) return Array.Empty<string>();
        return list.EnumerateArray()
            .Where(l => l.ValueKind == System.Text.Json.JsonValueKind.String && l.GetString() is { Length: > 0 and <= 8192 })
            .Take(50)
            .SelectMany(l => Util.UrlParser.Extract(l.GetString()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // The pipe is known to exist, so this is only the wait for the running instance to listen again after its last client
    // (a busy computer can take a moment to get there).
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether a pipe of this name is open, found by listing the pipes. Asking for its attributes (<c>File.Exists</c>) would OPEN it:
    /// the running instance sees a client that leaves at once, and until it listens again the real connection finds the pipe busy.
    /// </summary>
    internal static bool PipeExists(string pipeName)
    {
        var path = @"\\.\pipe\" + pipeName;
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;   // the list cannot be read: the connection attempt decides
        }
    }

    /// <summary>Asks a running instance to quit for real. <c>false</c> when none is running.</summary>
    public static async Task<bool> QuitRunningInstanceAsync(string? pipeName = null)
    {
        pipeName ??= BridgeProtocol.PipeName();
        if (!PipeExists(pipeName)) return false;

        await using var connection = await BridgeClient.TryConnectAsync(pipeName, ConnectTimeout);
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
            MagnetLinks = browser.MagnetLinks,
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

                    case MessageTypes.Grab:
                        if (message.As<GrabRequest>() is { } grab)
                        {
                            var accepted = Uri.TryCreate(grab.PageUrl, UriKind.Absolute, out var page) && page.Scheme is "http" or "https";
                            if (accepted)
                            {
                                var grabbed = ShadeNet.Core.Links.LinkGrabber.Clean((grab.Links ?? Array.Empty<GrabbedLinkItem>()).Take(10_000)
                                    .Select(l => new ShadeNet.Core.Links.GrabbedLink(l.Url ?? "", l.Text)), page);
                                var cookies = grab.Cookies?.Where(c => c.Key.Length < 256 && c.Value.Length < 16_384 && !c.Value.Contains('\n'))
                                    .Take(30).ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
                                var title = grab.Title is { Length: > 0 } t ? t[..Math.Min(t.Length, 300)] : null;
                                _ui.BringToFront();
                                _ui.ShowGrabber(new ShadeNet.Core.Links.GrabbedPage(page!.AbsoluteUri, title, grabbed), cookies,
                                    grab.UserAgent is { Length: < 1024 } ua ? ua : null);
                            }

                            await connection.SendAsync(new { type = MessageTypes.CaptureResult, id = grab.Id, accepted }).ConfigureAwait(false);
                        }

                        break;

                    case MessageTypes.Ping:
                        await connection.SendAsync(new { type = MessageTypes.Pong }).ConfigureAwait(false);
                        break;

                    case MessageTypes.Activate:
                        _ui.BringToFront();
                        if (LinksOf(message) is { Count: > 0 } links) _ui.OpenLinks(links);
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
