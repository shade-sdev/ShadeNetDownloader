using ShadeNet.Core.Models;
using ShadeNet.Core.Net;

namespace ShadeNet.Core.Tests;

/// <summary>IDM's "Refresh download address" without a browser: find the same file again on the owner page.</summary>
public class RefreshTests
{
    const long Size = 400_000;

    static async Task<(DownloadJob Job, string Page)> ExpiredJob(Harness h, DownloadManager m, params string[] pageLinks)
    {
        var old = h.Server.FileUrl(Size, name: "archive.zip", token: "t-old");
        h.Server.ValidTokens.Remove("t-old");   // the link has expired

        var page = h.Server.AddPage("<html><body>" + string.Join("\n", pageLinks.Select(l => $"<a href=\"{l}\">download</a>"))
                                    + "<img src=\"/logo.png\"><a href=\"/about.html\">about</a></body></html>");
        var job = h.Add(m, old, fileName: "archive.zip", referer: page);
        await Harness.Finished(job);
        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal(JobErrorKind.AddressExpired, job.ErrorKind);
        job.TotalSize = Size;   // a real download that expires mid-way has probed the file before
        return (job, page);
    }

    [Fact]
    public async Task The_same_file_is_found_on_the_owner_page_and_the_download_finishes()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var fresh = h.Server.FileUrl(Size, name: "archive.zip", token: "t-new");
        var (job, _) = await ExpiredJob(h, m, fresh);

        var result = await m.RefreshFromOwnerPageAsync(job.Id);

        Assert.Equal(RefreshOutcome.Found, result.Outcome);
        Assert.Equal(fresh, result.NewUrl);
        await Harness.Finished(job);
        Harness.AssertByteExact(job, Size);
    }

    [Fact]
    public async Task Links_to_other_files_or_with_another_size_are_not_mistaken_for_it()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var otherSize = h.Server.FileUrl(Size + 1, name: "archive.zip", token: "t-a");
        var otherName = h.Server.FileUrl(Size, name: "different.zip", token: "t-b");
        var otherType = h.Server.FileUrl(Size, name: "archive.exe", token: "t-c");
        var right = h.Server.FileUrl(Size, name: "archive.zip", token: "t-d");
        var (job, _) = await ExpiredJob(h, m, otherSize, otherName, otherType, right);

        var result = await m.RefreshFromOwnerPageAsync(job.Id);
        Assert.Equal(RefreshOutcome.Found, result.Outcome);
        Assert.Equal(right, result.NewUrl);
    }

    [Fact]
    public async Task Two_equally_good_candidates_are_reported_as_ambiguous_and_nothing_is_changed()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var a = h.Server.FileUrl(Size, name: "archive.zip", token: "t-1");
        var b = h.Server.FileUrl(Size, name: "archive.zip", token: "t-2");
        var (job, _) = await ExpiredJob(h, m, a, b);
        var before = job.Url;

        var result = await m.RefreshFromOwnerPageAsync(job.Id);

        Assert.Equal(RefreshOutcome.Ambiguous, result.Outcome);
        Assert.Equal(before, job.Url);
        Assert.Equal(DownloadState.Failed, job.State);
    }

    [Fact]
    public async Task A_page_that_no_longer_offers_the_file_says_so()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var (job, _) = await ExpiredJob(h, m, h.Server.FileUrl(Size, name: "unrelated.zip", token: "t-x"));
        Assert.Equal(RefreshOutcome.NotFound, (await m.RefreshFromOwnerPageAsync(job.Id)).Outcome);
    }

    [Fact]
    public async Task A_page_that_still_shows_the_same_address_means_the_session_expired_not_the_link()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var old = h.Server.FileUrl(Size, name: "archive.zip", token: "t-same");
        h.Server.ValidTokens.Remove("t-same");
        var page = h.Server.AddPage($"<a href=\"{old}\">download</a>");
        var job = h.Add(m, old, fileName: "archive.zip", referer: page);
        await Harness.Finished(job);

        Assert.Equal(RefreshOutcome.Unchanged, (await m.RefreshFromOwnerPageAsync(job.Id)).Outcome);
    }

    [Fact]
    public async Task A_download_without_an_owner_page_cannot_be_refreshed()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var url = h.Server.FileUrl(Size, name: "archive.zip", token: "t-n");
        h.Server.ValidTokens.Remove("t-n");
        var job = h.Add(m, url, fileName: "archive.zip");
        await Harness.Finished(job);

        Assert.Equal(RefreshOutcome.NoOwnerPage, (await m.RefreshFromOwnerPageAsync(job.Id)).Outcome);
    }

    [Fact]
    public void Links_are_found_in_attributes_scripts_json_and_meta_refresh_and_resolved_against_the_page()
    {
        var page = new Uri("https://example.com/files/index.html");
        var html = """
            <a href="a.zip">a</a> <a href='/b.zip?x=1&amp;y=2'>b</a>
            <button data-download-url="https://cdn.example.com/c.zip">c</button>
            <meta http-equiv="refresh" content="3; url=/d.zip">
            <script>var u = "https:\/\/cdn.example.com\/e.zip";</script>
            <a href="mailto:x@y.z">m</a> <a href="javascript:void(0)">j</a> <a href="#top">t</a>
            """;

        var links = LinkRefresher.ExtractLinks(html, page);

        Assert.Contains("https://example.com/files/a.zip", links);
        Assert.Contains("https://example.com/b.zip?x=1&y=2", links);
        Assert.Contains("https://cdn.example.com/c.zip", links);
        Assert.Contains("https://example.com/d.zip", links);
        Assert.Contains("https://cdn.example.com/e.zip", links);
        Assert.DoesNotContain(links, l => l.StartsWith("mailto") || l.StartsWith("javascript"));
        Assert.Equal(links.Count, links.Distinct().Count());
    }
}
