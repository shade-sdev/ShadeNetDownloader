using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Sites;

namespace ShadeNet.Core.Tests;

public class YtDlpTests
{
    /// <summary>What <c>yt-dlp -J</c> says about a video, cut down to the parts that matter (the shape is the real one).</summary>
    const string Sample = """
        {
          "title": "My: Holiday/Video",
          "duration": 212.5,
          "is_live": false,
          "formats": [
            { "format_id": "sb0", "ext": "mhtml", "protocol": "mhtml", "url": "https://i.ytimg.com/sb/x/storyboard", "vcodec": "none", "acodec": "none" },
            { "format_id": "140", "ext": "m4a", "protocol": "https", "url": "https://v.example/a-m4a", "vcodec": "none", "acodec": "mp4a.40.2", "tbr": 129, "filesize": 3400000, "http_headers": { "User-Agent": "UA/1" } },
            { "format_id": "251", "ext": "webm", "protocol": "https", "url": "https://v.example/a-opus", "vcodec": "none", "acodec": "opus", "tbr": 135, "filesize": 3500000 },
            { "format_id": "136", "ext": "mp4", "protocol": "https", "url": "https://v.example/v-720-avc", "vcodec": "avc1.4d401f", "acodec": "none", "height": 720, "fps": 30, "tbr": 1100, "filesize": 20000000, "http_headers": { "User-Agent": "UA/1" } },
            { "format_id": "247", "ext": "webm", "protocol": "https", "url": "https://v.example/v-720-vp9", "vcodec": "vp9", "acodec": "none", "height": 720, "fps": 30, "tbr": 900, "filesize": 15000000 },
            { "format_id": "398", "ext": "mp4", "protocol": "https", "url": "https://v.example/v-720-av1", "vcodec": "av01.0.05M.08", "acodec": "none", "height": 720, "fps": 30, "tbr": 600, "filesize": 9000000 },
            { "format_id": "299", "ext": "mp4", "protocol": "https", "url": "https://v.example/v-1080p60-avc", "vcodec": "avc1.64002a", "acodec": "none", "height": 1080, "fps": 60, "tbr": 3200, "filesize": 90000000, "http_headers": { "User-Agent": "UA/1" } },
            { "format_id": "313", "ext": "webm", "protocol": "https", "url": "https://v.example/v-2160-vp9", "vcodec": "vp9", "acodec": "none", "height": 2160, "fps": 30, "tbr": 17000, "filesize": 600000000 },
            { "format_id": "18", "ext": "mp4", "protocol": "https", "url": "https://v.example/muxed-360", "vcodec": "avc1.42001e", "acodec": "mp4a.40.2", "height": 360, "tbr": 500 },
            { "format_id": "hls-720", "ext": "mp4", "protocol": "m3u8_native", "url": "https://v.example/720.m3u8", "vcodec": "avc1.4d401f", "acodec": "mp4a.40.2", "height": 720, "tbr": 1500 }
          ]
        }
        """;

    [Fact]
    public void Qualities_are_chosen_per_height_h264_first_with_the_matching_sound_and_container()
    {
        var info = YtDlp.ParseInfo(Sample);
        Assert.Equal("My: Holiday/Video", info.Title);
        Assert.Equal(212.5, info.Seconds);
        Assert.False(info.IsLive);
        Assert.Equal(new[] { 2160, 1080, 720 }, info.Options.Select(o => o.Height));        // best first; no storyboard, no muxed 360 when split files exist

        var p1080 = info.Options.Single(o => o.Height == 1080);
        Assert.Equal("https://v.example/v-1080p60-avc", p1080.VideoUrl);
        Assert.Equal("https://v.example/a-m4a", p1080.AudioUrl);                              // MP4 picture takes the m4a sound
        Assert.Equal(".mp4", p1080.Extension);
        Assert.Equal("UA/1", p1080.UserAgent);
        Assert.Contains("1080p60", p1080.Label);
        Assert.Contains("H.264", p1080.Label);
        Assert.Equal(90_000_000 + 3_400_000, p1080.Size);

        var p720 = info.Options.Single(o => o.Height == 720);
        Assert.Equal("https://v.example/v-720-avc", p720.VideoUrl);                           // H.264 beats VP9 and AV1 at the same height

        var p2160 = info.Options.Single(o => o.Height == 2160);
        Assert.Equal("https://v.example/a-opus", p2160.AudioUrl);                             // VP9 picture takes the opus sound
        Assert.Equal(".mkv", p2160.Extension);
        Assert.Contains("VP9", p2160.Label);
        Assert.All(info.Options, o => Assert.False(o.IsHls));
    }

    [Fact]
    public void Files_with_picture_and_sound_together_are_used_when_nothing_is_split()
    {
        var info = YtDlp.ParseInfo("""{"title":"t","duration":5,"formats":[{"format_id":"18","ext":"mp4","protocol":"https","url":"https://v.example/m","vcodec":"avc1","acodec":"mp4a","height":360,"tbr":500,"filesize":1000000}]}""");
        var option = Assert.Single(info.Options);
        Assert.Null(option.AudioUrl);
        Assert.Equal((360, ".mp4"), (option.Height, option.Extension));
    }

    [Fact]
    public void A_live_stream_or_a_page_with_only_playlists_gives_hls_options()
    {
        var live = YtDlp.ParseInfo("""
            {"title":"Live show","is_live":true,"formats":[
              {"format_id":"91","ext":"mp4","protocol":"m3u8_native","url":"https://v.example/144.m3u8","vcodec":"avc1","acodec":"mp4a","height":144,"tbr":100},
              {"format_id":"95","ext":"mp4","protocol":"m3u8_native","url":"https://v.example/720.m3u8","vcodec":"avc1","acodec":"mp4a","height":720,"tbr":1500},
              {"format_id":"96","ext":"mp4","protocol":"m3u8_native","url":"https://v.example/720b.m3u8","vcodec":"avc1","acodec":"mp4a","height":720,"tbr":2500}]}
            """);
        Assert.True(live.IsLive);
        Assert.Equal(new[] { 720, 144 }, live.Options.Select(o => o.Height));
        Assert.All(live.Options, o => Assert.True(o.IsHls));
        Assert.Equal("https://v.example/720b.m3u8", live.Options[0].VideoUrl);                // the better of two at the same height
    }

    [Fact]
    public void Nothing_downloadable_and_unreadable_answers_are_reported()
    {
        var none = Assert.Throws<DownloadException>(() => YtDlp.ParseInfo("""{"title":"t","formats":[{"format_id":"sb0","protocol":"mhtml","url":"https://x/sb","vcodec":"none","acodec":"none"}]}"""));
        Assert.Equal(JobErrorKind.Unsupported, none.Kind);
        Assert.Equal(JobErrorKind.Tool, Assert.Throws<DownloadException>(() => YtDlp.ParseInfo("not json")).Kind);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ&t=3s", true)]
    [InlineData("https://youtu.be/aqz-KE-bpKQ", true)]
    [InlineData("https://m.youtube.com/shorts/abc123", true)]
    [InlineData("https://www.youtube.com/live/abc123", true)]
    [InlineData("https://music.youtube.com/watch?v=abc", true)]
    [InlineData("https://www.youtube.com/", false)]
    [InlineData("https://www.youtube.com/feed/subscriptions", false)]
    [InlineData("https://www.youtube.com/watch", false)]
    [InlineData("https://notyoutube.com/watch?v=abc", false)]
    [InlineData("https://youtube.com.evil.example/watch?v=abc", false)]
    [InlineData("ftp://www.youtube.com/watch?v=abc", false)]
    [InlineData("not a url", false)]
    public void Which_pages_are_asked_about(string url, bool expected) => Assert.Equal(expected, YtDlp.IsSupportedPage(url));

    [Fact]
    public void The_program_is_found_where_it_was_put_and_a_wrong_choice_is_not_replaced()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shadenet-ytd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "tools", "yt-dlp"));
        var exe = Path.Combine(dir, "tools", "yt-dlp", "yt-dlp.exe");
        File.WriteAllText(exe, "");
        try
        {
            Assert.Equal(exe, YtDlp.Locate(null, appFolder: dir, pathVariable: ""));
            Assert.Equal(exe, YtDlp.Locate(exe));
            Assert.Equal(exe, YtDlp.Locate(Path.GetDirectoryName(exe)));
            Assert.Null(YtDlp.Locate(Path.Combine(dir, "nope.exe"), appFolder: dir));
            Assert.Equal(exe, YtDlp.Locate(null, appFolder: Path.Combine(dir, "x"), pathVariable: Path.GetDirectoryName(exe)));
            Assert.Null(YtDlp.Locate(null, appFolder: Path.Combine(dir, "x"), pathVariable: Path.Combine(dir, "empty")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task The_resolver_runs_the_program_and_turns_its_answer_or_its_error_into_a_result()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shadenet-ytd-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // stand-ins for yt-dlp: one prints the sample, one fails the way yt-dlp does
            File.WriteAllText(Path.Combine(dir, "info.json"), Sample);
            File.WriteAllText(Path.Combine(dir, "works.cmd"), "@echo off\r\ntype \"%~dp0info.json\"\r\n");
            File.WriteAllText(Path.Combine(dir, "fails.cmd"), "@echo off\r\necho ERROR: [youtube] broken: Sign in to confirm you're not a bot 1>&2\r\nexit /b 1\r\n");

            var info = await YtDlp.ResolveAsync(Path.Combine(dir, "works.cmd"), "https://www.youtube.com/watch?v=fine", CancellationToken.None);
            Assert.Equal(3, info.Options.Count);
            Assert.Equal("My: Holiday/Video", info.Title);

            var ex = await Assert.ThrowsAsync<DownloadException>(() => YtDlp.ResolveAsync(Path.Combine(dir, "fails.cmd"), "https://www.youtube.com/watch?v=broken", CancellationToken.None));
            Assert.Equal(JobErrorKind.Tool, ex.Kind);
            Assert.Contains("Sign in to confirm", ex.Message);
            Assert.Contains("signed-in browser", ex.Message);                                // and what to do about it

            await Assert.ThrowsAsync<DownloadException>(() => YtDlp.ResolveAsync(Path.Combine(dir, "missing.exe"), "https://youtu.be/x", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
