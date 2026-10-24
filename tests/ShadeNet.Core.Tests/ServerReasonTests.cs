using System.Text;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;

namespace ShadeNet.Core.Tests;

/// <summary>A refusing server usually says why (S3, Google Cloud Storage, Azure, JSON APIs): that reason is part of the error shown.</summary>
public class ServerReasonTests
{
    [Theory]
    [InlineData("<?xml version='1.0' encoding='UTF-8'?><Error><Code>AccessDenied</Code><Message>Access denied.</Message><Details>Anonymous caller does not have storage.objects.get access to the Google Cloud Storage object.</Details></Error>",
        "AccessDenied: Anonymous caller does not have storage.objects.get access to the Google Cloud Storage object.")]
    [InlineData("<Error><Code>ExpiredToken</Code><Message>The provided token has expired.</Message></Error>", "ExpiredToken: The provided token has expired.")]
    [InlineData("<Error><Code>SignatureDoesNotMatch</Code></Error>", "SignatureDoesNotMatch")]
    [InlineData("{\"error\":{\"code\":403,\"message\":\"The caller does not have permission\"}}", "The caller does not have permission")]
    [InlineData("{\"error\":\"invalid_token\",\"error_description\":\"Token expired\"}", "Token expired")]
    [InlineData("Request has expired\n", "Request has expired")]
    [InlineData("<html><body><h1>403 Forbidden</h1></body></html>", null)]
    [InlineData("", null)]
    public void The_reason_is_read_from_the_usual_error_bodies(string body, string? expected) => Assert.Equal(expected, HttpFetcher.ReasonFrom(body));

    [Fact]
    public void The_servers_words_end_as_a_sentence()
    {
        Assert.Equal("The server answered 403 Forbidden. It said: UserProjectAccountProblem: The billing account is disabled in state absent.",
            new Engine.HttpStatusException(403, serverReason: "UserProjectAccountProblem: The billing account is disabled in state absent").Message);
        Assert.Equal("The server answered 403 Forbidden. It said: Access denied.", new Engine.HttpStatusException(403, serverReason: "Access denied.").Message);
        Assert.Equal("The server answered 403 Forbidden.", new Engine.HttpStatusException(403).Message);
    }

    [Fact]
    public async Task A_refused_stream_says_what_the_server_said()
    {
        await using var h = await Harness.CreateAsync();
        using var server = new HlsServer();
        server.AddVod("v", 3);
        server.Intercept = ctx =>
        {
            if (!ctx.Request.Url!.AbsolutePath.EndsWith(".m3u8")) return false;
            var body = Encoding.UTF8.GetBytes("<?xml version='1.0' encoding='UTF-8'?><Error><Code>AccessDenied</Code><Message>Access denied.</Message></Error>");
            ctx.Response.StatusCode = 403;
            ctx.Response.ContentType = "application/xml; charset=UTF-8";
            ctx.Response.OutputStream.Write(body);
            ctx.Response.Close();
            return true;
        };
        var m = h.NewManager(tweak: s => s.FfmpegPath = Path.Combine(h.Root, "no-such-ffmpeg.exe"));

        var job = m.Add(new AddDownloadRequest { Url = server.Url("/v/index.m3u8"), FileName = "clip.mp4", Stream = new StreamChoice() });
        await Harness.Finished(job);

        Assert.Equal(DownloadState.Failed, job.State);
        Assert.Equal("The server answered 403 Forbidden. It said: AccessDenied: Access denied.", job.Error);
    }
}
