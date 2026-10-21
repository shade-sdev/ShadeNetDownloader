using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

public class StreamUiMoreTests
{
    static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "Timed out waiting for: " + what);
    }

    [Fact]
    public async Task Putting_subtitles_inside_the_video_is_offered_and_passed_on_only_when_subtitles_are_wanted()
    {
        using var dir = new TempDir();
        using var server = new PlaylistServer();
        server.Put("/m/master.m3u8", "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"s\",NAME=\"English\",LANGUAGE=\"en\",URI=\"en.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360,SUBTITLES=\"s\"\nv.m3u8\n");
        server.Put("/m/v.m3u8", "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:4,\ns0.ts\n#EXT-X-ENDLIST\n");
        using var manager = new DownloadManager(new EngineSettings { DownloadRoot = dir.Path });
        using var vm = new AddDownloadViewModel(manager, null);

        vm.UrlText = server.Base + "/m/master.m3u8";
        await WaitFor(() => vm.ProbeState == ProbeState.Ready, "the stream");

        Assert.False(vm.EmbedSubtitles);                                                        // beside the video unless asked
        Assert.False(vm.Build(AddMode.Hold).Single().Request.Stream!.EmbedSubtitles);

        vm.EmbedSubtitles = true;
        Assert.True(vm.Build(AddMode.Hold).Single().Request.Stream!.EmbedSubtitles);

        vm.IncludeSubtitles = false;                                                            // no subtitles: nothing to put inside
        var choice = vm.Build(AddMode.Hold).Single().Request.Stream!;
        Assert.False(choice.Subtitles);
        Assert.False(choice.EmbedSubtitles);
    }

    [Fact]
    public void The_recording_limit_is_a_setting_in_minutes_that_is_saved_and_clamped()
    {
        using var dir = new TempDir();
        var settings = new SettingsService(dir.Path);
        settings.Load();
        settings.Current.Engine.DownloadRoot = dir.Path;

        var page = new DownloadSettingsViewModel(settings, () => { }, () => { });
        Assert.Equal(0, page.MaxRecordingMinutes);                                              // off unless the user chooses

        page.MaxRecordingMinutes = 90;
        Assert.Equal(TimeSpan.FromMinutes(90), settings.Current.Engine.MaxRecordingLength);

        page.MaxRecordingMinutes = -5;
        Assert.Equal(0, page.MaxRecordingMinutes);
        page.MaxRecordingMinutes = 999_999;
        Assert.Equal(24 * 60, page.MaxRecordingMinutes);

        page.MaxRecordingMinutes = 45;
        var again = new SettingsService(dir.Path);
        again.Load();
        Assert.Equal(TimeSpan.FromMinutes(45), again.Current.Engine.MaxRecordingLength);
    }
}
