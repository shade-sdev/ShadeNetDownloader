using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections.TrackerServer;
using MonoTorrent.TrackerServer;

/// <summary>
/// <c>swarm [-o dir] [--kbps n]</c>: a torrent and the people sharing it, all on this machine (a tracker and a seeder on 127.0.0.1), so
/// the app can be tried, and screenshots taken, without the internet. Prints the magnet link and the .torrent file's path, then shares
/// until stopped. The seeder sends at most <c>--kbps</c> KB/s, so the progress can be watched.
/// </summary>
internal static class SwarmCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? root = null;
        var kbps = 2048;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length) root = args[++i];
            else if (args[i] == "--kbps" && i + 1 < args.Length) kbps = int.Parse(args[++i]);
        }

        root ??= Path.Combine(Path.GetTempPath(), "shadenet-swarm-" + Guid.NewGuid().ToString("N")[..8]);
        var source = Path.Combine(root, "seed", "Open Movie Pack");
        var rnd = new Random();
        foreach (var (path, size) in new[]
                 {
                     ("Open Movie (2024) 1080p.mkv", 48 * 1024 * 1024), ("Extras\\Making of.mp4", 12 * 1024 * 1024), ("Subtitles\\English.srt", 60_000),
                     ("Subtitles\\Deutsch.srt", 62_000), ("poster.jpg", 900_000), ("README.txt", 2_000),
                 })
        {
            var full = Path.Combine(source, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var bytes = new byte[size];
            rnd.NextBytes(bytes);
            await File.WriteAllBytesAsync(full, bytes);
        }

        var announce = $"http://127.0.0.1:{FreePort()}/announce/";
        using var tracker = new TrackerServer { AllowUnregisteredTorrents = true };
        var listener = TrackerListenerFactory.CreateHttp(announce);
        tracker.RegisterListener(listener);
        listener.Start();

        var creator = new TorrentCreator(TorrentType.V1Only) { Announces = { new List<string> { announce } }, Comment = "ShadeNet test swarm" };
        var metadata = (await creator.CreateAsync(new TorrentFileSource(source))).Encode();
        var torrent = Torrent.Load(metadata);
        var torrentFile = Path.Combine(root, "Open Movie Pack.torrent");
        await File.WriteAllBytesAsync(torrentFile, metadata);

        using var engine = new ClientEngine(new EngineSettingsBuilder
        {
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            DhtEndPoint = null,
            CacheDirectory = Path.Combine(root, "seeder-cache"),
            ListenEndPoints = new() { ["ipv4"] = new IPEndPoint(IPAddress.Loopback, FreePort()) },
        }.ToSettings());
        // (MonoTorrent holds back a torrent by its own limit; the engine's total limit does not)
        var seeder = await engine.AddAsync(torrent, Path.GetDirectoryName(source)!, new TorrentSettingsBuilder { MaximumUploadRate = kbps * 1024 }.ToSettings());
        await seeder.HashCheckAsync(autoStart: true);

        Console.WriteLine($"FOLDER {source}");
        Console.WriteLine($"TORRENT {torrentFile}");
        Console.WriteLine($"MAGNET magnet:?xt=urn:btih:{torrent.InfoHashes.V1!.ToHex()}&dn={Uri.EscapeDataString(torrent.Name)}&tr={Uri.EscapeDataString(announce)}");
        Console.WriteLine($"Sharing {torrent.Name} ({torrent.Size / 1024 / 1024} MB, {torrent.Files.Count} files) at up to {kbps} KB/s. Ctrl+C to stop.");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
