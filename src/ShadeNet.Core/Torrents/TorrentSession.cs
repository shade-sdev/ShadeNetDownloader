using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;
using ShadeNet.Core.Engine;
using EngineSettings = ShadeNet.Core.Engine.EngineSettings;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Storage;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Torrents;

/// <summary>
/// The one torrent engine of a <see cref="DownloadManager"/>: unlike a web download, a torrent client owns things all torrents share (the
/// port peers connect to, the DHT, port forwarding, the total upload and download limits), so there is one, made the first time a torrent
/// is used (no port is opened before that). It also keeps the finished torrents that are still being shared, outside the download queue:
/// sharing never holds up the downloads waiting behind it.
/// </summary>
internal sealed class TorrentSession : IAsyncDisposable
{
    /// <summary>A .torrent file larger than this is not one (even huge torrents have a few megabytes of piece hashes).</summary>
    const int MaxMetadataBytes = 32 * 1024 * 1024;

    readonly EngineSettings _settings;
    readonly Func<long> _globalLimit;
    readonly ILogger _logger;
    readonly SemaphoreSlim _engineGate = new(1, 1);
    readonly SemaphoreSlim _applyGate = new(1, 1);
    readonly TorrentNetwork _network;
    ClientEngine? _engine;

    readonly object _seedGate = new();
    readonly Dictionary<Guid, Seeder> _seeders = new();
    Task? _seedLoop;
    readonly CancellationTokenSource _closing = new();

    sealed class Seeder
    {
        public required DownloadJob Job { get; init; }
        public required TorrentManager Manager { get; init; }
        public required long UploadedBase { get; init; }
        public long LastSaved = Stopwatch.GetTimestamp();
        public readonly RateMeter Up = new();
    }

    public TorrentSession(EngineSettings settings, Func<long> globalLimit, ILogger logger, Func<string, NetworkAdapterInfo?>? findAdapter = null)
    {
        _settings = settings;
        _globalLimit = globalLimit;
        _logger = logger;
        _network = new TorrentNetwork(settings, findAdapter);
        _network.RouteChanged += route => _ = OnRouteChangedAsync(route);
    }

    /// <summary>How torrents reach the network now (the proxy, the adapter they are bound to).</summary>
    public TorrentRoute Route => _network.Route;

    /// <summary>The port peers connect to (0 before the engine is made).</summary>
    public int ListenPort => _port;

    /// <summary>Raised when a shared torrent's progress changed (about once a second) or it stopped being shared.</summary>
    public Action<DownloadJob>? Changed { get; set; }

    public JobStore? Store { get; set; }

    public string Folder => _settings.TorrentFolder
                            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShadeNet", "torrents");

    /// <summary>Where a torrent's .torrent file is kept (the engine keeps those of magnet links there itself, by the same name).</summary>
    public string MetadataPath(string infoHash) => Path.Combine(Folder, "metadata", infoHash.ToUpperInvariant() + ".torrent");

    string FastResumePath(string infoHash) => Path.Combine(Folder, "fastresume", infoHash.ToUpperInvariant() + ".fresume");

    // ------------------------------------------------------------------------------------------------------------
    // The engine
    // ------------------------------------------------------------------------------------------------------------

    public async Task<ClientEngine> EngineAsync()
    {
        if (_engine != null) return _engine;
        await _engineGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_engine != null) return _engine;
            Directory.CreateDirectory(Folder);
            var engine = new ClientEngine(BuildSettings(null), _network.Factories);
            engine.CriticalException += (_, e) => _logger.LogError(e.Exception, "The torrent engine hit an error");
            // (asked for every peer about to be connected to, and every one that connects)
            engine.ConnectionManager.BanPeer += (_, e) => e.BanPeer |= IsBanned(e.Peer.ConnectionUri);
            _engine = engine;
            return engine;
        }
        finally
        {
            _engineGate.Release();
        }
    }

    int _port;

    MonoTorrent.Client.EngineSettings BuildSettings(MonoTorrent.Client.EngineSettings? current)
    {
        var route = _network.Route;
        var address = _settings.TorrentListenAddress;
        var local = IPAddress.IsLoopback(address);
        // a port of our own choosing when none is set: the engine tells trackers and the DHT the port it listens on
        if (_settings.TorrentPort is > 0 and <= 65535) _port = _settings.TorrentPort;
        else if (_port == 0) _port = FreePort(route.V4 ?? route.V6 ?? (route.Locked ? IPAddress.Loopback : address));

        // peers connect over IPv4 and IPv6 (bound to an adapter: to its addresses only; none while it is not connected)
        var listen = new Dictionary<string, IPEndPoint>();
        if (route.Locked)
        {
            if (route.V4 != null) listen["ipv4"] = new(route.V4, _port);
            if (route.V6 != null) listen["ipv6"] = new(route.V6, _port);
        }
        else
        {
            listen[address.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4"] = new(address, _port);
            if (Socket.OSSupportsIPv6 && address.Equals(IPAddress.Any)) listen["ipv6"] = new(IPAddress.IPv6Any, _port);
            if (Socket.OSSupportsIPv6 && address.Equals(IPAddress.Loopback)) listen["ipv6"] = new(IPAddress.IPv6Loopback, _port);
        }

        var builder = current == null ? new EngineSettingsBuilder() : new EngineSettingsBuilder(current);
        builder.CacheDirectory = Folder;
        builder.AutoSaveLoadFastResume = true;
        builder.AutoSaveLoadMagnetLinkMetadata = true;
        builder.AutoSaveLoadDhtCache = true;
        builder.ListenEndPoints = listen;
        // (the same object when nothing changed: MonoTorrent compares it by reference and would restart the DHT on every settings update)
        // (the DHT is UDP, which a proxy does not carry; MonoTorrent's DHT is IPv4 only)
        var dhtAddress = route.Locked ? route.V4 : address.AddressFamily == AddressFamily.InterNetwork ? address : null;
        var dht = UsesDht && dhtAddress != null ? new IPEndPoint(dhtAddress, _port) : null;
        builder.DhtEndPoint = dht != null && dht.Equals(current?.DhtEndPoint) ? current!.DhtEndPoint : dht;
        // (through a proxy or one adapter, nothing goes out another way: not the router's port forwarding, not the local network's broadcasts)
        builder.AllowPortForwarding = _settings.TorrentPortForwarding && !local && !route.Confined;
        builder.AllowLocalPeerDiscovery = _settings.TorrentLocalPeers && !local && !route.Confined;
        builder.AllowedEncryption = EncryptionOf(_settings.TorrentEncryption);
        // (the engine's own total limits do not hold back its transfers: the totals are shared out per torrent, see Rebalance)
        builder.MaximumDownloadRate = 0;
        builder.MaximumUploadRate = 0;
        return builder.ToSettings();
    }

    /// <summary>The DHT is used: on in Settings, and not through a proxy (it cannot carry it).</summary>
    bool UsesDht => _settings.TorrentUseDht && _network.Route.Proxy == null;

    static List<MonoTorrent.Connections.EncryptionType> EncryptionOf(TorrentEncryption mode) => mode switch
    {
        TorrentEncryption.Require => new() { MonoTorrent.Connections.EncryptionType.RC4Full, MonoTorrent.Connections.EncryptionType.RC4Header },
        TorrentEncryption.Off => new() { MonoTorrent.Connections.EncryptionType.PlainText },
        _ => new() { MonoTorrent.Connections.EncryptionType.RC4Header, MonoTorrent.Connections.EncryptionType.RC4Full, MonoTorrent.Connections.EncryptionType.PlainText },
    };

    static int Rate(long bytesPerSecond) => bytesPerSecond <= 0 ? 0 : (int)Math.Min(int.MaxValue, bytesPerSecond);

    /// <summary>A port free for <paramref name="address"/>, and for IPv6 as well (peers connect over both on the same port).</summary>
    static int FreePort(IPAddress address)
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(address, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            if (!Socket.OSSupportsIPv6 || address.AddressFamily == AddressFamily.InterNetworkV6 || attempt >= 8) return port;
            try
            {
                var v6 = new TcpListener(IPAddress.IsLoopback(address) ? IPAddress.IPv6Loopback : IPAddress.IPv6Any, port);
                v6.Start();
                v6.Stop();
                return port;
            }
            catch (SocketException)
            {
                // taken for IPv6: another one
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // The DHT: finding peers without a tracker
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Well-known DHT routers a new node first asks for others (MonoTorrent alone knows only the first).</summary>
    static readonly (string Host, int Port)[] DhtRouters =
    {
        ("router.bittorrent.com", 6881), ("dht.transmissionbt.com", 6881), ("router.utorrent.com", 6881), ("dht.libtorrent.org", 25401),
    };

    string DhtCachePath => Path.Combine(Folder, "dht_nodes.cache");

    /// <summary>
    /// Readies the nodes the DHT starts from, just before the engine starts it (it starts with the first torrent, and again after all were
    /// stopped). MonoTorrent (3.0.2) saves the nodes it knows as a bencoded list ("l26:…26:…e") but reads them back as plain 26-byte
    /// records, so from the second start on it began by asking addresses made of the list's own markers, and only fell back to its one
    /// router after those timed out: the DHT found next to nothing. The saved list is written back as the records it reads, and the
    /// well-known routers are added, so a DHT that knows few nodes is filled at once.
    /// </summary>
    async Task PrepareDhtAsync(ClientEngine engine)
    {
        if (!UsesDht || engine.IsRunning) return;
        try
        {
            var nodes = new List<byte[]>();
            if (File.Exists(DhtCachePath)) nodes.AddRange(CompactNodesOf(await File.ReadAllBytesAsync(DhtCachePath).ConfigureAwait(false)));

            if (nodes.Count < 16)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var resolved = await Task.WhenAll(DhtRouters.Select(async r =>
                {
                    try
                    {
                        var addresses = await Dns.GetHostAddressesAsync(r.Host, AddressFamily.InterNetwork, timeout.Token).ConfigureAwait(false);
                        return addresses.Select(a => CompactNode(a, r.Port)).ToArray();
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        return Array.Empty<byte[]>();   // that router is not reachable now: the others remain
                    }
                })).ConfigureAwait(false);
                nodes.AddRange(resolved.SelectMany(r => r));
            }

            if (nodes.Count == 0) return;
            Directory.CreateDirectory(Folder);
            await File.WriteAllBytesAsync(DhtCachePath, nodes.SelectMany(n => n).ToArray()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not ready the DHT's nodes");
        }
    }

    /// <summary>The 26-byte node records (20-byte id, IPv4 address, port) of a saved node list: plain records, or MonoTorrent's bencoded list of them.</summary>
    internal static IEnumerable<byte[]> CompactNodesOf(byte[] saved)
    {
        var records = new List<byte[]>();
        if (saved.Length > 2 && saved[0] == (byte)'l' && saved[^1] == (byte)'e')
        {
            // l26:<26 bytes>26:<26 bytes>…e
            var at = 1;
            while (at + 3 + 26 <= saved.Length - 1 && saved[at] == (byte)'2' && saved[at + 1] == (byte)'6' && saved[at + 2] == (byte)':')
            {
                records.Add(saved.AsSpan(at + 3, 26).ToArray());
                at += 3 + 26;
            }

            return records;
        }

        for (var at = 0; at + 26 <= saved.Length; at += 26) records.Add(saved.AsSpan(at, 26).ToArray());
        return records;
    }

    static byte[] CompactNode(IPAddress address, int port)
    {
        var node = new byte[26];
        System.Security.Cryptography.RandomNumberGenerator.Fill(node.AsSpan(0, 20));   // a router's own id is not known: any will do
        address.GetAddressBytes().CopyTo(node, 20);
        node[24] = (byte)(port >> 8);
        node[25] = (byte)port;
        return node;
    }

    /// <summary>The total limits (or the port, the DHT ...) changed: the engine and every torrent in it take them at once.</summary>
    public async Task ApplySettingsAsync()
    {
        _network.Refresh();   // (a new route is taken by OnRouteChangedAsync)
        await ApplyEngineSettingsAsync().ConfigureAwait(false);
        await RebalanceAsync().ConfigureAwait(false);
    }

    async Task ApplyEngineSettingsAsync()
    {
        if (_engine is not { } engine) return;
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await engine.UpdateSettingsAsync(BuildSettings(engine.Settings)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The torrent settings could not be applied");
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// The proxy or the adapter changed (or the adapter came back, or went): the engine listens where it now may, and every running
    /// torrent starts its connections again on the new route; none made the old way stays open.
    /// </summary>
    async Task OnRouteChangedAsync(TorrentRoute route)
    {
        _logger.LogInformation("Torrents now go {Route}", DescribeRoute(route, _settings));
        await ApplyEngineSettingsAsync().ConfigureAwait(false);
        List<TorrentManager> managers;
        lock (_activeGate) managers = _active.Keys.ToList();
        foreach (var m in managers)
        {
            try
            {
                if (m.State is TorrentState.Downloading or TorrentState.Seeding) await RestartAsync(m).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not move {Name} to the new route", m.Name);
            }
        }

        List<TorrentManager> fetching;
        lock (_fetching) fetching = _fetching.ToList();
        foreach (var m in fetching) _ = AnnounceEverywhereAsync(m);
    }

    /// <summary>How torrents reach the network, in words (Settings shows it).</summary>
    public static string DescribeRoute(TorrentRoute route, EngineSettings settings)
    {
        var through = route.Proxy is { } proxy ? $"through the proxy {proxy.Host}:{proxy.Port}" : null;
        var bound = !route.Locked ? null
            : route.Offline ? $"only through {route.AdapterName}, which is not connected: torrents wait for it"
            : $"only through {route.AdapterName} ({string.Join(", ", new[] { route.V4, route.V6 }.Where(a => a != null))})";
        var how = (through, bound) switch
        {
            (null, null) => "directly",
            ({ } t, null) => t,
            (null, { } b) => b,
            ({ } t, { } b) => $"{t}, reached {b}",
        };
        var off = new List<string>();
        if (route.Proxy != null)
        {
            off.Add("UDP trackers");
            if (settings.TorrentUseDht) off.Add("the DHT");
        }

        if (route.Confined)
        {
            if (settings.TorrentLocalPeers) off.Add("local peers");
            if (settings.TorrentPortForwarding) off.Add("port forwarding");
        }

        return off.Count == 0 ? how : $"{how} ({JoinWords(off)} not used: they cannot go that way)";
    }

    static string JoinWords(IReadOnlyList<string> words) => words.Count switch
    {
        0 => "",
        1 => words[0],
        _ => string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1],
    };

    public TorrentSettings TorrentSettings(DownloadJob job) => new TorrentSettingsBuilder
    {
        CreateContainingDirectory = false,   // the job decides the folder: see OpenAsync
        MaximumConnections = Math.Max(4, _settings.TorrentMaxPeers),
        MaximumDownloadRate = Rate(job.SpeedLimit ?? 0),
        AllowDht = UsesDht,
        AllowPeerExchange = true,
    }.ToSettings();

    // ------------------------------------------------------------------------------------------------------------
    // The total limits, shared out
    // ------------------------------------------------------------------------------------------------------------

    readonly object _activeGate = new();
    readonly Dictionary<TorrentManager, DownloadJob> _active = new();
    readonly SemaphoreSlim _rebalanceGate = new(1, 1);

    /// <summary>
    /// Gives each torrent in the engine its share of the totals: the speed limit of all downloads split between the torrents that are
    /// downloading (never above a torrent's own limit), and the upload limit split between all of them. Called whenever a torrent comes or
    /// goes, finishes, or a limit changes.
    /// </summary>
    public async Task RebalanceAsync()
    {
        await _rebalanceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<KeyValuePair<TorrentManager, DownloadJob>> active;
            lock (_activeGate) active = _active.ToList();
            if (active.Count == 0) return;

            var downloading = active.Count(a => a.Value.State != DownloadState.Completed);
            var global = _globalLimit();
            var downShare = global > 0 && downloading > 0 ? Math.Max(1024, global / downloading) : 0;
            var upShare = _settings.TorrentUploadLimit > 0 ? Math.Max(1024, _settings.TorrentUploadLimit / active.Count) : 0;

            foreach (var (manager, job) in active)
            {
                var own = job.SpeedLimit ?? 0;
                var down = job.State == DownloadState.Completed ? 0 : Lower(own, downShare);
                var settings = manager.Settings;
                if (settings.MaximumDownloadRate == Rate(down) && settings.MaximumUploadRate == Rate(upShare)) continue;
                try
                {
                    await manager.UpdateSettingsAsync(new TorrentSettingsBuilder(settings)
                    {
                        MaximumDownloadRate = Rate(down),
                        MaximumUploadRate = Rate(upShare),
                    }.ToSettings()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not apply the limits to {Name}", manager.Name);
                }
            }
        }
        finally
        {
            _rebalanceGate.Release();
        }

        static long Lower(long a, long b) => a <= 0 ? b : b <= 0 ? a : Math.Min(a, b);
    }

    void Track(TorrentManager manager, DownloadJob job)
    {
        lock (_activeGate) _active[manager] = job;
    }

    void Untrack(TorrentManager manager)
    {
        lock (_activeGate) _active.Remove(manager);
    }

    // ------------------------------------------------------------------------------------------------------------
    // What a torrent is
    // ------------------------------------------------------------------------------------------------------------

    public static bool IsMagnet(string? s) => s != null && s.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a .torrent file's bytes; throws a message for people when they are not one.</summary>
    public static Torrent Parse(ReadOnlySpan<byte> bytes, string what = "That")
    {
        if (bytes.Length == 0 || bytes.Length > MaxMetadataBytes || !Torrent.TryLoad(bytes, out var torrent) || torrent == null)
            throw new DownloadException(JobErrorKind.Unsupported, $"{what} is not a .torrent file.");
        if (torrent.InfoHashes.V1 == null)
            throw new DownloadException(JobErrorKind.Unsupported, "This torrent is of the newer kind only (BitTorrent v2), which ShadeNet cannot download yet.");
        return torrent;
    }

    public static string HashOf(Torrent torrent) => torrent.InfoHashes.V1!.ToHex().ToUpperInvariant();

    public static string MagnetOf(Torrent torrent) =>
        new MagnetLink(torrent.InfoHashes, torrent.Name, torrent.AnnounceUrls.SelectMany(t => t).Distinct().ToList(), null, torrent.Size).ToV1String();

    public static MagnetLink ParseMagnet(string link)
    {
        if (!MagnetLink.TryParse(link.Trim(), out var magnet) || magnet?.InfoHashes.V1OrV2 == null)
            throw new DownloadException(JobErrorKind.Unsupported, "That magnet link is not complete (it has no torrent identity).");
        return magnet;
    }

    public static string HashOf(MagnetLink magnet) => (magnet.InfoHashes.V1 ?? magnet.InfoHashes.V1OrV2).ToHex().ToUpperInvariant();

    /// <summary>The files of a torrent as a job lists them.</summary>
    public static List<TorrentFileEntry> FilesOf(Torrent torrent, IReadOnlyList<int>? selected = null)
    {
        var wanted = selected == null ? null : new HashSet<int>(selected);
        return torrent.Files.Select((f, i) => new TorrentFileEntry { Path = f.Path, Length = f.Length, Selected = wanted?.Contains(i) ?? true }).ToList();
    }

    public static TorrentInfo InfoOf(Torrent torrent, byte[] metadata) =>
        new(torrent.Name, HashOf(torrent), torrent.Size, FilesOf(torrent), metadata, MagnetOf(torrent), torrent.IsPrivate,
            string.IsNullOrWhiteSpace(torrent.Comment) ? null : torrent.Comment);

    /// <summary>
    /// The .torrent file behind a magnet link (asked of the peers that share it: this waits until one answers), a .torrent file on the disk,
    /// or one on the web.
    /// </summary>
    /// <param name="web">For a .torrent file on the web: who asks for it (the browser's sign-in, referring page and identity it was handed
    /// over with). Its address is <paramref name="source"/>.</param>
    /// <param name="edits">A download's own changes to its trackers (Details > Trackers), for a magnet link's.</param>
    public async Task<(Torrent Torrent, byte[] Metadata)> FetchAsync(string source, Action<string>? activity, CancellationToken ct, RequestSpec? web = null,
        TorrentJobData? edits = null)
    {
        source = source.Trim();
        if (IsMagnet(source))
        {
            var magnet = ParseMagnet(source);
            var cached = MetadataPath(HashOf(magnet));
            if (File.Exists(cached))
            {
                var known = await File.ReadAllBytesAsync(cached, ct).ConfigureAwait(false);
                if (Torrent.TryLoad(known, out var t) && t != null) return (t, known);
            }

            var trackers = TrackersOf(magnet.AnnounceUrls, edits);
            activity?.Invoke(FetchActivity(trackers));
            var bytes = await FetchMagnetMetadataAsync(magnet, trackers, ct).ConfigureAwait(false);
            var torrent = Parse(bytes, "What the peers sent");
            await SaveMetadataAsync(torrent, bytes).ConfigureAwait(false);
            return (torrent, bytes);
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            activity?.Invoke($"Getting the .torrent file from {uri.Host}…");
            using var http = HttpFetcher.CreateClient(2, _settings);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            // (with the sign-in and referring page it came with: a tracker site's download link usually wants both)
            var spec = (web ?? new RequestSpec(source)) with { Url = source };
            var (response, _) = await HttpFetcher.GetAsync(http, spec, _settings.UserAgent, null, timeout.Token).ConfigureAwait(false);
            using var _response = response;
            if (!response.IsSuccessStatusCode)
                throw new DownloadException(JobErrorKind.HttpStatus, $"The server answered {(int)response.StatusCode} ({response.ReasonPhrase}) for the .torrent file.");
            if (response.Content.Headers.ContentLength > MaxMetadataBytes)
                throw new DownloadException(JobErrorKind.Unsupported, "That address is not a .torrent file (it is far too large).");
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var copy = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                copy.Write(buffer, 0, read);
                if (copy.Length > MaxMetadataBytes) throw new DownloadException(JobErrorKind.Unsupported, "That address is not a .torrent file (it is far too large).");
            }

            var bytes = copy.ToArray();
            var torrent = Parse(bytes, "What that address sent");
            await SaveMetadataAsync(torrent, bytes).ConfigureAwait(false);
            return (torrent, bytes);
        }

        var path = uri is { IsFile: true } ? uri.LocalPath : source.Trim('"');
        if (!File.Exists(path)) throw new DownloadException(JobErrorKind.Disk, $"The .torrent file \"{path}\" is not there any more.");
        var file = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var parsed = Parse(file, "That file");
        await SaveMetadataAsync(parsed, file).ConfigureAwait(false);
        return (parsed, file);
    }

    static readonly System.Reflection.MethodInfo? StartMetadataOnly =
        typeof(TorrentManager).GetMethod("StartAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, new[] { typeof(bool) });

    static readonly System.Reflection.MethodInfo? AddMetadataReceived =
        typeof(TorrentManager).GetEvent("MetadataReceived", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetAddMethod(nonPublic: true);

    /// <summary>
    /// The .torrent file behind a magnet link, from the peers. MonoTorrent's own way (<c>DownloadMetadataAsync</c>) puts all of the link's
    /// trackers in one tier, which is asked one tracker at a time, about 45 seconds for each that does not answer; magnet links found on
    /// the web list many trackers that are long gone, so the one that works can be minutes away. Here every tracker is a tier of its own
    /// and all of them are asked at once (and the .torrent file that comes back keeps them that way, for the download).
    /// </summary>
    /// <summary>What the card says while a magnet link's file list is asked for.</summary>
    string FetchActivity(IReadOnlyList<string> trackers)
    {
        var route = _network.Route;
        if (route.Offline) return $"Waiting for {route.AdapterName} to connect…";
        // (through the proxy only HTTP trackers can be asked)
        var asked = trackers.Count(t => route.Proxy == null || !t.StartsWith("udp:", StringComparison.OrdinalIgnoreCase));
        var ways = new List<string>();
        if (asked > 0) ways.Add($"{asked} tracker{(asked == 1 ? "" : "s")}");
        if (UsesDht) ways.Add("the DHT");
        if (ways.Count == 0) return "Getting the file list from peers… (no tracker can be asked" + (route.Proxy != null ? " through the proxy" : "") + "; add one in Details > Trackers)";
        return $"Getting the file list from peers… (asking {string.Join(" and ", ways)}{(route.Proxy != null ? ", through the proxy" : "")})";
    }

    /// <summary>A torrent's trackers with a download's own changes: the removed ones out, the added ones in.</summary>
    static List<string> TrackersOf(IEnumerable<string> own, TorrentJobData? edits)
    {
        var removed = new HashSet<string>(edits?.RemovedTrackers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        return own.Concat(edits?.AddedTrackers ?? new List<string>())
            .Where(t => !removed.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    readonly HashSet<TorrentManager> _fetching = new();

    async Task<byte[]> FetchMagnetMetadataAsync(MagnetLink magnet, IReadOnlyList<string> trackers, CancellationToken ct)
    {
        var engine = await EngineAsync().ConfigureAwait(false);
        await PrepareDhtAsync(engine).ConfigureAwait(false);
        if (engine.Torrents.FirstOrDefault(m => m.InfoHashes.Equals(magnet.InfoHashes)) is { } stale)
            await CloseAsync(stale).ConfigureAwait(false);   // a fetch for the same link that was given up on

        var bare = new MagnetLink(magnet.InfoHashes, magnet.Name, new List<string>(), magnet.Webseeds, magnet.Size);
        var work = Path.Combine(Folder, "metadata-work", HashOf(magnet));
        var manager = await engine.AddAsync(bare, work, new TorrentSettingsBuilder
        {
            AllowDht = UsesDht,
            AllowPeerExchange = true,
            MaximumConnections = Math.Max(4, _settings.TorrentMaxPeers),
        }.ToSettings()).ConfigureAwait(false);
        lock (_fetching) _fetching.Add(manager);

        try
        {
            foreach (var tracker in trackers)
            {
                if (!Uri.TryCreate(tracker, UriKind.Absolute, out var uri)) continue;
                try
                {
                    await manager.TrackerManager.AddTrackerAsync(uri).ConfigureAwait(false);   // (a tier of its own)
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    // a kind of tracker MonoTorrent does not speak (wss://): the others and the DHT remain
                }
            }

            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancel = ct.Register(() => received.TrySetCanceled(ct));

            if (StartMetadataOnly != null && AddMetadataReceived != null)
            {
                // MonoTorrent's own "file list only" start: it stops once the list is in, nothing of the files is fetched
                AddMetadataReceived.Invoke(manager, new object[] { new EventHandler<ReadOnlyMemory<byte>>((_, data) => received.TrySetResult(data.ToArray())) });
                await ((Task)StartMetadataOnly.Invoke(manager, new object[] { true })!).ConfigureAwait(false);
            }
            else
            {
                // (without it: a usual start, stopped as soon as the list is in; it saves the list itself)
                await manager.StartAsync().ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    while (!received.Task.IsCompleted)
                    {
                        if (manager.HasMetadata && File.Exists(manager.MetadataPath))
                        {
                            received.TrySetResult(await File.ReadAllBytesAsync(manager.MetadataPath).ConfigureAwait(false));
                            return;
                        }

                        await Task.Delay(100).ConfigureAwait(false);
                    }
                });
            }

            return await received.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_fetching) _fetching.Remove(manager);
            await CloseAsync(manager).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // an empty leftover folder is harmless
            }
        }
    }

    /// <summary>
    /// Trackers in one tier are asked one at a time, the next only after the one before has failed (about 45 seconds each when it does not
    /// answer). A public torrent's trackers are spread out so that each is a tier of its own and all are asked at once; a private torrent
    /// keeps its own order (its tracker decides who may join, and other trackers may not be added).
    /// </summary>
    static async Task SpreadTrackersAsync(TorrentManager manager)
    {
        if (manager.Torrent?.IsPrivate == true) return;
        foreach (var tier in manager.TrackerManager.Tiers.ToList())
        {
            foreach (var tracker in tier.Trackers.Skip(1).ToList())
            {
                try
                {
                    await manager.TrackerManager.RemoveTrackerAsync(tracker).ConfigureAwait(false);
                    await manager.TrackerManager.AddTrackerAsync(tracker.Uri).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    // left where it was
                }
            }
        }
    }

    public async Task SaveMetadataAsync(Torrent torrent, byte[] bytes)
    {
        var path = MetadataPath(HashOf(torrent));
        try
        {
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not keep the .torrent file of {Name}", torrent.Name);
        }
    }

    /// <summary>The job's .torrent file, when it is known.</summary>
    public Torrent? LoadMetadata(TorrentJobData data)
    {
        if (string.IsNullOrEmpty(data.InfoHash)) return null;
        var path = MetadataPath(data.InfoHash);
        try
        {
            return File.Exists(path) && Torrent.TryLoad(path, out var torrent) ? torrent : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // A torrent in the engine
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Puts a job's torrent into the engine (not started): its files land where the job says (a folder named after the job for a torrent of
    /// several files, the job's file for one), every one of them inside that place, and the files the user left out are skipped.
    /// </summary>
    public async Task<TorrentManager> OpenAsync(DownloadJob job, Torrent torrent)
    {
        var data = job.Torrent!;
        var engine = await EngineAsync().ConfigureAwait(false);
        await PrepareDhtAsync(engine).ConfigureAwait(false);

        // a torrent can name any path ("..\..\Windows\x.dll"): nothing of it may land outside its own place
        var root = Path.GetFullPath(data.IsFolder ? job.TargetPath : job.SaveDirectory);
        foreach (var f in torrent.Files)
        {
            if (!IsInside(root, Path.Combine(root, f.Path)))
                throw new DownloadException(JobErrorKind.Unsupported, $"This torrent names a file outside its own folder (\"{f.Path}\"), so it was not downloaded.");
        }

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DiskException($"Cannot use the save folder \"{root}\": {ex.Message}", ex);
        }

        if (engine.Torrents.FirstOrDefault(m => m.InfoHashes.Equals(torrent.InfoHashes)) is { } stale)
            await engine.RemoveAsync(stale, RemoveMode.KeepAllData).ConfigureAwait(false);   // left by a run that ended badly

        // resume data says which pieces are on the disk: with none of the files there it is about other files (deleted, or another
        // folder), and is not to be believed
        if (!torrent.Files.Any(f => File.Exists(data.IsFolder ? Path.Combine(root, f.Path) : job.TargetPath)))
        {
            try
            {
                File.Delete(FastResumePath(HashOf(torrent)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // then the engine checks the pieces against the files itself
            }
        }

        var manager = await engine.AddAsync(torrent, root, TorrentSettings(job)).ConfigureAwait(false);
        try
        {
            if (!data.IsFolder && manager.Files.Count == 1 && !SamePath(manager.Files[0].FullPath, job.TargetPath))
                await manager.MoveFileAsync(manager.Files[0], job.TargetPath).ConfigureAwait(false);

            foreach (var f in manager.Files)
            {
                if (!IsInside(root, f.FullPath))
                    throw new DownloadException(JobErrorKind.Unsupported, $"This torrent names a file outside its own folder (\"{f.FullPath}\"), so it was not downloaded.");
            }

            await ApplyPrioritiesAsync(manager, data).ConfigureAwait(false);
            if (data.Sequential) await manager.ChangePickerAsync(Picker(sequential: true)).ConfigureAwait(false);
            await SpreadTrackersAsync(manager).ConfigureAwait(false);
            await ApplyTrackerEditsAsync(manager, data).ConfigureAwait(false);

            Track(manager, job);
            await RebalanceAsync().ConfigureAwait(false);
            return manager;
        }
        catch
        {
            await engine.RemoveAsync(manager, RemoveMode.KeepAllData).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The engine is told which files to skip, which to fetch first (a file keeps the torrent's own path; only where it is saved changes).</summary>
    static async Task ApplyPrioritiesAsync(TorrentManager manager, TorrentJobData data)
    {
        var entries = data.Files?.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var f in manager.Files)
        {
            var entry = entries != null && entries.TryGetValue(f.Path, out var e) ? e : null;
            var priority = entry == null ? Priority.Normal : !entry.Selected ? Priority.DoNotDownload : entry.High ? Priority.High : Priority.Normal;
            if (f.Priority != priority) await manager.SetFilePriorityAsync(f, priority).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How pieces are chosen. MonoTorrent's usual way asks for the rarest pieces first, in a shuffled order (best for the swarm); in order,
    /// it asks for the first missing piece every peer has, so the files fill from their start.
    /// </summary>
    static MonoTorrent.PiecePicking.IPieceRequester Picker(bool sequential) =>
        new MonoTorrent.PiecePicking.StandardPieceRequester(sequential
            ? new MonoTorrent.PiecePicking.PieceRequesterSettings(allowPrioritisation: true, allowRandomised: false, allowRarestFirst: false)
            : MonoTorrent.PiecePicking.PieceRequesterSettings.Default);

    /// <summary>The torrent of a job, when it is in the engine (downloading or being shared).</summary>
    public TorrentManager? ManagerOf(Guid jobId)
    {
        lock (_activeGate) return _active.FirstOrDefault(a => a.Value.Id == jobId).Key;
    }

    /// <summary>The job's choice of files (and their priorities) changed: a torrent in the engine follows it at once.</summary>
    public async Task ApplyFilesAsync(DownloadJob job)
    {
        if (ManagerOf(job.Id) is { } manager && job.Torrent != null) await ApplyPrioritiesAsync(manager, job.Torrent).ConfigureAwait(false);
    }

    /// <summary>The job was switched to (or from) fetching in order: a torrent in the engine follows it at once.</summary>
    public async Task ApplySequentialAsync(DownloadJob job)
    {
        if (ManagerOf(job.Id) is not { } manager || job.Torrent == null) return;

        // MonoTorrent changes how pieces are chosen only while a torrent is stopped: a moment's stop (the pieces it has stay in memory,
        // nothing is checked again)
        if (manager.State is TorrentState.Stopped or TorrentState.Error) await manager.ChangePickerAsync(Picker(job.Torrent.Sequential)).ConfigureAwait(false);
        else await RestartAsync(manager, () => manager.ChangePickerAsync(Picker(job.Torrent.Sequential))).ConfigureAwait(false);
    }

    readonly object _countGate = new();
    readonly Dictionary<TorrentManager, (long Sent, long Received)> _counted = new();

    /// <summary>File data sent to peers since the torrent was put in the engine (MonoTorrent's own count starts again at every stop).</summary>
    public long SentOf(TorrentManager manager)
    {
        lock (_countGate) return (_counted.TryGetValue(manager, out var c) ? c.Sent : 0) + manager.Monitor.DataBytesSent;
    }

    /// <summary>File data received from peers since the torrent was put in the engine.</summary>
    public long ReceivedOf(TorrentManager manager)
    {
        lock (_countGate) return (_counted.TryGetValue(manager, out var c) ? c.Received : 0) + manager.Monitor.DataBytesReceived;
    }

    /// <summary>
    /// Stops a running torrent for a moment and starts it again (every connection is made anew), doing <paramref name="whileStopped"/> in
    /// between. Its limits are lifted before the stop (see ReleaseLimitsAsync) and given back after; what it sent so far stays counted.
    /// </summary>
    async Task RestartAsync(TorrentManager manager, Func<Task>? whileStopped = null, bool check = false)
    {
        await ReleaseLimitsAsync(manager).ConfigureAwait(false);
        lock (_countGate)
        {
            var c = _counted.TryGetValue(manager, out var had) ? had : default;
            _counted[manager] = (c.Sent + manager.Monitor.DataBytesSent, c.Received + manager.Monitor.DataBytesReceived);
        }

        if (manager.State is not (TorrentState.Stopped or TorrentState.Error)) await manager.StopAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        if (whileStopped != null) await whileStopped().ConfigureAwait(false);
        if (check) await manager.HashCheckAsync(autoStart: true).ConfigureAwait(false);
        else await manager.StartAsync().ConfigureAwait(false);
        await RebalanceAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------------------
    // What the user does by hand: trackers, peers, checking
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The download's own changes to its trackers, on a torrent just put in the engine.</summary>
    static async Task ApplyTrackerEditsAsync(TorrentManager manager, TorrentJobData data)
    {
        foreach (var removed in data.RemovedTrackers ?? new List<string>())
        {
            foreach (var tracker in manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).Where(t => SameTracker(t.Uri, removed)).ToList())
                await TryAsync(() => manager.TrackerManager.RemoveTrackerAsync(tracker).AsTask()).ConfigureAwait(false);
        }

        if (manager.Torrent?.IsPrivate == true) return;
        foreach (var added in data.AddedTrackers ?? new List<string>())
        {
            if (manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).Any(t => SameTracker(t.Uri, added))) continue;
            if (Uri.TryCreate(added, UriKind.Absolute, out var uri)) await TryAsync(() => manager.TrackerManager.AddTrackerAsync(uri).AsTask()).ConfigureAwait(false);
        }
    }

    static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // a kind of tracker MonoTorrent does not speak, or one it already has
        }
    }

    static bool SameTracker(Uri a, string b) => Uri.TryCreate(b, UriKind.Absolute, out var u) && SameTracker(a, u);

    static bool SameTracker(Uri a, Uri b) => string.Equals(a.AbsoluteUri.TrimEnd('/'), b.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>A tracker address ShadeNet can ask: http, https or udp, with a host.</summary>
    public static Uri? ParseTracker(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "udp" && !string.IsNullOrEmpty(uri.Host)
                                                                         && (uri.Scheme != "udp" || !uri.IsDefaultPort)
            ? uri
            : null;
    }

    /// <summary>Adds trackers to a download (each asked at once, on its own); a torrent in the engine asks them now. Returns how many were new.</summary>
    public async Task<int> AddTrackersAsync(DownloadJob job, IReadOnlyList<Uri> trackers)
    {
        var data = job.Torrent!;
        if (LoadMetadata(data) is { IsPrivate: true })
            throw new InvalidOperationException("This torrent is private: only its own tracker may be used.");
        var known = KnownTrackers(job);
        var added = 0;
        data.AddedTrackers ??= new List<string>();
        foreach (var uri in trackers)
        {
            data.RemovedTrackers?.RemoveAll(r => SameTracker(uri, r));
            if (known.Any(k => SameTracker(uri, k))) continue;
            data.AddedTrackers.Add(uri.AbsoluteUri);
            known.Add(uri.AbsoluteUri);
            added++;
            if (ManagerOf(job.Id) is { } manager) await TryAsync(() => manager.TrackerManager.AddTrackerAsync(uri).AsTask()).ConfigureAwait(false);
        }

        // (a new tier has never announced, so it is due: the engine's usual round asks it now, and only it)
        if (added > 0 && ManagerOf(job.Id) is { } running) _ = AnnounceDueAsync(running);

        Save(job);
        return added;
    }

    /// <summary>Takes a tracker off a download (it stays off when the torrent starts again).</summary>
    public async Task RemoveTrackerAsync(DownloadJob job, string address)
    {
        var data = job.Torrent!;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return;
        var wasAdded = data.AddedTrackers?.RemoveAll(a => SameTracker(uri, a)) > 0;
        if (!wasAdded)
        {
            data.RemovedTrackers ??= new List<string>();
            if (!data.RemovedTrackers.Any(r => SameTracker(uri, r))) data.RemovedTrackers.Add(uri.AbsoluteUri);
        }

        if (ManagerOf(job.Id) is { } manager)
        {
            foreach (var tracker in manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).Where(t => SameTracker(t.Uri, uri)).ToList())
                await TryAsync(() => manager.TrackerManager.RemoveTrackerAsync(tracker).AsTask()).ConfigureAwait(false);
        }

        Save(job);
    }

    /// <summary>The trackers a download has now (its torrent's or magnet link's, with its own changes).</summary>
    List<string> KnownTrackers(DownloadJob job)
    {
        var data = job.Torrent!;
        if (ManagerOf(job.Id) is { } manager) return manager.TrackerManager.Tiers.SelectMany(t => t.Trackers).Select(t => t.Uri.AbsoluteUri).ToList();
        var own = LoadMetadata(data)?.AnnounceUrls.SelectMany(t => t)
                  ?? (MagnetLink.TryParse(data.Magnet, out var magnet) && magnet != null ? magnet.AnnounceUrls : Enumerable.Empty<string>());
        return TrackersOf(own, data);
    }

    /// <summary>
    /// "Ask for peers now": every tracker is asked at once (MonoTorrent would wait for each one's next turn), and the DHT and the local
    /// network too. False when the torrent is not running.
    /// </summary>
    public bool Reannounce(DownloadJob job)
    {
        if (ManagerOf(job.Id) is not { } manager) return false;
        _ = AnnounceEverywhereAsync(manager);
        return true;
    }

    async Task AnnounceEverywhereAsync(TorrentManager manager)
    {
        try
        {
            if (UsesDht && manager.Torrent?.IsPrivate != true) await manager.DhtAnnounceAsync().ConfigureAwait(false);
            if (_engine?.Settings.AllowLocalPeerDiscovery == true && manager.Torrent?.IsPrivate != true) await manager.LocalPeerAnnounceAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // stopped meanwhile
        }

        // MonoTorrent (3.0.2) has a way to ask one tracker now, but it drops the peers that tracker names (its "announce complete" goes
        // out without them). Its usual round passes them on, and asks every tier at once, but only the tiers that are due: every tier
        // is made due (the time since its last announce set long ago), and the round asks them all.
        if (SinceLastAnnounce != null)
        {
            try
            {
                foreach (var tier in manager.TrackerManager.Tiers.ToList()) SinceLastAnnounce.Invoke(tier, new object[] { TimeSpan.FromDays(1) });
            }
            catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Could not make the trackers of {Name} due", manager.Name);
            }
        }

        await AnnounceDueAsync(manager).ConfigureAwait(false);
    }

    static readonly System.Reflection.MethodInfo? SinceLastAnnounce =
        typeof(MonoTorrent.Trackers.TrackerTier).GetProperty(nameof(MonoTorrent.Trackers.TrackerTier.TimeSinceLastAnnounce))?.GetSetMethod(nonPublic: true);

    /// <summary>The engine's usual round of announces, now: every tier that is due is asked (at once).</summary>
    async Task AnnounceDueAsync(TorrentManager manager)
    {
        if (manager.State is TorrentState.Stopped or TorrentState.Stopping or TorrentState.Error) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await manager.TrackerManager.AnnounceAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Asking the trackers of {Name} for peers failed", manager.Name);
        }
    }

    /// <summary>
    /// "Check the files again" on a torrent in the engine: it stops, every piece on the disk is checked against the torrent, and it
    /// carries on with what is missing. False when it is not in the engine.
    /// </summary>
    public async Task<bool> RecheckAsync(DownloadJob job)
    {
        if (ManagerOf(job.Id) is not { } manager) return false;
        await RestartAsync(manager, check: true).ConfigureAwait(false);
        return true;
    }

    /// <summary>The saved record of which pieces are on the disk goes: the next start checks every piece.</summary>
    public void ForgetResumeData(string? infoHash)
    {
        if (string.IsNullOrEmpty(infoHash)) return;
        try
        {
            File.Delete(FastResumePath(infoHash));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not drop the resume data of {Hash}", infoHash);
        }
    }

    /// <summary>"Add a peer": connects to someone the user knows has the torrent (an address and port). False when the torrent is not running.</summary>
    public async Task<bool> AddPeerAsync(DownloadJob job, IPEndPoint peer)
    {
        if (ManagerOf(job.Id) is not { } manager) return false;
        if (IsBanned(peer.Address)) throw new InvalidOperationException("That address is banned (Settings > Torrents).");
        var uri = peer.AddressFamily == AddressFamily.InterNetworkV6 ? new Uri($"ipv6://[{peer.Address}]:{peer.Port}") : new Uri($"ipv4://{peer.Address}:{peer.Port}");
        await manager.AddPeerAsync(new PeerInfo(uri)).ConfigureAwait(false);
        return true;
    }

    /// <summary>"address:port", "[v6 address]:port" or an address alone (for a ban); null when it is none of those.</summary>
    public static IPEndPoint? ParsePeer(string? text, bool portRequired = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (IPEndPoint.TryParse(t, out var ep) && (ep.Port > 0 || !portRequired) && !ep.Address.Equals(IPAddress.Any) && !ep.Address.Equals(IPAddress.IPv6Any))
            return ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;
        return null;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Banned peers
    // ------------------------------------------------------------------------------------------------------------

    readonly object _banGate = new();
    HashSet<IPAddress>? _banned;

    string BannedPath => Path.Combine(Folder, "banned-peers.txt");

    HashSet<IPAddress> Banned()
    {
        lock (_banGate)
        {
            if (_banned != null) return _banned;
            _banned = new HashSet<IPAddress>();
            try
            {
                if (File.Exists(BannedPath))
                    foreach (var line in File.ReadAllLines(BannedPath))
                        if (IPAddress.TryParse(line.Trim(), out var a)) _banned.Add(Normal(a));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read the banned peers");
            }

            return _banned;
        }
    }

    static IPAddress Normal(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    bool IsBanned(IPAddress address)
    {
        var banned = Banned();
        lock (_banGate) return banned.Count > 0 && banned.Contains(Normal(address));
    }

    bool IsBanned(Uri peer) => IPAddress.TryParse(peer.Host.Trim('[', ']'), out var a) && IsBanned(a);

    /// <summary>The banned addresses (no connection is made to or accepted from them, in any torrent).</summary>
    public IReadOnlyList<string> BannedPeers
    {
        get
        {
            var banned = Banned();
            lock (_banGate) return banned.Select(a => a.ToString()).OrderBy(a => a, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Bans an address: it is disconnected from every torrent now and never connected to again (until unbanned).</summary>
    public async Task BanAsync(IPAddress address)
    {
        address = Normal(address);
        var banned = Banned();
        lock (_banGate)
        {
            if (!banned.Add(address)) return;
        }

        SaveBanned();
        List<TorrentManager> managers;
        lock (_activeGate) managers = _active.Keys.ToList();
        lock (_fetching) managers.AddRange(_fetching);
        foreach (var m in managers)
        {
            try
            {
                foreach (var peer in await m.GetPeersAsync().ConfigureAwait(false))
                    if (IPAddress.TryParse(peer.Uri.Host.Trim('[', ']'), out var a) && Normal(a).Equals(address)) Disconnect(peer);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // it was just stopped
            }
        }
    }

    public void UnbanAll()
    {
        var banned = Banned();
        lock (_banGate) banned.Clear();
        SaveBanned();
    }

    void SaveBanned()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string[] lines;
            lock (_banGate) lines = Banned().Select(a => a.ToString()).ToArray();
            File.WriteAllLines(BannedPath, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the banned peers");
        }
    }

    static readonly System.Reflection.PropertyInfo? PeerConnection =
        typeof(PeerId).GetProperty("Connection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

    /// <summary>Closes a peer's connection (MonoTorrent has no way of its own; it notices the closed connection and lets the peer go).</summary>
    static void Disconnect(PeerId peer)
    {
        try
        {
            (PeerConnection?.GetValue(peer) as IDisposable)?.Dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or System.Reflection.TargetInvocationException)
        {
            // gone already
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // The swarm, as the trackers count it
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The most seeders and leechers any tracker reported for the torrent (null when none did).</summary>
    public static (int? Seeds, int? Leechers) SwarmOf(TorrentManager manager)
    {
        int? seeds = null, leechers = null;
        try
        {
            foreach (var tracker in manager.TrackerManager.Tiers.SelectMany(t => t.Trackers))
            {
                if (ScrapeOf(tracker, manager.InfoHashes) is not { } info) continue;
                seeds = Math.Max(seeds ?? 0, info.Complete);
                leechers = Math.Max(leechers ?? 0, info.Incomplete);
            }
        }
        catch (InvalidOperationException)
        {
            // the trackers changed while being read: the next time
        }

        return (seeds, leechers);
    }

    static MonoTorrent.Trackers.ScrapeInfo? ScrapeOf(MonoTorrent.Trackers.ITracker tracker, InfoHashes hashes)
    {
        if (tracker.Status != MonoTorrent.Trackers.TrackerState.Ok) return null;
        var scrape = (tracker as MonoTorrent.Trackers.Tracker)?.ScrapeInfo;
        if (scrape == null) return null;
        foreach (var h in new[] { hashes.V1, hashes.V2 })
            if (h != null && scrape.TryGetValue(h, out var info) && (info.Complete > 0 || info.Incomplete > 0 || info.Downloaded > 0)) return info;
        return null;
    }

    /// <summary>
    /// How big a torrent's swarm is before it is added: its trackers are asked (all at once, for at most <paramref name="wait"/>) how many
    /// have all of it and how many are downloading. Answered: how many trackers did.
    /// </summary>
    public async Task<TorrentSwarmCount> CountSwarmAsync(Torrent torrent, TimeSpan wait, CancellationToken ct)
    {
        var trackers = torrent.AnnounceUrls.SelectMany(t => t).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ParseTracker).Where(u => u != null).Select(u => _network.Factories.CreateTracker(u!)).Where(t => t != null).ToList();
        if (trackers.Count == 0) return new TorrentSwarmCount(null, null, 0, 0);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        var request = new MonoTorrent.Trackers.ScrapeRequest(torrent.InfoHashes);
        var answers = await Task.WhenAll(trackers.Select(async t =>
        {
            try
            {
                if (!t!.CanScrape) return null;
                var response = await t.ScrapeAsync(request, timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
                if (response.State != MonoTorrent.Trackers.TrackerState.Ok) return null;
                foreach (var h in new[] { torrent.InfoHashes.V1, torrent.InfoHashes.V2 })
                    if (h != null && response.ScrapeInfo.TryGetValue(h, out var info)) return (MonoTorrent.Trackers.ScrapeInfo?)info;
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        })).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var got = answers.Where(a => a != null).Select(a => a!.Value).ToList();
        return got.Count == 0
            ? new TorrentSwarmCount(null, null, 0, trackers.Count)
            : new TorrentSwarmCount(got.Max(a => a.Complete), got.Max(a => a.Incomplete), got.Count, trackers.Count);
    }

    static bool IsInside(string root, string path)
    {
        var full = Path.GetFullPath(path);
        var r = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops a torrent (its resume data is saved) and takes it out of the engine; the files stay.</summary>
    public async Task CloseAsync(TorrentManager manager)
    {
        Untrack(manager);
        lock (_countGate) _counted.Remove(manager);
        await ReleaseLimitsAsync(manager).ConfigureAwait(false);
        try
        {
            if (manager.State is not (TorrentState.Stopped or TorrentState.Error)) await manager.StopAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopping torrent {Name} failed", manager.Name);
        }

        try
        {
            if (_engine is { } engine && engine.Contains(manager)) await engine.RemoveAsync(manager, RemoveMode.KeepAllData).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Removing torrent {Name} from the engine failed", manager.Name);
        }

        await RebalanceAsync().ConfigureAwait(false);
    }

    static readonly System.Reflection.MethodInfo? UpdateLimiters =
        typeof(TorrentManager).GetMethod("UpdateLimiters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, Type.EmptyTypes);

    /// <summary>
    /// MonoTorrent (3.0.2) keeps the reads and writes that wait for a speed limit in one queue for the whole program, and serves it strictly
    /// in order. A torrent's limit is only topped up while the torrent runs: one stopped with a transfer still waiting would block that
    /// queue for good, and with it every later torrent that has a limit (they would sit at a few hundred kilobytes for ever). So before a
    /// torrent stops, its limits are lifted and its limiter told at once, which lets whatever it left in the queue through.
    /// </summary>
    async Task ReleaseLimitsAsync(TorrentManager manager)
    {
        try
        {
            var settings = manager.Settings;
            if (settings.MaximumDownloadRate != 0 || settings.MaximumUploadRate != 0)
            {
                await manager.UpdateSettingsAsync(new TorrentSettingsBuilder(settings) { MaximumDownloadRate = 0, MaximumUploadRate = 0 }.ToSettings())
                    .ConfigureAwait(false);
            }

            if (UpdateLimiters != null) UpdateLimiters.Invoke(manager, null);
            else await Task.Delay(1200).ConfigureAwait(false);   // (the engine tops limiters up every second)
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not lift the limits of {Name} before stopping it", manager.Name);
        }
    }

    /// <summary>A removed download: the engine's own copies of its .torrent file and its resume data go too.</summary>
    public void Forget(string? infoHash)
    {
        if (string.IsNullOrEmpty(infoHash)) return;
        foreach (var path in new[] { MetadataPath(infoHash), FastResumePath(infoHash) })
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a leftover is harmless
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // What the details drawer shows
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The peers, files, trackers and pieces of a job's torrent. Live when it is in the engine; otherwise from what was saved (its .torrent
    /// file and resume data), with no peers.
    /// </summary>
    public async Task<TorrentDetails> DetailsAsync(DownloadJob job)
    {
        var data = job.Torrent;
        if (data == null) return TorrentDetails.Empty;

        if (ManagerOf(job.Id) is { HasMetadata: true } m && m.Torrent is { } live)
        {
            var peers = new List<TorrentPeer>();
            try
            {
                foreach (var p in await m.GetPeersAsync().ConfigureAwait(false))
                {
                    if (!p.IsConnected) continue;
                    peers.Add(new TorrentPeer(AddressOf(p.Uri), ClientOf(p.ClientApp), p.BitField.PercentComplete / 100.0, p.Monitor.DownloadRate,
                        p.Monitor.UploadRate, p.IsSeeder, p.EncryptionType != MonoTorrent.Connections.EncryptionType.PlainText,
                        p.ConnectionDirection == MonoTorrent.Direction.Incoming));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // it was just stopped
            }

            var files = m.Files.Select((f, i) => new TorrentFileProgress(i, f.Path, f.Length,
                f.Length == 0 || f.BitField.Length == 0 ? 1 : f.BitField.PercentComplete / 100.0, PriorityOf(f.Priority))).ToList();
            var trackers = new List<TorrentTracker>();
            var tiers = m.TrackerManager.Tiers;
            var route = _network.Route;
            for (var t = 0; t < tiers.Count; t++)
            {
                foreach (var tracker in tiers[t].Trackers)
                {
                    var message = string.IsNullOrWhiteSpace(tracker.FailureMessage) ? string.IsNullOrWhiteSpace(tracker.WarningMessage) ? null : tracker.WarningMessage : tracker.FailureMessage;
                    var status = StatusOf(tracker.Status);
                    // (one ShadeNet does not ask on this route: why, rather than "not working")
                    if (tracker.Status != MonoTorrent.Trackers.TrackerState.Ok && TorrentNetwork.NotUsed(route, tracker.Uri, AddressFamily.InterNetwork) is { } why)
                    {
                        status = "Not used";
                        message = why;
                    }

                    var counts = ScrapeOf(tracker, m.InfoHashes);
                    trackers.Add(new TorrentTracker(tracker.Uri.ToString(), status, message, t + 1, counts?.Complete, counts?.Incomplete,
                        counts is { Downloaded: > 0 } c ? c.Downloaded : null));
                }
            }

            trackers.Add(DhtRow(live.IsPrivate));

            var bits = m.Bitfield;
            var pieces = new bool[bits.Length];
            for (var i = 0; i < pieces.Length; i++) pieces[i] = bits[i];
            var (swarmSeeds, swarmLeechers) = SwarmOf(m);
            return new TorrentDetails(true, peers, files, trackers, pieces, live.PieceLength, live.Size, data.Sequential, swarmSeeds, swarmLeechers, live.IsPrivate);
        }

        // not in the engine: what was saved
        var torrent = LoadMetadata(data);
        if (torrent == null)
        {
            var known = (data.Files ?? new List<TorrentFileEntry>()).Select((f, i) => new TorrentFileProgress(i, f.Path, f.Length,
                job.State == DownloadState.Completed && f.Selected ? 1 : null, PriorityOf(f))).ToList();
            return TorrentDetails.Empty with { Files = known, Sequential = data.Sequential };
        }

        var saved = new bool[torrent.PieceCount];
        if (FastResume.TryLoad(FastResumePath(HashOf(torrent)), out var resume) && resume?.Bitfield.Length == saved.Length)
        {
            for (var i = 0; i < saved.Length; i++) saved[i] = resume.Bitfield[i];
        }
        else if (job.State == DownloadState.Completed)
        {
            // finished, and nothing saved since: the chosen files are complete
            for (var i = 0; i < torrent.Files.Count; i++)
            {
                if (data.Files is { } list && i < list.Count && !list[i].Selected) continue;
                for (var p = torrent.Files[i].StartPieceIndex; p <= torrent.Files[i].EndPieceIndex && p < saved.Length; p++) saved[p] = true;
            }
        }

        var savedFiles = torrent.Files.Select((f, i) =>
        {
            var count = Math.Max(1, f.EndPieceIndex - f.StartPieceIndex + 1);
            var done = 0;
            for (var p = f.StartPieceIndex; p <= f.EndPieceIndex && p < saved.Length; p++) if (saved[p]) done++;
            var entry = data.Files is { } list && i < list.Count ? list[i] : null;
            return new TorrentFileProgress(i, f.Path, f.Length, f.Length == 0 ? 1 : (double)done / count, entry == null ? TorrentFilePriority.Normal : PriorityOf(entry));
        }).ToList();
        var announced = TrackersOf(torrent.AnnounceUrls.SelectMany(t => t), data)
            .Select((u, i) => new TorrentTracker(u, "Not contacted (the torrent is not running)", null, i + 1)).ToList();
        announced.Add(new TorrentTracker("DHT", torrent.IsPrivate ? "Off: the torrent is private" : DhtOff() ?? "Used when the torrent runs", null, 0));
        return new TorrentDetails(false, Array.Empty<TorrentPeer>(), savedFiles, announced, saved, torrent.PieceLength, torrent.Size, data.Sequential,
            IsPrivate: torrent.IsPrivate);
    }

    /// <summary>Why the DHT is not used (null: it is).</summary>
    string? DhtOff() =>
        !_settings.TorrentUseDht ? "Off (Settings)"
        : _network.Route.Proxy != null ? "Not used: torrents go through the proxy, which cannot carry the DHT"
        : _network.Route.Locked && _network.Route.V4 == null ? $"Not used: {_network.Route.AdapterName} has no IPv4 address"
        : null;

    TorrentTracker DhtRow(bool isPrivate)
    {
        if (isPrivate) return new TorrentTracker("DHT", "Off: the torrent is private", null, 0);
        if (DhtOff() is { } off) return new TorrentTracker("DHT", off, null, 0);
        if (_engine?.Dht is not { } dht) return new TorrentTracker("DHT", "Off (Settings)", null, 0);
        return dht.State == MonoTorrent.Dht.DhtState.Ready
            ? new TorrentTracker("DHT", $"Working ({dht.NodeCount} nodes)", null, 0)
            : new TorrentTracker("DHT", "Starting…", null, 0);
    }

    static TorrentFilePriority PriorityOf(Priority p) => p switch
    {
        Priority.DoNotDownload => TorrentFilePriority.Skip,
        Priority.High or Priority.Highest or Priority.Immediate => TorrentFilePriority.High,
        _ => TorrentFilePriority.Normal,
    };

    static TorrentFilePriority PriorityOf(TorrentFileEntry e) => !e.Selected ? TorrentFilePriority.Skip : e.High ? TorrentFilePriority.High : TorrentFilePriority.Normal;

    static string StatusOf(MonoTorrent.Trackers.TrackerState state) => state switch
    {
        MonoTorrent.Trackers.TrackerState.Ok => "Working",
        MonoTorrent.Trackers.TrackerState.Connecting => "Contacting…",
        MonoTorrent.Trackers.TrackerState.Offline => "Not working (no answer)",
        MonoTorrent.Trackers.TrackerState.InvalidResponse => "Not working (a wrong answer)",
        _ => "Not contacted yet",
    };

    static string AddressOf(Uri uri) => uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.Host.Trim('[', ']')}]:{uri.Port}" : $"{uri.Host}:{uri.Port}";

    /// <summary>The program a peer uses, by name, with the short ID its peer ID starts with ("qBittorrent (qB4630)").</summary>
    static string ClientOf(Software software)
    {
        var id = software.ShortId?.Trim('-', ' ') ?? "";
        if (software.Client == ClientApp.Unknown) return id.Length > 0 ? $"Unknown program ({id})" : "Unknown program";
        return id.Length > 0 ? $"{software.Client} ({id})" : software.Client.ToString();
    }

    /// <summary>Bytes of the chosen files that are in (whole pieces that passed their check).</summary>
    public static long BytesDone(TorrentManager manager, long wanted)
    {
        if (!manager.HasMetadata || wanted <= 0) return 0;
        var fraction = Math.Clamp(manager.PartialProgress / 100.0, 0, 1);
        return (long)(fraction * wanted);
    }

    /// <summary>Every chosen file is complete.</summary>
    public static bool WantedComplete(TorrentManager manager) =>
        manager.HasMetadata && manager.State is not (TorrentState.Hashing or TorrentState.HashingPaused or TorrentState.Metadata or TorrentState.Starting)
                            && manager.Files.Where(f => f.Priority != Priority.DoNotDownload).All(f => f.BitField.AllTrue);

    // ------------------------------------------------------------------------------------------------------------
    // Sharing finished torrents
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Whether a finished torrent should be shared now (the limits are not reached yet).</summary>
    public bool ShouldSeed(DownloadJob job)
    {
        var data = job.Torrent;
        if (data == null || _settings.TorrentSeedRatio <= 0) return false;
        var wanted = data.WantedBytes;
        if (wanted > 0 && (double)data.Uploaded / wanted >= _settings.TorrentSeedRatio) return false;
        return _settings.TorrentSeedMinutes <= 0 || data.SeedSeconds < _settings.TorrentSeedMinutes * 60.0;
    }

    public bool IsSeeding(Guid id)
    {
        lock (_seedGate) return _seeders.ContainsKey(id);
    }

    /// <summary>Takes over a finished torrent that is still in the engine and shares it until a limit is reached.</summary>
    public void Seed(DownloadJob job, TorrentManager manager, long uploadedBase)
    {
        job.Torrent!.Seeding = true;
        lock (_seedGate)
        {
            _seeders[job.Id] = new Seeder { Job = job, Manager = manager, UploadedBase = uploadedBase };
            _seedLoop ??= Task.Run(SeedLoopAsync);
        }

        _ = RebalanceAsync();   // it no longer downloads: the others get its share

        Save(job);
        Changed?.Invoke(job);
    }

    /// <summary>Shares a finished torrent again (after ShadeNet started, or when the user asks), if its files are still all there.</summary>
    public async Task<bool> ResumeSeedingAsync(DownloadJob job)
    {
        var data = job.Torrent;
        if (data == null || IsSeeding(job.Id)) return false;
        var torrent = LoadMetadata(data);
        var root = data.IsFolder ? job.TargetPath : job.SaveDirectory;
        bool Present(TorrentFileEntry f)
        {
            var path = data.IsFolder ? Path.Combine(root, f.Path) : job.TargetPath;
            return File.Exists(path) && new FileInfo(path).Length == f.Length;
        }

        if (torrent == null || data.Files == null || !data.Files.Where(f => f.Selected).All(Present))
        {
            StopSeedingState(job, "the files are not where they were saved");
            return false;
        }

        TorrentManager manager;
        try
        {
            manager = await OpenAsync(job, torrent).ConfigureAwait(false);
            await manager.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not share {Name} again", job.FileName);
            StopSeedingState(job, ex.Message);
            return false;
        }

        Seed(job, manager, data.Uploaded);
        return true;
    }

    /// <summary>"Stop sharing": the torrent leaves the engine; the files stay.</summary>
    public async Task StopSeedingAsync(Guid id)
    {
        Seeder? seeder;
        lock (_seedGate)
        {
            if (!_seeders.Remove(id, out seeder)) return;
        }

        await CloseAsync(seeder.Manager).ConfigureAwait(false);
        StopSeedingState(seeder.Job, null);
    }

    void StopSeedingState(DownloadJob job, string? why)
    {
        if (job.Torrent == null) return;
        job.Torrent.Seeding = false;
        if (why != null) _logger.LogInformation("Stopped sharing {Name}: {Why}", job.FileName, why);
        Save(job);
        Changed?.Invoke(job);
    }

    /// <summary>The live state of a shared torrent, for its snapshot.</summary>
    public TorrentStatus? StatusOf(DownloadJob job)
    {
        Seeder? seeder;
        lock (_seedGate) _seeders.TryGetValue(job.Id, out seeder);
        if (seeder == null) return null;
        var m = seeder.Manager;
        var data = job.Torrent!;
        var (swarmSeeds, swarmLeechers) = SwarmOf(m);
        return new TorrentStatus(m.OpenConnections, m.Peers.Seeds, seeder.Up.Rate, data.Uploaded, Ratio(data), true,
            _network.Route.Offline ? $"Waiting for {_network.Route.AdapterName} to connect" : null, swarmSeeds, swarmLeechers);
    }

    public static double Ratio(TorrentJobData data) => data.Ratio;

    async Task SeedLoopAsync()
    {
        var last = Stopwatch.GetTimestamp();
        while (!_closing.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, _closing.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - last) / (double)Stopwatch.Frequency;
            last = now;

            List<Seeder> seeders;
            lock (_seedGate) seeders = _seeders.Values.ToList();
            foreach (var s in seeders)
            {
                var data = s.Job.Torrent!;
                data.Uploaded = s.UploadedBase + SentOf(s.Manager);
                s.Up.Sample(SentOf(s.Manager));
                data.SeedSeconds += elapsed;

                string? stop = null;
                if (s.Manager.State == TorrentState.Error) stop = s.Manager.Error?.Exception.Message ?? "the torrent engine reported an error";
                else if (s.Manager.State is TorrentState.Downloading && !WantedComplete(s.Manager)) stop = "its files changed on the disk";
                else if (!ShouldSeed(s.Job)) stop = "its sharing limit was reached";

                if (stop != null)
                {
                    lock (_seedGate) _seeders.Remove(s.Job.Id);
                    await CloseAsync(s.Manager).ConfigureAwait(false);
                    StopSeedingState(s.Job, stop);
                    continue;
                }

                if ((now - s.LastSaved) / (double)Stopwatch.Frequency >= 15)
                {
                    s.LastSaved = now;
                    Save(s.Job);
                }

                Changed?.Invoke(s.Job);
            }
        }
    }

    void Save(DownloadJob job)
    {
        try
        {
            Store?.SaveJob(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist job {Id}", job.Id);
        }
    }

    /// <summary>Stops sharing everything (the jobs stay marked as sharing, so it goes on when ShadeNet starts again) and the engine.</summary>
    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        List<Seeder> seeders;
        lock (_seedGate)
        {
            seeders = _seeders.Values.ToList();
            _seeders.Clear();
        }

        foreach (var s in seeders)
        {
            s.Job.Torrent!.Uploaded = s.UploadedBase + SentOf(s.Manager);
            await CloseAsync(s.Manager).ConfigureAwait(false);
            Save(s.Job);
        }

        if (_seedLoop != null)
        {
            try
            {
                await _seedLoop.ConfigureAwait(false);
            }
            catch
            {
                // it only ever ends
            }
        }

        if (_engine is { } engine)
        {
            try
            {
                await engine.StopAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stopping the torrent engine failed");
            }

            engine.Dispose();
            _engine = null;
        }

        _network.Dispose();
    }
}
