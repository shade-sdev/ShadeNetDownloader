using ShadeNet.Core.Hls;
using ShadeNet.Core.Models;

namespace ShadeNet.Core.Tests;

/// <summary>
/// A playlist address with a token in its query (Reddit's ?a=…) that is refused (expired, or refused outside the browser) while the same
/// playlist is served without it: it is read once more without the query.
/// </summary>
public class ExpiredTokenTests
{
    static DownloadManager Manager(Harness h) => h.NewManager(connections: 4, tweak: s => s.FfmpegPath = Path.Combine(h.Root, "no-such-ffmpeg.exe"));

    static void RefuseTokens(HlsServer server, bool alsoWithout = false) => server.Intercept = ctx =>
    {
        var url = ctx.Request.Url!;
        if (url.Query.Contains("token=") || (alsoWithout && url.AbsolutePath.EndsWith(".m3u8")))
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            return true;
        }

        return false;
    };

    [Fact]
    public async Task A_refused_playlist_is_read_again_without_its_token()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var expected = server.AddVod("v", 4);
        RefuseTokens(server);
        var m = Manager(h);

        var job = m.Add(new AddDownloadRequest { Url = server.Url("/v/index.m3u8?token=expired&f=sd"), FileName = "clip.mp4", Stream = new StreamChoice() });
        await Harness.Finished(job);

        Assert.True(job.State == DownloadState.Completed, Harness.Describe(job));
        Assert.Equal(expected, File.ReadAllBytes(job.TargetPath));
    }

    [Fact]
    public async Task Describing_a_stream_tries_without_the_token_too()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.AddVod("v", 3);
        RefuseTokens(server);
        var m = Manager(h);

        var info = await m.DescribeStreamAsync(new Net.RequestSpec(server.Url("/v/index.m3u8?token=expired")));

        Assert.Equal(server.Url("/v/index.m3u8"), info.Url.AbsoluteUri);   // read from the address without the token
    }

    [Fact]
    public async Task When_it_is_refused_without_the_token_too_the_first_refusal_stands()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.AddVod("v", 3);
        RefuseTokens(server, alsoWithout: true);
        var m = Manager(h);

        var job = m.Add(new AddDownloadRequest { Url = server.Url("/v/index.m3u8?token=expired"), FileName = "clip.mp4", Stream = new StreamChoice() });
        await Harness.Finished(job);

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Contains("403", job.Error);
    }

    [Theory]
    [InlineData("https://v.example/a/HLSPlaylist.m3u8?a=1792%2CYj%3D%3D&v=1&f=sd", "https://v.example/a/HLSPlaylist.m3u8")]
    [InlineData("https://v.example/a/HLSPlaylist.m3u8", null)]
    [InlineData("https://v.example/a/HLSPlaylist.m3u8?", null)]
    [InlineData("not an address", null)]
    public void The_address_without_its_query(string url, string? expected) => Assert.Equal(expected, HlsClient.WithoutQuery(url));
}
