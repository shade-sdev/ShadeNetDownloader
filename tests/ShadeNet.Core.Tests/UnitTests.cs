using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Storage;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Tests;

public class FileNameHelperTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("a/b\\c.txt", "c.txt")]
    [InlineData("..\\..\\windows\\evil.exe", "evil.exe")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("NUL", "_NUL")]
    [InlineData("lpt1.log", "_lpt1.log")]
    [InlineData("file.", "file")]
    [InlineData("  padded name  ", "padded name")]
    [InlineData("a:b*c?d.txt", "a_b_c_d.txt")]
    [InlineData("stream.txt:hidden", "stream.txt_hidden")]
    [InlineData("", "download")]
    [InlineData(".", "download")]
    [InlineData("...", "download")]
    [InlineData("???", "download")]
    public void Sanitize_produces_a_safe_single_file_name(string input, string expected) =>
        Assert.Equal(expected, FileNameHelper.Sanitize(input));

    [Fact]
    public void Sanitize_null_gives_fallback() => Assert.Equal("download", FileNameHelper.Sanitize(null));

    [Fact]
    public void Sanitize_strips_control_characters() => Assert.Equal("a_b.txt", FileNameHelper.Sanitize("a\u0001b.txt"));

    [Fact]
    public void Sanitize_caps_length_but_keeps_extension()
    {
        var result = FileNameHelper.Sanitize(new string('x', 400) + ".mp4");
        Assert.True(result.Length <= 200);
        Assert.EndsWith(".mp4", result);
    }

    [Fact]
    public void Sanitize_result_never_contains_invalid_characters()
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var raw in new[] { "a<b>c", "x|y", "q\"r", "t\tu", "..:..", "dir\\file", "c:\\windows\\x" })
            Assert.DoesNotContain(FileNameHelper.Sanitize(raw), c => invalid.Contains(c));
    }

    [Theory]
    [InlineData("Ã©", "é")]            // UTF-8 bytes read as Latin-1
    [InlineData("plain.txt", "plain.txt")]
    [InlineData("caf\u00e9", "caf\u00e9")] // genuine Latin-1 that is not valid UTF-8 stays as it is
    [InlineData("日本語.txt", "日本語.txt")] // already real Unicode
    public void FixMojibake_repairs_only_what_is_broken(string input, string expected) =>
        Assert.Equal(expected, FileNameHelper.FixMojibake(input));

    [Fact]
    public void MakeUnique_adds_a_counter_and_respects_partial_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shadenet-unique-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal("a.zip", FileNameHelper.MakeUnique(dir, "a.zip"));
            File.WriteAllText(Path.Combine(dir, "a.zip"), "x");
            Assert.Equal("a (1).zip", FileNameHelper.MakeUnique(dir, "a.zip"));
            File.WriteAllText(Path.Combine(dir, "a (1).zip.sndpart"), "x"); // someone else's partial download
            Assert.Equal("a (2).zip", FileNameHelper.MakeUnique(dir, "a.zip"));
            File.WriteAllText(Path.Combine(dir, "b.tar.gz"), "x");
            Assert.Equal("b (1).tar.gz", FileNameHelper.MakeUnique(dir, "b.tar.gz"));
            Assert.Equal("c.bin", FileNameHelper.MakeUnique(dir, "c.bin", p => false));
            Assert.Equal("c (1).bin", FileNameHelper.MakeUnique(dir, "c.bin", p => p.EndsWith("c.bin")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("video/mp4", ".mp4")]
    [InlineData("application/pdf; charset=binary", ".pdf")]
    [InlineData("VIDEO/WEBM", ".webm")]
    [InlineData("application/x-unknown", null)]
    [InlineData(null, null)]
    public void ExtensionForMime(string? mime, string? expected) => Assert.Equal(expected, FileNameHelper.ExtensionForMime(mime));
}

public class CategoryResolverTests
{
    [Theory]
    [InlineData("movie.MP4", "Video")]
    [InlineData("song.flac", "Music")]
    [InlineData("paper.pdf", "Documents")]
    [InlineData("setup.exe", "Programs")]
    [InlineData("backup.tar.gz", "Compressed")]
    [InlineData("photo.jpeg", "Images")]
    [InlineData("mystery.xyz", "General")]
    [InlineData("noextension", "General")]
    public void Maps_extensions_to_categories(string file, string category) =>
        Assert.Equal(category, CategoryResolver.FromFileName(file));

    [Fact]
    public void Settings_resolve_category_folders()
    {
        var s = new EngineSettings { DownloadRoot = @"C:\dl" };
        Assert.Equal(@"C:\dl\Video", s.ResolveCategoryFolder("Video"));
        Assert.Equal(@"C:\dl", s.ResolveCategoryFolder("General"));
        s.CategoryFolders["Video"] = @"D:\movies";
        Assert.Equal(@"D:\movies", s.ResolveCategoryFolder("Video"));
        s.UseCategoryFolders = false;
        Assert.Equal(@"C:\dl", s.ResolveCategoryFolder("Music"));
    }

    [Fact]
    public void Settings_apply_host_connection_limits()
    {
        var s = new EngineSettings();
        s.HostConnectionLimits["strict.example"] = 2;
        Assert.Equal(8, s.EffectiveConnections("free.example", 8));
        Assert.Equal(2, s.EffectiveConnections("STRICT.example", 8));
        Assert.Equal(1, s.EffectiveConnections("free.example", 0));
    }
}

public class SpeedLimiterTests
{
    const int KiB = 1024;

    [Fact]
    public void Unlimited_never_waits()
    {
        var l = new SpeedLimiter();
        Assert.False(l.IsEnabled);
        Assert.Equal(TimeSpan.Zero, l.Reserve(100 * 1024 * KiB));
    }

    [Fact]
    public void Reservations_queue_up_as_debt()
    {
        var l = new SpeedLimiter(100 * KiB);
        var first = l.Reserve(100 * KiB).TotalSeconds;
        var second = l.Reserve(100 * KiB).TotalSeconds;
        Assert.InRange(first, 0.9, 1.1);
        Assert.InRange(second, 1.9, 2.1); // the second caller waits behind the first
    }

    [Fact]
    public void Refund_gives_the_budget_back()
    {
        var l = new SpeedLimiter(100 * KiB);
        l.Reserve(100 * KiB);
        l.Refund(100 * KiB);
        Assert.True(l.Reserve(10 * KiB).TotalSeconds < 0.2);
    }

    [Fact]
    public void Refund_is_capped_at_the_bucket_size()
    {
        var l = new SpeedLimiter(100 * KiB);
        l.Refund(10_000 * KiB); // must not bank a huge burst
        Assert.True(l.Reserve(100 * KiB).TotalSeconds > 0.7);
    }

    [Fact]
    public void Rate_changes_apply_to_the_next_reservation()
    {
        var l = new SpeedLimiter(10 * KiB);
        Assert.True(l.Reserve(10 * KiB).TotalSeconds > 0.9);
        l.BytesPerSecond = 0;
        Assert.Equal(TimeSpan.Zero, l.Reserve(10 * KiB));
        l.BytesPerSecond = 1000 * KiB;
        Assert.True(l.Reserve(10 * KiB).TotalSeconds < 0.1);
    }

    [Fact]
    public async Task Waiting_is_cancellable_and_refunds()
    {
        var l = new SpeedLimiter(1 * KiB);
        using var cts = new CancellationTokenSource(50);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SpeedLimiter.AcquireAsync(60 * KiB, cts.Token, l)); // would take a minute
        Assert.True(sw.ElapsedMilliseconds < 1000);
        Assert.True(l.Reserve(1 * KiB).TotalSeconds < 2); // the cancelled 60 KiB were given back
    }

    [Fact]
    public async Task The_stricter_of_two_limiters_decides()
    {
        var fast = new SpeedLimiter(10_000 * KiB);
        var slow = new SpeedLimiter(100 * KiB);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await SpeedLimiter.AcquireAsync(30 * KiB, CancellationToken.None, fast, slow);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.2, 0.6);
    }

    [Fact]
    public void Preferred_read_size_follows_the_strictest_limit()
    {
        const int max = 128 * KiB;
        Assert.Equal(max, SpeedLimiter.PreferredReadSize(max, new SpeedLimiter(), null));
        Assert.Equal(20 * KiB, SpeedLimiter.PreferredReadSize(max, new SpeedLimiter(100 * KiB)));
        Assert.Equal(1024, SpeedLimiter.PreferredReadSize(max, new SpeedLimiter(KiB)));
        Assert.Equal(max, SpeedLimiter.PreferredReadSize(max, new SpeedLimiter(100_000 * KiB)));
        Assert.Equal(2 * KiB, SpeedLimiter.PreferredReadSize(max, new SpeedLimiter(100 * KiB), new SpeedLimiter(10 * KiB)));
    }
}

public class RampUpPolicyTests
{
    static RampUpPolicy Policy() => new(TimeSpan.Zero);

    [Fact]
    public void Doubles_while_connections_are_receiving()
    {
        var p = Policy();
        Assert.Equal(1, p.Plan(active: 1, receiving: 1, speed: 1e6, limit: 0, max: 8)); // 1 → 2
        Assert.Equal(2, p.Plan(2, 2, 1e6, 0, 8));                                        // 2 → 4
        Assert.Equal(4, p.Plan(4, 4, 1e6, 0, 8));                                        // 4 → 8
        Assert.Equal(0, p.Plan(8, 8, 1e6, 0, 8));                                        // at the cap
    }

    [Fact]
    public void Never_exceeds_the_cap()
    {
        Assert.Equal(1, Policy().Plan(2, 2, 1e6, 0, 3));   // 2 → 3, not 4
        Assert.Equal(0, Policy().Plan(3, 3, 1e6, 0, 3));
    }

    [Fact]
    public void Waits_until_most_connections_actually_receive_data()
    {
        Assert.Equal(0, Policy().Plan(4, 0, 0, 0, 8));
        Assert.Equal(0, Policy().Plan(4, 2, 1e6, 0, 8)); // exactly half is not "more than half"
        Assert.Equal(4, Policy().Plan(4, 3, 1e6, 0, 8));
    }

    [Fact]
    public void Respects_the_interval()
    {
        var p = new RampUpPolicy(TimeSpan.FromSeconds(30));
        Assert.Equal(0, p.Plan(1, 1, 1e6, 0, 8)); // the clock starts when the job starts
    }

    [Fact]
    public void Stops_when_the_limit_is_nearly_saturated()
    {
        Assert.Equal(0, Policy().Plan(2, 2, speed: 90_000, limit: 100_000, max: 8));  // 90 % ≥ 85 %
        Assert.Equal(2, Policy().Plan(2, 2, speed: 50_000, limit: 100_000, max: 8));  // plenty of headroom
    }

    [Fact]
    public void A_tiny_limit_caps_the_job_at_two_connections()
    {
        Assert.Equal(1, Policy().Plan(1, 1, 100, limit: 5_000, max: 8));
        Assert.Equal(0, Policy().Plan(2, 2, 100, limit: 5_000, max: 8));
    }
}

public class JobStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "shadenet-store-" + Guid.NewGuid().ToString("N")[..8]);

    public JobStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // sqlite may still hold the file briefly
        }
    }

    static DownloadJob Sample() => new()
    {
        Url = "https://example.com/a.iso",
        FinalUrl = "https://cdn.example.com/a.iso?sig=1",
        Referer = "https://example.com/",
        Cookies = "sid=1; theme=dark",
        UserAgent = "UA/1.0",
        Headers = { ["X-Test"] = "ünicode ✓" },
        FileName = "a.iso",
        SaveDirectory = @"C:\dl",
        Category = "Programs",
        Probed = true,
        TotalSize = 10_000_000_000,
        ContentType = "application/x-iso9660-image",
        ETag = "\"abc\"",
        LastModified = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero),
        AcceptsRanges = true,
        State = DownloadState.Paused,
        SpeedLimit = 123_456,
        MaxConnections = 6,
        Priority = 2,
        Error = "boom",
        ErrorKind = JobErrorKind.Network,
        NextSegmentId = 3,
        Segments =
        {
            new Segment { Id = 0, Start = 0, End = 4_999_999_999, Downloaded = 1_234, State = SegmentState.Pending },
            new Segment { Id = 2, Start = 5_000_000_000, End = 9_999_999_999, Downloaded = 5_000_000_000, State = SegmentState.Done },
        },
    };

    [Fact]
    public void Round_trips_everything_including_big_numbers_and_unicode()
    {
        var path = Path.Combine(_dir, "a.db");
        var job = Sample();
        using (var store = new JobStore(path)) store.SaveJob(job);

        using var again = new JobStore(path);
        var loaded = Assert.Single(again.LoadAll());
        Assert.Equal(job.Id, loaded.Id);
        Assert.Equal(job.Url, loaded.Url);
        Assert.Equal(job.FinalUrl, loaded.FinalUrl);
        Assert.Equal(job.Cookies, loaded.Cookies);
        Assert.Equal("ünicode ✓", loaded.Headers["X-Test"]);
        Assert.Equal(job.TotalSize, loaded.TotalSize);
        Assert.Equal(job.LastModified, loaded.LastModified);
        Assert.Equal(job.ETag, loaded.ETag);
        Assert.True(loaded.AcceptsRanges && loaded.Probed);
        Assert.Equal(job.SpeedLimit, loaded.SpeedLimit);
        Assert.Equal(DownloadState.Paused, loaded.State);
        Assert.Equal(JobErrorKind.Network, loaded.ErrorKind);
        Assert.Equal(3, loaded.NextSegmentId);
        Assert.Equal(2, loaded.Segments.Count);
        Assert.Equal(5_000_000_000, loaded.Segments[1].Start);
        Assert.Equal(SegmentState.Done, loaded.Segments[1].State);
        Assert.Equal(1_234, loaded.Segments[0].Downloaded);
    }

    [Fact]
    public void Saving_again_replaces_segments_instead_of_duplicating()
    {
        var path = Path.Combine(_dir, "b.db");
        var job = Sample();
        using var store = new JobStore(path);
        store.SaveJob(job);
        job.Segments[0].Downloaded = 999;
        store.SaveJob(job);
        var loaded = Assert.Single(store.LoadAll());
        Assert.Equal(2, loaded.Segments.Count);
        Assert.Equal(999, loaded.Segments[0].Downloaded);
    }

    [Fact]
    public void A_captured_snapshot_is_what_gets_written()
    {
        var path = Path.Combine(_dir, "c.db");
        var job = Sample();
        using var store = new JobStore(path);
        var snapshot = job.CaptureSegments();
        job.Segments[0].Downloaded = 777_777; // progress made after the capture must not leak into the checkpoint
        store.SaveJob(job, snapshot);
        Assert.Equal(1_234, store.LoadAll().Single().Segments[0].Downloaded);
    }

    [Fact]
    public void Completed_jobs_drop_their_segment_table()
    {
        var path = Path.Combine(_dir, "d.db");
        var job = Sample();
        using var store = new JobStore(path);
        store.SaveJob(job);
        job.State = DownloadState.Completed;
        store.SaveJob(job);
        Assert.Empty(store.LoadAll().Single().Segments);
    }

    [Fact]
    public void Delete_removes_job_and_segments()
    {
        var path = Path.Combine(_dir, "e.db");
        using var store = new JobStore(path);
        var job = Sample();
        store.SaveJob(job);
        store.Delete(job.Id);
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void Reopening_an_existing_database_keeps_working()
    {
        var path = Path.Combine(_dir, "f.db");
        for (var i = 0; i < 3; i++)
        {
            using var store = new JobStore(path);
            store.SaveJob(Sample());
        }

        using var final = new JobStore(path);
        Assert.Equal(3, final.LoadAll().Count);
    }
}
