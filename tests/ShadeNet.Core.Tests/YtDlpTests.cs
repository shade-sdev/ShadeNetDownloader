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

    /// <summary>TikTok: one file with picture and sound; its server wants the page as referrer and the cookies yt-dlp got (403 without).</summary>
    const string TikTok = """
        {
          "title": "send this to someone",
          "duration": 11,
          "formats": [
            { "format_id": "h264_540p", "ext": "mp4", "protocol": "https", "url": "https://v16.tiktokcdn.example/video/tos/abc/", "vcodec": "h264", "acodec": "aac", "height": 1024, "tbr": 900,
              "http_headers": { "User-Agent": "UA/1", "Referer": "https://www.tiktok.com/@scout2015/video/6718335390845095173", "Accept": "*/*" },
              "cookies": "tt_chain_token=\"abc\\075\\075\"; Domain=.tiktokcdn.example; Path=/; Secure; sid_tt=plain; Domain=.tiktokcdn.example; Path=/; Expires=1790000000" }
          ]
        }
        """;

    [Fact]
    public void The_referrer_and_cookies_yt_dlp_names_for_a_file_travel_with_it()
    {
        var option = Assert.Single(YtDlp.ParseInfo(TikTok).Options);

        Assert.Equal("https://www.tiktok.com/@scout2015/video/6718335390845095173", option.Referer);
        Assert.Equal("tt_chain_token=abc==; sid_tt=plain", option.Cookies);        // no attributes, the quotes and the octal escapes undone
        Assert.Equal("UA/1", option.UserAgent);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Secure; Path=/", null)]
    [InlineData("a=1; Domain=.x.com; Path=/; Secure; b=2", "a=1; b=2")]
    [InlineData("a=\"x\\\"y\"; b=\"p\\\\q\"", "a=x\"y; b=p\\q")]
    public void Cookies_are_written_as_a_request_header(string? yt, string? header) => Assert.Equal(header, YtDlp.CookieHeaderFrom(yt));

    [Fact]
    public void Any_other_header_yt_dlp_names_travels_too_but_not_the_ones_a_request_has_anyway()
    {
        const string json = """
            { "title": "t", "formats": [
              { "format_id": "a", "ext": "mp4", "protocol": "https", "url": "https://v.example/a", "vcodec": "avc1", "acodec": "aac", "height": 720,
                "http_headers": { "User-Agent": "UA/1", "Accept": "*/*", "Accept-Language": "en", "Accept-Encoding": "gzip", "Sec-Fetch-Mode": "navigate",
                                  "Referer": "https://site.example/p", "Origin": "https://site.example", "X-Token": "abc", "Range": "bytes=0-" } } ] }
            """;

        var option = Assert.Single(YtDlp.ParseInfo(json).Options);

        Assert.Equal(new[] { ("Origin", "https://site.example"), ("X-Token", "abc") }, option.Headers!.OrderBy(h => h.Key).Select(h => (h.Key, h.Value)));
        Assert.Equal("https://site.example/p", option.Referer);
    }

    /// <summary>A TikTok live as yt-dlp lists it: an endless FLV and an HLS stream of each quality (the shape of its extractor's answer).</summary>
    [Fact]
    public void A_live_stream_is_offered_as_its_hls_stream_per_quality_with_the_sites_headers()
    {
        const string json = """
            { "title": "someone is live", "is_live": true, "formats": [
              { "format_id": "flv-sd", "ext": "flv", "protocol": "https", "url": "https://pull.example/sd.flv", "vcodec": "h264", "height": 540 },
              { "format_id": "hls-sd", "ext": "mp4", "protocol": "m3u8_native", "url": "https://pull.example/sd.m3u8", "vcodec": "h264", "height": 540, "tbr": 800,
                "http_headers": { "User-Agent": "UA/1", "Referer": "https://www.tiktok.com/@someone/live" }, "cookies": "k=v; Domain=.example; Path=/" },
              { "format_id": "hls-hd", "ext": "mp4", "protocol": "m3u8_native", "url": "https://pull.example/hd.m3u8", "vcodec": "h264", "height": 720, "tbr": 1500,
                "http_headers": { "User-Agent": "UA/1", "Referer": "https://www.tiktok.com/@someone/live" }, "cookies": "k=v; Domain=.example; Path=/" }
            ] }
            """;

        var info = YtDlp.ParseInfo(json);

        Assert.True(info.IsLive);
        Assert.Equal(new[] { "https://pull.example/hd.m3u8", "https://pull.example/sd.m3u8" }, info.Options.Select(o => o.VideoUrl));   // best first; the endless FLV is not a file to fetch
        Assert.All(info.Options, o => Assert.True(o.IsHls));
        Assert.Equal(("https://www.tiktok.com/@someone/live", "k=v"), (info.Options[0].Referer, info.Options[0].Cookies));
    }

    /// <summary>Facebook, Instagram, X: a live (or not) video that comes as a DASH manifest and nothing else.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_dash_only_video_is_one_stream_per_manifest(bool live)
    {
        var json = $$"""
            { "title": "Going live", "is_live": {{(live ? "true" : "false")}}, "formats": [
              { "format_id": "v1", "ext": "mp4", "protocol": "http_dash_segments", "url": "https://dash.example/live.mpd", "manifest_url": "https://dash.example/live.mpd", "vcodec": "avc1.64001f", "acodec": "none", "height": 720, "tbr": 1500 },
              { "format_id": "v2", "ext": "mp4", "protocol": "http_dash_segments", "url": "https://dash.example/live.mpd", "manifest_url": "https://dash.example/live.mpd", "vcodec": "avc1.4d401e", "acodec": "none", "height": 480, "tbr": 700 },
              { "format_id": "a1", "ext": "m4a", "protocol": "http_dash_segments", "url": "https://dash.example/live.mpd", "manifest_url": "https://dash.example/live.mpd", "vcodec": "none", "acodec": "mp4a.40.2", "tbr": 64 }
            ] }
            """;

        var info = YtDlp.ParseInfo(json);

        Assert.Equal(live, info.IsLive);
        var option = Assert.Single(info.Options);
        Assert.Equal("https://dash.example/live.mpd", option.VideoUrl);       // the manifest: the engine reads the qualities and the sound from it
        Assert.True(option.IsHls);
        Assert.Equal(720, option.Height);
    }

    [Fact]
    public void Formats_without_headers_or_cookies_carry_none()
    {
        Assert.All(YtDlp.ParseInfo(Sample).Options, o => Assert.Equal((null, null, null), (o.Referer, o.Cookies, o.Headers)));
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

    [Fact]
    public void Facebook_files_that_say_nothing_about_their_codecs_are_picture_and_sound_together_best_first()
    {
        // the real shape of yt-dlp's answer for a public Facebook video: no codecs, no height, only "hd" and "sd"
        var info = YtDlp.ParseInfo("""
            {"title":"119 reactions · 1.4K shares | How to Share With Just Friends | Facebook","duration":74.3,
             "formats":[
              {"format_id":"sd","quality":-3,"url":"https://video.example/sd.mp4","protocol":"https","ext":"mp4","video_ext":"mp4","audio_ext":"none","http_headers":{"User-Agent":"facebookexternalhit/1.1"}},
              {"format_id":"hd","quality":-2,"url":"https://video.example/hd.mp4","protocol":"https","ext":"mp4","video_ext":"mp4","audio_ext":"none","http_headers":{"User-Agent":"facebookexternalhit/1.1"}}]}
            """);

        Assert.Equal("How to Share With Just Friends", info.Title);
        Assert.Equal(new[] { "HD · mp4", "SD · mp4" }, info.Options.Select(o => o.Label).ToArray());
        Assert.Equal("https://video.example/hd.mp4", info.Options[0].VideoUrl);
        Assert.All(info.Options, o => { Assert.Null(o.AudioUrl); Assert.Equal(".mp4", o.Extension); Assert.Equal("facebookexternalhit/1.1", o.UserAgent); });
    }

    [Theory]
    [InlineData("119 reactions · 1.4K shares | Asif Nawab Butt on Reels", "Asif Nawab Butt on Reels")]
    [InlineData("2.8M views · 10K reactions | How to Share With Just Friends | Facebook", "How to Share With Just Friends")]
    [InlineData("How to Share With Just Friends", "How to Share With Just Friends")]
    [InlineData("My: Holiday/Video | Notes", "My: Holiday/Video | Notes")]
    [InlineData("12 views | ", "12 views | ")]   // nothing would be left: keep what the site said
    public void Counters_and_the_site_name_are_taken_off_a_title(string raw, string expected) => Assert.Equal(expected, YtDlp.CleanTitle(raw));

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
