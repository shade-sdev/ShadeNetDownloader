using System.Diagnostics;
using System.Security.Cryptography;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

/// <summary>
/// The stream engine (HLS, DASH, and a video file fetched as pieces, as YouTube's are): a piece that fails is asked for
/// again at once, a piece far slower than the others starts over on a new connection, and an encrypted piece that was cut
/// off is fetched again rather than reported as one that cannot be decrypted.
/// </summary>
public class StreamSpeedTests
{
    const long MiB = 1024 * 1024;

    static DownloadManager Manager(Harness h, Action<EngineSettings>? tweak = null) =>
        h.NewManager(connections: 8, tweak: s =>
        {
            s.FfmpegPath = Path.Combine(h.Root, "no-such-ffmpeg.exe");   // one picture-only track is saved as it is
            s.StreamChunkBytes = (int)MiB;                                // many pieces, so a few of them can misbehave
            tweak?.Invoke(s);
        });

    /// <summary>A video file fetched as pieces in parallel (how a YouTube picture or sound file is downloaded).</summary>
    static DownloadJob AddDirect(DownloadManager m, string url, long? limit = null) =>
        m.Add(new AddDownloadRequest
        {
            Url = url, FileName = "video.mp4", Stream = new StreamChoice(Direct: true), MaxConnections = 8, SpeedLimit = limit,
            Mode = AddMode.Start,
        });

    [Fact]
    public async Task A_piece_cut_off_is_asked_for_again_at_once()
    {
        await using var h = await Harness.CreateAsync();
        // a long wait between tries, so only an immediate first try again keeps this quick
        var m = Manager(h, s => s.RetryBaseDelay = TimeSpan.FromSeconds(5));
        var sw = Stopwatch.StartNew();
        var job = AddDirect(m, h.Server.FileUrl(16 * MiB, name: "cut.mp4", kbps: 4096, dropAfter: 256 * 1024, drops: 4));
        await Finished(job, 60);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        AssertByteExact(job, 16 * MiB);
        Assert.True(job.Stats.Retries >= 1, Describe(job));
        Assert.True(sw.Elapsed.TotalSeconds < 4, $"took {sw.Elapsed.TotalSeconds:0.0} s; a 5 s wait per cut was not needed. {Describe(job)}");
    }

    [Fact]
    public async Task A_piece_far_slower_than_the_others_starts_over_on_a_new_connection()
    {
        await using var h = await Harness.CreateAsync();
        var m = Manager(h);
        var sw = Stopwatch.StartNew();
        // every third request crawls at 32 KiB/s: a crawling 1 MiB piece kept to the end alone takes 32 s
        var job = AddDirect(m, h.Server.FileUrl(24 * MiB, name: "crawl.mp4", kbps: 4096, slowEvery: 3, slowKbps: 32));
        await Finished(job, 120);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        AssertByteExact(job, 24 * MiB);
        Assert.True(job.Stats.ReplacedConnections >= 1, Describe(job));
        Assert.True(sw.Elapsed.TotalSeconds < 15, $"took {sw.Elapsed.TotalSeconds:0.0} s. {Describe(job)}");
    }

    [Fact]
    public async Task Under_a_speed_limit_no_piece_starts_over_for_being_slow()
    {
        await using var h = await Harness.CreateAsync();
        var m = Manager(h, s => s.StreamChunkBytes = 512 * 1024);
        var job = AddDirect(m, h.Server.FileUrl(4 * MiB, name: "limited.mp4", kbps: 4096, slowEvery: 3, slowKbps: 64), limit: 2 * MiB);
        await Finished(job, 120);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        AssertByteExact(job, 4 * MiB);
        Assert.Equal(0, job.Stats.ReplacedConnections);
    }

    [Fact]
    public async Task An_encrypted_piece_cut_off_half_way_is_fetched_again_not_called_undecryptable()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var key = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(16);
        server.Files["/enc/key.bin"] = key;
        var expected = server.AddVod("enc", 6, 10_000, (_, plain) => Encrypt(plain, key, iv),
            new[] { "#EXT-X-PLAYLIST-TYPE:VOD", $"#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x{Convert.ToHexString(iv)}" });
        var cut = 0;
        server.Intercept = ctx =>
        {
            // the first answer for piece 2 stops half-way, in the middle of a cipher block
            if (ctx.Request.Url!.AbsolutePath != "/enc/seg2.ts" || Interlocked.Increment(ref cut) > 1) return false;
            var body = server.Files["/enc/seg2.ts"];
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length / 2 + 5);
            ctx.Response.OutputStream.Flush();
            ctx.Response.Abort();
            return true;
        };
        var m = h.NewManager(connections: 4, tweak: s => s.FfmpegPath = Path.Combine(h.Root, "no-such-ffmpeg.exe"));
        var job = m.Add(new AddDownloadRequest
        {
            Url = server.Url("/enc/index.m3u8"), FileName = "clip.mp4", Stream = new StreamChoice(), MaxConnections = 4, Mode = AddMode.Start,
        });
        await Finished(job, 60);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal(expected, File.ReadAllBytes(job.TargetPath));
        Assert.True(server.Hits["/enc/seg2.ts"] >= 2, "piece 2 was fetched again");
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
