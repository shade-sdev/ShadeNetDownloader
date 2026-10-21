using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

public class ProbeTests
{
    [Fact]
    public async Task Detects_size_ranges_validators_and_name()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var r = await m.ProbeAsync(new RequestSpec(h.Server.FileUrl(5 * MiB, name: "data set.bin", etag: "v7")));

        Assert.Equal(206, r.StatusCode);
        Assert.Equal(5 * MiB, r.TotalSize);
        Assert.True(r.AcceptsRanges);
        Assert.Equal("\"v7\"", r.ETag);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), r.LastModified);
        Assert.Equal("data set.bin", r.FileName);
        Assert.Equal("application/octet-stream", r.ContentType);
    }

    [Fact]
    public async Task A_server_that_ignores_range_is_not_resumable_but_the_size_is_known()
    {
        await using var h = await Harness.CreateAsync();
        var r = await h.NewManager().ProbeAsync(new RequestSpec(h.Server.FileUrl(3 * MiB, ranges: false)));
        Assert.Equal(200, r.StatusCode);
        Assert.Equal(3 * MiB, r.TotalSize);
        Assert.False(r.AcceptsRanges);
    }

    [Fact]
    public async Task An_empty_file_has_size_zero()
    {
        await using var h = await Harness.CreateAsync();
        var r = await h.NewManager().ProbeAsync(new RequestSpec(h.Server.FileUrl(0)));
        Assert.Equal(0, r.TotalSize);
        Assert.False(r.AcceptsRanges);
    }

    [Fact]
    public async Task Unknown_size_is_reported_as_null()
    {
        await using var h = await Harness.CreateAsync();
        var r = await h.NewManager().ProbeAsync(new RequestSpec(h.Server.ChunkedUrl(100_000, "stream.bin")));
        Assert.Null(r.TotalSize);
        Assert.False(r.AcceptsRanges);
        Assert.Equal("stream.bin", r.FileName);
    }

    [Fact]
    public async Task Rfc5987_filenames_are_decoded()
    {
        await using var h = await Harness.CreateAsync();
        var r = await h.NewManager().ProbeAsync(new RequestSpec(h.Server.FileUrl(10, nameStar: "résumé 日本.txt")));
        Assert.Equal("résumé 日本.txt", r.FileName);
    }

    [Theory]
    [InlineData("..\\..\\evil:name?.exe", "evil_name_.exe")]
    [InlineData("CON", "_CON")]
    [InlineData("trailing.dots...", "trailing.dots")]
    public async Task Hostile_server_supplied_names_are_made_safe(string served, string expected)
    {
        await using var h = await Harness.CreateAsync();
        var r = await h.NewManager().ProbeAsync(new RequestSpec(h.Server.FileUrl(10, name: served)));
        Assert.Equal(expected, r.FileName);
    }

    [Fact]
    public async Task Without_a_header_the_name_comes_from_the_url()
    {
        await using var h = await Harness.CreateAsync();
        var r = await h.NewManager().ProbeAsync(new RequestSpec(h.Server.BaseUrl + "/file/10"));
        Assert.Equal("10", r.FileName);
    }

    [Fact]
    public async Task Redirects_are_followed_and_the_final_url_is_reported()
    {
        await using var h = await Harness.CreateAsync();
        var target = h.Server.FileUrl(1000, name: "moved.bin");
        var hop2 = $"{h.Server.BaseUrl}/redir?code=307&to={Uri.EscapeDataString(target)}";
        var hop1 = $"{h.Server.BaseUrl}/redir?code=301&to={Uri.EscapeDataString(hop2)}";

        var r = await h.NewManager().ProbeAsync(new RequestSpec(hop1));
        Assert.Equal(target, r.FinalUrl);
        Assert.Equal("moved.bin", r.FileName);
        Assert.Equal(1000, r.TotalSize);
    }

    [Fact]
    public async Task An_endless_redirect_chain_is_reported_instead_of_hanging()
    {
        await using var h = await Harness.CreateAsync();
        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            h.NewManager().ProbeAsync(new RequestSpec(h.Server.BaseUrl + "/loop")));
        Assert.Contains("redirect", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(404, JobErrorKind.AddressExpired)]
    [InlineData(403, JobErrorKind.AddressExpired)]
    [InlineData(410, JobErrorKind.AddressExpired)]
    [InlineData(401, JobErrorKind.HttpStatus)]
    public async Task Http_errors_surface_with_a_useful_kind(int status, JobErrorKind kind)
    {
        await using var h = await Harness.CreateAsync();
        var ex = await Assert.ThrowsAsync<HttpStatusException>(() =>
            h.NewManager().ProbeAsync(new RequestSpec($"{h.Server.BaseUrl}/status/{status}")));
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task Sends_user_agent_referer_cookie_and_custom_headers()
    {
        await using var h = await Harness.CreateAsync();
        await h.NewManager().ProbeAsync(new RequestSpec(h.Server.FileUrl(10), "https://ref.example/page", "sid=42", "MyAgent/9",
            new Dictionary<string, string> { ["X-Token"] = "t" }));
        var req = Assert.Single(h.Server.Requests);
        Assert.Equal("MyAgent/9", req.UserAgent);
        Assert.Equal("https://ref.example/page", req.Referer);
        Assert.Equal("sid=42", req.Cookie);
        Assert.Equal("bytes=0-0", req.Range);
    }
}

public class BasicDownloadTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(65536)]
    [InlineData(1_048_577)]
    public async Task Small_and_awkward_sizes_are_byte_exact(long size)
    {
        await using var h = await Harness.CreateAsync();
        var job = await Download(h, h.NewManager(), h.Server.FileUrl(size, name: "f.bin"));
        AssertByteExact(job, size);
        Assert.False(File.Exists(job.PartPath));
    }

    [Fact]
    public async Task A_server_without_ranges_still_downloads_correctly()
    {
        await using var h = await Harness.CreateAsync();
        var job = await Download(h, h.NewManager(), h.Server.FileUrl(3 * MiB, ranges: false), connections: 8);
        AssertByteExact(job, 3 * MiB);
        Assert.Equal(1, job.Stats.PeakConnections);
    }

    [Fact]
    public async Task Unknown_length_streams_are_downloaded_to_the_end_and_the_size_learned()
    {
        await using var h = await Harness.CreateAsync();
        var job = await Download(h, h.NewManager(), h.Server.ChunkedUrl(1_500_000, "stream.bin"));
        AssertByteExact(job, 1_500_000);
        Assert.Equal(1_500_000, job.TotalSize);
        Assert.Equal(1, job.Stats.PeakConnections);
    }

    [Fact]
    public async Task The_server_timestamp_is_applied_to_the_file()
    {
        await using var h = await Harness.CreateAsync();
        var job = await Download(h, h.NewManager(), h.Server.FileUrl(1000, name: "t.bin"));
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(job.TargetPath));
    }

    [Fact]
    public async Task Mark_of_the_web_is_written_with_referrer_and_url()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.WriteMarkOfTheWeb = true);
        var job = h.Add(m, h.Server.FileUrl(1000, name: "motw.bin"), referer: "https://page.example/dl");
        await Finished(job);

        var zone = File.ReadAllText(job.TargetPath + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", zone);
        Assert.Contains("ReferrerUrl=https://page.example/dl", zone);
        Assert.Contains("HostUrl=" + h.Server.BaseUrl, zone);
    }

    [Fact]
    public async Task Session_data_is_wiped_when_the_download_completes()
    {
        await using var h = await Harness.CreateAsync();
        var job = h.Add(h.NewManager(), h.Server.FileUrl(1000), cookies: "sid=secret", headers: new() { ["X-A"] = "1" });
        await Finished(job);
        Assert.Null(job.Cookies);
        Assert.Empty(job.Headers);
    }

    [Fact]
    public async Task Name_and_folder_are_refined_after_the_first_probe_when_they_were_only_guessed()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(h.Dir(), tweak: s => s.UseCategoryFolders = true);
        // Added without a probe (as the browser extension does): the URL path says "10", the server says movie.mp4.
        var job = h.Add(m, h.Server.FileUrl(1000, name: "movie.mp4"));
        await Finished(job);

        Assert.Equal("movie.mp4", job.FileName);
        Assert.Equal("Video", job.Category);
        Assert.EndsWith(Path.Combine("dl", "Video"), job.SaveDirectory);
        Assert.True(File.Exists(job.TargetPath));
    }

    [Fact]
    public async Task Headers_cookie_and_user_agent_reach_every_connection()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        // Slow enough that the engine adds connections after the first request, which is also the first connection.
        var job = h.Add(m, h.Server.FileUrl(6 * MiB, kbps: 1024), 4, userAgent: "UA-Test/3", cookies: "sid=7", referer: "https://r.example/");
        await Finished(job);
        AssertByteExact(job, 6 * MiB);

        var requests = h.Server.Requests;
        Assert.True(requests.Count >= 2);
        Assert.All(requests, r =>
        {
            Assert.Equal("UA-Test/3", r.UserAgent);
            Assert.Equal("sid=7", r.Cookie);
            Assert.Equal("https://r.example/", r.Referer);
        });
    }
}
