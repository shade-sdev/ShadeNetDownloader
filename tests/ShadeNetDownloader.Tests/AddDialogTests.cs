using System.Net;
using ShadeNet.Core;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

public class AddDialogTests
{
    /// <summary>A server that bounces <c>/files/x.zip</c> to an HTML page unless the request carries the session cookie.</summary>
    internal sealed class GatedServer : IDisposable
    {
        readonly HttpListener _listener = new();
        readonly CancellationTokenSource _stop = new();

        public GatedServer()
        {
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public string BaseUrl { get; }

        async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
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

                var path = ctx.Request.Url!.AbsolutePath;
                var cookie = ctx.Request.Headers["Cookie"] ?? "";
                if (path.StartsWith("/files/") && cookie.Contains("session=ok"))
                {
                    ctx.Response.StatusCode = 206;
                    ctx.Response.ContentType = "application/zip";
                    ctx.Response.AddHeader("Content-Range", "bytes 0-0/5000");
                    ctx.Response.AddHeader("Accept-Ranges", "bytes");
                    ctx.Response.OutputStream.WriteByte(0);
                }
                else if (path.StartsWith("/files/"))
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.RedirectLocation = "/landing";
                }
                else
                {
                    ctx.Response.ContentType = "text/html";
                    var html = System.Text.Encoding.UTF8.GetBytes("<html><body>sign in</body></html>");
                    ctx.Response.OutputStream.Write(html);
                }

                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }

    static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "Timed out waiting for: " + what);
    }

    [Fact]
    public async Task A_link_that_returns_a_web_page_asks_for_the_cookie_and_recovers_once_it_is_pasted()
    {
        using var dir = new TempDir();
        using var server = new GatedServer();
        using var manager = new DownloadManager(new ShadeNet.Core.Engine.EngineSettings { DownloadRoot = dir.Path });
        using var vm = new AddDownloadViewModel(manager, null);

        vm.UrlText = server.BaseUrl + "/files/archive.zip";
        await WaitFor(() => vm.ProbeState == ProbeState.Failed, "the probe to fail");

        Assert.Contains("web page", vm.ProbeError);
        Assert.Contains("Cookie", vm.ProbeHint);
        Assert.True(vm.ShowAdvanced, "the cookie field should be opened for the user");

        vm.Cookies = "Cookie: session=ok";
        await WaitFor(() => vm.ProbeState == ProbeState.Ready, "the probe to succeed with the cookie");

        Assert.Equal("archive.zip", vm.FileName);
        var (request, probe) = Assert.Single(vm.Build(ShadeNet.Core.Models.AddMode.Start));
        Assert.Equal("session=ok", request.Cookies);   // "Cookie:" prefix stripped
        Assert.NotNull(probe);
    }
}
