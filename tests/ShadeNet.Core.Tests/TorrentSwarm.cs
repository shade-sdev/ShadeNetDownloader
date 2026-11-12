using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections.TrackerServer;
using MonoTorrent.TrackerServer;
using MtSettings = MonoTorrent.Client.EngineSettingsBuilder;

namespace ShadeNet.Core.Tests;

/// <summary>
/// A torrent and the people sharing it, all on this machine: random files, a .torrent file for them, a tracker on 127.0.0.1 and a seeder
/// (MonoTorrent itself) that has every piece. ShadeNet downloads from it as from any swarm.
/// </summary>
public sealed class TorrentSwarm : IAsyncDisposable
{
    readonly List<ClientEngine> _engines = new();
    readonly TrackerServer _tracker;
    readonly ITrackerListener _listener;

    TorrentSwarm(string root, string announce, TrackerServer tracker, ITrackerListener listener)
    {
        Root = root;
        Announce = announce;
        _tracker = tracker;
        _listener = listener;
    }

    public string Root { get; }
    public string Announce { get; }

    /// <summary>Announces the tracker answered (requests through a proxy count too).</summary>
    public long Announces => _tracker.Requests.TotalAnnounces;

    long _seederAnnounces;

    /// <summary>Announces from others than the seeder (the client under test).</summary>
    public long ClientAnnounces => Announces - _seederAnnounces;

    /// <summary>The port the seeder listens on.</summary>
    public int SeederPort { get; private set; }

    /// <summary>The shared content: a folder (several files) or one file.</summary>
    public string Source { get; private set; } = "";
    public Torrent Torrent { get; private set; } = null!;
    public byte[] Metadata { get; private set; } = Array.Empty<byte>();
    public string TorrentFile { get; private set; } = "";
    public string Magnet => $"magnet:?xt=urn:btih:{Torrent.InfoHashes.V1!.ToHex()}&dn={Uri.EscapeDataString(Torrent.Name)}&tr={Uri.EscapeDataString(Announce)}";
    public TorrentManager? Seeder { get; private set; }

    /// <param name="files">Path inside the torrent → size; one entry makes a torrent of one file.</param>
    /// <param name="seederUploadLimit">Bytes per second the seeder sends at most (0 = as fast as it can).</param>
    /// <param name="udpTracker">The tracker speaks UDP (udp://127.0.0.1:port) rather than HTTP.</param>
    /// <param name="seederEncryption">What the seeder accepts (null: MonoTorrent's default, encrypted or not).</param>
    /// <param name="seederAddress">Where the seeder listens (default 127.0.0.1).</param>
    public static async Task<TorrentSwarm> CreateAsync(string root, IReadOnlyList<(string Path, int Size)> files, string name = "Pack",
        int seederUploadLimit = 0, bool seed = true, int pieceLength = 0, bool udpTracker = false,
        IList<MonoTorrent.Connections.EncryptionType>? seederEncryption = null, IPAddress? seederAddress = null)
    {
        Directory.CreateDirectory(root);
        var tracker = new TrackerServer { AllowUnregisteredTorrents = true };
        string announce;
        ITrackerListener listener;
        if (udpTracker)
        {
            var port = FreeUdpPort();
            announce = $"udp://127.0.0.1:{port}/announce";
            listener = TrackerListenerFactory.CreateUdp(IPAddress.Loopback, port);
        }
        else
        {
            announce = $"http://127.0.0.1:{FreePort()}/announce/";
            listener = TrackerListenerFactory.CreateHttp(announce);
        }

        tracker.RegisterListener(listener);
        listener.Start();

        var swarm = new TorrentSwarm(root, announce, tracker, listener);
        var rnd = new Random(Guid.NewGuid().GetHashCode());   // every swarm its own torrent (the same bytes would be the same torrent)
        string source;
        if (files.Count == 1 && !files[0].Path.Contains('\\') && !files[0].Path.Contains('/'))
        {
            Directory.CreateDirectory(Path.Combine(root, "seed"));
            source = Path.Combine(root, "seed", files[0].Path);
            File.WriteAllBytes(source, Bytes(rnd, files[0].Size));
        }
        else
        {
            source = Path.Combine(root, "seed", name);
            foreach (var (p, size) in files)
            {
                var full = Path.Combine(source, p);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, Bytes(rnd, size));
            }
        }

        swarm.Source = source;
        var creator = new TorrentCreator(TorrentType.V1Only) { Announces = { new List<string> { announce } } };
        if (pieceLength > 0) creator.PieceLength = pieceLength;
        var dict = await creator.CreateAsync(new TorrentFileSource(source));
        swarm.Metadata = dict.Encode();
        swarm.Torrent = Torrent.Load(swarm.Metadata);
        swarm.TorrentFile = Path.Combine(root, swarm.Torrent.Name + ".torrent");
        File.WriteAllBytes(swarm.TorrentFile, swarm.Metadata);

        if (seed)
        {
            await swarm.StartSeederAsync(seederUploadLimit, seederEncryption, seederAddress);
            // (the seeder is known to the tracker before anyone asks it)
            for (var i = 0; i < 100 && swarm.Announces == 0; i++) await Task.Delay(50);
            swarm._seederAnnounces = swarm.Announces;
        }

        return swarm;
    }

    static int FreeUdpPort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    static byte[] Bytes(Random rnd, int size)
    {
        var b = new byte[size];
        rnd.NextBytes(b);
        return b;
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>A MonoTorrent client of its own, on the loopback address (no DHT, no port forwarding).</summary>
    public ClientEngine NewEngine(string cache, int uploadLimit = 0, IList<MonoTorrent.Connections.EncryptionType>? encryption = null, IPAddress? address = null)
    {
        address ??= IPAddress.Loopback;
        var builder = new MtSettings
        {
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            DhtEndPoint = null,
            CacheDirectory = cache,
            ListenEndPoints = new() { [address.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4"] = new IPEndPoint(address, FreePort(address)) },
            MaximumUploadRate = uploadLimit,
        };
        if (encryption != null) builder.AllowedEncryption = new List<MonoTorrent.Connections.EncryptionType>(encryption);
        var engine = new ClientEngine(builder.ToSettings());
        _engines.Add(engine);
        return engine;
    }

    /// <summary>The port an engine of the swarm listens on.</summary>
    public static int PortOf(ClientEngine engine) => engine.Settings.ListenEndPoints.Values.First().Port;

    static int FreePort(IPAddress address)
    {
        var l = new TcpListener(address, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async Task StartSeederAsync(int uploadLimit = 0, IList<MonoTorrent.Connections.EncryptionType>? encryption = null, IPAddress? address = null)
    {
        var engine = NewEngine(Path.Combine(Root, "seeder-cache-" + _engines.Count), uploadLimit, encryption, address);
        SeederPort = PortOf(engine);
        Seeder = await engine.AddAsync(Torrent, Path.GetDirectoryName(Source)!);
        await Seeder.HashCheckAsync(autoStart: true);
    }

    public async Task StopSeederAsync()
    {
        if (Seeder == null) return;
        await Seeder.StopAsync();
        await Seeder.Engine!.RemoveAsync(Seeder);
        Seeder = null;
    }

    /// <summary>Another downloader joining the swarm (it can only get the pieces from whoever is sharing them).</summary>
    public async Task<TorrentManager> StartLeecherAsync(string saveTo, IPAddress? address = null)
    {
        var engine = NewEngine(Path.Combine(Root, "leecher-cache-" + _engines.Count), address: address);
        var manager = await engine.AddAsync(Torrent, saveTo);
        await manager.StartAsync();
        return manager;
    }

    /// <summary>The bytes of a shared file (by its path inside the torrent).</summary>
    public byte[] Expected(string path) =>
        File.ReadAllBytes(Torrent.Files.Count == 1 && File.Exists(Source) ? Source : Path.Combine(Source, path));

    /// <summary>A torrent whose file list names a path outside its folder (which no real client may write).</summary>
    public static byte[] WithHostilePath(byte[] metadata, params string[] path)
    {
        var dict = BEncodedDictionary.Decode<BEncodedDictionary>(metadata);
        var info = (BEncodedDictionary)dict["info"];
        var files = (BEncodedList)info["files"];
        var first = (BEncodedDictionary)files[0];
        var list = new BEncodedList();
        foreach (var p in path) list.Add(new BEncodedString(p));
        first["path"] = list;
        return dict.Encode();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var e in _engines)
        {
            try
            {
                await e.StopAllAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // gone already
            }

            e.Dispose();
        }

        _listener.Stop();
        _tracker.Dispose();
    }
}
