using System.IO;
using System.Net;
using System.Text;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Models;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Services.Browser;
using ShadeNetDownloader.ViewModels;
using ShadeNet.Bridge;

namespace ShadeNetDownloader.Tests;

/// <summary>Serves fixed text/bytes: playlists and a few segments.</summary>
sealed class PlaylistServer : IDisposable
{
    readonly HttpListener _listener = new();
    public Dictionary<string, byte[]> Files { get; } = new();

    public PlaylistServer()
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

    public void Put(string path, string text) => Files[path] = Encoding.UTF8.GetBytes(text);

    async Task Loop()
    {
        while (true)
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

            try
            {
                var path = ctx.Request.Url!.AbsolutePath;
                if (!Files.TryGetValue(path, out var body))
                {
                    ctx.Response.StatusCode = 404;
                }
                else
                {
                    ctx.Response.ContentType = path.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "application/octet-stream";
                    var from = 0;
                    var to = body.Length - 1;
                    if (ctx.Request.Headers["Range"] is { } r && r.StartsWith("bytes="))
                    {
                        var parts = r[6..].Split('-');
                        from = int.Parse(parts[0]);
                        if (parts[1].Length > 0) to = Math.Min(int.Parse(parts[1]), to);
                        ctx.Response.StatusCode = 206;
                        ctx.Response.AddHeader("Content-Range", $"bytes {from}-{to}/{body.Length}");
                    }

                    ctx.Response.ContentLength64 = to - from + 1;
                    await ctx.Response.OutputStream.WriteAsync(body.AsMemory(from, to - from + 1));
                }

                ctx.Response.Close();
            }
            catch
            {
                // client went away
            }
        }
    }

    public void Dispose() => _listener.Close();
}

public class StreamUiTests
{
    const string Master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nlow/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=4000000,RESOLUTION=1920x1080\nhigh/index.m3u8\n";

    static string Vod(int count) =>
        "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n" + string.Concat(Enumerable.Range(0, count).Select(i => $"#EXTINF:4,\ns{i}.ts\n")) + "#EXT-X-ENDLIST\n";

    static PlaylistServer Server(int segments = 3)
    {
        var s = new PlaylistServer();
        s.Put("/movies/holiday/master.m3u8", Master);
        foreach (var q in new[] { "low", "high" })
        {
            s.Put($"/movies/holiday/{q}/index.m3u8", Vod(segments));
            for (var i = 0; i < segments; i++) s.Files[$"/movies/holiday/{q}/s{i}.ts"] = new byte[2000 + i];
        }

        s.Put("/live/index.m3u8", "#EXTM3U\n#EXTINF:4,\na.ts\n");
        return s;
    }

    static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "Timed out waiting for: " + what);
    }

    [Fact]
    public async Task A_playlist_link_shows_qualities_and_adds_a_stream_download_not_a_file()
    {
        using var dir = new TempDir();
        using var server = Server();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path, UseCategoryFolders = false, FfmpegPath = Path.Combine(dir.Path, "none.exe") });
        using var vm = new AddDownloadViewModel(manager, null);

        vm.UrlText = server.Base + "/movies/holiday/master.m3u8";
        await WaitFor(() => vm.ProbeState == ProbeState.Ready, "the stream to be read");

        Assert.True(vm.IsStream);
        Assert.True(vm.HasQualities);
        Assert.Equal(new[] { 1080, 360 }, vm.Qualities.Select(q => q.Choice.Height));   // best first, and selected
        Assert.Equal(1080, vm.SelectedQuality!.Choice.Height);
        Assert.Equal("holiday.mp4", vm.FileName);                                         // the folder name, not "master"
        Assert.Equal("Video stream (HLS)", vm.TypeText);
        Assert.Contains("0:12", vm.SizeText);                                             // 3 × 4 s
        Assert.Contains("about", vm.SizeText);
        Assert.True(vm.CanAdd);

        var before = vm.SizeText;
        vm.SelectedQuality = vm.Qualities[1];
        Assert.NotEqual(before, vm.SizeText);                                             // the estimate follows the quality

        var (request, probe) = Assert.Single(vm.Build(AddMode.Hold));
        Assert.Null(probe);
        Assert.Equal(360, request.Stream!.Height);
        Assert.Equal("holiday.mp4", request.FileName);

        var job = manager.Add(request);
        Assert.Equal(DownloadKind.Hls, job.Kind);
        Assert.Equal("Video", job.Category);
    }

    [Fact]
    public async Task A_live_stream_can_be_added_and_the_dialog_says_it_is_recorded_until_stopped()
    {
        using var dir = new TempDir();
        using var server = Server();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path });
        using var vm = new AddDownloadViewModel(manager, null);

        vm.UrlText = server.Base + "/live/index.m3u8";
        await WaitFor(() => vm.ProbeState == ProbeState.Ready, "the live stream to be read");

        Assert.True(vm.IsLiveStream);
        Assert.True(vm.CanAdd);
        Assert.Contains("until you stop it", vm.SizeText);
        Assert.Equal("Live stream (HLS)", vm.TypeText);
    }

    [Fact]
    public async Task Subtitles_and_dash_show_up_in_the_dialog_and_travel_with_the_request()
    {
        using var dir = new TempDir();
        using var server = Server();
        server.Put("/withsubs/master.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"s\",NAME=\"English\",LANGUAGE=\"en\",URI=\"en.m3u8\"\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"s\",NAME=\"Deutsch\",LANGUAGE=\"de\",URI=\"de.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360,SUBTITLES=\"s\"\nv.m3u8\n");
        server.Put("/withsubs/v.m3u8", Vod(2));
        server.Put("/dash/manifest.mpd", "<MPD type=\"static\" mediaPresentationDuration=\"PT8S\"><Period><AdaptationSet contentType=\"video\" mimeType=\"video/mp4\"><SegmentTemplate timescale=\"1\" duration=\"4\" media=\"s$Number$.m4s\"/><Representation id=\"a\" bandwidth=\"800000\" width=\"640\" height=\"360\"/><Representation id=\"b\" bandwidth=\"2000000\" width=\"1280\" height=\"720\"/></AdaptationSet></Period></MPD>");
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path });

        using (var vm = new AddDownloadViewModel(manager, null))
        {
            vm.UrlText = server.Base + "/withsubs/master.m3u8";
            await WaitFor(() => vm.ProbeState == ProbeState.Ready, "the stream");
            Assert.True(vm.HasSubtitles);
            Assert.Contains("English", vm.SubtitleText);
            Assert.Contains("Deutsch", vm.SubtitleText);
            Assert.True(vm.IncludeSubtitles);
            Assert.True(vm.Build(AddMode.Hold).Single().Request.Stream!.Subtitles);

            vm.IncludeSubtitles = false;
            Assert.False(vm.Build(AddMode.Hold).Single().Request.Stream!.Subtitles);
        }

        using (var vm = new AddDownloadViewModel(manager, null))
        {
            vm.UrlText = server.Base + "/dash/manifest.mpd";
            await WaitFor(() => vm.ProbeState == ProbeState.Ready, "the DASH manifest");
            Assert.Equal("Video stream (DASH)", vm.TypeText);
            Assert.Equal(new[] { 720, 360 }, vm.Qualities.Select(q => q.Choice.Height));
            Assert.False(vm.HasSubtitles);
        }
    }

    [Fact]
    public void A_recording_card_shows_time_and_size_and_its_button_stops_and_saves()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path });
        var job = manager.Add(new AddDownloadRequest { Url = "http://127.0.0.1:9/live/index.m3u8", FileName = "show.mp4", Stream = new StreamChoice(), Mode = AddMode.Hold });
        var owner = new DownloadsViewModel(manager, new ShadeNetDownloader.Services.ToastService());
        var item = new DownloadItemViewModel(job, manager.Snapshot(job), owner);

        item.Update(new JobSnapshot(job.Id, DownloadState.Downloading, "show.mp4", dir.Path, 14_000_000, null, 500_000, null, 2,
            Array.Empty<SegmentInfo>(), true, null, JobErrorKind.None, Parts: 40, PartsDone: 38, Live: true, Seconds: 133));

        Assert.True(item.IsRecording);
        Assert.Equal("Recording", item.StatusText);
        Assert.Contains("2:13 recorded", item.DetailText);
        Assert.True(item.IsIndeterminate);                       // no known end: no percentage
        Assert.Equal("", item.PercentText);
        Assert.Equal("Stop and save the recording", item.PrimaryTooltip);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Stop24, item.PrimaryIcon);

        item.Update(new JobSnapshot(job.Id, DownloadState.Paused, "show.mp4", dir.Path, 14_000_000, null, 0, null, 0,
            Array.Empty<SegmentInfo>(), true, null, JobErrorKind.None, Live: true, Seconds: 133));
        Assert.False(item.IsRecording);                          // paused: Resume, not Stop
        Assert.Equal("Resume", item.PrimaryTooltip);
    }

    [Fact]
    public void A_stream_card_speaks_of_pieces_and_estimates_and_a_failed_join_says_what_to_do()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path });
        var job = manager.Add(new AddDownloadRequest { Url = "http://127.0.0.1:9/a/index.m3u8", FileName = "clip.mp4", Stream = new StreamChoice(), Mode = AddMode.Hold });
        var owner = new DownloadsViewModel(manager, new ShadeNetDownloader.Services.ToastService());
        var item = new DownloadItemViewModel(job, manager.Snapshot(job), owner);
        Assert.True(item.IsStream);

        item.Update(new JobSnapshot(job.Id, DownloadState.Downloading, "clip.mp4", dir.Path, 5_000_000, 20_000_000, 1_000_000, TimeSpan.FromSeconds(15), 4,
            Array.Empty<SegmentInfo>(), true, null, JobErrorKind.None, Parts: 40, PartsDone: 9));
        Assert.Contains("about", item.DetailText);
        Assert.Contains("piece 10 of 40", item.DetailText);
        Assert.DoesNotContain("connections", item.DetailText);
        Assert.Equal("25 %".Replace(" ", ""), item.PercentText.Replace(" ", "").Replace(" ", ""));

        item.Update(new JobSnapshot(job.Id, DownloadState.Downloading, "clip.mp4", dir.Path, 20_000_000, 20_000_000, 0, null, 0,
            Array.Empty<SegmentInfo>(), true, null, JobErrorKind.None, Note: "Joining the pieces…", Parts: 40, PartsDone: 40));
        Assert.Equal("Joining", item.StatusText);

        item.Update(new JobSnapshot(job.Id, DownloadState.Failed, "clip.mp4", dir.Path, 0, null, 0, null, 0,
            Array.Empty<SegmentInfo>(), true, "ffmpeg is missing", JobErrorKind.Tool));
        Assert.Contains("ffmpeg", item.HintText);
    }

    [Fact]
    public async Task The_browser_sending_a_stream_in_immediate_mode_starts_a_stream_download_at_the_best_quality()
    {
        using var dir = new TempDir();
        using var server = Server();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path, UseCategoryFolders = false, WriteMarkOfTheWeb = false, FfmpegPath = Path.Combine(dir.Path, "none.exe") });
        var ui = new FakeCaptureUi();
        var settings = new BrowserSettings { Mode = CaptureMode.StartImmediately };
        var processor = new CaptureProcessor(manager, () => settings, ui);

        var result = await processor.HandleAsync(new CaptureRequest
        {
            Id = "m1", Url = server.Base + "/movies/holiday/master.m3u8", Source = "media", FileName = "My Holiday Video",
            Referrer = "https://site.example/watch/1", Cookies = "a=1",
        });

        Assert.True(result.Accepted);
        var job = Assert.Single(manager.Jobs);
        Assert.Equal(DownloadKind.Hls, job.Kind);
        Assert.Equal("My Holiday Video.mp4", job.FileName);
        await WaitFor(() => job.State is DownloadState.Completed or DownloadState.Failed, "the stream download");
        Assert.True(job.State == DownloadState.Completed, job.Error);
        Assert.Equal(1080, job.Hls!.Height);
        Assert.True(File.Exists(job.TargetPath));
    }

    [Fact]
    public async Task The_sound_s_own_playlist_from_the_browser_reaches_the_job_in_immediate_mode()
    {
        using var dir = new TempDir();
        using var server = Server();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path, UseCategoryFolders = false, WriteMarkOfTheWeb = false, FfmpegPath = Path.Combine(dir.Path, "none.exe") });
        var settings = new BrowserSettings { Mode = CaptureMode.StartImmediately };
        var processor = new CaptureProcessor(manager, () => settings, new FakeCaptureUi());
        var sound = server.Base + "/movies/holiday/audio.m3u8";

        await processor.HandleAsync(new CaptureRequest { Id = "m2", Url = server.Base + "/movies/holiday/master.m3u8", Source = "media", FileName = "Split", AudioUrl = sound });

        Assert.Equal(sound, Assert.Single(manager.Jobs).Hls!.AudioUrl);
        Assert.False(manager.Jobs.Single().Hls!.Direct);   // a playlist to be read, not a file
    }

    [Fact]
    public void Only_a_stream_or_picture_and_sound_files_may_carry_a_sound_address()
    {
        string? Audio(string source, string? audio) => CaptureProcessor.Sanitize(new CaptureRequest { Url = "https://x.example/v.m3u8", Source = source, AudioUrl = audio })!.AudioUrl;

        Assert.Equal("https://x.example/a.m3u8", Audio("media", "https://x.example/a.m3u8"));
        Assert.Equal("https://x.example/a.m4a", Audio("tracks", "https://x.example/a.m4a"));
        Assert.Null(Audio("download", "https://x.example/a.m3u8"));
        Assert.Null(Audio("media", "javascript:alert(1)"));
    }

    [Fact]
    public async Task The_dialog_adds_the_sound_playlist_to_the_stream_it_builds()
    {
        using var dir = new TempDir();
        using var server = Server();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path, UseCategoryFolders = false, WriteMarkOfTheWeb = false });
        var sound = server.Base + "/movies/holiday/audio.m3u8";
        var capture = new CaptureInfo(server.Base + "/movies/holiday/master.m3u8", null, null, null, new Dictionary<string, string>(), "Split", null, "media", sound);
        using var vm = new AddDownloadViewModel(manager, null, capture);

        await WaitFor(() => vm.ProbeState == ProbeState.Ready, "the stream to be read");

        Assert.Equal(sound, vm.Build(AddMode.Hold).Single().Request.Stream!.AudioUrl);
    }
}
