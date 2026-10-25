using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ShadeNet.Core.Hls;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

// ================================================================================================================
// the playlist parser (strings in, objects out)
// ================================================================================================================

public class HlsParserTests
{
    static readonly Uri Base = new("https://cdn.example.com/video/master.m3u8?token=abc");

    const string Master = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="English, stereo",LANGUAGE="en",DEFAULT=YES,AUTOSELECT=YES,URI="audio/en.m3u8"
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="Deutsch",LANGUAGE="de",DEFAULT=NO,URI="audio/de.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=900000,AVERAGE-BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2",AUDIO="aud",FRAME-RATE=29.97
        360p/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720,AUDIO="aud"
        https://other.example.com/720p/index.m3u8
        #EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=100000,URI="iframes.m3u8"
        """;

    [Fact]
    public void A_master_playlist_lists_its_qualities_and_audio_tracks()
    {
        var master = Assert.IsType<HlsMasterPlaylist>(HlsParser.Parse(Master, Base));

        Assert.Equal(2, master.Variants.Count);   // the I-frame list is not a quality
        var low = master.Variants[0];
        Assert.Equal(new Uri("https://cdn.example.com/video/360p/index.m3u8"), low.Url);
        Assert.Equal(800000, low.Bandwidth);       // the average is the honest figure
        Assert.Equal((640, 360), (low.Width, low.Height));
        Assert.Equal("avc1.4d401e,mp4a.40.2", low.Codecs);   // a quoted value may hold commas
        Assert.Equal(29.97, low.FrameRate);
        Assert.Equal(new Uri("https://other.example.com/720p/index.m3u8"), master.Variants[1].Url);

        Assert.Equal(2, master.Renditions.Count);
        Assert.Equal("English, stereo", master.Renditions[0].Name);
        Assert.True(master.Renditions[0].IsDefault);
        Assert.Equal(new Uri("https://cdn.example.com/video/audio/en.m3u8"), master.Renditions[0].Url);
    }

    [Fact]
    public void The_best_quality_is_the_sharpest_and_a_choice_is_matched_by_height_then_bit_rate()
    {
        var master = (HlsMasterPlaylist)HlsParser.Parse(Master, Base);
        Assert.Equal(720, HlsChoice.Best(master.Variants).Height);
        Assert.Equal(360, HlsChoice.Match(master.Variants, 360, 123).Height);
        Assert.Equal(720, HlsChoice.Match(master.Variants, 720, 1).Height);
        Assert.Equal(720, HlsChoice.Match(master.Variants, 1080, 2_900_000).Height);   // no such height: the nearest bit rate

        var audio = HlsChoice.AudioFor(master, master.Variants[0]);
        Assert.Equal("English, stereo", audio!.Name);
        Assert.Equal("Deutsch", HlsChoice.AudioFor(master, master.Variants[0], "Deutsch")!.Name);
        Assert.Null(HlsChoice.AudioFor(master, new HlsVariant(Base, 1, 0, 0, null, null, 0)));
    }

    [Fact]
    public void A_media_playlist_gives_segments_with_resolved_addresses_and_sequence_numbers()
    {
        var media = Assert.IsType<HlsMediaPlaylist>(HlsParser.Parse("""
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:100
            #EXT-X-PLAYLIST-TYPE:VOD
            #EXTINF:5.5,first
            seg0.ts
            #EXTINF:6.0,
            /abs/seg1.ts?x=1
            #EXT-X-DISCONTINUITY
            #EXTINF:4.25,
            https://cdn2.example.com/seg2.ts
            #EXT-X-ENDLIST
            """, Base));

        Assert.True(media.IsEnded);
        Assert.False(media.IsLive);
        Assert.Equal(100, media.MediaSequence);
        Assert.Equal(new[] { 100L, 101, 102 }, media.Segments.Select(s => s.Sequence));
        Assert.Equal(new[] { 0, 1, 2 }, media.Segments.Select(s => s.Index));
        Assert.Equal(new Uri("https://cdn.example.com/video/seg0.ts"), media.Segments[0].Url);
        Assert.Equal(new Uri("https://cdn.example.com/abs/seg1.ts?x=1"), media.Segments[1].Url);
        Assert.Equal(new Uri("https://cdn2.example.com/seg2.ts"), media.Segments[2].Url);
        Assert.Equal(15.75, media.TotalDuration);
        Assert.False(media.Segments[1].Discontinuity);
        Assert.True(media.Segments[2].Discontinuity);
    }

    [Fact]
    public void A_playlist_without_an_end_marker_is_live_unless_it_says_vod()
    {
        var live = (HlsMediaPlaylist)HlsParser.Parse("#EXTM3U\n#EXTINF:4,\na.ts\n#EXTINF:4,\nb.ts\n", Base);
        Assert.True(live.IsLive);
        var vod = (HlsMediaPlaylist)HlsParser.Parse("#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:4,\na.ts\n", Base);
        Assert.False(vod.IsLive);
    }

    [Fact]
    public void Byte_ranges_continue_where_the_previous_one_of_the_same_file_ended()
    {
        var media = (HlsMediaPlaylist)HlsParser.Parse("""
            #EXTM3U
            #EXTINF:4,
            #EXT-X-BYTERANGE:1000@500
            all.ts
            #EXTINF:4,
            #EXT-X-BYTERANGE:2000
            all.ts
            #EXTINF:4,
            #EXT-X-BYTERANGE:300
            other.ts
            #EXT-X-ENDLIST
            """, Base);

        Assert.Equal((500L, 1000L), (media.Segments[0].ByteOffset!.Value, media.Segments[0].ByteLength!.Value));
        Assert.Equal((1500L, 2000L), (media.Segments[1].ByteOffset!.Value, media.Segments[1].ByteLength!.Value));
        Assert.Equal((0L, 300L), (media.Segments[2].ByteOffset!.Value, media.Segments[2].ByteLength!.Value));   // another file starts at 0
    }

    [Fact]
    public void Keys_and_init_sections_apply_to_the_segments_that_follow_them()
    {
        var media = (HlsMediaPlaylist)HlsParser.Parse("""
            #EXTM3U
            #EXT-X-MEDIA-SEQUENCE:7
            #EXT-X-MAP:URI="init.mp4"
            #EXTINF:4,
            a.m4s
            #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example.com/k1",IV=0x000102030405060708090A0B0C0D0E0F
            #EXTINF:4,
            b.m4s
            #EXT-X-KEY:METHOD=AES-128,URI="k2.bin"
            #EXTINF:4,
            c.m4s
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:4,
            d.m4s
            #EXT-X-ENDLIST
            """, Base);

        Assert.All(media.Segments, s => Assert.Equal(new Uri("https://cdn.example.com/video/init.mp4"), s.Map!.Url));
        Assert.True(media.UsesInitSection);
        Assert.Null(media.Segments[0].Key);
        Assert.Equal(HlsEncryption.Aes128, media.Segments[1].Key!.Method);
        Assert.Equal(new Uri("https://keys.example.com/k1"), media.Segments[1].Key!.Url);
        Assert.Equal(Convert.FromHexString("000102030405060708090A0B0C0D0E0F"), media.Segments[1].Key!.Iv);
        Assert.Null(media.Segments[2].Key!.Iv);   // the IV is then the sequence number
        Assert.Equal(HlsEncryption.None, media.Segments[3].Key!.Method);
        Assert.Equal(HlsEncryption.Aes128, media.Encryption);
    }

    [Fact]
    public void The_default_iv_is_the_sequence_number_big_endian()
    {
        Assert.Equal(new byte[16], HlsParser.DefaultIv(0));
        var iv = HlsParser.DefaultIv(0x0102);
        Assert.Equal(1, iv[14]);
        Assert.Equal(2, iv[15]);
        Assert.Equal(0, iv[0]);
    }

    [Theory]
    [InlineData("METHOD=SAMPLE-AES,URI=\"k\"")]
    [InlineData("METHOD=AES-128,URI=\"skd://x\",KEYFORMAT=\"com.apple.streamingkeydelivery\"")]
    [InlineData("METHOD=SAMPLE-AES-CTR,URI=\"data:text/plain;base64,AAAA\",KEYFORMAT=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"")]
    public void Protection_that_cannot_be_undone_is_reported_not_downloaded(string key)
    {
        var media = (HlsMediaPlaylist)HlsParser.Parse($"#EXTM3U\n#EXT-X-KEY:{key}\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n", Base);
        Assert.Equal(HlsEncryption.Unsupported, media.Encryption);
    }

    [Fact]
    public void Things_that_are_not_playlists_are_refused_with_a_reason()
    {
        Assert.Throws<HlsFormatException>(() => HlsParser.Parse("<html>sign in</html>", Base));
        Assert.Throws<HlsFormatException>(() => HlsParser.Parse("#EXTM3U\n#EXT-X-ENDLIST\n", Base));   // no segments
        Assert.Throws<HlsFormatException>(() => HlsParser.Parse("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n", Base));   // no stream address
        Assert.True(HlsParser.LooksLikePlaylist("﻿#EXTM3U\r\n"));
        Assert.False(HlsParser.LooksLikePlaylist(""));
    }

    [Fact]
    public void Windows_line_endings_and_blank_lines_do_not_matter()
    {
        var media = (HlsMediaPlaylist)HlsParser.Parse("#EXTM3U\r\n\r\n#EXTINF:4,\r\n\r\na.ts\r\n#EXT-X-ENDLIST\r\n", Base);
        Assert.Single(media.Segments);
    }

    [Theory]
    [InlineData("https://x.example/a/b/stream.m3u8?x=1", null, true)]
    [InlineData("https://x.example/play", "application/vnd.apple.mpegurl", true)]
    [InlineData("https://x.example/play", "application/x-mpegURL; charset=utf-8", true)]
    [InlineData("https://x.example/file.zip", "application/zip", false)]
    [InlineData("https://x.example/movie.mp4", null, false)]
    public void Addresses_that_are_streams_are_recognised(string url, string? contentType, bool expected) =>
        Assert.Equal(expected, DownloadManager.IsStreamAddress(url, contentType));
}

// ================================================================================================================
// downloading streams
// ================================================================================================================

/// <summary>A small web server for HLS tests: fixed files, and hooks to make things go wrong.</summary>
sealed class HlsServer : IDisposable
{
    readonly HttpListener _listener = new();
    readonly CancellationTokenSource _stop = new();

    public HlsServer()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        Base = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Base + "/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    public string Base { get; }
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentDictionary<string, int> Hits { get; } = new();
    public ConcurrentQueue<(string Path, string? Cookie, string? Referer, string? UserAgent)> Requests { get; } = new();

    /// <summary>Called first for every request; return true when it answered itself.</summary>
    public Func<HttpListenerContext, bool>? Intercept { get; set; }

    public int DelayMs { get; set; }

    public string Url(string path) => Base + path;

    public void Put(string path, string text) => Files[path] = Encoding.UTF8.GetBytes(text);

    async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => Handle(ctx));
        }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            Hits.AddOrUpdate(path, 1, (_, n) => n + 1);
            Requests.Enqueue((path, ctx.Request.Headers["Cookie"], ctx.Request.Headers["Referer"], ctx.Request.UserAgent));

            if (Intercept?.Invoke(ctx) == true) return;

            if (DelayMs > 0 && path.EndsWith(".ts")) await Task.Delay(DelayMs);

            if (!Files.TryGetValue(path, out var body))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            ctx.Response.ContentType = path.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "application/octet-stream";
            ctx.Response.AddHeader("Accept-Ranges", "bytes");
            var from = 0;
            var to = body.Length - 1;
            if (ctx.Request.Headers["Range"] is { } range && range.StartsWith("bytes="))
            {
                var parts = range[6..].Split('-');
                from = int.Parse(parts[0]);
                if (parts[1].Length > 0) to = Math.Min(int.Parse(parts[1]), body.Length - 1);
                ctx.Response.StatusCode = 206;
                ctx.Response.AddHeader("Content-Range", $"bytes {from}-{to}/{body.Length}");
            }

            ctx.Response.ContentLength64 = to - from + 1;
            await ctx.Response.OutputStream.WriteAsync(body.AsMemory(from, to - from + 1));
            ctx.Response.Close();
        }
        catch
        {
            // a client that went away
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }

    // ---- building streams ----

    public static byte[] Bytes(string track, int index, int size)
    {
        var data = new byte[size];
        new Random(HashCode.Combine(track, index)).NextBytes(data);
        return data;
    }

    public static string Playlist(IEnumerable<string> header, IEnumerable<(string Uri, double Duration, string? Tag)> segments, bool end = true)
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:4\n");
        foreach (var h in header) sb.Append(h).Append('\n');
        foreach (var (uri, duration, tag) in segments)
        {
            if (tag != null) sb.Append(tag).Append('\n');
            sb.Append($"#EXTINF:{duration:0.###},\n{uri}\n");
        }

        if (end) sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>A plain VOD stream "/{name}/index.m3u8" with <paramref name="count"/> segments; returns the concatenated content.</summary>
    public byte[] AddVod(string name, int count, int size = 20_000, Func<int, byte[], byte[]>? transform = null, string[]? header = null)
    {
        var all = new List<byte>();
        var entries = new List<(string, double, string?)>();
        for (var i = 0; i < count; i++)
        {
            var plain = Bytes(name, i, size);
            all.AddRange(plain);
            Files[$"/{name}/seg{i}.ts"] = transform != null ? transform(i, plain) : plain;
            entries.Add(($"seg{i}.ts", 4, null));
        }

        Put($"/{name}/index.m3u8", Playlist(header ?? new[] { "#EXT-X-PLAYLIST-TYPE:VOD" }, entries));
        return all.ToArray();
    }
}

public class HlsDownloadTests
{
    static string NoFfmpeg(Harness h) => Path.Combine(h.Root, "no-such-ffmpeg.exe");

    static DownloadManager Manager(Harness h, bool withFfmpeg = false, string? db = null) =>
        h.NewManager(connections: 4, tweak: s => { if (!withFfmpeg) s.FfmpegPath = NoFfmpeg(h); }, db: db);

    static DownloadJob AddStream(DownloadManager m, string url, string fileName = "clip.mp4", StreamChoice? choice = null,
        int connections = 4, AddMode mode = AddMode.Start, string? cookies = null, string? referer = null, string? userAgent = null) =>
        m.Add(new AddDownloadRequest
        {
            Url = url,
            FileName = fileName,
            Stream = choice ?? new StreamChoice(),
            MaxConnections = connections,
            Mode = mode,
            Cookies = cookies,
            Referer = referer,
            UserAgent = userAgent,
        });

    [Fact]
    public async Task A_plain_stream_is_downloaded_and_joined_in_order_without_ffmpeg()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var expected = server.AddVod("movie", 9);
        var m = Manager(h);

        var job = AddStream(m, server.Url("/movie/index.m3u8"));
        await Finished(job);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal("clip.ts", job.FileName);                 // transport-stream pieces make a .ts without ffmpeg
        Assert.Equal(expected, File.ReadAllBytes(job.TargetPath));
        Assert.Equal(expected.Length, job.TotalSize);
        Assert.False(Directory.Exists(job.WorkFolder), "the pieces are removed once joined");
        Assert.Equal(job.TargetPath, Assert.Single(Directory.GetFiles(job.SaveDirectory)));   // nothing but the finished file is left
    }

    [Fact]
    public async Task Progress_rises_steadily_and_ends_exact()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer { DelayMs = 40 };
        server.AddVod("movie", 12, 30_000);
        var m = Manager(h);
        var seen = new ConcurrentQueue<JobSnapshot>();
        m.JobUpdated += s => seen.Enqueue(s);

        var job = AddStream(m, server.Url("/movie/index.m3u8"), connections: 2);
        await Finished(job);

        var snaps = seen.Where(s => s.Id == job.Id && s.State is DownloadState.Downloading).ToList();
        Assert.NotEmpty(snaps);
        Assert.Equal(snaps.Select(s => s.Downloaded).OrderBy(x => x), snaps.Select(s => s.Downloaded));   // never goes back
        Assert.All(snaps, s => Assert.True(s.Parts == 12 && s.PartsDone <= 12));
        Assert.Contains(snaps, s => s.Total is > 0);
        Assert.All(snaps.Where(s => s.Total != null), s => Assert.True(s.Downloaded <= s.Total));

        var last = seen.Last(s => s.Id == job.Id);
        Assert.Equal(DownloadState.Completed, last.State);
        Assert.Equal(12 * 30_000, last.Downloaded);
        Assert.Equal(last.Downloaded, last.Total);
    }

    [Fact]
    public async Task AES_128_segments_are_decrypted_with_an_explicit_iv()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var key = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(16);
        server.Files["/enc/key.bin"] = key;
        var expected = server.AddVod("enc", 6, 10_000, (_, plain) => Encrypt(plain, key, iv),
            new[] { "#EXT-X-PLAYLIST-TYPE:VOD", $"#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x{Convert.ToHexString(iv)}" });
        var m = Manager(h);

        var job = AddStream(m, server.Url("/enc/index.m3u8"));
        await Finished(job);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal(expected, File.ReadAllBytes(job.TargetPath));
        Assert.Equal(1, server.Hits["/enc/key.bin"]);           // one key, fetched once for all segments
    }

    [Fact]
    public async Task AES_128_without_an_iv_uses_the_sequence_number()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var key = RandomNumberGenerator.GetBytes(16);
        server.Files["/enc/key.bin"] = key;
        const int firstSequence = 250;   // above 255: the second byte of the IV matters
        var expected = server.AddVod("enc", 5, 7_000, (i, plain) => Encrypt(plain, key, HlsParser.DefaultIv(firstSequence + i)),
            new[] { "#EXT-X-PLAYLIST-TYPE:VOD", $"#EXT-X-MEDIA-SEQUENCE:{firstSequence}", "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"" });
        var m = Manager(h);

        var job = AddStream(m, server.Url("/enc/index.m3u8"));
        await Finished(job);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal(expected, File.ReadAllBytes(job.TargetPath));
    }

    [Fact]
    public async Task A_wrong_key_fails_clearly_instead_of_saving_garbage()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var key = RandomNumberGenerator.GetBytes(16);
        server.Files["/enc/key.bin"] = RandomNumberGenerator.GetBytes(16);   // not the key the segments were made with
        server.AddVod("enc", 3, 5_000, (_, plain) => Encrypt(plain, key, new byte[16]),
            new[] { "#EXT-X-PLAYLIST-TYPE:VOD", "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x00000000000000000000000000000000" });
        var m = Manager(h);

        var job = AddStream(m, server.Url("/enc/index.m3u8"));
        await Finished(job);

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal(JobErrorKind.DataCorruption, job.ErrorKind);
        Assert.Empty(Directory.GetFiles(job.SaveDirectory, "*.ts"));
    }

    [Fact]
    public async Task Fragmented_mp4_is_joined_with_its_init_section_into_an_mp4()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var init = HlsServer.Bytes("init", 0, 700);
        server.Files["/f/init.mp4"] = init;
        var all = new List<byte>(init);
        var entries = new List<(string, double, string?)>();
        for (var i = 0; i < 5; i++)
        {
            var seg = HlsServer.Bytes("frag", i, 9_000);
            server.Files[$"/f/s{i}.m4s"] = seg;
            all.AddRange(seg);
            entries.Add(($"s{i}.m4s", 4, null));
        }

        server.Put("/f/index.m3u8", HlsServer.Playlist(new[] { "#EXT-X-PLAYLIST-TYPE:VOD", "#EXT-X-MAP:URI=\"init.mp4\"" }, entries));
        var m = Manager(h);

        var job = AddStream(m, server.Url("/f/index.m3u8"));
        await Finished(job);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal("clip.mp4", job.FileName);
        Assert.Equal(all.ToArray(), File.ReadAllBytes(job.TargetPath));
        Assert.Equal(1, server.Hits["/f/init.mp4"]);
    }

    [Fact]
    public async Task Segments_that_are_byte_ranges_of_one_file_are_fetched_as_ranges()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var whole = HlsServer.Bytes("whole", 0, 6_000);
        server.Files["/r/all.ts"] = whole;
        server.Put("/r/index.m3u8", "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:4,\n#EXT-X-BYTERANGE:2000@0\nall.ts\n#EXTINF:4,\n#EXT-X-BYTERANGE:2000\nall.ts\n#EXTINF:4,\n#EXT-X-BYTERANGE:2000\nall.ts\n#EXT-X-ENDLIST\n");
        var m = Manager(h);

        var job = AddStream(m, server.Url("/r/index.m3u8"));
        await Finished(job);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal(whole, File.ReadAllBytes(job.TargetPath));
    }

    [Fact]
    public async Task The_chosen_quality_is_the_one_downloaded_and_the_default_is_the_best()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var low = server.AddVod("low", 3, 4_000);
        var high = server.AddVod("high", 3, 8_000);
        server.Put("/master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=640x360\nlow/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1920x1080\nhigh/index.m3u8\n");
        var m = Manager(h);

        var chosen = AddStream(m, server.Url("/master.m3u8"), "low.mp4", new StreamChoice(360, 500000));
        var best = AddStream(m, server.Url("/master.m3u8"), "best.mp4");
        await Finished(chosen);
        await Finished(best);

        Assert.Equal(low, File.ReadAllBytes(chosen.TargetPath));
        Assert.Equal(high, File.ReadAllBytes(best.TargetPath));
    }

    [Fact]
    public async Task Separate_audio_without_ffmpeg_fails_with_advice_and_keeps_the_pieces()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.AddVod("v", 3, 4_000);
        server.AddVod("a", 3, 2_000);
        server.Put("/master.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",DEFAULT=YES,URI=\"a/index.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360,AUDIO=\"aud\"\nv/index.m3u8\n");
        var m = Manager(h);

        var job = AddStream(m, server.Url("/master.m3u8"));
        await Finished(job);

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal(JobErrorKind.Tool, job.ErrorKind);
        Assert.Contains("ffmpeg", job.Error);
        Assert.True(Directory.Exists(job.WorkFolder), "nothing has to be downloaded again once ffmpeg is there");
        Assert.Equal(6, Directory.GetFiles(job.WorkFolder, "*.seg").Length);
    }

    [Fact]
    public async Task A_protected_stream_is_refused_with_the_reason()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.Files["/drm/seg0.ts"] = new byte[100];
        server.Put("/drm/index.m3u8", "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://abc\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:4,\nseg0.ts\n#EXT-X-ENDLIST\n");
        var m = Manager(h);

        var drm = AddStream(m, server.Url("/drm/index.m3u8"));
        await Finished(drm);

        Assert.Equal((DownloadState.Failed, JobErrorKind.Unsupported), (drm.State, drm.ErrorKind));
        Assert.Contains("protected", drm.Error);
        Assert.False(server.Hits.ContainsKey("/drm/seg0.ts"), "no media is fetched for a stream that cannot be saved");
    }

    [Fact]
    public async Task A_hiccup_on_one_segment_is_retried_and_a_gone_segment_fails_with_an_expired_address()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.AddVod("flaky", 5);
        server.AddVod("gone", 5);
        var failedOnce = 0;
        server.Intercept = ctx =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (path == "/flaky/seg2.ts" && Interlocked.Increment(ref failedOnce) == 1)
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.Close();
                return true;
            }

            if (path == "/gone/seg3.ts")
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.Close();
                return true;
            }

            return false;
        };
        var m = Manager(h);

        var flaky = AddStream(m, server.Url("/flaky/index.m3u8"), "flaky.mp4");
        var gone = AddStream(m, server.Url("/gone/index.m3u8"), "gone.mp4");
        await Finished(flaky);
        await Finished(gone);

        Assert.Equal(DownloadState.Completed, flaky.State);
        Assert.True(flaky.Stats.Retries >= 1);
        Assert.Equal(DownloadState.Failed, gone.State);
        Assert.Equal(JobErrorKind.AddressExpired, gone.ErrorKind);   // so the card offers "Refresh address"
    }

    [Fact]
    public async Task The_browsers_session_goes_to_every_segment_request()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.AddVod("movie", 4);
        var m = Manager(h);

        var job = AddStream(m, server.Url("/movie/index.m3u8"), cookies: "sid=abc123", referer: "https://site.example/watch/1", userAgent: "Browser-UA/7");
        await Finished(job);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        var segments = server.Requests.Where(r => r.Path.EndsWith(".ts")).ToList();
        Assert.Equal(4, segments.Count);
        Assert.All(segments, r =>
        {
            Assert.Equal("sid=abc123", r.Cookie);
            Assert.Equal("https://site.example/watch/1", r.Referer);
            Assert.Equal("Browser-UA/7", r.UserAgent);
        });
    }

    [Fact]
    public async Task Pause_and_resume_do_not_fetch_finished_segments_again()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer { DelayMs = 120 };
        var expected = server.AddVod("slow", 16, 15_000);
        var m = Manager(h);
        var doneParts = 0;
        m.JobUpdated += s => doneParts = Math.Max(doneParts, s.PartsDone);

        var job = AddStream(m, server.Url("/slow/index.m3u8"), connections: 2);
        await WaitUntil(() => doneParts >= 5, 30, "some segments to finish");
        m.Pause(job.Id);
        await WaitUntil(() => job.State == DownloadState.Paused, 30, "the pause");

        var kept = Directory.GetFiles(job.WorkFolder, "*.seg").Length;
        Assert.InRange(kept, 5, 15);
        var snapshot = m.Snapshot(job);
        Assert.True(snapshot.Downloaded > 0 && snapshot.PartsDone >= 5, "a paused stream still shows how far it got");

        server.Hits.Clear();
        m.Start(job.Id);
        await Finished(job);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal(expected, File.ReadAllBytes(job.TargetPath));
        var refetched = server.Hits.Count(kv => kv.Key.EndsWith(".ts"));
        Assert.True(refetched <= 16 - kept + 0, $"{refetched} segments requested again, but only {16 - kept} were missing");
    }

    [Fact]
    public async Task Removing_a_running_stream_download_leaves_nothing_behind()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer { DelayMs = 100 };
        server.AddVod("slow", 12);
        var m = Manager(h);
        var parts = 0;
        m.JobUpdated += s => parts = Math.Max(parts, s.PartsDone);

        var job = AddStream(m, server.Url("/slow/index.m3u8"), connections: 2);
        await WaitUntil(() => parts >= 2, 30, "a few segments");
        await m.RemoveAsync(job.Id, deleteFiles: true);

        Assert.False(Directory.Exists(job.WorkFolder));
        Assert.Empty(Directory.GetFileSystemEntries(job.SaveDirectory));
    }

    [Fact]
    public async Task A_stream_job_survives_a_restart_with_its_quality_and_progress()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.AddVod("low", 3);
        var db = Path.Combine(h.Root, "jobs.db");
        Guid id;
        {
            var m = Manager(h, db: db);
            var job = AddStream(m, server.Url("/master.m3u8"), choice: new StreamChoice(720, 2_000_000, "English"), mode: AddMode.Hold);
            job.Hls!.PartsDone = 4;
            job.Hls.Parts = 10;
            job.Hls.BytesDone = 123_456;
            job.Hls.EstimatedTotal = 400_000;
            m.SetMaxConnections(job.Id, 3);   // saves the job
            id = job.Id;
            m.Dispose();
        }

        var again = h.NewManager(db: db);
        again.Load();
        var back = again.Find(id)!;
        Assert.Equal(DownloadKind.Hls, back.Kind);
        Assert.Equal((720, 2_000_000L, "English"), (back.Hls!.Height, back.Hls.Bandwidth, back.Hls.AudioName));
        var snap = again.Snapshot(back);
        Assert.Equal((123_456L, (long?)400_000, 4, 10), (snap.Downloaded, snap.Total, snap.PartsDone, snap.Parts));
    }

    [Fact]
    public async Task The_dialog_can_ask_what_a_stream_offers_before_anything_is_added()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.AddVod("low", 3);
        server.AddVod("high", 5);
        server.Put("/master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=640x360\nlow/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1920x1080\nhigh/index.m3u8\n");
        server.Put("/live/index.m3u8", "#EXTM3U\n#EXTINF:4,\na.ts\n");
        var m = Manager(h);

        var info = await m.DescribeStreamAsync(new RequestSpec(server.Url("/master.m3u8")));
        Assert.Equal(new[] { 1080, 360 }, info.Variants.Select(v => v.Height));   // best first
        Assert.Equal(20, info.DurationSeconds);                                  // the best quality's length
        Assert.Null(info.Problem);
        Assert.NotEqual("master", info.SuggestedName);                           // "master" is not a name: the host is used

        var live = await m.DescribeStreamAsync(new RequestSpec(server.Url("/live/index.m3u8")));
        Assert.True(live.IsLive);
        Assert.Null(live.Problem);   // a live stream is recorded

        server.Put("/page.m3u8", "<html>sign in</html>");
        await Assert.ThrowsAsync<HlsFormatException>(() => m.DescribeStreamAsync(new RequestSpec(server.Url("/page.m3u8"))));
    }

    [Fact]
    public async Task A_quality_that_leads_to_another_list_of_qualities_is_followed()
    {
        // some servers nest masters: the site's master has one "quality", which is the CDN's own master
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.AddVod("low", 3);
        var high = server.AddVod("high", 5);
        server.Put("/cdn/master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=640x360\n/low/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1920x1080\n/high/index.m3u8\n");
        server.Put("/site/master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=2500000\n/cdn/master.m3u8\n");
        var m = Manager(h);

        var info = await m.DescribeStreamAsync(new RequestSpec(server.Url("/site/master.m3u8")));
        Assert.Equal(new[] { 1080, 360 }, info.Variants.Select(v => v.Height));   // the qualities the inner master offers
        Assert.Equal(20, info.DurationSeconds);

        var job = AddStream(m, server.Url("/site/master.m3u8"));
        await Finished(job);
        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal(high, File.ReadAllBytes(job.TargetPath));                    // the best quality, through both masters
    }

    [Fact]
    public async Task A_server_that_gives_bots_a_decoy_list_gets_the_client_hints_a_browser_sends()
    {
        // like a real video host: a request that says Chrome but lacks Chrome's client hints gets a list of qualities that
        // leads only to itself, never to the video
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var expected = server.AddVod("movie", 4);
        var hints = new ConcurrentQueue<string?>();
        server.Intercept = ctx =>
        {
            hints.Enqueue(ctx.Request.Headers["Sec-CH-UA"]);
            if (ctx.Request.Headers["Sec-CH-UA"] != null) return false;
            var decoy = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1400000,RESOLUTION=1280x720\nindex.m3u8\n");
            ctx.Response.ContentType = "application/vnd.apple.mpegurl";
            ctx.Response.OutputStream.Write(decoy);
            ctx.Response.Close();
            return true;
        };
        var m = Manager(h);

        var job = AddStream(m, server.Url("/movie/index.m3u8"));
        await Finished(job);
        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal(expected, File.ReadAllBytes(job.TargetPath));
        Assert.All(hints, v => Assert.Contains("\"Chromium\";v=\"124\"", v));   // every request, worked out from ShadeNet's own Chrome User-Agent
    }

    [Fact]
    public void The_address_the_player_loaded_is_used_when_it_only_adds_parameters_of_its_own()
    {
        var listed = new Uri("https://cdn.example/hls/,x_n,.urlset/index-v1-a1.m3u8?t=T&s=1");
        Assert.Equal("https://cdn.example/hls/,x_n,.urlset/index-v1-a1.m3u8?t=T&s=1&gk=K",
            HlsChoice.AsPlayed(listed, ["https://cdn.example/hls/,x_n,.urlset/index-a1.m3u8?t=T&s=1&gk=K", "https://cdn.example/hls/,x_n,.urlset/index-v1-a1.m3u8?t=T&s=1&gk=K"]).AbsoluteUri);
        Assert.Same(listed, HlsChoice.AsPlayed(listed, ["https://cdn.example/hls/,x_n,.urlset/index-v1-a1.m3u8?t=OLD&s=1&gk=K"]));   // a refreshed link keeps its own
        Assert.Same(listed, HlsChoice.AsPlayed(listed, ["https://elsewhere.example/hls/,x_n,.urlset/index-v1-a1.m3u8?t=T&s=1&gk=K"]));
        Assert.Same(listed, HlsChoice.AsPlayed(listed, null));
    }

    [Fact]
    public async Task A_quality_is_asked_for_with_the_key_the_browsers_player_added()
    {
        // like a video host whose player adds "&gk=..." to every playlist the master names: without it, the server refuses
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var expected = server.AddVod("movie", 4);
        server.Put("/master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=649000,RESOLUTION=864x488\n/movie/index.m3u8?t=T\n");
        server.Intercept = ctx =>
        {
            if (ctx.Request.Url!.AbsolutePath != "/movie/index.m3u8" || ctx.Request.QueryString["gk"] == "K") return false;
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            return true;
        };
        var m = Manager(h);

        var job = AddStream(m, server.Url("/master.m3u8"), choice: new StreamChoice(Played: [server.Url("/movie/index.m3u8?t=T&gk=K")]));
        await Finished(job);
        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal(expected, File.ReadAllBytes(job.TargetPath));
    }

    [Fact]
    public async Task Lists_of_qualities_that_point_to_one_another_for_ever_are_refused()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.Put("/loop.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\n/loop.m3u8\n");
        var m = Manager(h);

        var ex = await Assert.ThrowsAsync<HlsFormatException>(() => m.DescribeStreamAsync(new RequestSpec(server.Url("/loop.m3u8"))));
        Assert.Contains("without ever reaching the video", ex.Message);
        Assert.Contains("asking for loop.m3u8 gave another list of qualities, which leads to loop.m3u8", ex.Message);   // says where it went round
    }

    static byte[] Encrypt(byte[] plain, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes.CreateEncryptor().TransformFinalBlock(plain, 0, plain.Length);
    }
}

// ================================================================================================================
// with a real ffmpeg (the tests return at once on a machine that has none)
// ================================================================================================================

public class HlsFfmpegTests
{
    static readonly string? Ffmpeg = Hls.Ffmpeg.Locate(null);

    /// <summary>Makes a real 6 s stream (video + audio) with ffmpeg itself: 2 s segments, optionally with the audio kept apart.</summary>
    static async Task<string> MakeStreamAsync(string folder, bool separateAudio)
    {
        Directory.CreateDirectory(folder);
        var args = new List<string>
        {
            "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=25",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "50", "-c:a", "aac", "-b:a", "64k",
            "-f", "hls", "-hls_time", "2", "-hls_playlist_type", "vod",
        };
        if (separateAudio)
        {
            args.AddRange(new[] { "-map", "0:v", "-map", "1:a", "-master_pl_name", "master.m3u8", "-var_stream_map", "v:0,agroup:aud a:0,agroup:aud,default:yes,name:audio" });
            args.Add(Path.Combine(folder, "stream_%v.m3u8"));
        }
        else
        {
            args.AddRange(new[] { "-map", "0:v", "-map", "1:a" });
            args.Add(Path.Combine(folder, "index.m3u8"));
        }

        var (code, log) = await Hls.Ffmpeg.RunAsync(Ffmpeg!, args, CancellationToken.None);
        Assert.True(code == 0, "ffmpeg could not make the test stream: " + log);
        return folder;
    }

    static void Publish(HlsServer server, string folder)
    {
        foreach (var file in Directory.GetFiles(folder))
            server.Files["/s/" + Path.GetFileName(file)] = File.ReadAllBytes(file);
    }

    /// <summary>Asks ffmpeg to decode the whole file; returns what it reports.</summary>
    static async Task<string> InspectAsync(string file)
    {
        var (code, log) = await Hls.Ffmpeg.RunAsync(Ffmpeg!, new[] { "-hide_banner", "-v", "error", "-i", file, "-f", "null", "-" }, CancellationToken.None);
        Assert.True(code == 0 && log.Length == 0, "the file does not decode cleanly: " + log);

        var info = new ProcessStartInfo(Ffmpeg!, $"-hide_banner -i \"{file}\"") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(info)!;
        var text = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return text;
    }

    [Fact]
    public async Task A_real_stream_becomes_a_playable_mp4_that_keeps_picture_and_sound()
    {
        if (Ffmpeg == null) return;
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        Publish(server, await MakeStreamAsync(Path.Combine(h.Root, "made"), separateAudio: false));
        var m = h.NewManager(connections: 4, tweak: s => s.WriteMarkOfTheWeb = false);

        var job = m.Add(new AddDownloadRequest { Url = server.Url("/s/index.m3u8"), FileName = "clip.mp4", Stream = new StreamChoice(), Mode = AddMode.Start });
        await Finished(job, 90);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal("clip.mp4", job.FileName);
        var info = await InspectAsync(job.TargetPath);
        Assert.Contains("Video: h264", info);
        Assert.Contains("Audio: aac", info);
        Assert.Contains("Duration: 00:00:06", info);
        Assert.False(Directory.Exists(job.WorkFolder));
    }

    [Fact]
    public async Task Picture_and_sound_kept_apart_are_joined_into_one_file()
    {
        if (Ffmpeg == null) return;
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var folder = await MakeStreamAsync(Path.Combine(h.Root, "made"), separateAudio: true);
        Publish(server, folder);
        Assert.Contains("EXT-X-MEDIA", File.ReadAllText(Path.Combine(folder, "master.m3u8")));   // really two playlists
        var m = h.NewManager(connections: 4, tweak: s => s.WriteMarkOfTheWeb = false);

        var job = m.Add(new AddDownloadRequest { Url = server.Url("/s/master.m3u8"), FileName = "film.mp4", Stream = new StreamChoice(), Mode = AddMode.Start });
        await Finished(job, 90);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        var info = await InspectAsync(job.TargetPath);
        Assert.Contains("Video: h264", info);
        Assert.Contains("Audio: aac", info);
    }

    [Fact]
    public async Task A_failed_join_can_be_tried_again_once_ffmpeg_is_found_without_downloading_again()
    {
        if (Ffmpeg == null) return;
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        Publish(server, await MakeStreamAsync(Path.Combine(h.Root, "made"), separateAudio: true));
        var m = h.NewManager(connections: 4, tweak: s =>
        {
            s.WriteMarkOfTheWeb = false;
            s.FfmpegPath = Path.Combine(h.Root, "not-there.exe");   // the user pointed to a wrong place
        });

        var job = m.Add(new AddDownloadRequest { Url = server.Url("/s/master.m3u8"), FileName = "film.mp4", Stream = new StreamChoice(), Mode = AddMode.Start });
        await Finished(job, 90);
        Assert.Equal((DownloadState.Failed, JobErrorKind.Tool), (job.State, job.ErrorKind));

        var hitsBefore = server.Hits.Where(kv => kv.Key.EndsWith(".ts")).Sum(kv => kv.Value);
        m.Settings.FfmpegPath = "";   // fixed in Settings: ffmpeg is found on the PATH again
        m.Start(job.Id);
        await Finished(job, 90);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal(hitsBefore, server.Hits.Where(kv => kv.Key.EndsWith(".ts")).Sum(kv => kv.Value));   // no segment twice
        Assert.Contains("Audio: aac", await InspectAsync(job.TargetPath));
    }
}

public class FfmpegLocatorTests
{
    [Fact]
    public void An_explicit_choice_wins_and_a_wrong_one_is_not_silently_replaced()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shadenet-ff-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        var exe = Path.Combine(dir, "bin", "ffmpeg.exe");
        File.WriteAllText(exe, "");
        try
        {
            Assert.Equal(exe, Ffmpeg.Locate(exe));                         // the file itself
            Assert.Equal(exe, Ffmpeg.Locate(dir));                         // the folder of a download ("...\\bin" inside)
            Assert.Equal(exe, Ffmpeg.Locate(Path.Combine(dir, "bin")));    // or the bin folder
            Assert.Null(Ffmpeg.Locate(Path.Combine(dir, "nope.exe")));     // wrong: no fallback to the PATH
            Assert.Equal(exe, Ffmpeg.Locate(null, appFolder: Path.Combine(dir, "x"), pathVariable: Path.Combine(dir, "bin")));   // from the PATH
            Assert.Null(Ffmpeg.Locate(null, appFolder: Path.Combine(dir, "x"), pathVariable: Path.Combine(dir, "empty")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_remux_command_copies_without_re_encoding()
    {
        var one = Ffmpeg.RemuxArguments(new[] { "v.bin" }, "out.mp4", fastStart: true);
        Assert.Equal(new[] { "-c", "copy" }, one.Skip(one.ToList().IndexOf("-c")).Take(2));
        Assert.Contains("+faststart", one);
        Assert.Equal("out.mp4", one[^1]);

        var two = Ffmpeg.RemuxArguments(new[] { "v.bin", "a.bin" }, "out.mkv", fastStart: true);
        Assert.Contains("1:a:0", two);
        Assert.DoesNotContain("+faststart", two);   // an mkv has no moov atom to move
    }
}
