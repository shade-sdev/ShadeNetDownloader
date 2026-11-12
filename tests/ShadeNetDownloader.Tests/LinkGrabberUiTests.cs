using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using ShadeNet.Core;
using ShadeNet.Core.Archives;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Links;
using ShadeNet.Core.Models;
using ShadeNet.Core.Storage;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Tests;

/// <summary>The link grabber's list (narrowed, chosen, added) and a finished archive's Extract on its card.</summary>
public class LinkGrabberUiTests
{
    static EngineSettings Settings(string dir) => new()
    {
        DownloadRoot = Path.Combine(dir, "dl"),
        UseCategoryFolders = false,
        TorrentFolder = Path.Combine(dir, "torrents"),
        TorrentListenAddress = System.Net.IPAddress.Loopback,
        TorrentUseDht = false,
        TorrentPortForwarding = false,
        TorrentLocalPeers = false,
    };

    static GrabbedPage Page() => new("https://site.example/holiday", "Holiday photos", new[]
    {
        new GrabbedLink("https://files.example/photos-1.zip", "First part"),
        new GrabbedLink("https://files.example/photos-2.zip", null),
        new GrabbedLink("https://cdn.example/film.mp4", "The film"),
        new GrabbedLink("https://site.example/about", "About us"),
        new GrabbedLink("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Debian", "Debian"),
    });

    [Fact]
    public void The_grabber_lists_the_files_narrows_them_and_adds_the_chosen_ones_with_the_sites_sign_in()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(Settings(dir.Path));
        var vm = new LinkGrabberViewModel(manager);
        vm.Show(Page(), new Dictionary<string, string> { ["files.example"] = "sid=42" }, "Test-UA");

        Assert.Equal("Holiday photos", vm.PageTitle);
        Assert.Equal(4, vm.Shown.Count);   // files only: the "About us" page is left out
        Assert.Contains(vm.Kinds, k => k.Name == "Compressed" && k.Count == 2);
        Assert.False(vm.CanAdd);

        vm.OnlyFiles = false;
        Assert.Equal(5, vm.Shown.Count);
        vm.OnlyFiles = true;
        vm.Filter = "photos";
        Assert.Equal(new[] { "photos-1.zip", "photos-2.zip" }, vm.Shown.Select(r => r.Name));
        vm.SelectShownCommand.Execute(null);
        Assert.Equal(2, vm.SelectedCount);
        Assert.Equal("Download 2 files", vm.PrimaryText);

        vm.Filter = "";
        vm.Kinds.Single(k => k.Name == "Compressed").IsOn = false;   // the zips hidden: still chosen
        Assert.DoesNotContain(vm.Shown, r => r.Name.EndsWith(".zip"));
        vm.Shown.Single(r => r.Category == "Torrents").IsSelected = true;

        vm.IntoFolder = true;
        var requests = vm.Build(AddMode.Queue);
        Assert.Equal(3, requests.Count);
        var zip = requests.First(r => r.Url.EndsWith("photos-1.zip"));
        Assert.Equal("sid=42", zip.Cookies);
        Assert.Equal("https://site.example/holiday", zip.Referer);
        Assert.Equal("Test-UA", zip.UserAgent);
        Assert.Equal(Path.Combine(manager.Settings.DownloadRoot, "Holiday photos"), zip.SaveDirectory);
        Assert.Equal(AddMode.Queue, zip.Mode);
        var magnet = requests.Single(r => r.Url.StartsWith("magnet:"));
        Assert.NotNull(magnet.Torrent);
        Assert.Null(magnet.Cookies);

        vm.SelectNoneCommand.Execute(null);
        Assert.False(vm.CanAdd);
    }

    [Fact]
    public void A_page_with_no_file_shows_its_links_and_one_with_no_link_says_so()
    {
        using var dir = new TempDir();
        using var manager = new DownloadManager(Settings(dir.Path));
        var vm = new LinkGrabberViewModel(manager);
        vm.Show(new GrabbedPage("https://site.example/", null, new[] { new GrabbedLink("https://site.example/a", "A") }), null, null);
        Assert.False(vm.OnlyFiles);
        Assert.Single(vm.Shown);
        Assert.Equal("site.example", vm.PageTitle);

        vm.Show(new GrabbedPage("https://site.example/", "Empty", Array.Empty<GrabbedLink>()), null, null);
        Assert.Contains("no links", vm.LoadError);
    }

    /// <summary>A finished download of this file, as ShadeNet finds it after a restart.</summary>
    static (DownloadManager Manager, DownloadJob Job) Finished(string dir, string fileName, byte[] bytes)
    {
        var settings = Settings(dir);
        Directory.CreateDirectory(settings.DownloadRoot);
        File.WriteAllBytes(Path.Combine(settings.DownloadRoot, fileName), bytes);
        var db = Path.Combine(dir, "jobs.db");
        using (var store = new JobStore(db))
        {
            store.SaveJob(new DownloadJob
            {
                Url = "https://files.example/" + fileName, FileName = fileName, SaveDirectory = settings.DownloadRoot, Category = "Compressed",
                State = DownloadState.Completed, TotalSize = bytes.Length, CompletedAt = DateTimeOffset.Now,
            });
        }

        var manager = new DownloadManager(settings, new JobStore(db));
        manager.Load();
        return (manager, manager.Jobs.Single());
    }

    [Fact]
    public async Task Extract_on_a_finished_zip_unpacks_it_and_the_card_says_where()
    {
        using var dir = new TempDir();
        byte[] zip;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            using (var w = new StreamWriter(z.CreateEntry("photo.txt").Open()))
                w.Write("a photo");
            zip = ms.ToArray();
        }

        var (manager, job) = Finished(dir.Path, "Pack.zip", zip);
        using var _ = manager;
        var toasts = new ToastService();
        var downloads = new DownloadsViewModel(manager, toasts, dialogs: new FakeDialogs());
        var item = downloads.Items.Single();
        Assert.True(item.CanExtract);

        await downloads.CheckExtractAsync(item);

        Assert.Equal("a photo", File.ReadAllText(Path.Combine(manager.Settings.DownloadRoot, "Pack", "photo.txt")));
        Assert.Equal("Extracted to Pack", job.Extraction);
        Assert.Equal("Extracted to Pack", manager.Snapshot(job).Note);
        item.Update(manager.Snapshot(job));
        Assert.Contains("Extracted to Pack", item.DetailText);

        // what the user is told (the archive stays where it is: the Recycle Bin is off)
        downloads.OnExtracted(job, ArchiveExtractor.SetOf(job.TargetPath)!, new ExtractResult(ExtractStatus.Extracted, Path.Combine(manager.Settings.DownloadRoot, "Pack"), 1));
        Assert.Equal("Extracted Pack.zip", toasts.Current!.Message);
        Assert.Equal("Open folder", toasts.Current.ActionText);
        Assert.True(File.Exists(job.TargetPath));
    }

    [Fact]
    public async Task Extract_on_an_archive_with_a_password_asks_for_it()
    {
        const string rar = @"C:\Program Files\WinRAR\Rar.exe";
        if (!File.Exists(rar)) return;   // (WinRAR is not on this computer: skipped)
        using var dir = new TempDir();
        var source = Path.Combine(dir.Path, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "secret.txt"), "the secret");
        var archive = Path.Combine(dir.Path, "Locked.rar");
        var info = new ProcessStartInfo(rar) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "a", "-ep1", "-idq", "-pS3cret", archive, Path.Combine(source, "*") }) info.ArgumentList.Add(a);
        using (var p = Process.Start(info)!)
        {
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(30_000);
        }

        var (manager, job) = Finished(Path.Combine(dir.Path, "app"), "Locked.rar", File.ReadAllBytes(archive));
        using var _ = manager;
        var toasts = new ToastService();
        var dialogs = new FakeDialogs { Password = "S3cret" };
        var downloads = new DownloadsViewModel(manager, toasts, dialogs: dialogs);

        await downloads.CheckExtractAsync(downloads.Items.Single());
        Assert.Equal("the secret", File.ReadAllText(Path.Combine(manager.Settings.DownloadRoot, "Locked", "secret.txt")));

        // found locked when it finished by itself: the user is asked to give the password
        downloads.OnExtracted(job, ArchiveExtractor.SetOf(job.TargetPath)!, new ExtractResult(ExtractStatus.NeedsPassword));
        Assert.Equal("Enter password", toasts.Current!.ActionText);
    }
}
