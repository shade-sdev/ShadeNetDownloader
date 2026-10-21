using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Tests;

/// <summary>
/// Hosts like gofile only hand out the file when the browser's session cookie is sent; without it they redirect to an
/// HTML page. The engine must not save that page as the file, and must work once the cookie is supplied.
/// </summary>
public class GatedLinkTests
{
    const long Size = 300_000;

    [Fact]
    public async Task A_file_link_answered_with_a_web_page_is_rejected_with_a_clear_error()
    {
        await using var h = await Harness.CreateAsync();
        var url = $"{h.Server.BaseUrl}/gated/{Size}/archive.zip";

        var ex = await Assert.ThrowsAsync<WebPageException>(() => h.NewManager().ProbeAsync(new RequestSpec(url)));
        Assert.Equal(JobErrorKind.WebPage, ex.Kind);
        Assert.Contains("cookies", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_same_link_works_with_the_session_cookie()
    {
        await using var h = await Harness.CreateAsync();
        var url = $"{h.Server.BaseUrl}/gated/{Size}/archive.zip";

        var probe = await h.NewManager().ProbeAsync(new RequestSpec(url, null, "session=ok"));
        Assert.Equal(Size, probe.TotalSize);
        Assert.True(probe.AcceptsRanges);
        Assert.Equal("archive.zip", probe.FileName);
    }

    [Fact]
    public async Task A_job_without_the_cookie_fails_as_WebPage_instead_of_saving_html()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var job = await Harness.Download(h, m, $"{h.Server.BaseUrl}/gated/{Size}/archive.zip");

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal(JobErrorKind.WebPage, job.ErrorKind);
        Assert.Empty(Directory.GetFiles(job.SaveDirectory));
    }

    [Fact]
    public async Task A_job_with_the_cookie_downloads_the_real_file()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var job = h.Add(m, $"{h.Server.BaseUrl}/gated/{Size}/archive.zip", cookies: "session=ok");
        await Harness.Finished(job);
        Harness.AssertByteExact(job, Size);
    }

    [Fact]
    public async Task A_page_that_really_is_html_is_still_downloadable()
    {
        await using var h = await Harness.CreateAsync();
        // The requested name is not a known file type, so an HTML answer is plausible and left alone.
        var probe = await h.NewManager().ProbeAsync(new RequestSpec($"{h.Server.BaseUrl}/landing"));
        Assert.Equal("text/html", probe.ContentType);
    }

    [Theory]
    [InlineData("a=1; b=2", "a=1; b=2")]
    [InlineData("Cookie: a=1; b=2", "a=1; b=2")]
    [InlineData("cookie:a=1;", "a=1")]
    [InlineData("  accountToken=abc123  ", "accountToken=abc123")]
    [InlineData("-H 'cookie: a=1; b=2'", "a=1; b=2")]
    [InlineData("a=1\r\nb=2", "a=1; b=2")]
    [InlineData("Cookie: \"a=1\"", "a=1")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("justavalue", null)]
    public void Cookie_text_pasted_from_a_browser_is_normalised(string input, string? expected) =>
        Assert.Equal(expected, CookieHeader.Normalize(input));
}

/// <summary>Behaviour copied from IDM's link resolution (report §13): cookie jar, first-request reuse.</summary>
public class SessionResolutionTests
{
    const long Size = 300_000;

    [Fact]
    public async Task Set_Cookie_on_a_redirect_is_sent_on_the_next_hop()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        // No cookie supplied: the login redirect creates the session, the gated address needs it.
        var job = h.Add(m, $"{h.Server.BaseUrl}/login/{Size}/archive.zip");
        await Harness.Finished(job);
        Harness.AssertByteExact(job, Size);
    }

    [Fact]
    public async Task A_one_time_link_downloads_because_the_first_response_is_reused()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var url = $"{h.Server.BaseUrl}/once/{Size}/archive.zip";
        var job = h.Add(m, url);
        await Harness.Finished(job);

        Harness.AssertByteExact(job, Size);
        Assert.Equal(1, h.Server.Requests.Count(r => r.PathAndQuery.StartsWith("/once/")));
    }

    [Fact]
    public async Task A_throw_away_probe_spends_a_one_time_link()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var url = $"{h.Server.BaseUrl}/once/{Size}/archive.zip";

        var probe = await m.ProbeAsync(new RequestSpec(url));   // reads the headers and drops the connection
        var job = m.Add(new AddDownloadRequest { Url = url, Mode = AddMode.Start }, probe);
        await Harness.Finished(job);

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal(JobErrorKind.WebPage, job.ErrorKind);
        Assert.False(File.Exists(job.TargetPath), "the web page must never be saved as the file");
        Assert.Equal(0, job.DownloadedBytes);
    }

    [Fact]
    public async Task A_held_probe_hands_its_connection_to_the_download()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var url = $"{h.Server.BaseUrl}/once/{Size}/archive.zip";

        var probe = await m.ProbeAndHoldAsync(new RequestSpec(url));   // what the Add dialog does
        Assert.Equal(Size, probe.TotalSize);
        var job = m.Add(new AddDownloadRequest { Url = url, Mode = AddMode.Start }, probe);
        await Harness.Finished(job);

        Harness.AssertByteExact(job, Size);
        Assert.Equal(1, h.Server.Requests.Count(r => r.PathAndQuery.StartsWith("/once/")));
    }

    [Fact]
    public async Task A_held_probe_that_is_discarded_leaves_nothing_behind()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var probe = await m.ProbeAndHoldAsync(new RequestSpec(h.Server.FileUrl(Size)));
        probe.Discard();
        probe.Discard();   // idempotent
    }

    [Fact]
    public async Task A_stale_held_probe_falls_back_to_a_fresh_request()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.HeldProbeLifetime = TimeSpan.Zero);
        var url = h.Server.FileUrl(Size);
        var probe = await m.ProbeAndHoldAsync(new RequestSpec(url));
        await Task.Delay(50);
        var job = m.Add(new AddDownloadRequest { Url = url, Mode = AddMode.Start }, probe);
        await Harness.Finished(job);
        Harness.AssertByteExact(job, Size);
    }

    [Fact]
    public async Task A_refreshed_download_continues_on_the_first_response_from_its_first_missing_byte()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(connections: 1);
        var url = h.Server.FileUrl(2_000_000, kbps: 1024);
        var job = h.Add(m, url, connections: 1);
        await Harness.ReachAsync(job, 0.3);
        await Harness.PauseAsync(m, job);
        var before = h.Server.Requests.Count;

        m.RefreshAddress(job.Id, url);   // probes again, from the first missing byte, and keeps that response
        await Harness.Finished(job);

        Harness.AssertByteExact(job, 2_000_000);
        Assert.Equal(before + 1, h.Server.Requests.Count);   // one request carried the rest of the file
    }
}

public class SessionCookiesTests
{
    static Uri U(string s) => new(s);

    [Fact]
    public void Captured_cookies_reach_other_hosts_of_the_same_site_but_never_another_site()
    {
        var jar = new SessionCookies("accountToken=abc", "file-ap-sgp-3.gofile.io");
        Assert.Equal("accountToken=abc", jar.HeaderFor(U("https://file-ap-sgp-3.gofile.io/download/x.zip")));
        Assert.Equal("accountToken=abc", jar.HeaderFor(U("https://gofile.io/d/xyz")));
        Assert.Equal("accountToken=abc", jar.HeaderFor(U("https://store9.gofile.io/a")));
        Assert.Null(jar.HeaderFor(U("https://evil-gofile.io/a")));
        Assert.Null(jar.HeaderFor(U("https://cdn.example.com/a")));
    }

    [Fact]
    public void Cookies_captured_for_an_ip_address_stay_on_that_address()
    {
        var jar = new SessionCookies("a=1", "127.0.0.1");
        Assert.Equal("a=1", jar.HeaderFor(U("http://127.0.0.1:5000/f")));
        Assert.Null(jar.HeaderFor(U("http://localhost:5000/f")));
    }

    [Fact]
    public void Set_Cookie_domain_path_secure_and_expiry_are_applied()
    {
        var jar = new SessionCookies(null, "www.example.com");
        jar.Store(U("https://www.example.com/login"), new[]
        {
            "site=1; Domain=example.com; Path=/",
            "narrow=2; Path=/files",
            "tls=3; Secure",
            "host=4",
        });

        Assert.Equal(new[] { "host=4", "site=1", "tls=3" },
            jar.HeaderFor(U("https://www.example.com/x"))!.Split("; ").OrderBy(x => x).ToArray());
        Assert.Contains("narrow=2", jar.HeaderFor(U("https://www.example.com/files/a.zip"))!);
        Assert.DoesNotContain("narrow=2", jar.HeaderFor(U("https://www.example.com/other"))!);
        Assert.DoesNotContain("tls=3", jar.HeaderFor(U("http://www.example.com/x"))!);   // Secure only over https
        Assert.Equal("site=1", jar.HeaderFor(U("https://dl.example.com/x")));            // Domain cookie reaches sub-domains, host-only does not

        jar.Store(U("https://www.example.com/logout"), new[] { "site=; Domain=example.com; Path=/; Max-Age=0" });
        Assert.DoesNotContain("site=", jar.HeaderFor(U("https://www.example.com/x"))!);
    }

    [Fact]
    public void A_server_cannot_set_cookies_for_another_site_or_a_public_suffix()
    {
        var jar = new SessionCookies(null, "a.example.com");
        jar.Store(U("https://a.example.com/"), new[]
        {
            "x=1; Domain=other.com", "y=2; Domain=com", "z=3; Domain=co.uk",
        });
        Assert.True(jar.IsEmpty);
    }

    [Fact]
    public void A_newer_cookie_with_the_same_name_replaces_the_older_one()
    {
        var jar = new SessionCookies("t=old", "example.com");
        jar.Store(U("https://example.com/"), new[] { "t=new; Domain=example.com; Path=/" });
        Assert.Equal("t=new", jar.HeaderFor(U("https://example.com/x")));
    }

    [Theory]
    [InlineData("file-ap-sgp-3.gofile.io", "gofile.io")]
    [InlineData("gofile.io", "gofile.io")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("www.example.com.au", "example.com.au")]
    [InlineData("localhost", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    public void The_registrable_domain_is_the_site_not_the_host(string host, string expected) =>
        Assert.Equal(expected, HostNames.RegistrableDomain(host));
}
