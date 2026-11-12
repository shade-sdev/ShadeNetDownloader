using System.IO;
using MonoTorrent;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Torrents;
using ShadeNetDownloader.Util;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

/// <summary>Torrents in the app: links found in pasted text, the Add dialog's file list, and how a torrent's card reads.</summary>
public class TorrentUiTests
{
    static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), "Timed out waiting for: " + what);
    }

    /// <summary>A .torrent file of a folder with a video, its subtitles and a small text file (nobody shares it: it is only read).</summary>
    static async Task<string> MakeTorrentAsync(string dir)
    {
        var source = Path.Combine(dir, "src", "Big Buck Bunny");
        Directory.CreateDirectory(Path.Combine(source, "Subs"));
        File.WriteAllBytes(Path.Combine(source, "movie.mkv"), new byte[300_000]);
        File.WriteAllBytes(Path.Combine(source, "Subs", "en.srt"), new byte[2_000]);
        File.WriteAllBytes(Path.Combine(source, "readme.txt"), new byte[500]);
        var dict = await new TorrentCreator(TorrentType.V1Only).CreateAsync(new TorrentFileSource(source));
        var path = Path.Combine(dir, "bunny.torrent");
        File.WriteAllBytes(path, dict.Encode());
        return path;
    }

    static DownloadManager Manager(TempDir dir) => new(new EngineSettings
    {
        DownloadRoot = Path.Combine(dir.Path, "dl"),
        TorrentFolder = Path.Combine(dir.Path, "torrents"),   // never the user's own
        TorrentListenAddress = System.Net.IPAddress.Loopback,
        TorrentUseDht = false,
        TorrentPortForwarding = false,
        TorrentLocalPeers = false,
    });

    [Fact]
    public void Magnet_links_and_torrent_files_are_found_in_pasted_text()
    {
        var links = UrlParser.Extract(
            "Grab magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Debian, and\n" +
            "C:\\Users\\me\\Downloads\\ubuntu-24.04.torrent\n" +
            "\"D:\\torrents\\with space.torrent\"\n" +
            "https://example.com/a.zip  magnet:?dn=no-identity");

        Assert.Equal(new[]
        {
            "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Debian",
            @"C:\Users\me\Downloads\ubuntu-24.04.torrent",
            @"D:\torrents\with space.torrent",
            "https://example.com/a.zip",
        }, links);
        Assert.True(UrlParser.IsSingleUrl("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"));
    }

    [Fact]
    public async Task The_add_dialog_lists_a_torrents_files_and_adds_only_the_ticked_ones()
    {
        using var dir = new TempDir();
        var torrent = await MakeTorrentAsync(dir.Path);
        using var manager = Manager(dir);
        using var vm = new AddDownloadViewModel(manager, null);

        vm.UrlText = torrent;
        await WaitFor(() => vm.ProbeState == ProbeState.Ready, "the torrent to be read");

        Assert.True(vm.IsTorrent);
        Assert.False(vm.ShowConnections);
        Assert.True(vm.HasTorrentFiles);
        Assert.Equal("Big Buck Bunny", vm.FileName);
        Assert.Equal(3, vm.TorrentFiles.Count);
        Assert.Equal("Video", vm.Category);   // the largest file decides
        Assert.Contains("shares it back", vm.TorrentNote);

        vm.TorrentFiles.Single(f => f.Path.EndsWith("readme.txt")).IsSelected = false;
        Assert.StartsWith("2 of 3 files", vm.TorrentSelectionText);

        var (request, probe) = Assert.Single(vm.Build(AddMode.Hold));
        Assert.Null(probe);
        Assert.NotNull(request.Torrent?.Metadata);
        Assert.Equal(2, request.Torrent!.Files!.Count);

        var job = manager.Add(request);
        Assert.Equal(DownloadKind.Torrent, job.Kind);
        Assert.Equal(302_000, job.TotalSize);
        Assert.Equal(new[] { false, true, true }.OrderBy(x => x), job.Torrent!.Files!.Select(f => f.Selected).OrderBy(x => x));

        vm.SelectNoTorrentFilesCommand.Execute(null);
        Assert.False(vm.CanAdd);   // nothing to download
    }

    [Fact]
    public async Task A_torrent_already_in_the_list_is_named_in_the_dialog()
    {
        using var dir = new TempDir();
        var torrent = await MakeTorrentAsync(dir.Path);
        using var manager = Manager(dir);
        manager.Add(new AddDownloadRequest { Url = torrent, Mode = AddMode.Hold });
        using var vm = new AddDownloadViewModel(manager, null);

        vm.UrlText = torrent;
        await WaitFor(() => vm.ProbeState == ProbeState.Failed, "the dialog to say so");

        Assert.Contains("already in your list", vm.ProbeError);
    }

    [Fact]
    public void A_torrent_card_says_peers_while_downloading_and_sharing_once_finished()
    {
        var job = new DownloadJob { Kind = DownloadKind.Torrent, Url = "magnet:?xt=urn:btih:00", FileName = "Pack", Torrent = new TorrentJobData { IsFolder = true } };
        var downloading = new JobSnapshot(job.Id, DownloadState.Downloading, "Pack", @"C:\dl", 1_000_000, 4_000_000, 250_000, TimeSpan.FromSeconds(12), 5,
            Array.Empty<SegmentInfo>(), true, null, JobErrorKind.None, Torrent: new TorrentStatus(5, 2, 10_000, 0, 0, false, null));
        var item = new DownloadItemViewModel(job, downloading, null!);

        Assert.Equal("BitTorrent", item.Host);
        Assert.Equal("Downloading", item.StatusText);
        Assert.Contains("5 peers (2 sharing all of it)", item.DetailText);
        Assert.Contains("sending", item.DetailText);
        Assert.DoesNotContain("connections", item.DetailText);

        item.Update(downloading with { State = DownloadState.Probing, Torrent = new TorrentStatus(0, 0, 0, 0, 0, false, "Getting the file list from peers…") });
        Assert.Equal("Getting the file list from peers…", item.DetailText);

        item.Update(downloading with { State = DownloadState.Completed, Downloaded = 4_000_000, Torrent = new TorrentStatus(3, 0, 50_000, 2_000_000, 0.5, true, null) });
        Assert.Equal("Sharing", item.StatusText);
        Assert.Contains("shared 0.50× its size", item.DetailText);
        Assert.True(item.IsSeeding);
        Assert.False(item.CanShareAgain);
        Assert.Equal("Open folder", item.PrimaryTooltip);

        item.Update(downloading with { State = DownloadState.Completed, Downloaded = 4_000_000, Torrent = new TorrentStatus(0, 0, 0, 4_000_000, 1, false, null) });
        Assert.Equal("Completed", item.StatusText);
        Assert.False(item.IsSeeding);
        Assert.True(item.CanShareAgain);
    }
}
