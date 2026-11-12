using System.Diagnostics;
using System.IO.Compression;
using ShadeNet.Core.Archives;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Links;
using ShadeNet.Core.Models;
using System.Formats.Tar;
using static ShadeNet.Core.Tests.Harness;

namespace ShadeNet.Core.Tests;

/// <summary>The link grabber (a page's links, made absolute and cleaned up) and the unpacking of downloaded archives.</summary>
public class LinksAndArchivesTests
{
    // ------------------------------------------------------------------------------------------------------------
    // Links
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pages_links_are_found_made_absolute_and_kept_once()
    {
        const string html = """
            <html><head><title>Holiday &amp; more</title><base href="https://cdn.example.com/files/"></head><body>
            <a href="photos.zip">The <b>photos</b></a>
            <a href='/docs/guide.pdf'>Guide</a>
            <a href=video.mp4>Film</a>
            <a href="photos.zip">again</a>
            <a href="#top">Top</a> <a href="javascript:void(0)">Nothing</a> <a href="mailto:a@b.c">Mail</a>
            <img src="https://img.example.com/a.jpg"> <video><source src="clip.webm"></video>
            <a href="magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&amp;dn=Debian">Debian</a>
            <script>var x = "https://tracker.example.com/never.js";</script>
            <p>Mirror: https://mirror.example.org/pack.7z.</p>
            </body></html>
            """;
        var page = LinkGrabber.FromHtml(html, new Uri("https://site.example.com/holiday/index.html"));

        Assert.Equal("Holiday & more", page.Title);
        Assert.Equal(new[]
        {
            "https://cdn.example.com/files/photos.zip",
            "https://cdn.example.com/docs/guide.pdf",
            "https://cdn.example.com/files/video.mp4",
            "https://img.example.com/a.jpg",
            "https://cdn.example.com/files/clip.webm",
            "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Debian",
            "https://mirror.example.org/pack.7z",
        }, page.Links.Select(l => l.Url));
        Assert.Equal("The photos", page.Links[0].Text);
        Assert.Equal("Holiday & more", LinkGrabber.FolderNameOf(page));
    }

    [Fact]
    public void Files_are_told_from_pages_and_named()
    {
        Assert.True(LinkGrabber.LooksLikeFile("https://a.example/x/setup.exe?token=1"));
        Assert.True(LinkGrabber.LooksLikeFile("magnet:?xt=urn:btih:00"));
        Assert.False(LinkGrabber.LooksLikeFile("https://a.example/forum/thread.php?id=4"));
        Assert.False(LinkGrabber.LooksLikeFile("https://a.example/about/"));
        Assert.Equal("setup.exe", LinkGrabber.NameOf("https://a.example/x/setup.exe?token=1"));
        Assert.Equal("Debian 12", LinkGrabber.NameOf("magnet:?xt=urn:btih:00&dn=Debian+12"));
        Assert.Equal("a.example", LinkGrabber.NameOf("https://a.example/"));
    }

    [Fact]
    public async Task A_page_is_read_for_its_links_through_the_engine()
    {
        await using var h = await Harness.CreateAsync();
        var m = h.NewManager();
        var file = h.Server.FileUrl(1000, name: "one.bin");
        var page = h.Server.AddPage($"""<title>Files</title><a href="{file}">One</a> <a href="/file/2000?name=two.bin">Two</a>""");

        var grabbed = await m.GrabPageAsync(page);
        Assert.Equal("Files", grabbed.Title);
        Assert.Equal(2, grabbed.Links.Count);
        Assert.Equal($"{h.Server.BaseUrl}/file/2000?name=two.bin", grabbed.Links[1].Url);

        var ex = await Assert.ThrowsAsync<DownloadException>(() => m.GrabPageAsync(h.Server.AddFile(new byte[10], "x.bin")));
        Assert.Contains("is a file", ex.Message);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Archives
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The 7-Zip the archive tests use: the one tools\get-tools.ps1 put in the repository's tools\7zip (else an installed one).</summary>
    static readonly string? SevenZipExe = FindSevenZip();

    static string? FindSevenZip()
    {
        for (var d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "tools", "7zip", "7z.exe"))) return Path.Combine(d, "tools", "7zip", "7z.exe");
        return ShadeNet.Core.Archives.SevenZip.Locate(null, appFolder: Path.GetTempPath(), pathVariable: "");
    }

    static ExtractResult Unpack(ArchiveSet set, string? password = null) => ArchiveExtractor.Extract(set, password, default, SevenZipExe);

    static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "sn-arc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
        }

        return ms.ToArray();
    }

    [Fact]
    public void Parts_are_recognised_and_ordered()
    {
        var dir = NewDir();
        try
        {
            foreach (var n in new[] { "Pack.part1.rar", "Pack.part2.rar", "Pack.part10.rar", "Other.part1.rar", "Old.rar", "Old.r00", "Old.r01", "Big.7z.001", "Big.7z.002" })
                File.WriteAllBytes(Path.Combine(dir, n), new byte[1]);

            var rar = ArchiveExtractor.SetOf(Path.Combine(dir, "Pack.part2.rar"))!;
            Assert.Equal("Pack", rar.Stem);
            Assert.Equal(new[] { "Pack.part1.rar", "Pack.part2.rar", "Pack.part10.rar" }, rar.Parts.Select(Path.GetFileName));
            Assert.Equal(Path.Combine(dir, "Pack.part1.rar"), rar.First);
            Assert.True(rar.Includes(Path.Combine(dir, "Pack.part3.rar")));    // not here yet, but of this set
            Assert.False(rar.Includes(Path.Combine(dir, "Other.part1.rar")));

            Assert.Equal(new[] { "Old.rar", "Old.r00", "Old.r01" }, ArchiveExtractor.SetOf(Path.Combine(dir, "Old.r01"))!.Parts.Select(Path.GetFileName));
            var split = ArchiveExtractor.SetOf(Path.Combine(dir, "Big.7z.002"))!;
            Assert.True(split.Split);
            Assert.Equal("Big", split.Stem);

            Assert.Equal("photos", ArchiveExtractor.SetOf(Path.Combine(dir, "photos.tar.gz"))!.Stem);
            Assert.Null(ArchiveExtractor.SetOf(Path.Combine(dir, "film.mkv")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_zip_is_unpacked_into_a_folder_named_after_it_and_one_with_a_folder_of_its_own_is_not_nested()
    {
        if (SevenZipExe == null) return;   // (tools\get-tools.ps1 -Tool 7zip fetches it)
        var dir = NewDir();
        try
        {
            var loose = Path.Combine(dir, "Loose.zip");
            File.WriteAllBytes(loose, Zip(("a.txt", "A"), ("sub/b.txt", "B")));
            var r = Unpack(ArchiveExtractor.SetOf(loose)!);
            Assert.Equal(ExtractStatus.Extracted, r.Status);
            Assert.Equal(Path.Combine(dir, "Loose"), r.Folder);
            Assert.Equal(2, r.Files);
            Assert.Equal("B", File.ReadAllText(Path.Combine(dir, "Loose", "sub", "b.txt")));

            // the same again: a new folder, nothing overwritten
            Assert.Equal(Path.Combine(dir, "Loose (1)"), Unpack(ArchiveExtractor.SetOf(loose)!).Folder);

            var tidy = Path.Combine(dir, "Tidy.zip");
            File.WriteAllBytes(tidy, Zip(("Album/01.txt", "1"), ("Album/02.txt", "2")));
            var t = Unpack(ArchiveExtractor.SetOf(tidy)!);
            Assert.Equal(Path.Combine(dir, "Album"), t.Folder);
            Assert.True(File.Exists(Path.Combine(dir, "Album", "02.txt")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Nothing_in_an_archive_can_land_outside_its_folder()
    {
        if (SevenZipExe == null) return;   // (tools\get-tools.ps1 -Tool 7zip fetches it)
        var dir = NewDir();
        try
        {
            var evil = Path.Combine(dir, "evil.zip");
            File.WriteAllBytes(evil, Zip(("ok.txt", "fine"), ("../../escaped.txt", "bad")));
            var r = Unpack(ArchiveExtractor.SetOf(evil)!);
            Assert.Equal(ExtractStatus.Failed, r.Status);
            Assert.Contains("outside its folder", r.Message);
            Assert.False(File.Exists(Path.Combine(dir, "..", "escaped.txt")));
            Assert.False(Directory.Exists(Path.Combine(dir, "evil")));   // nothing half-made is left behind

            Assert.Null(ArchiveExtractor.SafePath(dir, @"C:\Windows\x.dll"));
            Assert.Null(ArchiveExtractor.SafePath(dir, "a/../../x"));
            Assert.NotNull(ArchiveExtractor.SafePath(dir, "a/./b/../c.txt"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Tar_gz_a_lone_gz_and_a_zip_cut_in_pieces_are_unpacked()
    {
        if (SevenZipExe == null) return;   // (tools\get-tools.ps1 -Tool 7zip fetches it)
        var dir = NewDir();
        try
        {
            var tgz = Path.Combine(dir, "src.tar.gz");
            var tarSource = Path.Combine(dir, "tar-source");
            Directory.CreateDirectory(Path.Combine(tarSource, "src"));
            File.WriteAllText(Path.Combine(tarSource, "src", "main.c"), "int main(){}");
            File.WriteAllText(Path.Combine(tarSource, "README"), "hello");
            using (var stream = File.Create(tgz))
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
                TarFile.CreateFromDirectory(tarSource, gzip, includeBaseDirectory: false);

            var t = Unpack(ArchiveExtractor.SetOf(tgz)!);
            Assert.Equal(ExtractStatus.Extracted, t.Status);
            Assert.Equal("hello", File.ReadAllText(Path.Combine(t.Folder!, "README")));

            var gz = Path.Combine(dir, "notes.txt.gz");
            using (var stream = File.Create(gz))
            using (var g = new GZipStream(stream, CompressionLevel.Fastest))
                g.Write("some notes"u8);
            var n = Unpack(ArchiveExtractor.SetOf(gz)!);
            Assert.Equal(ExtractStatus.Extracted, n.Status);
            Assert.Equal("some notes", File.ReadAllText(Directory.GetFiles(n.Folder!).Single()));

            // a zip cut in three pieces (.001, .002, .003)
            var whole = Zip(Enumerable.Range(0, 20).Select(i => ($"f{i}.txt", new string((char)('a' + i), 5000))).ToArray());
            var third = whole.Length / 3 + 1;
            for (var i = 0; i < 3; i++)
                File.WriteAllBytes(Path.Combine(dir, $"Cut.zip.{i + 1:000}"), whole.Skip(i * third).Take(third).ToArray());
            var c = Unpack(ArchiveExtractor.SetOf(Path.Combine(dir, "Cut.zip.003"))!);
            Assert.Equal(ExtractStatus.Extracted, c.Status);
            Assert.Equal(20, c.Files);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    static readonly string? Rar = File.Exists(@"C:\Program Files\WinRAR\Rar.exe") ? @"C:\Program Files\WinRAR\Rar.exe" : null;

    static void MakeRar(string archive, string source, params string[] options)
    {
        var info = new ProcessStartInfo(Rar!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "a", "-ep1", "-m0", "-idq", "-y" }.Concat(options).Append(archive).Append(Path.Combine(source, "*"))) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000);
        Assert.True(p.ExitCode == 0, $"rar exited with {p.ExitCode}: {p.StandardError.ReadToEnd()}");
    }

    [Fact]
    public void A_rar_with_a_password_asks_for_it_and_unpacks_with_it()
    {
        if (Rar == null || SevenZipExe == null) return;   // (WinRAR makes the archives, 7-Zip unpacks them: without either, skipped)
        var dir = NewDir();
        try
        {
            var source = Path.Combine(dir, "source");
            Directory.CreateDirectory(source);
            var bytes = new byte[300_000];
            Random.Shared.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(source, "data.bin"), bytes);
            File.WriteAllText(Path.Combine(source, "read me.txt"), "secret");
            var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);
            MakeRar(Path.Combine(outDir, "Locked.rar"), source, "-pS3cret");

            var set = ArchiveExtractor.SetOf(Path.Combine(outDir, "Locked.rar"))!;
            Assert.Equal(ExtractStatus.NeedsPassword, Unpack(set).Status);
            Assert.Equal(ExtractStatus.WrongPassword, Unpack(set, "nope").Status);
            Assert.Empty(Directory.GetDirectories(outDir));   // nothing left from the failed tries

            var ok = Unpack(set, "S3cret");
            Assert.Equal(ExtractStatus.Extracted, ok.Status);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(ok.Folder!, "data.bin")));
            Assert.Equal("secret", File.ReadAllText(Path.Combine(ok.Folder!, "read me.txt")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_rar_in_parts_unpacks_with_or_without_a_password_and_waits_for_a_missing_part()
    {
        if (Rar == null || SevenZipExe == null) return;
        var dir = NewDir();
        try
        {
            var source = Path.Combine(dir, "source");
            Directory.CreateDirectory(source);
            var bytes = new byte[300_000];
            Random.Shared.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(source, "data.bin"), bytes);
            var open = Path.Combine(dir, "open");
            var locked = Path.Combine(dir, "locked");
            Directory.CreateDirectory(open);
            Directory.CreateDirectory(locked);
            MakeRar(Path.Combine(open, "Pack.rar"), source, "-v100k");
            MakeRar(Path.Combine(locked, "Pack.rar"), source, "-v100k", "-pS3cret");
            var parts = Directory.GetFiles(open, "Pack.part*.rar").OrderBy(p => p).ToArray();
            Assert.True(parts.Length >= 3, string.Join(", ", parts));

            var ok = Unpack(ArchiveExtractor.SetOf(parts[^1])!);
            Assert.Equal(ExtractStatus.Extracted, ok.Status);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(ok.Folder!, "data.bin")));

            // a part missing: it waits for it rather than failing
            File.Delete(parts[1]);
            Assert.Equal(ExtractStatus.Incomplete, Unpack(ArchiveExtractor.SetOf(parts[0])!).Status);

            // with a password too (what the library ShadeNet used before got wrong where a part ends)
            var lockedSet = ArchiveExtractor.SetOf(Directory.GetFiles(locked, "Pack.part1.rar").Single())!;
            Assert.Equal(ExtractStatus.NeedsPassword, Unpack(lockedSet).Status);
            Assert.Equal(ExtractStatus.WrongPassword, Unpack(lockedSet, "nope").Status);
            Assert.Empty(Directory.GetDirectories(locked));   // nothing left from the failed tries
            var unlocked = Unpack(lockedSet, "S3cret");
            Assert.Equal(ExtractStatus.Extracted, unlocked.Status);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(unlocked.Folder!, "data.bin")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_damaged_archive_is_caught_by_its_checksums()
    {
        if (SevenZipExe == null) return;   // (tools\get-tools.ps1 -Tool 7zip fetches it)
        var dir = NewDir();
        try
        {
            var text = new string('x', 4000) + "the end";
            byte[] zip;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                using (var w = new StreamWriter(z.CreateEntry("page.txt", CompressionLevel.NoCompression).Open()))
                    w.Write(text);
                zip = ms.ToArray();
            }

            // one byte of the stored text changed
            var at = Array.IndexOf(zip, (byte)'x', 100);
            zip[at] = (byte)'y';
            var path = Path.Combine(dir, "Damaged.zip");
            File.WriteAllBytes(path, zip);

            var r = Unpack(ArchiveExtractor.SetOf(path)!);
            Assert.Equal(ExtractStatus.Failed, r.Status);
            Assert.Contains("damaged", r.Message);
            Assert.False(Directory.Exists(Path.Combine(dir, "Damaged")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_downloaded_archive_in_parts_is_unpacked_when_its_last_part_is_in()
    {
        if (Rar == null || SevenZipExe == null) return;
        await using var h = await Harness.CreateAsync();
        var dir = NewDir();
        try
        {
            var source = Path.Combine(dir, "source");
            Directory.CreateDirectory(source);
            var bytes = new byte[250_000];
            Random.Shared.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(source, "movie.bin"), bytes);
            MakeRar(Path.Combine(dir, "Movie.rar"), source, "-v100k");
            var parts = Directory.GetFiles(dir, "Movie.part*.rar").OrderBy(p => p).ToList();

            var m = h.NewManager(tweak: s =>
            {
                s.ExtractArchives = true;
                s.SevenZipPath = SevenZipExe!;
            });
            var results = new List<ExtractResult>();
            m.ArchiveExtracted += (_, _, r) => { lock (results) results.Add(r); };
            var jobs = parts.Select(p => m.Add(new AddDownloadRequest { Url = h.Server.AddFile(File.ReadAllBytes(p), Path.GetFileName(p)) })).ToList();
            foreach (var j in jobs) await Finished(j, 30);

            await WaitUntil(() => { lock (results) return results.Count > 0; }, 20, "the archive to be unpacked");
            await Task.Delay(500);
            lock (results)
            {
                var r = Assert.Single(results);   // once, for all of its parts
                Assert.Equal(ExtractStatus.Extracted, r.Status);
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(r.Folder!, "movie.bin")));
            }

            Assert.Contains(jobs, j => j.Extraction?.StartsWith("Extracted to") == true);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
