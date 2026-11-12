using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using MonoTorrent;
using MonoTorrent.Connections.Peer;
using MonoTorrent.Connections.Tracker;
using MonoTorrent.Messages.UdpTracker;
using MonoTorrent.Trackers;
using ReusableTasks;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Net;
using EngineSettings = ShadeNet.Core.Engine.EngineSettings;

namespace ShadeNet.Core.Torrents;

/// <summary>A network adapter of this computer, as Settings lists them.</summary>
/// <param name="Id">Windows' id for it (what <see cref="EngineSettings.NetworkAdapter"/> keeps).</param>
/// <param name="Connected">It is up and has an address torrents can use.</param>
public sealed record NetworkAdapterInfo(string Id, string Name, string Description, bool Connected, IReadOnlyList<IPAddress> Addresses);

public static class NetworkAdapters
{
    /// <summary>The adapters torrents can be bound to (not the loopback one), connected ones first.</summary>
    public static IReadOnlyList<NetworkAdapterInfo> List()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) || IsVpnTunnel(n))
                .Select(InfoOf)
                .OrderByDescending(a => a.Connected)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<NetworkAdapterInfo>();
        }
    }

    // (a Teredo or 6to4 adapter is a "tunnel" of Windows' own; a VPN's adapter may call itself a tunnel too, and has an address of its own)
    static bool IsVpnTunnel(NetworkInterface n) =>
        n.NetworkInterfaceType == NetworkInterfaceType.Tunnel
        && !n.Description.Contains("Teredo", StringComparison.OrdinalIgnoreCase)
        && !n.Description.Contains("6to4", StringComparison.OrdinalIgnoreCase)
        && !n.Description.Contains("ISATAP", StringComparison.OrdinalIgnoreCase);

    /// <summary>The adapter with this id, also the loopback one (tests bind torrents to it); null when Windows does not have it now.</summary>
    public static NetworkAdapterInfo? Find(string id)
    {
        try
        {
            var n = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            return n == null ? null : InfoOf(n);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    static NetworkAdapterInfo InfoOf(NetworkInterface n)
    {
        var addresses = new List<IPAddress>();
        try
        {
            addresses.AddRange(n.GetIPProperties().UnicastAddresses.Select(u => u.Address).Where(Usable));
        }
        catch (NetworkInformationException)
        {
            // gone while being looked at
        }

        var up = n.OperationalStatus == OperationalStatus.Up && addresses.Count > 0;
        return new NetworkAdapterInfo(n.Id, n.Name, n.Description, up, addresses);
    }

    /// <summary>An address peers on the internet can reach (or the loopback one): not link-local, not the 169.254 one Windows gives without a network.</summary>
    static bool Usable(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetwork
            ? !a.ToString().StartsWith("169.254.", StringComparison.Ordinal)
            : a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal && !a.IsIPv6Multicast;
}

/// <summary>
/// How torrents reach the network right now: through ShadeNet's proxy, bound to one adapter, both, or as they please.
/// </summary>
/// <param name="Proxy">Every tracker request and every connection to a peer goes through this proxy (and nothing else is used).</param>
/// <param name="HttpProxy">For HTTP trackers and web seeds without <paramref name="Proxy"/>: true uses Windows' proxy, false none.</param>
/// <param name="Locked">Bound to an adapter: only its addresses are used, nothing when it has none.</param>
internal sealed record TorrentRoute(Uri? Proxy, bool HttpProxy, bool Locked, string? AdapterName, IPAddress? V4, IPAddress? V6)
{
    public static TorrentRoute Open { get; } = new(null, true, false, null, null, null);

    /// <summary>Bound to an adapter that is not connected: no traffic at all until it is.</summary>
    public bool Offline => Locked && V4 == null && V6 == null;

    /// <summary>The adapter's address of this kind (null when not bound, or when it has none of that kind).</summary>
    public IPAddress? LocalFor(AddressFamily family) => family == AddressFamily.InterNetworkV6 ? V6 : family == AddressFamily.InterNetwork ? V4 : null;

    /// <summary>What can be used on this route: a family the adapter has (any when not bound).</summary>
    public bool Allows(AddressFamily family) => !Locked || LocalFor(family) != null;

    /// <summary>The DHT, local peers and port forwarding cannot pass a proxy, and are not used through one adapter.</summary>
    public bool Confined => Proxy != null || Locked;
}

/// <summary>
/// Puts torrents on the route the user chose. MonoTorrent makes its connections itself (peers, HTTP and UDP trackers, web seeds); its
/// factories are given ones of ShadeNet's own that go through the proxy (SOCKS4/5 or HTTP CONNECT for peers, the proxy of .NET's HTTP
/// stack for trackers), bind every socket to the chosen adapter's address, and refuse what would leave another way.
/// </summary>
internal sealed class TorrentNetwork : IDisposable
{
    readonly EngineSettings _settings;
    readonly object _gate = new();
    readonly Dictionary<(TorrentRoute, AddressFamily), SocketsHttpHandler> _handlers = new();
    readonly List<SocketsHttpHandler> _retired = new();
    TorrentRoute _route;
    Timer? _debounce;
    bool _disposed;

    /// <summary>MonoTorrent's own client name for trackers (some refuse clients they do not know).</summary>
    static readonly string UserAgent = TryUserAgent();

    public TorrentNetwork(EngineSettings settings)
    {
        _settings = settings;
        _route = Compute();
        Factories = MonoTorrent.Factories.Default
            .WithPeerConnectionCreator("ipv4", uri => new SocketPeerConnection(uri, new RoutedSocketConnector(this)))
            .WithPeerConnectionCreator("ipv6", uri => new SocketPeerConnection(uri, new RoutedSocketConnector(this)))
            .WithHttpClientCreator(CreateHttpClient)
            .WithTrackerCreator("http", HttpTracker)
            .WithTrackerCreator("https", HttpTracker)
            .WithTrackerCreator("udp", uri => new Tracker(new RoutedUdpTrackerConnection(this, uri, AddressFamily.InterNetwork),
                new RoutedUdpTrackerConnection(this, uri, AddressFamily.InterNetworkV6)));
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    public MonoTorrent.Factories Factories { get; }

    public TorrentRoute Route => _route;

    /// <summary>The route changed (an adapter came or went, its address changed, or the settings did).</summary>
    public event Action<TorrentRoute>? RouteChanged;

    /// <summary>Works the route out again from the settings and the adapters; raises <see cref="RouteChanged"/> when it is not what it was.</summary>
    public TorrentRoute Refresh()
    {
        var next = Compute();
        bool changed;
        lock (_gate)
        {
            changed = next != _route;
            _route = next;
        }

        if (changed) RouteChanged?.Invoke(next);
        return next;
    }

    TorrentRoute Compute() => RouteFor(_settings);

    /// <summary>The route the settings and the adapters give now (the adapter as ShadeNet's network lock has it: see <see cref="NetworkLock"/>).</summary>
    internal static TorrentRoute RouteFor(EngineSettings settings)
    {
        Uri? proxy = null;
        var httpProxy = settings.Proxy != ProxyMode.None;
        if (settings.Proxy == ProxyMode.Manual && settings.TorrentUseProxy && HttpFetcher.TryParseProxy(settings.ProxyAddress) is { } p) proxy = p;
        else if (settings.Proxy == ProxyMode.Manual && !settings.TorrentUseProxy) httpProxy = false;

        var binding = NetworkLock.Resolve(settings);
        return new TorrentRoute(proxy, httpProxy, binding.Locked, binding.AdapterName, binding.V4, binding.V6);
    }

    void OnAddressChanged(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_settings.NetworkAdapter)) return;
        // (Windows raises it several times for one change: once things have settled)
        lock (_gate)
        {
            if (_disposed) return;
            _debounce?.Dispose();
            _debounce = new Timer(_ =>
            {
                NetworkLock.Invalidate();
                Refresh();
            }, null, 1500, Timeout.Infinite);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Sockets
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A TCP connection to a host on this route: bound to the adapter's address when bound to one (so it leaves through that adapter
    /// or not at all), of the given kind when one is asked for.
    /// </summary>
    internal static async Task<Socket> ConnectTcpAsync(string host, int port, AddressFamily family, TorrentRoute route, CancellationToken ct)
    {
        if (route.Offline) throw new SocketException((int)SocketError.NetworkDown);

        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) addresses = new[] { literal };
        else addresses = await Dns.GetHostAddressesAsync(host, family is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 ? family : AddressFamily.Unspecified, ct)
            .ConfigureAwait(false);

        Exception? last = null;
        foreach (var address in addresses.Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            if (family is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && address.AddressFamily != family) continue;
            if (!route.Allows(address.AddressFamily)) continue;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                if (route.LocalFor(address.AddressFamily) is { } local) socket.Bind(new IPEndPoint(local, 0));
                await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                return socket;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                socket.Dispose();
                ct.ThrowIfCancellationRequested();
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>Connections to peers: through the proxy, or bound to the adapter.</summary>
    sealed class RoutedSocketConnector : MonoTorrent.Connections.ISocketConnector
    {
        readonly TorrentNetwork _network;

        public RoutedSocketConnector(TorrentNetwork network) => _network = network;

        public async ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken token)
        {
            var route = _network.Route;
            var target = new IPEndPoint(IPAddress.Parse(uri.Host.Trim('[', ']')), uri.Port);
            if (route.Proxy is not { } proxy)
                return await ConnectTcpAsync(target.Address.ToString(), target.Port, target.AddressFamily, route, token).ConfigureAwait(false);

            var socket = await ConnectTcpAsync(proxy.Host, proxy.Port, AddressFamily.Unspecified, route, token).ConfigureAwait(false);
            try
            {
                await ProxyTunnel.OpenAsync(socket, proxy, target, token).ConfigureAwait(false);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // HTTP (trackers and web seeds)
    // ------------------------------------------------------------------------------------------------------------

    HttpClient CreateHttpClient(AddressFamily family)
    {
        var route = Route;
        SocketsHttpHandler handler;
        lock (_gate)
        {
            if (!_handlers.TryGetValue((route, family), out handler!))
            {
                // the handlers of a route that is gone are kept until the end (a request may still be using one)
                foreach (var stale in _handlers.Where(h => h.Key.Item1 != route).ToList())
                {
                    _retired.Add(stale.Value);
                    _handlers.Remove(stale.Key);
                }

                _handlers[(route, family)] = handler = CreateHandler(route, family);
            }
        }

        var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    static SocketsHttpHandler CreateHandler(TorrentRoute route, AddressFamily family)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (route.Proxy is { } proxy)
        {
            handler.UseProxy = true;
            handler.Proxy = new EveryRequest(HttpFetcher.WebProxyOf(proxy));
        }
        else if (!route.HttpProxy)
        {
            handler.UseProxy = false;
        }

        // (through a proxy, the connection is to the proxy: any kind of address it has; otherwise only the kind MonoTorrent asked for,
        // as it announces over IPv4 and IPv6 separately)
        var kind = route.Proxy != null ? AddressFamily.Unspecified : family;
        handler.ConnectCallback = async (context, ct) =>
        {
            var socket = await ConnectTcpAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, kind, route, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        };
        return handler;
    }

    /// <summary>The proxy for every request (.NET's own leaves out addresses on this computer: a torrent's traffic goes through it all the same).</summary>
    sealed class EveryRequest : IWebProxy
    {
        readonly WebProxy _proxy;

        public EveryRequest(WebProxy proxy) => _proxy = proxy;

        public ICredentials? Credentials
        {
            get => _proxy.Credentials;
            set => _proxy.Credentials = value;
        }

        public Uri GetProxy(Uri destination) => _proxy.Address!;

        public bool IsBypassed(Uri host) => false;
    }

    ITracker HttpTracker(Uri uri)
    {
        Func<AddressFamily, HttpClient> create = CreateHttpClient;
        return new Tracker(new RoutedHttpTrackerConnection(this, new HttpTrackerConnection(uri, create, AddressFamily.InterNetwork)),
            new RoutedHttpTrackerConnection(this, new HttpTrackerConnection(uri, create, AddressFamily.InterNetworkV6)));
    }

    static string TryUserAgent()
    {
        try
        {
            using var probe = MonoTorrent.Factories.Default.CreateHttpClient(AddressFamily.Unspecified);
            var ua = probe.DefaultRequestHeaders.UserAgent.ToString();
            if (!string.IsNullOrWhiteSpace(ua)) return ua;
        }
        catch (Exception)
        {
            // the name below
        }

        return "MonoTorrent/3.0.2";
    }

    /// <summary>Why a tracker is not asked on this route (null: it is).</summary>
    internal static string? NotUsed(TorrentRoute route, Uri uri, AddressFamily family)
    {
        if (route.Offline) return $"Waiting for {route.AdapterName} to connect";
        var udp = uri.Scheme == "udp";
        if (udp && route.Proxy != null) return "Not used: torrents go through the proxy, which cannot carry UDP trackers";
        // (one request through the proxy is enough: it is the proxy's address the tracker sees either way)
        if (route.Proxy != null) return family == AddressFamily.InterNetworkV6 ? "Asked over IPv4 through the proxy" : null;
        if (!route.Allows(family)) return $"{route.AdapterName} has no {(family == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4")} address";
        return null;
    }

    sealed class RoutedHttpTrackerConnection : ITrackerConnection
    {
        readonly TorrentNetwork _network;
        readonly HttpTrackerConnection _inner;

        public RoutedHttpTrackerConnection(TorrentNetwork network, HttpTrackerConnection inner)
        {
            _network = network;
            _inner = inner;
        }

        public bool CanScrape => _inner.CanScrape;
        public Uri Uri => _inner.Uri;

        public ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest parameters, CancellationToken token) =>
            NotUsed(_network.Route, Uri, _inner.AddressFamily) is { } why
                ? ReusableTask.FromResult(new AnnounceResponse(TrackerState.Offline, failureMessage: why))
                : _inner.AnnounceAsync(parameters, token);

        public ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest parameters, CancellationToken token) =>
            NotUsed(_network.Route, Uri, _inner.AddressFamily) is { } why
                ? ReusableTask.FromResult(new ScrapeResponse(TrackerState.Offline, failureMessage: why))
                : _inner.ScrapeAsync(parameters, token);
    }

    // ------------------------------------------------------------------------------------------------------------
    // UDP trackers (BEP 15)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A UDP tracker, asked from the chosen adapter's address (MonoTorrent's own asks from wherever Windows routes it). Its answer's counts
    /// of seeders and leechers are kept too, which MonoTorrent's drops: the swarm's size shows without a separate scrape.
    /// </summary>
    sealed class RoutedUdpTrackerConnection : ITrackerConnection
    {
        readonly TorrentNetwork _network;
        readonly AddressFamily _family;
        long _connectionId;
        long _connectedAt;

        public RoutedUdpTrackerConnection(TorrentNetwork network, Uri uri, AddressFamily family)
        {
            _network = network;
            Uri = uri;
            _family = family;
        }

        public bool CanScrape => true;
        public Uri Uri { get; }

        /// <summary>How long one exchange may take, its request sent again on the way (BEP 15 resends a lost one).</summary>
        internal static TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(25);

        public async ReusableTask<AnnounceResponse> AnnounceAsync(AnnounceRequest parameters, CancellationToken token)
        {
            var route = _network.Route;
            if (NotUsed(route, Uri, _family) is { } why) return new AnnounceResponse(TrackerState.Offline, failureMessage: why);
            try
            {
                using var udp = await OpenAsync(route, token).ConfigureAwait(false);
                var id = await ConnectionIdAsync(udp, token).ConfigureAwait(false);
                var port = parameters.GetReportedAddress(_family == AddressFamily.InterNetwork ? "ipv4" : "ipv6").Item2;
                var peers = new Dictionary<InfoHash, IList<PeerInfo>>();
                var scrape = new Dictionary<InfoHash, ScrapeInfo>();
                TimeSpan? interval = null;
                foreach (var infoHash in new[] { parameters.InfoHashes.V1, parameters.InfoHashes.V2 }.Where(h => h != null))
                {
                    var answer = await ExchangeAsync(udp, new AnnounceMessage(NewTransaction(), id, parameters, infoHash!, port), token).ConfigureAwait(false);
                    if (answer is ErrorMessage error)
                    {
                        _connectedAt = 0;
                        return new AnnounceResponse(TrackerState.InvalidResponse, failureMessage: error.Error ?? "The tracker refused");
                    }

                    if (answer is not AnnounceResponseMessage a) return new AnnounceResponse(TrackerState.InvalidResponse, failureMessage: "The tracker's answer was not one");
                    peers[infoHash!] = a.Peers;
                    scrape[infoHash!] = new ScrapeInfo(a.Seeders, 0, a.Leechers);
                    interval = a.Interval;
                }

                var every = interval is { } i ? TimeSpan.FromSeconds(Math.Clamp(i.TotalSeconds, 60, 7200)) : (TimeSpan?)null;
                return new AnnounceResponse(TrackerState.Ok, peers, updateInterval: every, scrapeInfo: scrape);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _connectedAt = 0;
                return new AnnounceResponse(TrackerState.Offline, failureMessage: "No answer");
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException or ArgumentException)
            {
                _connectedAt = 0;
                return new AnnounceResponse(TrackerState.Offline, failureMessage: ex is SocketException s && s.SocketErrorCode == SocketError.HostNotFound
                    ? $"Its name does not resolve to an {(_family == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4")} address"
                    : "No answer");
            }
        }

        public async ReusableTask<ScrapeResponse> ScrapeAsync(ScrapeRequest parameters, CancellationToken token)
        {
            var route = _network.Route;
            if (NotUsed(route, Uri, _family) is { } why) return new ScrapeResponse(TrackerState.Offline, failureMessage: why);
            try
            {
                using var udp = await OpenAsync(route, token).ConfigureAwait(false);
                var id = await ConnectionIdAsync(udp, token).ConfigureAwait(false);
                var hashes = new[] { parameters.InfoHashes.V1, parameters.InfoHashes.V2 }.Where(h => h != null).Select(h => h!).ToList();
                var answer = await ExchangeAsync(udp, new ScrapeMessage(NewTransaction(), id, hashes), token).ConfigureAwait(false);
                if (answer is ErrorMessage error) return new ScrapeResponse(TrackerState.InvalidResponse, failureMessage: error.Error ?? "The tracker refused");
                if (answer is not ScrapeResponseMessage s) return new ScrapeResponse(TrackerState.InvalidResponse, failureMessage: "The tracker's answer was not one");
                var info = new Dictionary<InfoHash, ScrapeInfo>();
                for (var i = 0; i < s.Scrapes.Count && i < hashes.Count; i++)
                    info[hashes[i]] = new ScrapeInfo(s.Scrapes[i].Seeds, s.Scrapes[i].Complete, s.Scrapes[i].Leeches);
                return new ScrapeResponse(TrackerState.Ok, info);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException or ArgumentException)
            {
                _connectedAt = 0;
                token.ThrowIfCancellationRequested();
                return new ScrapeResponse(TrackerState.Offline, failureMessage: "No answer");
            }
        }

        /// <summary>A UDP socket of this connection's kind, bound to the adapter's address when bound to one, "connected" to the tracker.</summary>
        async Task<UdpClient> OpenAsync(TorrentRoute route, CancellationToken token)
        {
            var host = Uri.Host.Trim('[', ']');
            var address = IPAddress.TryParse(host, out var literal)
                ? literal
                : (await Dns.GetHostAddressesAsync(host, _family, token).ConfigureAwait(false)).FirstOrDefault(a => a.AddressFamily == _family);
            if (address == null || address.AddressFamily != _family) throw new SocketException((int)SocketError.HostNotFound);

            var local = route.LocalFor(_family) ?? (_family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any);
            var udp = new UdpClient(new IPEndPoint(local, 0));
            try
            {
                udp.Connect(address, Uri.Port);
                return udp;
            }
            catch
            {
                udp.Dispose();
                throw;
            }
        }

        async Task<long> ConnectionIdAsync(UdpClient udp, CancellationToken token)
        {
            // (an id is good for two minutes: one is asked for again after one)
            if (_connectedAt != 0 && Stopwatch.GetElapsedTime(_connectedAt) < TimeSpan.FromMinutes(1)) return _connectionId;
            var answer = await ExchangeAsync(udp, new ConnectMessage(), token).ConfigureAwait(false);
            if (answer is ErrorMessage error) throw new ArgumentException(error.Error ?? "The tracker refused");
            if (answer is not ConnectResponseMessage connected) throw new ArgumentException("The tracker's answer was not one");
            _connectionId = connected.ConnectionId;
            _connectedAt = Stopwatch.GetTimestamp();
            return _connectionId;
        }

        static int NewTransaction() => Random.Shared.Next();

        /// <summary>Sends a request (again after 5 and 12 seconds if no answer came) and waits for the answer to it.</summary>
        async Task<UdpTrackerMessage> ExchangeAsync(UdpClient udp, UdpTrackerMessage request, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(Deadline);
            var bytes = request.Encode();
            var resend = Task.Run(async () =>
            {
                foreach (var wait in new[] { 0, 5000, 7000 })
                {
                    await Task.Delay(wait, deadline.Token).ConfigureAwait(false);
                    await udp.SendAsync(bytes, deadline.Token).ConfigureAwait(false);
                }
            }, deadline.Token);

            try
            {
                while (true)
                {
                    var received = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                    UdpTrackerMessage answer;
                    try
                    {
                        answer = UdpTrackerMessage.DecodeMessage(received.Buffer, MessageType.Response, _family);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        continue;   // not an answer: the next one may be
                    }

                    if (answer.TransactionId == request.TransactionId) return answer;
                }
            }
            finally
            {
                deadline.Cancel();
                try
                {
                    await resend.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // stopped
                }
            }
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        lock (_gate)
        {
            _disposed = true;
            _debounce?.Dispose();
            foreach (var h in _handlers.Values.Concat(_retired)) h.Dispose();
            _handlers.Clear();
            _retired.Clear();
        }
    }
}

/// <summary>Opens a tunnel to a peer through a proxy, on a socket already connected to the proxy: SOCKS5, SOCKS4(a) or HTTP CONNECT.</summary>
internal static class ProxyTunnel
{
    public static async Task OpenAsync(Socket socket, Uri proxy, IPEndPoint target, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        switch (proxy.Scheme)
        {
            case "socks5" or "socks5h":
                await Socks5Async(socket, HttpFetcher.CredentialsOf(proxy), target, token).ConfigureAwait(false);
                break;
            case "socks4" or "socks4a":
                await Socks4Async(socket, HttpFetcher.CredentialsOf(proxy)?.UserName ?? "", target, token).ConfigureAwait(false);
                break;
            case "http":
                await HttpConnectAsync(socket, HttpFetcher.CredentialsOf(proxy), target, token).ConfigureAwait(false);
                break;
            default:
                // (the connection to an https proxy is itself encrypted, which a peer connection, a plain socket, cannot be)
                throw new NotSupportedException("An https proxy cannot carry connections to peers; use its http or socks5 address.");
        }
    }

    static async Task Socks5Async(Socket socket, NetworkCredential? credentials, IPEndPoint target, CancellationToken ct)
    {
        await SendAsync(socket, credentials != null ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct).ConfigureAwait(false);
        var choice = await ReceiveAsync(socket, 2, ct).ConfigureAwait(false);
        if (choice[0] != 5) throw new ProxyException("The proxy does not speak SOCKS5.");
        if (choice[1] == 2)
        {
            if (credentials == null) throw new ProxyException("The proxy wants a sign-in.");
            var user = Encoding.UTF8.GetBytes(credentials.UserName);
            var pass = Encoding.UTF8.GetBytes(credentials.Password);
            if (user.Length > 255 || pass.Length > 255) throw new ProxyException("The proxy's user name or password is too long.");
            var auth = new byte[3 + user.Length + pass.Length];
            auth[0] = 1;
            auth[1] = (byte)user.Length;
            user.CopyTo(auth, 2);
            auth[2 + user.Length] = (byte)pass.Length;
            pass.CopyTo(auth, 3 + user.Length);
            await SendAsync(socket, auth, ct).ConfigureAwait(false);
            var ok = await ReceiveAsync(socket, 2, ct).ConfigureAwait(false);
            if (ok[1] != 0) throw new ProxyException("The proxy refused the user name or password.");
        }
        else if (choice[1] != 0)
        {
            throw new ProxyException("The proxy refused every sign-in method ShadeNet offered.");
        }

        var address = target.Address.GetAddressBytes();
        var request = new byte[4 + address.Length + 2];
        request[0] = 5;
        request[1] = 1;   // CONNECT
        request[3] = (byte)(address.Length == 4 ? 1 : 4);
        address.CopyTo(request, 4);
        request[^2] = (byte)(target.Port >> 8);
        request[^1] = (byte)target.Port;
        await SendAsync(socket, request, ct).ConfigureAwait(false);

        var head = await ReceiveAsync(socket, 4, ct).ConfigureAwait(false);
        if (head[1] != 0) throw new ProxyException(head[1] switch
        {
            2 => "The proxy does not allow that connection.",
            3 or 4 => "The proxy cannot reach that peer.",
            5 => "The peer refused the proxy's connection.",
            _ => $"The proxy could not connect (SOCKS error {head[1]}).",
        });
        var rest = head[3] switch
        {
            1 => 4,
            4 => 16,
            3 => (await ReceiveAsync(socket, 1, ct).ConfigureAwait(false))[0],
            _ => throw new ProxyException("The proxy's answer was not SOCKS5."),
        };
        await ReceiveAsync(socket, rest + 2, ct).ConfigureAwait(false);   // the address it bound (not needed)
    }

    static async Task Socks4Async(Socket socket, string user, IPEndPoint target, CancellationToken ct)
    {
        if (target.AddressFamily != AddressFamily.InterNetwork) throw new ProxyException("A SOCKS4 proxy cannot reach IPv6 peers.");
        var id = Encoding.ASCII.GetBytes(user);
        var request = new byte[9 + id.Length];
        request[0] = 4;
        request[1] = 1;
        request[2] = (byte)(target.Port >> 8);
        request[3] = (byte)target.Port;
        target.Address.GetAddressBytes().CopyTo(request, 4);
        id.CopyTo(request, 8);
        await SendAsync(socket, request, ct).ConfigureAwait(false);
        var answer = await ReceiveAsync(socket, 8, ct).ConfigureAwait(false);
        if (answer[1] != 0x5A) throw new ProxyException("The proxy could not connect to that peer.");
    }

    static async Task HttpConnectAsync(Socket socket, NetworkCredential? credentials, IPEndPoint target, CancellationToken ct)
    {
        var where = target.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{target.Address}]:{target.Port}" : $"{target.Address}:{target.Port}";
        var request = new StringBuilder($"CONNECT {where} HTTP/1.1\r\nHost: {where}\r\n");
        if (credentials != null)
            request.Append("Proxy-Authorization: Basic ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.UserName}:{credentials.Password}"))).Append("\r\n");
        request.Append("\r\n");
        await SendAsync(socket, Encoding.ASCII.GetBytes(request.ToString()), ct).ConfigureAwait(false);

        // the answer's head, byte by byte (whatever follows it is the peer's)
        var head = new List<byte>(256);
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (head.Count > 8192) throw new ProxyException("The proxy's answer was not HTTP.");
            if (await socket.ReceiveAsync(one, SocketFlags.None, ct).ConfigureAwait(false) == 0) throw new ProxyException("The proxy closed the connection.");
            head.Add(one[0]);
        }

        var status = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n")[0];
        var parts = status.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal)) throw new ProxyException("The proxy's answer was not HTTP.");
        if (parts[1] != "200")
            throw new ProxyException(parts[1] == "407" ? "The proxy wants a sign-in (add user:password@ to its address)." : $"The proxy refused the connection ({status[(parts[0].Length + 1)..]}).");
    }

    static async Task SendAsync(Socket socket, byte[] bytes, CancellationToken ct)
    {
        var sent = 0;
        while (sent < bytes.Length) sent += await socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, ct).ConfigureAwait(false);
    }

    static async Task<byte[]> ReceiveAsync(Socket socket, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, ct).ConfigureAwait(false);
            if (n == 0) throw new ProxyException("The proxy closed the connection.");
            read += n;
        }

        return buffer;
    }
}

/// <summary>The proxy did not open the tunnel (a socket error to MonoTorrent: the peer is tried again later like any unreachable one).</summary>
internal sealed class ProxyException : SocketException
{
    public ProxyException(string message) : base((int)SocketError.ConnectionRefused) => Text = message;

    public string Text { get; }

    public override string Message => Text;
}
