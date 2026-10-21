using System.IO;
using ShadeNet.Bridge;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Services.Browser;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

public class YouTubeCaptureTests
{
    const string VideoUrl = "https://rr2---sn-abc.googlevideo.com/videoplayback?itag=137&mime=video%2Fmp4&sig=S";
    const string AudioUrl = "https://rr3---sn-abc.googlevideo.com/videoplayback?itag=140&mime=audio%2Fmp4&sig=T";

    static CaptureRequest Tracks(string? audioUrl = AudioUrl) => new()
    {
        Id = "t1", Url = VideoUrl, Source = "tracks", AudioUrl = audioUrl, AudioSize = 3_400_000, Size = 48_000_000, Duration = 212.5,
        FileName = "My Holiday Video.mp4", Referrer = "https://www.youtube.com/watch?v=abc", Cookies = "a=1",
    };

    [Fact]
    public void The_message_keeps_what_belongs_to_picture_and_sound_and_drops_what_does_not()
    {
        var capture = CaptureProcessor.Sanitize(Tracks())!;
        Assert.Equal("tracks", capture.Source);
        Assert.Equal(AudioUrl, capture.AudioUrl);
        Assert.Equal((3_400_000L, 212.5), (capture.AudioSize, capture.Duration));

        // the extra fields mean nothing for an ordinary download, and a bad address is dropped
        Assert.Null(CaptureProcessor.Sanitize(Tracks() with { Source = "download" })!.AudioUrl);
        Assert.Null(CaptureProcessor.Sanitize(Tracks(audioUrl: "javascript:alert(1)"))!.AudioUrl);
        Assert.Null(CaptureProcessor.Sanitize(Tracks() with { Duration = -4 })!.Duration);

        // "media" used to be turned into "download" here
        Assert.Equal("media", CaptureProcessor.Sanitize(new CaptureRequest { Url = "https://x.example/play", Source = "media" })!.Source);
    }

    [Fact]
    public async Task In_immediate_mode_picture_and_sound_become_one_job_with_both_addresses()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path, UseCategoryFolders = false, FfmpegPath = Path.Combine(dir.Path, "none.exe") });
        var ui = new FakeCaptureUi();
        var settings = new BrowserSettings { Mode = CaptureMode.StartImmediately };
        var processor = new CaptureProcessor(manager, () => settings, ui);

        var result = await processor.HandleAsync(Tracks());
        Assert.True(result.Accepted);

        var job = Assert.Single(manager.Jobs);
        Assert.Equal(DownloadKind.Hls, job.Kind);
        Assert.True(job.Hls!.Direct);
        Assert.Equal(AudioUrl, job.Hls.AudioUrl);
        Assert.Equal(212.5, job.Hls.Seconds);
        Assert.Equal(VideoUrl, job.Url);
        Assert.Equal("My Holiday Video.mp4", job.FileName);
        Assert.Equal("https://www.youtube.com/watch?v=abc", job.Referer);
        await manager.StopAllAsync();
    }

    [Fact]
    public async Task In_ask_mode_the_dialog_shows_the_video_at_once_without_probing_and_adds_it_as_two_tracks()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path, UseCategoryFolders = false });
        var capture = CaptureProcessor.Sanitize(Tracks())!;
        using var vm = new AddDownloadViewModel(manager, null, capture);

        for (var i = 0; i < 100 && vm.ProbeState != ProbeState.Ready; i++) await Task.Delay(50);   // the 0.5 s typing pause

        Assert.Equal(ProbeState.Ready, vm.ProbeState);
        Assert.Equal("My Holiday Video.mp4", vm.FileName);
        Assert.Contains("3:32", vm.SizeText);                       // 212 s
        Assert.Contains("MB", vm.SizeText);
        Assert.Equal("Video and sound from your browser", vm.TypeText);
        Assert.True(vm.CanAdd);
        Assert.False(vm.HasQualities);

        var (request, probe) = Assert.Single(vm.Build(AddMode.Hold));
        Assert.Null(probe);
        Assert.True(request.Stream!.Direct);
        Assert.Equal(AudioUrl, request.Stream.AudioUrl);
        Assert.Equal(212.5, request.Stream.Seconds);
    }
}
