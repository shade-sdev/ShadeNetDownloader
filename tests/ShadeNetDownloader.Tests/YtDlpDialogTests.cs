using System.IO;
using ShadeNet.Bridge;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Sites;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Services.Browser;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

public class YtDlpDialogTests
{
    const string Page = "https://www.youtube.com/watch?v=aqz-KE-bpKQ";

    static YtDlpInfo Info(bool live = false) => new("Big Buck Bunny: the film", 635, live, new[]
    {
        new YtDlpOption("1080p60 · H.264 · about 89 MB", 1080, "https://v.example/v1080", "https://v.example/a-m4a", ".mp4", 93_400_000, "UA/1", false),
        new YtDlpOption("2160p · VP9 · about 570 MB", 2160, "https://v.example/v2160", "https://v.example/a-opus", ".mkv", 600_000_000, "UA/1", false),
        new YtDlpOption("720p · H.264", 720, "https://v.example/v720", null, ".mp4", 0, null, false),
    });

    static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "Timed out waiting for: " + what);
    }

    [Fact]
    public async Task A_youtube_link_shows_what_yt_dlp_found_and_adds_the_files_not_the_page()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path, UseCategoryFolders = false });
        var asked = new List<string>();
        using var vm = new AddDownloadViewModel(manager, null, null, (url, _) =>
        {
            asked.Add(url);
            return Task.FromResult(Info());
        });

        vm.UrlText = Page;
        await WaitFor(() => vm.ProbeState == ProbeState.Ready, "yt-dlp's answer");

        Assert.Equal(new[] { Page }, asked);
        Assert.True(vm.IsStream);
        Assert.True(vm.HasQualities);
        Assert.Equal(new[] { 1080, 2160, 720 }.Length, vm.Qualities.Count);
        Assert.Equal("Big Buck Bunny_ the film.mp4", vm.FileName);          // the title as a file name, with the container of the quality
        Assert.Equal("YouTube video", vm.TypeText);
        Assert.Contains("10:35", vm.SizeText);
        Assert.Contains("about 89", vm.SizeText);
        Assert.True(vm.CanAdd);

        vm.SelectedQuality = vm.Qualities[1];                               // 2160p: VP9 + opus, so an MKV
        Assert.Equal("Big Buck Bunny_ the film.mkv", vm.FileName);
        Assert.Contains("572", vm.SizeText);

        var (request, probe) = Assert.Single(vm.Build(AddMode.Hold));
        Assert.Null(probe);
        Assert.Equal("https://v.example/v2160", request.Url);              // the picture, not the page
        Assert.True(request.Stream!.Direct);
        Assert.Equal("https://v.example/a-opus", request.Stream.AudioUrl);
        Assert.Equal(635, request.Stream.Seconds);
        Assert.Equal("UA/1", request.UserAgent);
        Assert.Null(request.Referer);
        Assert.Null(request.Cookies);                                       // nothing of the browser session goes to the CDN

        var job = manager.Add(request);
        Assert.Equal((DownloadKind.Hls, "Video"), (job.Kind, job.Category));
        Assert.True(job.Hls!.Direct);
    }

    [Fact]
    public async Task A_page_the_extension_sends_is_resolved_by_yt_dlp_whatever_the_site()
    {
        // Facebook's player hands the browser a buffer, not an address: only the page can be given to yt-dlp
        const string facebook = "https://www.facebook.com/facebook/videos/10153231379946729/";
        using var dir = new TempDir();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path, UseCategoryFolders = false });
        var asked = new List<string>();
        var capture = new CaptureInfo(facebook, facebook, null, null, new Dictionary<string, string>(), "How to Share", null, "page", null, null, null);
        using var vm = new AddDownloadViewModel(manager, null, capture, (url, _) =>
        {
            asked.Add(url);
            return Task.FromResult(Info());
        });

        await WaitFor(() => vm.ProbeState == ProbeState.Ready, "yt-dlp's answer");

        Assert.Equal(new[] { facebook }, asked);
        Assert.True(vm.HasQualities);
        Assert.Equal("Video from facebook.com", vm.TypeText);
        Assert.Equal("https://v.example/v1080", vm.Build(AddMode.Hold).Single().Request.Url);   // the file, not the page
    }

    [Fact]
    public async Task A_name_the_user_typed_is_kept_when_the_quality_changes()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path });
        using var vm = new AddDownloadViewModel(manager, null, null, (_, _) => Task.FromResult(Info()));
        vm.UrlText = Page;
        await WaitFor(() => vm.ProbeState == ProbeState.Ready, "the answer");

        vm.FileName = "my own name.mp4";
        vm.SelectedQuality = vm.Qualities[1];
        Assert.Equal("my own name.mp4", vm.FileName);
    }

    [Fact]
    public async Task A_live_broadcast_says_it_is_recorded()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path });
        var live = new YtDlpInfo("Live show", 0, true, new[] { new YtDlpOption("720p · stream", 720, "https://v.example/720.m3u8", null, ".mp4", 0, null, true) });
        using var vm = new AddDownloadViewModel(manager, null, null, (_, _) => Task.FromResult(live));
        vm.UrlText = Page;
        await WaitFor(() => vm.ProbeState == ProbeState.Ready, "the answer");

        Assert.True(vm.IsLiveStream);
        Assert.Contains("until you stop it", vm.SizeText);
        var request = vm.Build(AddMode.Hold).Single().Request;
        Assert.Equal("https://v.example/720.m3u8", request.Url);
        Assert.False(request.Stream!.Direct);                               // a playlist: the HLS engine records it
    }

    [Fact]
    public async Task When_yt_dlp_cannot_help_the_dialog_says_why_and_how_to_fix_it()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path, YtDlpPath = Path.Combine(dir.Path, "missing.exe") });

        // the real resolver, pointed at a program that is not there
        using var vm = new AddDownloadViewModel(manager, null);
        vm.UrlText = Page;
        await WaitFor(() => vm.ProbeState == ProbeState.Failed, "the failure");

        Assert.Contains("yt-dlp was not found", vm.ProbeError);
        Assert.Contains("Update", vm.ProbeHint);
    }

    [Fact]
    public async Task A_youtube_page_from_the_browser_opens_the_dialog_even_in_immediate_mode()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path });
        var ui = new FakeCaptureUi();
        var processor = new CaptureProcessor(manager, () => new BrowserSettings { Mode = CaptureMode.StartImmediately }, ui);

        var result = await processor.HandleAsync(new CaptureRequest { Id = "p1", Url = Page, Source = "media", FileName = "Big Buck Bunny" });

        Assert.True(result.Accepted);
        Assert.Empty(manager.Jobs);                                         // nothing is added blindly: yt-dlp has to say what is there
        Assert.Equal(Page, Assert.Single(ui.Shown).Url);
        Assert.Equal(1, ui.BroughtToFront);
    }
}
