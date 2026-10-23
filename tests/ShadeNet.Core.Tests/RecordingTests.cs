using ShadeNet.Core.Engine;
using ShadeNet.Core.Hls;
using ShadeNet.Core.Models;

namespace ShadeNet.Core.Tests;

/// <summary>A live stream that is one endless response (an FLV pull) is recorded with ffmpeg. (The real recording is checked by hand with a live stream.)</summary>
public class RecordingTests
{
    [Fact]
    public void The_recording_command_carries_what_the_players_request_carried()
    {
        var args = Ffmpeg.RecordArguments("https://pull.example/live.flv?k=1", @"C:\Videos\show.mkv.sndpart", "UA/1", "https://site.example/live", "a=1; b=2",
            new Dictionary<string, string> { ["Origin"] = "https://site.example", ["User-Agent"] = "ignored", ["Bad Name"] = "x", ["X-Token"] = "t\r\nInjected: yes" });

        Assert.Equal("-user_agent", args[args.ToList().IndexOf("UA/1") - 1]);
        var headers = args[args.ToList().IndexOf("-headers") + 1];
        Assert.Equal("Referer: https://site.example/live\r\nCookie: a=1; b=2\r\nOrigin: https://site.example\r\n", headers);   // no other header: a name with a space, a value with a line break
        Assert.Equal(new[] { "-i", "https://pull.example/live.flv?k=1" }, args.SkipWhile(a => a != "-i").Take(2));
        Assert.Equal(new[] { "-f", "matroska", @"C:\Videos\show.mkv.sndpart" }, args.TakeLast(3));                                  // the part file has no .mkv ending: say what it is
        Assert.Contains("-progress", args);
        Assert.DoesNotContain("-nostdin", args);                                                                                    // "q" on the standard input ends the recording cleanly
    }

    [Fact]
    public void A_recording_without_any_header_has_no_headers_option()
    {
        var args = Ffmpeg.RecordArguments("https://pull.example/live.flv", "out.part", null, null, null, null);

        Assert.DoesNotContain("-headers", args);
        Assert.DoesNotContain("-user_agent", args);
    }

    [Fact]
    public async Task A_recording_is_a_live_stream_job_saved_as_matroska()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();

        var job = m.Add(new AddDownloadRequest
        {
            Url = "https://pull.example/live.flv?k=1",
            FileName = "Show.mp4",
            Stream = new StreamChoice(Subtitles: false, Record: true),
            Mode = AddMode.Hold,
        });

        Assert.Equal(DownloadKind.Hls, job.Kind);
        Assert.True(job.Hls!.Record);
        Assert.True(job.Hls.Live);
        Assert.Equal("Show.mkv", job.FileName);                                  // whatever the stream carries fits in Matroska
        Assert.True(m.Snapshot(job).Live);
    }

    [Fact]
    public async Task A_recording_without_ffmpeg_fails_with_advice()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.FfmpegPath = Path.Combine(h.Root, "no-such-ffmpeg.exe"));

        var job = m.Add(new AddDownloadRequest
        {
            Url = "https://pull.example/live.flv",
            FileName = "Show.mkv",
            Stream = new StreamChoice(Subtitles: false, Record: true),
        });
        await Harness.Finished(job);

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal(JobErrorKind.Tool, job.ErrorKind);
        Assert.Contains("ffmpeg", job.Error);
    }
}
