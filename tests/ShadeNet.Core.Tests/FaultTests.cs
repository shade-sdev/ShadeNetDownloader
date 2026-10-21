using System.Diagnostics;
using ShadeNet.Core.Models;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

/// <summary>What happens when the network or the server misbehaves.</summary>
public class FaultTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Connections_dropped_mid_stream_are_retried_without_losing_data(int connections)
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var url = h.Server.FileUrl(4 * MiB, name: "drop.bin", dropAfter: 700 * KiB, drops: 3);
        var job = await Download(h, m, url, connections);

        AssertByteExact(job, 4 * MiB);
        Assert.True(job.Stats.Retries >= 1, $"expected retries, got {job.Stats.Retries}");
    }

    [Fact]
    public async Task A_connection_that_goes_silent_is_abandoned_and_retried()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.StallTimeout = TimeSpan.FromSeconds(1));
        var url = h.Server.FileUrl(2 * MiB, name: "stall.bin", stallAfter: 512 * KiB, stalls: 1);
        var sw = Stopwatch.StartNew();
        var job = await Download(h, m, url, 1);

        AssertByteExact(job, 2 * MiB);
        Assert.True(job.Stats.Retries >= 1);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.9, 15);
    }

    [Fact]
    public async Task Retry_after_is_honoured_for_503_responses()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        // The probe consumes two failures (ranged + plain attempt), then the first retry succeeds.
        var url = h.Server.FileUrl(1 * MiB, name: "busy.bin", failStatus: 503, failCount: 2, retryAfter: 2);
        var sw = Stopwatch.StartNew();
        var job = await Download(h, m, url, 2);

        AssertByteExact(job, 1 * MiB);
        Assert.True(sw.Elapsed.TotalSeconds >= 1.8, $"finished in {sw.Elapsed.TotalSeconds:0.0}s, Retry-After was ignored");
        Assert.True(job.Stats.Retries >= 1);
    }

    [Fact]
    public async Task Rate_limiting_429_during_a_multi_connection_download_backs_off_and_completes()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.RampUpInterval = TimeSpan.FromMilliseconds(300));
        // The first 4 requests (probe included) are answered with 429 + Retry-After, then everything works.
        var url = h.Server.FileUrl(8 * MiB, name: "limited.bin", kbps: 2048, failStatus: 429, failCount: 4, retryAfter: 1);
        var job = await Download(h, m, url, 6, seconds: 90);

        AssertByteExact(job, 8 * MiB);
        Assert.True(job.Stats.Retries >= 1);
    }

    [Fact]
    public async Task A_404_fails_fast_without_retrying()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var sw = Stopwatch.StartNew();
        var job = h.Add(m, $"{h.Server.BaseUrl}/status/404", fileName: "gone.bin");
        await Finished(job, 10);

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal(JobErrorKind.AddressExpired, job.ErrorKind);
        Assert.Contains("404", job.Error);
        Assert.True(sw.Elapsed.TotalSeconds < 3);
        Assert.Equal(0, job.Stats.Retries);
    }

    [Fact]
    public async Task A_401_is_reported_as_an_http_error_and_not_retried()
    {
        await using var h = await Harness.CreateAsync();
        var job = h.Add(h.NewManager(), $"{h.Server.BaseUrl}/status/401", fileName: "private.bin");
        await Finished(job, 10);
        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal(JobErrorKind.HttpStatus, job.ErrorKind);
        Assert.Equal(0, job.Stats.Retries);
    }

    [Fact]
    public async Task A_server_that_keeps_failing_eventually_gives_up_with_a_clear_error()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s =>
        {
            s.MaxRetries = 3;
            s.RetryBaseDelay = TimeSpan.FromMilliseconds(50);
            s.RetryMaxDelay = TimeSpan.FromMilliseconds(100);
        });
        var job = h.Add(m, h.Server.FileUrl(1 * MiB, failStatus: 500, failCount: 1000), 1);
        await Finished(job, 30);

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.False(string.IsNullOrWhiteSpace(job.Error));
        Assert.Contains("500", job.Error);
    }

    [Fact]
    public async Task A_dead_host_fails_quickly_instead_of_retrying_for_minutes()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.ConnectTimeout = TimeSpan.FromSeconds(3));
        var sw = Stopwatch.StartNew();
        var job = h.Add(m, "http://127.0.0.1:1/never.bin", fileName: "never.bin");
        await Finished(job, 30);
        Assert.Equal(DownloadState.Failed, job.State);
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"took {sw.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public async Task Wrong_content_range_makes_the_engine_fall_back_to_one_connection()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.RampUpInterval = TimeSpan.FromMilliseconds(400));
        var url = h.Server.FileUrl(8 * MiB, name: "badrange.bin", kbps: 2048, badRange: true);
        var job = await Download(h, m, url, 4, seconds: 90);

        AssertByteExact(job, 8 * MiB);
        Assert.Equal(1, job.Stats.Fallbacks);
        Assert.False(job.AcceptsRanges);
    }

    [Fact]
    public async Task Redirect_chains_are_followed_for_the_transfer_too()
    {
        await using var h = await Harness.CreateAsync();
        var target = h.Server.FileUrl(2 * MiB, name: "final.bin");
        var url = $"{h.Server.BaseUrl}/redir?code=302&to={Uri.EscapeDataString($"{h.Server.BaseUrl}/redir?code=307&to={Uri.EscapeDataString(target)}")}";
        var job = await Download(h, h.NewManager(), url);

        AssertByteExact(job, 2 * MiB);
        Assert.Equal("final.bin", job.FileName);
        Assert.Equal(target, job.FinalUrl);
    }

    [Fact]
    public async Task Cookies_are_not_sent_to_a_different_host_after_a_redirect()
    {
        await using var h = await Harness.CreateAsync();
        var target = $"{h.Server.AltBaseUrl}/file/2000?name=cross.bin&u=1";
        var url = $"{h.Server.BaseUrl}/redir?code=302&to={Uri.EscapeDataString(target)}";
        var job = h.Add(h.NewManager(), url, 2, cookies: "sid=secret", headers: new() { ["X-Private"] = "1" }, referer: "https://page.example/");
        await Finished(job);
        AssertByteExact(job, 2000);

        var origin = h.Server.Requests.Where(r => r.Host == "127.0.0.1").ToList();
        var foreign = h.Server.Requests.Where(r => r.Host == "localhost").ToList();
        Assert.NotEmpty(origin);
        Assert.NotEmpty(foreign);
        Assert.All(origin, r => Assert.Equal("sid=secret", r.Cookie));
        Assert.All(foreign, r => Assert.Null(r.Cookie)); // the secret never leaves the origin host
        Assert.All(foreign, r => Assert.Equal("https://page.example/", r.Referer));
    }

    [Fact]
    public async Task Adding_with_an_unusable_save_folder_is_rejected_immediately()
    {
        await using var h = await Harness.CreateAsync();
        var blocker = Path.Combine(h.Root, "iam-a-file");
        File.WriteAllText(blocker, "x");
        var m = h.NewManager();
        Assert.ThrowsAny<IOException>(() => m.Add(new AddDownloadRequest
        {
            Url = h.Server.FileUrl(1000),
            SaveDirectory = Path.Combine(blocker, "sub"),
        }));
        Assert.Empty(m.Jobs);
    }

    [Fact]
    public async Task A_folder_that_disappears_before_the_start_fails_the_job_with_a_disk_error()
    {
        await using var h = await Harness.CreateAsync();
        var dir = h.Dir("doomed");
        var m = h.NewManager(dir);
        var job = h.Add(m, h.Server.FileUrl(1000), mode: AddMode.Hold);

        // Replace the folder by a file so the runner cannot create it.
        Directory.Delete(dir, true);
        File.WriteAllText(dir, "now I am a file");

        m.Start(job.Id);
        await Finished(job, 10);
        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal(JobErrorKind.Disk, job.ErrorKind);
    }

    [Fact]
    public async Task Rejects_schemes_other_than_http_and_https()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        foreach (var bad in new[] { "ftp://example.com/a.bin", "file:///c:/windows/notepad.exe", "javascript:alert(1)", "not a url" })
            Assert.Throws<ArgumentException>(() => m.Add(new AddDownloadRequest { Url = bad }));
        Assert.Empty(m.Jobs);
    }
}
