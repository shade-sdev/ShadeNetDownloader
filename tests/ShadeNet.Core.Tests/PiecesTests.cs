using ShadeNet.Core.Models;

namespace ShadeNet.Core.Tests;

/// <summary>
/// A player that keeps its list of pieces itself (no playlist or manifest on the network, like https://r-w-c.github.io/mse-demo/):
/// the pieces it asked for are downloaded in that order and joined after their initialisation part, as a playlist's would be.
/// </summary>
public class PiecesTests
{
    static DownloadManager Manager(Harness h) => h.NewManager(connections: 4, tweak: s => s.FfmpegPath = Path.Combine(h.Root, "no-such-ffmpeg.exe"));

    [Fact]
    public async Task Pieces_with_no_playlist_are_joined_after_their_init_part_in_the_order_given()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var init = HlsServer.Bytes("init", 0, 700);
        server.Files["/v/part_init.mp4"] = init;
        var expected = new List<byte>(init);
        var names = new[] { "part_0.m4s", "part_153856.m4s", "part_296704.m4s", "part_1158912.m4s" };   // the player's order, not the names' order
        foreach (var (name, i) in names.Select((n, i) => (n, i)))
        {
            var piece = HlsServer.Bytes("frag", i, 9_000);
            server.Files["/v/" + name] = piece;
            expected.AddRange(piece);
        }

        var m = Manager(h);
        var job = m.Add(new AddDownloadRequest
        {
            Url = server.Url("/v/part_init.mp4"),
            FileName = "Demo",
            Referer = "https://site.example/watch",
            Cookies = "session=1",
            Stream = new StreamChoice(Subtitles: false, Seconds: 94, Pieces: names.Select(n => server.Url("/v/" + n)).ToList(), Init: server.Url("/v/part_init.mp4")),
        });
        await Harness.Finished(job);

        Assert.True(job.State == DownloadState.Completed, Harness.Describe(job));
        Assert.Equal("Demo.mp4", job.FileName);
        Assert.Equal(expected.ToArray(), File.ReadAllBytes(job.TargetPath));
        Assert.Equal(1, server.Hits["/v/part_init.mp4"]);                                   // the init part once, as for a playlist
        Assert.DoesNotContain(server.Hits.Keys, k => k.EndsWith(".m3u8"));                   // nothing to read: there is no playlist
        Assert.All(server.Requests.Where(r => r.Path.EndsWith(".m4s")), r => Assert.Equal(("session=1", "https://site.example/watch"), (r.Cookie, r.Referer)));
    }

    [Fact]
    public async Task Mpeg_ts_pieces_need_no_init_part()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var expected = new List<byte>();
        for (var i = 0; i < 3; i++)
        {
            var piece = HlsServer.Bytes("ts", i, 5_000);
            server.Files[$"/t/seg{i}.ts"] = piece;
            expected.AddRange(piece);
        }

        var m = Manager(h);
        var job = m.Add(new AddDownloadRequest
        {
            Url = server.Url("/t/seg0.ts"),
            FileName = "Clip",
            Stream = new StreamChoice(Subtitles: false, Pieces: Enumerable.Range(0, 3).Select(i => server.Url($"/t/seg{i}.ts")).ToList()),
        });
        await Harness.Finished(job);

        Assert.True(job.State == DownloadState.Completed, Harness.Describe(job));
        Assert.Equal(expected.ToArray(), File.ReadAllBytes(job.TargetPath));
    }

    [Fact]
    public async Task The_piece_list_is_kept_with_the_job()
    {
        await using var h = await Harness.CreateAsync();
        var m = Manager(h);

        var job = m.Add(new AddDownloadRequest
        {
            Url = "https://cdn.example/v/init.mp4",
            Stream = new StreamChoice(Pieces: new[] { "https://cdn.example/v/1.m4s", "https://cdn.example/v/2.m4s" }, Init: "https://cdn.example/v/init.mp4",
                AudioPieces: new[] { "https://cdn.example/a/1.m4s" }, AudioInit: "https://cdn.example/a/init.mp4"),
            Mode = AddMode.Hold,
        });

        var data = Hls.HlsJobData.FromJson(job.Hls!.ToJson());
        Assert.Equal(new[] { "https://cdn.example/v/1.m4s", "https://cdn.example/v/2.m4s" }, data.Pieces);
        Assert.Equal(("https://cdn.example/v/init.mp4", "https://cdn.example/a/init.mp4"), (data.InitUrl, data.AudioInitUrl));
        Assert.Equal(new[] { "https://cdn.example/a/1.m4s" }, data.AudioPieces);
    }
}
