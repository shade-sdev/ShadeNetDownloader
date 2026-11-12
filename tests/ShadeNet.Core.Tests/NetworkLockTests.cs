using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Sites;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

/// <summary>
/// One network adapter for everything (a VPN's): while it is not connected nothing goes out and downloads wait (they do not fail,
/// and spend no retry); once it is there they carry on through it. The loopback adapter stands in for the VPN (the test server is
/// on it); an id no adapter has stands for a VPN that is down.
/// </summary>
public class NetworkLockTests
{
    const string Gone = "{0BADC0DE-0000-4000-8000-0000000000AA}";

    static string Loopback() => NetworkInterface.GetAllNetworkInterfaces().First(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback).Id;

    [Fact]
    public async Task A_download_waits_for_the_adapter_without_failing_and_finishes_through_it_once_it_is_there()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s =>
        {
            s.NetworkAdapter = Gone;
            s.NetworkAdapterName = "Test VPN";
            s.MaxRetries = 2;   // (a wait that spent retries would fail quickly)
        });
        JobSnapshot? last = null;
        m.JobUpdated += s => last = s;
        var size = 3 * MiB;

        var job = h.Add(m, h.Server.FileUrl(size, name: "locked.bin"));
        await WaitUntil(() => last?.Waiting != null, 15, "the download to say it waits", () => Describe(job));
        Assert.Contains("Waiting for Test VPN", last!.Waiting);

        await Task.Delay(4000);   // longer than every retry would take
        Assert.NotEqual(DownloadState.Failed, job.State);
        Assert.Equal(0, h.Server.RequestCount);   // nothing left the computer
        Assert.Equal(0, job.Stats.Retries);

        // the VPN connects
        m.Settings.NetworkAdapter = Loopback();
        m.ApplySettings();
        await Finished(job, 30);
        AssertByteExact(job, size);
        Assert.Null(last!.Waiting);
    }

    [Fact]
    public async Task Bound_to_a_connected_adapter_downloads_go_through_it()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager(tweak: s => s.NetworkAdapter = Loopback());

        var job = await Download(h, m, h.Server.FileUrl(2 * MiB, name: "bound.bin"));
        AssertByteExact(job, 2 * MiB);
        Assert.True(NetworkLock.Resolve(m.Settings) is { Locked: true, Offline: false, V4: { } v4 } && IPAddress.IsLoopback(v4));
    }

    [Fact]
    public async Task A_connection_to_a_site_the_adapter_cannot_reach_is_refused_rather_than_sent_elsewhere()
    {
        var binding = new NetworkBinding(true, "Test VPN", null, null);
        var ex = await Assert.ThrowsAsync<NetworkAdapterOfflineException>(() => NetworkLock.ConnectAsync("127.0.0.1", 80, System.Net.Sockets.AddressFamily.Unspecified, binding, CancellationToken.None));
        Assert.Contains("Test VPN is not connected", ex.Message);

        // an adapter with only IPv4 is not used for an IPv6 address (and nothing else is)
        var v4Only = new NetworkBinding(true, "Test VPN", IPAddress.Loopback, null);
        await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(() => NetworkLock.ConnectAsync("::1", 80, System.Net.Sockets.AddressFamily.Unspecified, v4Only, CancellationToken.None));
    }

    [Fact]
    public async Task The_relay_for_ffmpeg_goes_through_the_adapter_and_refuses_while_it_is_gone()
    {
        await using var h = await Harness.CreateAsync();
        var settings = h.Settings(tweak: s => s.NetworkAdapter = Loopback());
        await using var relay = LocalRelay.Start(settings);
        // (.NET never proxies 127.0.0.1 itself: the relay is asked by hand, as ffmpeg asks it)
        using var raw = new System.Net.Sockets.TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, new Uri(relay.Address).Port);
        var stream = raw.GetStream();
        var url = h.Server.FileUrl(100_000, name: "via-relay.bin");
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"GET {url} HTTP/1.1\r\nHost: {new Uri(url).Authority}\r\nConnection: keep-alive\r\n\r\n"));
        using var reader = new MemoryStream();
        await stream.CopyToAsync(reader);   // (one request per connection: the relay closes it after)
        var answer = System.Text.Encoding.Latin1.GetString(reader.ToArray());
        Assert.StartsWith("HTTP/1.1 200", answer);
        Assert.Equal(1, h.Server.RequestCount);

        // the VPN goes down: the relay refuses
        settings.NetworkAdapter = Gone;
        NetworkLock.Invalidate();
        using var second = new System.Net.Sockets.TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, new Uri(relay.Address).Port);
        await second.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes($"CONNECT {new Uri(url).Authority} HTTP/1.1\r\n\r\n"));
        var buffer = new byte[64];
        var n = await second.GetStream().ReadAsync(buffer);
        Assert.StartsWith("HTTP/1.1 502", System.Text.Encoding.ASCII.GetString(buffer, 0, n));
        Assert.Equal(1, relay.Refused);
        Assert.Equal(1, h.Server.RequestCount);
    }

    [Fact]
    public void yt_dlp_is_given_the_adapters_address_and_ShadeNets_proxy_and_is_not_started_while_the_adapter_is_gone()
    {
        var bound = new EngineSettings { NetworkAdapter = Loopback() };
        NetworkLock.Invalidate();
        Assert.Equal(new[] { "--source-address", "127.0.0.1" }, YtDlp.NetworkArguments(bound).Take(2));

        var proxied = new EngineSettings { Proxy = ProxyMode.Manual, ProxyAddress = "socks5://u:p@10.0.0.2:1080" };
        Assert.Equal(new[] { "--proxy", "socks5://u:p@10.0.0.2:1080" }, YtDlp.NetworkArguments(proxied));
        Assert.Equal(new[] { "--proxy", "" }, YtDlp.NetworkArguments(new EngineSettings { Proxy = ProxyMode.None }));
        Assert.Empty(YtDlp.NetworkArguments(new EngineSettings()));

        var gone = new EngineSettings { NetworkAdapter = Gone, NetworkAdapterName = "Test VPN" };
        var ex = Assert.Throws<DownloadException>(() => YtDlp.NetworkArguments(gone));
        Assert.Contains("Test VPN is not connected", ex.Message);
    }

    [Fact]
    public void The_adapter_chosen_for_torrents_in_an_earlier_version_now_holds_for_everything()
    {
        var old = JsonSerializer.Deserialize<EngineSettings>("""{"TorrentNetworkAdapter":"{ABC}","TorrentNetworkAdapterName":"Proton VPN"}""")!;
        old.Normalize();
        Assert.Equal("{ABC}", old.NetworkAdapter);
        Assert.Equal("Proton VPN", old.NetworkAdapterName);
        var saved = JsonSerializer.Serialize(old);
        Assert.DoesNotContain("TorrentNetworkAdapter", saved);
        Assert.Contains("\"NetworkAdapter\":\"{ABC}\"", saved);
    }
}
