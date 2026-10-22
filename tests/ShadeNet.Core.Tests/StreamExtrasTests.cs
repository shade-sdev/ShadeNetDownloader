using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using ShadeNet.Core.Hls;
using ShadeNet.Core.Models;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

// ================================================================================================================
// DASH manifests
// ================================================================================================================

public class DashParserTests
{
    static readonly Uri Base = new("https://cdn.example.com/show/manifest.mpd?t=1");

    const string Static = """
        <?xml version="1.0"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT18S">
          <BaseURL>media/</BaseURL>
          <Period>
            <AdaptationSet contentType="video" mimeType="video/mp4" segmentAlignment="true">
              <SegmentTemplate timescale="1000" duration="4000" startNumber="1" initialization="init-$RepresentationID$.mp4" media="$RepresentationID$/seg-$Number%03d$.m4s"/>
              <Representation id="v360" bandwidth="500000" width="640" height="360" codecs="avc1.4d401e"/>
              <Representation id="v720" bandwidth="1800000" width="1280" height="720" codecs="avc1.4d401f"/>
            </AdaptationSet>
            <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="en">
              <Role schemeIdUri="urn:mpeg:dash:role:2011" value="main"/>
              <SegmentTemplate timescale="48000" duration="192000" initialization="a-init.mp4" media="a-$Number$.m4s"/>
              <Representation id="a1" bandwidth="64000" codecs="mp4a.40.2"/>
              <Representation id="a2" bandwidth="128000" codecs="mp4a.40.2"/>
            </AdaptationSet>
            <AdaptationSet contentType="text" mimeType="text/vtt" lang="de">
              <Representation id="sub-de" bandwidth="100"><BaseURL>subs/de.vtt</BaseURL></Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    [Fact]
    public void A_template_manifest_gives_numbered_segments_with_init_sections()
    {
        var mpd = DashParser.Parse(Static, Base);
        Assert.False(mpd.IsDynamic);
        Assert.Equal(2, mpd.Video.Count);
        Assert.Equal(new[] { 720, 360 }, mpd.VideoVariants().Select(v => v.Variant).OrderByDescending(v => v.Height).Select(v => v.Height));

        var playlist = mpd.Video.Single(r => r.Id == "v720").Build(DateTimeOffset.UtcNow);
        Assert.True(playlist.IsEnded);
        Assert.Equal(5, playlist.Segments.Count);                                    // 18 s in 4 s pieces
        Assert.Equal(new Uri("https://cdn.example.com/show/media/v720/seg-001.m4s"), playlist.Segments[0].Url);
        Assert.Equal(new Uri("https://cdn.example.com/show/media/v720/seg-005.m4s"), playlist.Segments[4].Url);
        Assert.Equal(new Uri("https://cdn.example.com/show/media/init-v720.mp4"), playlist.Segments[0].Map!.Url);
        Assert.Equal(18, playlist.TotalDuration, 3);                                  // the last piece is the short one
        Assert.Equal(new[] { 0L, 1, 2, 3, 4 }, playlist.Segments.Select(s => s.Sequence));
    }

    [Fact]
    public void The_main_audio_track_is_the_default_and_the_best_of_it_wins()
    {
        var mpd = DashParser.Parse(Static, Base);
        Assert.Equal("a2", mpd.PickAudio(null)!.Id);
        Assert.Equal("en", mpd.PickAudio("en")!.Language);
        Assert.Equal("a2", mpd.PickAudio("not there")!.Id);
        var audio = mpd.PickAudio(null)!.Build(DateTimeOffset.UtcNow);
        Assert.Equal(new Uri("https://cdn.example.com/show/media/a-1.m4s"), audio.Segments[0].Url);   // timescale 48000, 4 s each
    }

    [Fact]
    public void Subtitle_tracks_that_are_one_file_are_found()
    {
        var mpd = DashParser.Parse(Static, Base);
        var text = Assert.Single(mpd.Text);
        Assert.Equal(("sub-de", ".vtt", "de"), (text.Id, text.TextExtension, text.Language));
        var playlist = text.Build(DateTimeOffset.UtcNow);
        Assert.Equal(new Uri("https://cdn.example.com/show/media/subs/de.vtt"), Assert.Single(playlist.Segments).Url);
    }

    [Fact]
    public void A_timeline_lists_every_segment_with_repeats_and_times()
    {
        var mpd = DashParser.Parse("""
            <MPD type="static" mediaPresentationDuration="PT10S"><Period>
              <AdaptationSet mimeType="video/mp4"><SegmentTemplate timescale="90000" media="c-$Time$.m4s" initialization="i.mp4">
                <SegmentTimeline><S t="900000" d="180000" r="2"/><S d="90000"/></SegmentTimeline></SegmentTemplate>
                <Representation id="v" bandwidth="1" width="10" height="10"/></AdaptationSet></Period></MPD>
            """, Base);

        var segments = mpd.Video[0].Build(DateTimeOffset.UtcNow).Segments;
        Assert.Equal(new[] { "c-900000.m4s", "c-1080000.m4s", "c-1260000.m4s", "c-1440000.m4s" }, segments.Select(s => s.Url.Segments[^1]));
        Assert.Equal(new[] { 2.0, 2.0, 2.0, 1.0 }, segments.Select(s => s.Duration));
    }

    [Fact]
    public void A_segment_list_and_a_plain_base_url_work_too()
    {
        var mpd = DashParser.Parse("""
            <MPD type="static" mediaPresentationDuration="PT8S"><Period>
              <AdaptationSet mimeType="video/mp4">
                <Representation id="list" bandwidth="1" width="2" height="2"><SegmentList duration="4"><Initialization sourceURL="init.mp4"/>
                  <SegmentURL media="a.m4s"/><SegmentURL media="all.mp4" mediaRange="1000-2999"/></SegmentList></Representation>
                <Representation id="single" bandwidth="2" width="4" height="4"><BaseURL>whole.mp4</BaseURL></Representation>
              </AdaptationSet></Period></MPD>
            """, Base);

        var list = mpd.Video.Single(r => r.Id == "list").Build(DateTimeOffset.UtcNow).Segments;
        Assert.Equal(2, list.Count);
        Assert.Equal(new Uri("https://cdn.example.com/show/init.mp4"), list[0].Map!.Url);
        Assert.Equal((1000L, 2000L), (list[1].ByteOffset!.Value, list[1].ByteLength!.Value));

        var single = Assert.Single(mpd.Video.Single(r => r.Id == "single").Build(DateTimeOffset.UtcNow).Segments);
        Assert.Equal(new Uri("https://cdn.example.com/show/whole.mp4"), single.Url);
        Assert.Null(single.Map);
    }

    [Fact]
    public void A_live_manifest_offers_the_window_that_is_available_at_that_moment()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var xml = $"""
            <MPD type="dynamic" availabilityStartTime="{start:O}" timeShiftBufferDepth="PT10S" minimumUpdatePeriod="PT2S"><Period start="PT0S">
              <AdaptationSet mimeType="video/mp4"><SegmentTemplate timescale="1" duration="2" startNumber="1" media="s-$Number$.m4s" initialization="i.mp4"/>
                <Representation id="v" bandwidth="1" width="2" height="2"/></AdaptationSet></Period></MPD>
            """;
        var mpd = DashParser.Parse(xml, Base);
        Assert.True(mpd.IsDynamic);
        Assert.Equal(2, mpd.UpdateSeconds);

        var at21 = mpd.Video[0].Build(start.AddSeconds(21));
        Assert.False(at21.IsEnded);
        Assert.Equal(new long[] { 6, 7, 8, 9, 10 }, at21.Segments.Select(s => s.Sequence));   // the last complete one is #10 (20 s / 2)
        var at31 = mpd.Video[0].Build(start.AddSeconds(31));
        Assert.Equal(new long[] { 11, 12, 13, 14, 15 }, at31.Segments.Select(s => s.Sequence));
        Assert.Empty(mpd.Video[0].Build(start.AddSeconds(1)).Segments);                       // nothing is complete yet
    }

    [Fact]
    public void Protected_content_and_things_that_are_not_manifests_are_reported()
    {
        var protectedMpd = DashParser.Parse("""
            <MPD type="static" mediaPresentationDuration="PT4S"><Period><AdaptationSet mimeType="video/mp4">
              <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>
              <SegmentTemplate duration="4" media="s.m4s"/><Representation id="v" bandwidth="1"/></AdaptationSet></Period></MPD>
            """, Base);
        Assert.Equal("urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed", protectedMpd.Protection);

        Assert.Throws<HlsFormatException>(() => DashParser.Parse("<html></html>", Base));
        Assert.Throws<HlsFormatException>(() => DashParser.Parse("not xml at all <", Base));
        Assert.Throws<HlsFormatException>(() => DashParser.Parse("<MPD><Period/></MPD>", Base));
        Assert.True(DashParser.LooksLikeManifest("<?xml version=\"1.0\"?>\n<MPD xmlns=\"x\">"));
        Assert.False(DashParser.LooksLikeManifest("#EXTM3U"));
    }

    [Theory]
    [InlineData("a/$RepresentationID$/$Number$.m4s", "a/v1/7.m4s")]
    [InlineData("s-$Number%05d$.m4s", "s-00007.m4s")]
    [InlineData("t$Time$-$Bandwidth$.m4s", "t123-500.m4s")]
    [InlineData("price$$5.m4s", "price$5.m4s")]
    public void Template_variables_are_filled_in(string template, string expected) =>
        Assert.Equal(expected, DashParser.Expand(template, "v1", 500, 7, 123));
}

// ================================================================================================================
// downloading DASH, live streams and subtitles
// ================================================================================================================

public class StreamExtrasTests
{
    static DownloadManager Manager(Harness h, bool withFfmpeg = false) =>
        h.NewManager(connections: 4, tweak: s => { if (!withFfmpeg) s.FfmpegPath = Path.Combine(h.Root, "no-such-ffmpeg.exe"); });

    static DownloadJob AddStream(DownloadManager m, string url, string fileName = "clip.mp4", StreamChoice? choice = null) =>
        m.Add(new AddDownloadRequest { Url = url, FileName = fileName, Stream = choice ?? new StreamChoice(), MaxConnections = 4, Mode = AddMode.Start });

    static string Mpd(string body, string type = "static", string extra = "mediaPresentationDuration=\"PT12S\"") =>
        $"<?xml version=\"1.0\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"{type}\" {extra}><Period>{body}</Period></MPD>";

    // ---- DASH ----

    [Fact]
    public async Task A_dash_stream_with_one_track_is_joined_into_an_mp4_without_ffmpeg()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var init = HlsServer.Bytes("dinit", 0, 600);
        server.Files["/d/init.mp4"] = init;
        var all = new List<byte>(init);
        for (var i = 1; i <= 3; i++)
        {
            var seg = HlsServer.Bytes("dseg", i, 7_000);
            server.Files[$"/d/seg-{i}.m4s"] = seg;
            all.AddRange(seg);
        }

        server.Put("/d/manifest.mpd", Mpd("""<AdaptationSet contentType="video" mimeType="video/mp4"><SegmentTemplate timescale="1" duration="4" startNumber="1" initialization="init.mp4" media="seg-$Number$.m4s"/><Representation id="v" bandwidth="800000" width="640" height="360"/></AdaptationSet>"""));
        var m = Manager(h);

        var job = AddStream(m, server.Url("/d/manifest.mpd"));
        await Finished(job);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal("clip.mp4", job.FileName);
        Assert.Equal(all.ToArray(), File.ReadAllBytes(job.TargetPath));
        Assert.Equal(360, job.Hls!.Height);
    }

    [Fact]
    public async Task A_dash_stream_with_separate_audio_needs_ffmpeg_and_says_so()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.Files["/d/v.mp4"] = new byte[1000];
        server.Files["/d/a.mp4"] = new byte[500];
        server.Put("/d/manifest.mpd", Mpd("""
            <AdaptationSet contentType="video" mimeType="video/mp4"><Representation id="v" bandwidth="8" width="2" height="2"><BaseURL>v.mp4</BaseURL></Representation></AdaptationSet>
            <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="en"><Representation id="a" bandwidth="4"><BaseURL>a.mp4</BaseURL></Representation></AdaptationSet>
            """));
        var m = Manager(h);

        var job = AddStream(m, server.Url("/d/manifest.mpd"));
        await Finished(job);

        Assert.Equal((DownloadState.Failed, JobErrorKind.Tool), (job.State, job.ErrorKind));
        Assert.True(Directory.Exists(job.WorkFolder));
    }

    [Fact]
    public async Task A_protected_dash_stream_is_refused()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.Put("/d/manifest.mpd", Mpd("""<AdaptationSet mimeType="video/mp4"><ContentProtection schemeIdUri="urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95"/><SegmentTemplate duration="4" media="s.m4s"/><Representation id="v" bandwidth="1"/></AdaptationSet>"""));
        var m = Manager(h);

        var job = AddStream(m, server.Url("/d/manifest.mpd"));
        await Finished(job);

        Assert.Equal((DownloadState.Failed, JobErrorKind.Unsupported), (job.State, job.ErrorKind));
        Assert.Contains("protected", job.Error);
    }

    [Fact]
    public async Task The_dialog_describes_a_dash_manifest()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.Put("/d/manifest.mpd", Mpd("""
            <AdaptationSet contentType="video" mimeType="video/mp4"><SegmentTemplate timescale="1" duration="4" media="s-$RepresentationID$-$Number$.m4s"/>
              <Representation id="lo" bandwidth="500000" width="640" height="360"/><Representation id="hi" bandwidth="2000000" width="1280" height="720"/></AdaptationSet>
            <AdaptationSet contentType="audio" mimeType="audio/mp4" lang="en"><SegmentTemplate timescale="1" duration="4" media="a-$Number$.m4s"/><Representation id="a" bandwidth="64000"/></AdaptationSet>
            <AdaptationSet contentType="text" mimeType="text/vtt" lang="fr"><Representation id="t"><BaseURL>fr.vtt</BaseURL></Representation></AdaptationSet>
            """));
        var m = Manager(h);

        var info = await m.DescribeStreamAsync(new ShadeNet.Core.Net.RequestSpec(server.Url("/d/manifest.mpd")));
        Assert.True(info.IsDash);
        Assert.Equal(new[] { 720, 360 }, info.Variants.Select(v => v.Height));
        Assert.Equal(12, info.DurationSeconds);
        Assert.True(info.SeparateAudio);
        Assert.Equal(new[] { "fr" }, info.Subtitles);
        Assert.Null(info.Problem);
        Assert.True(DownloadManager.IsStreamAddress("https://x.example/a.mpd"));
        Assert.True(DownloadManager.IsStreamAddress("https://x.example/play", "application/dash+xml"));
    }

    // ---- subtitles ----

    [Fact]
    public async Task Hls_subtitles_are_joined_and_saved_next_to_the_video()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.AddVod("v", 3);
        server.Put("/subs/en/a.vtt", "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\n00:00:01.000 --> 00:00:02.000\nHello\n");
        server.Put("/subs/en/b.vtt", "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\n00:00:05.000 --> 00:00:06.000\nWorld\n");
        server.Put("/subs/en/index.m3u8", "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:4,\na.vtt\n#EXTINF:4,\nb.vtt\n#EXT-X-ENDLIST\n");
        server.Put("/master.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,URI=\"subs/en/index.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360,SUBTITLES=\"subs\"\nv/index.m3u8\n");
        var m = Manager(h);

        var job = AddStream(m, server.Url("/master.m3u8"));
        await Finished(job);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        var sidecar = Path.Combine(job.SaveDirectory, "clip.en.vtt");
        Assert.True(File.Exists(sidecar), string.Join(", ", Directory.GetFiles(job.SaveDirectory)));
        var text = File.ReadAllText(sidecar);
        Assert.StartsWith("WEBVTT", text);
        Assert.Equal(1, text.Split("WEBVTT").Length - 1);          // one header
        Assert.Contains("Hello", text);
        Assert.Contains("World", text);
        Assert.True(text.IndexOf("Hello", StringComparison.Ordinal) < text.IndexOf("World", StringComparison.Ordinal));
        Assert.DoesNotContain("X-TIMESTAMP-MAP", text);
    }

    [Fact]
    public async Task Subtitles_can_be_switched_off_and_a_missing_one_never_fails_the_video()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.AddVod("v", 2);
        server.Put("/master.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",URI=\"gone/index.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360,SUBTITLES=\"subs\"\nv/index.m3u8\n");
        var m = Manager(h);

        var broken = AddStream(m, server.Url("/master.m3u8"), "broken.mp4");
        var without = AddStream(m, server.Url("/master.m3u8"), "without.mp4", new StreamChoice(Subtitles: false));
        await Finished(broken);
        await Finished(without);

        Assert.Equal(DownloadState.Completed, broken.State);   // the subtitle playlist is a 404
        Assert.Equal(DownloadState.Completed, without.State);
        Assert.DoesNotContain(Directory.GetFiles(broken.SaveDirectory), f => f.EndsWith(".vtt"));
        Assert.False(server.Hits.Where(kv => kv.Key.Contains("gone")).Sum(kv => kv.Value) > 3, "no need to ask for the subtitles when they are switched off");
    }

    [Fact]
    public async Task A_dash_subtitle_file_is_saved_as_it_is()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.Files["/d/v.mp4"] = new byte[2000];
        server.Put("/d/de.vtt", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHallo\n");
        server.Put("/d/manifest.mpd", Mpd("""
            <AdaptationSet contentType="video" mimeType="video/mp4"><Representation id="v" bandwidth="8" width="2" height="2"><BaseURL>v.mp4</BaseURL></Representation></AdaptationSet>
            <AdaptationSet contentType="text" mimeType="text/vtt" lang="de"><Representation id="t"><BaseURL>de.vtt</BaseURL></Representation></AdaptationSet>
            """));
        var m = Manager(h);

        var job = AddStream(m, server.Url("/d/manifest.mpd"), "film.mp4");
        await Finished(job);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Contains("Hallo", File.ReadAllText(Path.Combine(job.SaveDirectory, "film.de.vtt")));
    }

    [Fact]
    public void Segmented_webvtt_merges_into_one_file_in_order()
    {
        var merged = WebVtt.Merge(new[]
        {
            "WEBVTT\nX-TIMESTAMP-MAP=LOCAL:00:00:00.000,MPEGTS:900000\n\n00:00:01.000 --> 00:00:02.000\none\n",
            "WEBVTT\r\n\r\n00:00:05.000 --> 00:00:06.000\r\ntwo\r\n",
            "WEBVTT\n\n",
        });
        Assert.Equal("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\none\n\n00:00:05.000 --> 00:00:06.000\ntwo\n\n", merged);
    }

    // ---- live streams ----

    /// <summary>A live playlist that grows by a segment per request and ends (or just stops answering) after <paramref name="total"/> segments.</summary>
    static (Func<int> Served, byte[][] Segments) Live(HlsServer server, string name, int total, bool endMarker, int window = 3)
    {
        var segments = Enumerable.Range(0, total).Select(i => HlsServer.Bytes(name, i, 6_000)).ToArray();
        for (var i = 0; i < total; i++) server.Files[$"/{name}/seg{i}.ts"] = segments[i];

        var requests = 0;
        server.Intercept = ctx =>
        {
            if (ctx.Request.Url!.AbsolutePath != $"/{name}/index.m3u8") return false;
            var n = Math.Min(total, 2 + Interlocked.Increment(ref requests));   // two at first, one more every time
            if (n >= total && !endMarker && requests > total + 8)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return true;
            }

            var first = Math.Max(0, n - window);
            var sb = new StringBuilder($"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:{first}\n");
            for (var i = first; i < n; i++) sb.Append($"#EXTINF:2,\nseg{i}.ts\n");
            if (endMarker && n >= total) sb.Append("#EXT-X-ENDLIST\n");

            var body = Encoding.UTF8.GetBytes(sb.ToString());
            ctx.Response.ContentType = "application/vnd.apple.mpegurl";
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
            ctx.Response.Close();
            return true;
        };
        return (() => requests, segments);
    }

    [Fact]
    public async Task A_live_stream_is_recorded_until_it_ends_and_every_piece_is_kept_once_in_order()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var (_, segments) = Live(server, "live", total: 7, endMarker: true);
        var m = Manager(h);

        var job = AddStream(m, server.Url("/live/index.m3u8"));
        await Finished(job, 60);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.True(job.Hls!.Live);
        Assert.Equal(segments.SelectMany(s => s).ToArray(), File.ReadAllBytes(job.TargetPath));   // the window slid, nothing was lost or doubled
        Assert.All(Enumerable.Range(0, 7), i => Assert.Equal(1, server.Hits[$"/live/seg{i}.ts"]));
    }

    [Fact]
    public async Task A_live_stream_whose_playlist_disappears_ends_with_what_was_recorded()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        Live(server, "live", total: 4, endMarker: false);
        var m = Manager(h);

        var job = AddStream(m, server.Url("/live/index.m3u8"));
        await Finished(job, 90);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.True(new FileInfo(job.TargetPath).Length >= 3 * 6_000);
    }

    [Fact]
    public async Task Stop_and_save_finishes_a_recording_in_progress()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var (_, segments) = Live(server, "live", total: 200, endMarker: false);
        var m = Manager(h);
        var seen = new ConcurrentQueue<JobSnapshot>();
        m.JobUpdated += s => seen.Enqueue(s);

        var job = AddStream(m, server.Url("/live/index.m3u8"));
        await WaitUntil(() => seen.Any(s => s.Id == job.Id && s.PartsDone >= 4), 60, "four pieces");
        var snapshot = seen.Last(s => s.Id == job.Id && s.State == DownloadState.Downloading);
        Assert.True(snapshot.Live);
        Assert.Null(snapshot.Total);                 // no known end
        Assert.True(snapshot.Seconds >= 6);

        Assert.True(m.FinishRecording(job.Id));
        await Finished(job, 30);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        var file = File.ReadAllBytes(job.TargetPath);
        Assert.True(file.Length >= 4 * 6_000);
        Assert.Equal(0, file.Length % 6_000);
        // what was saved is an unbroken run from the first piece
        var expected = segments.Take(file.Length / 6_000).SelectMany(s => s).ToArray();
        Assert.Equal(expected, file);
        Assert.False(m.FinishRecording(job.Id), "nothing is running any more");
    }

    [Fact]
    public async Task A_live_dash_stream_is_recorded_from_the_growing_manifest()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var start = DateTimeOffset.UtcNow.AddSeconds(-30);
        server.Files["/l/init.mp4"] = HlsServer.Bytes("linit", 0, 400);
        server.Intercept = ctx =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            if (path == "/l/manifest.mpd")
            {
                var xml = Mpd("""<AdaptationSet contentType="video" mimeType="video/mp4"><SegmentTemplate timescale="1" duration="1" startNumber="1" initialization="init.mp4" media="seg-$Number$.m4s"/><Representation id="v" bandwidth="800000" width="640" height="360"/></AdaptationSet>""",
                    "dynamic", $"availabilityStartTime=\"{start:O}\" timeShiftBufferDepth=\"PT6S\" minimumUpdatePeriod=\"PT1S\"");
                var body = Encoding.UTF8.GetBytes(xml);
                ctx.Response.ContentType = "application/dash+xml";
                ctx.Response.OutputStream.Write(body);
                ctx.Response.Close();
                return true;
            }

            if (path.StartsWith("/l/seg-") && int.TryParse(path[7..^4], out var n))
            {
                var body = HlsServer.Bytes("lseg", n, 2_000);
                ctx.Response.ContentLength64 = body.Length;
                ctx.Response.OutputStream.Write(body);
                ctx.Response.Close();
                return true;
            }

            return false;
        };
        var m = Manager(h);
        var seen = new ConcurrentQueue<JobSnapshot>();
        m.JobUpdated += s => seen.Enqueue(s);

        var job = AddStream(m, server.Url("/l/manifest.mpd"));
        await WaitUntil(() => seen.Any(s => s.Id == job.Id && s.PartsDone >= 9), 60, "nine pieces");   // 6 s window, then 3+ new ones
        Assert.True(m.FinishRecording(job.Id));
        await Finished(job, 30);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        var numbers = server.Hits.Keys.Where(k => k.StartsWith("/l/seg-")).Select(k => int.Parse(k[7..^4])).OrderBy(x => x).ToList();
        Assert.True(numbers.Count >= 9);
        Assert.Equal(numbers, Enumerable.Range(numbers[0], numbers.Count));                          // no gap, none twice
        var expected = HlsServer.Bytes("linit", 0, 400).Concat(numbers.SelectMany(n => HlsServer.Bytes("lseg", n, 2_000))).ToArray();
        var file = File.ReadAllBytes(job.TargetPath);
        Assert.Equal(expected.Length, file.Length);
        Assert.Equal(expected, file);
    }
}

// ================================================================================================================
// real ffmpeg: DASH made by ffmpeg itself
// ================================================================================================================

public class DashFfmpegTests
{
    static readonly string? Ffmpeg = Hls.Ffmpeg.Locate(null);

    [Fact]
    public async Task A_real_dash_stream_with_separate_audio_becomes_one_playable_mp4()
    {
        if (Ffmpeg == null) return;
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var folder = Path.Combine(h.Root, "made");
        Directory.CreateDirectory(folder);

        var (code, log) = await Hls.Ffmpeg.RunAsync(Ffmpeg, new[]
        {
            "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "50", "-c:a", "aac",
            "-f", "dash", "-seg_duration", "2", "-use_template", "1", "-use_timeline", "1",
            "-init_seg_name", "init-$RepresentationID$.m4s", "-media_seg_name", "chunk-$RepresentationID$-$Number%05d$.m4s",
            "-adaptation_sets", "id=0,streams=v id=1,streams=a",
            "stream.mpd",
        }, CancellationToken.None, workingDirectory: folder);   // the dash muxer writes its pieces relative to where it runs
        Assert.True(code == 0, "ffmpeg could not make the test stream: " + log);
        foreach (var file in Directory.GetFiles(folder)) server.Files["/s/" + Path.GetFileName(file)] = File.ReadAllBytes(file);

        var m = h.NewManager(connections: 4, tweak: s => s.WriteMarkOfTheWeb = false);
        var job = m.Add(new AddDownloadRequest { Url = server.Url("/s/stream.mpd"), FileName = "dash.mp4", Stream = new StreamChoice(), Mode = AddMode.Start });
        await Finished(job, 90);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        var info = new ProcessStartInfo(Ffmpeg, $"-hide_banner -i \"{job.TargetPath}\"") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(info)!;
        var text = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.Contains("Video: h264", text);
        Assert.Contains("Audio: aac", text);
        Assert.Contains("Duration: 00:00:06", text);

        var (decodeCode, decodeLog) = await Hls.Ffmpeg.RunAsync(Ffmpeg, new[] { "-v", "error", "-i", job.TargetPath, "-f", "null", "-" }, CancellationToken.None);
        Assert.True(decodeCode == 0 && decodeLog.Length == 0, "the joined file does not decode cleanly: " + decodeLog);
    }

    [Fact]
    public async Task Picture_and_sound_as_two_separate_playlists_become_one_playable_mp4()
    {
        // a player that asks for the picture's playlist and the sound's playlist on its own, with no master playlist above them:
        // the extension names the sound's playlist, and the app downloads both and joins them
        if (Ffmpeg == null) return;
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var folder = Path.Combine(h.Root, "made");
        Directory.CreateDirectory(Path.Combine(folder, "v"));
        Directory.CreateDirectory(Path.Combine(folder, "a"));

        foreach (var (dir, map, codec) in new[] { ("v", "0:v", new[] { "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "50" }), ("a", "1:a", new[] { "-c:a", "aac" }) })
        {
            var arguments = new List<string>
            {
                "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-map", map,
            };
            arguments.AddRange(codec);
            arguments.AddRange(new[] { "-f", "hls", "-hls_time", "2", "-hls_playlist_type", "vod", "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4", "index.m3u8" });
            var (code, log) = await Hls.Ffmpeg.RunAsync(Ffmpeg, arguments, CancellationToken.None, workingDirectory: Path.Combine(folder, dir));
            Assert.True(code == 0, "ffmpeg could not make the test stream: " + log);
            foreach (var file in Directory.GetFiles(Path.Combine(folder, dir))) server.Files[$"/{dir}/" + Path.GetFileName(file)] = File.ReadAllBytes(file);
        }

        var m = h.NewManager(connections: 4, tweak: s => s.WriteMarkOfTheWeb = false);
        var job = m.Add(new AddDownloadRequest
        {
            Url = server.Url("/v/index.m3u8"), FileName = "split.mp4", Mode = AddMode.Start,
            Stream = new StreamChoice(AudioUrl: server.Url("/a/index.m3u8")),
        });
        await Finished(job, 90);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        var info = new ProcessStartInfo(Ffmpeg, $"-hide_banner -i \"{job.TargetPath}\"") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(info)!;
        var text = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.Contains("Video: h264", text);
        Assert.Contains("Audio: aac", text);
        Assert.Contains("Duration: 00:00:06", text);
        var (decodeCode, decodeLog) = await Hls.Ffmpeg.RunAsync(Ffmpeg, new[] { "-v", "error", "-i", job.TargetPath, "-f", "null", "-" }, CancellationToken.None);
        Assert.True(decodeCode == 0 && decodeLog.Length == 0, "the joined file does not decode cleanly: " + decodeLog);
    }
}

// ================================================================================================================
// subtitles inside the file, subtitles of recordings, recording limit, big single files
// ================================================================================================================

public class StreamMoreTests
{
    static DownloadManager Manager(Harness h, bool withFfmpeg = false, Action<ShadeNet.Core.Engine.EngineSettings>? tweak = null) =>
        h.NewManager(connections: 4, tweak: s =>
        {
            if (!withFfmpeg) s.FfmpegPath = Path.Combine(h.Root, "no-such-ffmpeg.exe");
            tweak?.Invoke(s);
        });

    static DownloadJob AddStream(DownloadManager m, string url, string fileName = "clip.mp4", StreamChoice? choice = null) =>
        m.Add(new AddDownloadRequest { Url = url, FileName = fileName, Stream = choice ?? new StreamChoice(), MaxConnections = 4, Mode = AddMode.Start });

    static string Mpd(string body) =>
        "<?xml version=\"1.0\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT12S\"><Period>" + body + "</Period></MPD>";

    [Fact]
    public void The_remux_command_can_take_subtitle_files_along()
    {
        var mp4 = Ffmpeg.RemuxArguments(new[] { "v.bin" }, "out.mp4", true, new[] { ("a.vtt", "eng"), ("b.vtt", "deu") });
        Assert.Equal(new[] { "-i", "v.bin", "-i", "a.vtt", "-i", "b.vtt" }, mp4.Where((_, i) => i is >= 5 and <= 10));
        Assert.Contains("1:0", mp4);
        Assert.Contains("2:0", mp4);
        Assert.Equal("mov_text", mp4[mp4.ToList().IndexOf("-c:s") + 1]);
        Assert.Contains("language=deu", mp4);

        var mkv = Ffmpeg.RemuxArguments(new[] { "v.bin", "a.bin" }, "out.mkv", true, new[] { ("a.vtt", "eng") });
        Assert.Equal("srt", mkv[mkv.ToList().IndexOf("-c:s") + 1]);
        Assert.Contains("2:0", mkv);                 // after the two media inputs
        Assert.Contains("1:a:0", mkv);
    }

    [Fact]
    public async Task Subtitles_can_be_put_inside_an_mp4_and_a_file_that_cannot_stays_beside_it()
    {
        if (Hls.Ffmpeg.Locate(null) == null) return;
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var folder = Path.Combine(h.Root, "made");
        Directory.CreateDirectory(folder);
        var (code, log) = await Hls.Ffmpeg.RunAsync(Hls.Ffmpeg.Locate(null)!, new[]
        {
            "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "50", "-c:a", "aac",
            "-f", "hls", "-hls_time", "2", "-hls_playlist_type", "vod", "index.m3u8",
        }, CancellationToken.None, workingDirectory: folder);
        Assert.True(code == 0, log);
        foreach (var f in Directory.GetFiles(folder)) server.Files["/s/" + Path.GetFileName(f)] = File.ReadAllBytes(f);
        server.Put("/s/en.vtt", "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nHello inside\n");
        server.Put("/s/en.m3u8", "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:6,\nen.vtt\n#EXT-X-ENDLIST\n");
        server.Put("/s/master.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"s\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,URI=\"en.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=320x240,SUBTITLES=\"s\"\nindex.m3u8\n");
        var m = Manager(h, withFfmpeg: true, tweak: s => s.WriteMarkOfTheWeb = false);

        var inside = AddStream(m, server.Url("/s/master.m3u8"), "inside.mp4", new StreamChoice(EmbedSubtitles: true));
        var beside = AddStream(m, server.Url("/s/master.m3u8"), "beside.mp4");
        await Finished(inside, 90);
        await Finished(beside, 90);

        Assert.True(inside.State == DownloadState.Completed, Describe(inside));
        Assert.False(File.Exists(Path.Combine(inside.SaveDirectory, "inside.en.vtt")), "inside the file: no extra file");
        Assert.True(File.Exists(Path.Combine(beside.SaveDirectory, "beside.en.vtt")));

        var info = new System.Diagnostics.ProcessStartInfo(Hls.Ffmpeg.Locate(null)!, $"-hide_banner -i \"{inside.TargetPath}\"") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(info)!;
        var text = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.Contains("Subtitle: mov_text", text);
        Assert.Contains("(eng)", text);
        Assert.Contains("Video: h264", text);
        Assert.Contains("Audio: aac", text);
    }

    [Fact]
    public async Task A_live_recording_keeps_the_subtitles_too()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var segments = Enumerable.Range(0, 5).Select(i => HlsServer.Bytes("lv", i, 4_000)).ToArray();
        for (var i = 0; i < 5; i++) server.Files[$"/live/seg{i}.ts"] = segments[i];
        var calls = 0;
        server.Intercept = ctx =>
        {
            if (ctx.Request.Url!.AbsolutePath != "/live/index.m3u8") return false;
            var n = Math.Min(5, 2 + Interlocked.Increment(ref calls));
            var sb = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n");
            for (var i = 0; i < n; i++) sb.Append($"#EXTINF:2,\nseg{i}.ts\n");
            if (n >= 5) sb.Append("#EXT-X-ENDLIST\n");
            var body = Encoding.UTF8.GetBytes(sb.ToString());
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
            ctx.Response.Close();
            return true;
        };
        server.Put("/live/a.vtt", "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nlive one\n");
        server.Put("/live/b.vtt", "WEBVTT\n\n00:00:05.000 --> 00:00:06.000\nlive two\n");
        server.Put("/live/en.m3u8", "#EXTM3U\n#EXTINF:4,\na.vtt\n#EXTINF:4,\nb.vtt\n#EXT-X-ENDLIST\n");
        server.Put("/master.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"s\",NAME=\"English\",LANGUAGE=\"en\",URI=\"live/en.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=320x240,SUBTITLES=\"s\"\nlive/index.m3u8\n");
        var m = Manager(h);

        var job = AddStream(m, server.Url("/master.m3u8"), "show.mp4");
        await Finished(job, 60);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.True(job.Hls!.Live);
        var text = File.ReadAllText(Path.Combine(job.SaveDirectory, "show.en.vtt"));
        Assert.Contains("live one", text);
        Assert.Contains("live two", text);
        Assert.Equal(segments.SelectMany(s => s).ToArray(), File.ReadAllBytes(job.TargetPath));   // subtitles never end up in the video bytes
    }

    [Fact]
    public async Task A_recording_stops_by_itself_at_the_length_limit()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var calls = 0;
        for (var i = 0; i < 100; i++) server.Files[$"/live/seg{i}.ts"] = HlsServer.Bytes("lim", i, 3_000);
        server.Intercept = ctx =>
        {
            if (ctx.Request.Url!.AbsolutePath != "/live/index.m3u8") return false;
            var n = 2 + Interlocked.Increment(ref calls);
            var first = Math.Max(0, n - 3);
            var sb = new StringBuilder($"#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:{first}\n");
            for (var i = first; i < n; i++) sb.Append($"#EXTINF:2,\nseg{i}.ts\n");
            var body = Encoding.UTF8.GetBytes(sb.ToString());
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
            ctx.Response.Close();
            return true;
        };
        var m = Manager(h, tweak: s => s.MaxRecordingLength = TimeSpan.FromSeconds(9));

        var job = AddStream(m, server.Url("/live/index.m3u8"));
        await Finished(job, 60);   // nobody presses Stop

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        var length = new FileInfo(job.TargetPath).Length;
        Assert.InRange(length, 5 * 3_000, 12 * 3_000);   // about 9 s of 2 s pieces, plus what was in flight
    }

    [Fact]
    public async Task One_big_dash_file_is_fetched_in_parallel_pieces_and_comes_out_identical()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var whole = HlsServer.Bytes("big", 0, 21_000);
        server.Files["/d/video.mp4"] = whole;
        server.Put("/d/manifest.mpd", Mpd("<AdaptationSet contentType=\"video\" mimeType=\"video/mp4\"><Representation id=\"v\" bandwidth=\"800000\" width=\"640\" height=\"360\"><BaseURL>video.mp4</BaseURL><SegmentBase indexRange=\"0-900\"/></Representation></AdaptationSet>"));
        var ranges = new System.Collections.Concurrent.ConcurrentBag<string>();
        var original = server.Intercept;
        server.Intercept = ctx =>
        {
            if (ctx.Request.Headers["Range"] is { } r && ctx.Request.Url!.AbsolutePath == "/d/video.mp4") ranges.Add(r);
            return original?.Invoke(ctx) ?? false;
        };
        var m = Manager(h, tweak: s => s.StreamChunkBytes = 4_000);

        var job = AddStream(m, server.Url("/d/manifest.mpd"));
        await Finished(job, 60);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal("clip.mp4", job.FileName);                          // MP4 inside, even without ffmpeg and without an init section
        Assert.Equal(whole, File.ReadAllBytes(job.TargetPath));
        Assert.Equal(6, job.Hls!.Parts);                                  // 21 000 bytes in pieces of 4 000
        Assert.True(ranges.Count >= 6, string.Join(" ", ranges));
    }

    [Fact]
    public async Task A_big_file_from_a_server_without_ranges_stays_one_piece()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var whole = HlsServer.Bytes("norange", 0, 15_000);
        server.Files["/d/video.mp4"] = whole;
        server.Put("/d/manifest.mpd", Mpd("<AdaptationSet contentType=\"video\" mimeType=\"video/mp4\"><Representation id=\"v\" bandwidth=\"1\" width=\"2\" height=\"2\"><BaseURL>video.mp4</BaseURL></Representation></AdaptationSet>"));
        server.Intercept = ctx =>
        {
            if (ctx.Request.Url!.AbsolutePath != "/d/video.mp4") return false;
            ctx.Response.ContentLength64 = whole.Length;               // always the whole thing, whatever Range says
            ctx.Response.OutputStream.Write(whole);
            ctx.Response.Close();
            return true;
        };
        var m = Manager(h, tweak: s => s.StreamChunkBytes = 4_000);

        var job = AddStream(m, server.Url("/d/manifest.mpd"));
        await Finished(job, 60);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal(whole, File.ReadAllBytes(job.TargetPath));
        Assert.Equal(1, job.Hls!.Parts);
    }
}

// ================================================================================================================
// picture and sound a browser's player fetched as separate files (the "direct" mode)
// ================================================================================================================

public class DirectTracksTests
{
    static DownloadManager Manager(Harness h, bool withFfmpeg, Action<ShadeNet.Core.Engine.EngineSettings>? tweak = null) =>
        h.NewManager(connections: 4, tweak: s =>
        {
            s.WriteMarkOfTheWeb = false;
            if (!withFfmpeg) s.FfmpegPath = Path.Combine(h.Root, "no-such-ffmpeg.exe");
            tweak?.Invoke(s);
        });

    [Fact]
    public async Task One_file_with_picture_and_sound_is_fetched_in_parallel_ranges_and_saved_as_it_is()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var whole = HlsServer.Bytes("muxed", 0, 30_000);
        server.Files["/videoplayback"] = whole;
        var ranges = new ConcurrentBag<string>();
        server.Intercept = ctx =>
        {
            if (ctx.Request.Headers["Range"] is { } r) ranges.Add(r);
            return false;
        };
        var m = Manager(h, withFfmpeg: false, s => s.StreamChunkBytes = 5_000);

        var job = m.Add(new AddDownloadRequest
        {
            Url = server.Url("/videoplayback?itag=18&signature=abc"), FileName = "My Video.mp4", Mode = AddMode.Start,
            Stream = new StreamChoice(Direct: true, Seconds: 30), Referer = "https://www.youtube.com/watch?v=abc",
        });
        await Finished(job, 60);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.Equal(DownloadKind.Hls, job.Kind);
        Assert.Equal("My Video.mp4", job.FileName);
        Assert.Equal(whole, File.ReadAllBytes(job.TargetPath));
        Assert.Equal(6, job.Hls!.Parts);
        Assert.True(ranges.Count >= 6, "fetched in ranges: " + string.Join(" ", ranges));
        Assert.Contains(server.Requests, r => r.Referer == "https://www.youtube.com/watch?v=abc" && r.Path == "/videoplayback");
    }

    [Fact]
    public async Task Separate_sound_without_ffmpeg_fails_with_advice_and_keeps_what_was_fetched()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.Files["/v"] = new byte[8_000];
        server.Files["/a"] = new byte[3_000];
        var m = Manager(h, withFfmpeg: false);

        var job = m.Add(new AddDownloadRequest
        {
            Url = server.Url("/v"), FileName = "x.mp4", Mode = AddMode.Start, Stream = new StreamChoice(Direct: true, AudioUrl: server.Url("/a")),
        });
        await Finished(job, 60);

        Assert.Equal((DownloadState.Failed, JobErrorKind.Tool), (job.State, job.ErrorKind));
        Assert.True(Directory.Exists(job.WorkFolder));
    }

    [Fact]
    public async Task Real_picture_and_real_sound_from_two_addresses_become_one_playable_file()
    {
        var ffmpeg = Hls.Ffmpeg.Locate(null);
        if (ffmpeg == null) return;
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var folder = Path.Combine(h.Root, "made");
        Directory.CreateDirectory(folder);

        // what the player fetches: an MP4 with only the picture, an M4A with only the sound
        var (code, log) = await Hls.Ffmpeg.RunAsync(ffmpeg, new[]
        {
            "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-map", "0:v", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "video.mp4",
            "-map", "1:a", "-c:a", "aac", "-b:a", "96k", "audio.m4a",
        }, CancellationToken.None, workingDirectory: folder);
        Assert.True(code == 0, log);
        server.Files["/videoplayback_v"] = File.ReadAllBytes(Path.Combine(folder, "video.mp4"));
        server.Files["/videoplayback_a"] = File.ReadAllBytes(Path.Combine(folder, "audio.m4a"));
        var m = Manager(h, withFfmpeg: true, s => s.StreamChunkBytes = 4_096);

        var job = m.Add(new AddDownloadRequest
        {
            Url = server.Url("/videoplayback_v?itag=137"), FileName = "Holiday.mp4", Mode = AddMode.Start,
            Stream = new StreamChoice(Direct: true, AudioUrl: server.Url("/videoplayback_a?itag=140"), Seconds: 6),
        });
        await Finished(job, 90);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.True(job.Hls!.Parts > 2, "cut into ranges: " + job.Hls.Parts);
        var info = new System.Diagnostics.ProcessStartInfo(ffmpeg, $"-hide_banner -i \"{job.TargetPath}\"") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(info)!;
        var text = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.Contains("Video: h264", text);
        Assert.Contains("Audio: aac", text);
        Assert.Contains("Duration: 00:00:06", text);
        var (decodeCode, decodeLog) = await Hls.Ffmpeg.RunAsync(ffmpeg, new[] { "-v", "error", "-i", job.TargetPath, "-f", "null", "-" }, CancellationToken.None);
        Assert.True(decodeCode == 0 && decodeLog.Length == 0, decodeLog);
    }

    [Fact]
    public async Task A_small_file_is_still_asked_for_by_range_because_some_sites_throttle_a_whole_file_request()
    {
        // YouTube answers a request for a whole file at a few tens of KB/s, and a ranged request at full speed. A 1.5 MB sound
        // track (smaller than a piece) used to be requested whole and took 45 s after the picture had arrived in one second.
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        var body = HlsServer.Bytes("small", 0, 1_500_000);
        server.Files["/videoplayback_small"] = body;
        var requests = new System.Collections.Concurrent.ConcurrentBag<string?>();
        server.Intercept = ctx =>
        {
            if (ctx.Request.Url!.AbsolutePath == "/videoplayback_small") requests.Add(ctx.Request.Headers["Range"]);
            return false;
        };
        var m = Manager(h, withFfmpeg: false);

        var job = m.Add(new AddDownloadRequest { Url = server.Url("/videoplayback_small"), FileName = "small.mp4", Mode = AddMode.Start, Stream = new StreamChoice(Direct: true) });
        await Finished(job, 60);

        Assert.True(job.State == DownloadState.Completed, Describe(job));
        Assert.All(requests, range => Assert.False(string.IsNullOrEmpty(range), "a request for the whole file went out without a Range"));
        Assert.Contains("bytes=0-1499999", requests);   // one ranged piece for the whole (small) file
        Assert.Equal(body, File.ReadAllBytes(job.TargetPath));
    }
}
