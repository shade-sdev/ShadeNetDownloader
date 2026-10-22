using System.Diagnostics;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.TestServer;

// ShadeNet CLI - a thin console front end over ShadeNet.Core for trying the engine without the UI.
//   probe <url>                         what the server says about the file
//   get <url> [-o dir] [-c n]           download (default: 1 connection)
//   direct <url> [-o dir] [-c n] [--chunk mb] [--ua text]   download through the STREAM engine (what yt-dlp results use)
//   selftest                            start a local test server and verify downloads byte for byte
//   segtest                             segmentation scenarios: speed-up, takeover, corruption, liars
//   resumetest                          pause/resume, restart, hard kill, changed file, expired link
//   limittest                           speed limiter: accuracy, per-job, live changes, pause latency
//   serve                               run the test server so you can point probe/get at it

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("""
        ShadeNet CLI
          probe <url>                    show size, range support, file name, validators
          get <url> [-o dir] [-c n]      download; -c = connections (default 1)
          selftest                       local server + byte-exact verification
          segtest                        multi-connection scenarios (splitting, takeover, fallbacks)
          resumetest                     pause/resume, restart, hard kill, changed file, expired link
          limittest                      speed limiter: accuracy, per-job, live changes, pause latency
          serve                          run the local test server (Ctrl+C to stop)
        """);
    return 1;
}

switch (args[0])
{
    case "probe" when args.Length >= 2:
        return await ProbeCommand(args[1], args.Length >= 4 && args[2] == "--cookie" ? args[3] : null);
    case "get" when args.Length >= 2:
        return await GetCommand(args);
    case "direct" when args.Length >= 2:
        return await DirectCommand(args);
    case "selftest":
        return await SelfTest();
    case "serve":
        return await Serve();
    case "segtest":
        return await SegTest();
    case "resumetest":
        return await ResumeTest();
    case "limittest":
        return await LimitTest();
    default:
        Console.WriteLine("Unknown command.");
        return 1;
}

static async Task<int> ProbeCommand(string url, string? cookie)
{
    var manager = NewManager(Path.GetTempPath(), 1);
    try
    {
        var r = await manager.ProbeAsync(new RequestSpec(url, null, ShadeNet.Core.Util.CookieHeader.Normalize(cookie)));
        PrintProbe(r);
        return 0;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Probe failed: {ex.Message}");
        Console.ResetColor();
        return 2;
    }
}

static async Task<int> GetCommand(string[] args)
{
    var url = args[1];
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    var connections = 1;
    string? db = null;
    for (var i = 2; i < args.Length - 1; i++)
    {
        if (args[i] == "-o") dir = args[i + 1];
        if (args[i] == "-c") connections = int.Parse(args[i + 1]);
        if (args[i] == "--db") db = args[i + 1];
    }

    var manager = NewManager(dir, connections, db);
    var probe = await manager.ProbeAsync(new RequestSpec(url));
    PrintProbe(probe);
    Console.WriteLine();

    var job = await Download(manager, url, probe, connections, live: true);
    return job.State == DownloadState.Completed ? 0 : 2;
}

/// <summary>The stream engine on one plain file (picture only): parallel byte ranges, then ffmpeg. For comparing it with "get".</summary>
static async Task<int> DirectCommand(string[] args)
{
    var url = args[1];
    var dir = Path.Combine(Path.GetTempPath(), "shadenet-direct");
    var connections = 8;
    var chunkMb = 4;
    string? ua = null;
    string? audio = null;
    var trace = args.Contains("--trace");
    for (var i = 2; i < args.Length - 1; i++)
    {
        if (args[i] == "-o") dir = args[i + 1];
        if (args[i] == "-c") connections = int.Parse(args[i + 1]);
        if (args[i] == "--chunk") chunkMb = int.Parse(args[i + 1]);
        if (args[i] == "--ua") ua = args[i + 1];
        if (args[i] == "--audio") audio = args[i + 1];
    }

    Directory.CreateDirectory(dir);
    var manager = NewManager(dir, connections);
    manager.Settings.StreamChunkBytes = chunkMb * 1024 * 1024;

    var done = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    var sw = Stopwatch.StartNew();
    TimeSpan? lastTransfer = null;
    long lastBytes = 0;
    var peak = 0.0;
    var maxConnections = 0;
    manager.JobUpdated += s =>
    {
        if (trace) Console.WriteLine($"  t={sw.Elapsed.TotalSeconds,5:0.00}s {s.State,-12} {Fmt(s.Downloaded),9} of {(s.Total is { } tt ? Fmt(tt) : "?"),9}  speed {Fmt((long)s.Speed),9}/s  conns {s.Connections}  parts {s.PartsDone}/{s.Parts} {s.Note}");
        if (s.State == DownloadState.Downloading)
        {
            lastTransfer = sw.Elapsed;
            lastBytes = s.Downloaded;
            peak = Math.Max(peak, s.Speed);
            maxConnections = Math.Max(maxConnections, s.Connections);
        }

        if (s.State is DownloadState.Completed or DownloadState.Failed) done.TrySetResult(s);
    };

    var job = manager.Add(new AddDownloadRequest
    {
        Url = url, FileName = "direct-" + Guid.NewGuid().ToString("N")[..6] + ".mp4", MaxConnections = connections, UserAgent = ua,
        Stream = new StreamChoice(Direct: true, AudioUrl: audio), Mode = AddMode.Start,
    });
    await done.Task.WaitAsync(TimeSpan.FromMinutes(10));
    var total = sw.Elapsed;
    Console.WriteLine($"{job.State}: workers {connections}, piece {chunkMb} MB | transfer {lastTransfer?.TotalSeconds:0.0}s for {Fmt(lastBytes)} = {Fmt((long)(lastBytes / Math.Max(0.1, (lastTransfer ?? total).TotalSeconds)))}/s avg, peak {Fmt((long)peak)}/s, max conns shown {maxConnections} | total {total.TotalSeconds:0.0}s");
    if (job.Error != null) Console.WriteLine("  error: " + job.Error);
    try { File.Delete(job.TargetPath); } catch { }
    return job.State == DownloadState.Completed ? 0 : 2;
}

static async Task<int> SelfTest()
{
    await using var server = await TestServer.StartAsync();
    var dir = Path.Combine(Path.GetTempPath(), "shadenet-selftest-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    Console.WriteLine($"Test server: {server.BaseUrl}");
    Console.WriteLine($"Output dir : {dir}");
    Console.WriteLine();

    var manager = NewManager(dir, 1);
    var failures = 0;

    var cases = new (string Title, string Url, long Size, string ExpectName, bool ExpectRanges)[]
    {
        ("5 MiB, ranges, name from Content-Disposition", server.FileUrl(5 * 1024 * 1024, name: "report 2024.bin", etag: "v1"), 5 * 1024 * 1024, "report 2024.bin", true),
        ("3 MiB, server ignores Range", server.FileUrl(3 * 1024 * 1024, name: "legacy.dat", ranges: false), 3 * 1024 * 1024, "legacy.dat", false),
        ("1 byte", server.FileUrl(1, name: "tiny.txt"), 1, "tiny.txt", true),
        ("empty file", server.FileUrl(0, name: "empty.bin"), 0, "empty.bin", false),
        ("name taken from the URL", server.BaseUrl + "/file/65536", 65536, "65536", true),
        ("hostile name is sanitised", server.FileUrl(1000, name: "..\\..\\evil:name?.exe"), 1000, "evil_name_.exe", true),
    };

    foreach (var c in cases)
    {
        Console.WriteLine($"-- {c.Title}");
        var probe = await manager.ProbeAsync(new RequestSpec(c.Url));
        PrintProbe(probe, indent: "   ");

        var job = await Download(manager, c.Url, probe, 1, live: false);
        var path = job.TargetPath;
        var ok = job.State == DownloadState.Completed && File.Exists(path)
                 && new FileInfo(path).Length == c.Size
                 && ContentGen.Sha256HexOfFile(path) == ContentGen.Sha256Hex(c.Size)
                 && job.FileName == c.ExpectName
                 && probe.AcceptsRanges == (c.ExpectRanges && c.Size > 0)
                 && !File.Exists(path + ".sndpart");

        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"   {(ok ? "PASS" : "FAIL")}  saved as '{job.FileName}', state {job.State}"
                          + (job.Error != null ? $", error: {job.Error}" : ""));
        Console.ResetColor();
        Console.WriteLine();
        if (!ok) failures++;
    }

    Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
    try
    {
        Directory.Delete(dir, true);
    }
    catch
    {
        // leave it for inspection
    }

    return failures == 0 ? 0 : 3;
}

static async Task<int> Serve()
{
    await using var server = await TestServer.StartAsync();
    const long MiB = 1024 * 1024;
    Console.WriteLine($"Test server on {server.BaseUrl}");
    Console.WriteLine($"DEMO {server.FileUrl(120 * MiB, name: "ubuntu-24.04-desktop-amd64.iso", etag: "u", kbps: 2048)}");
    Console.WriteLine($"DEMO {server.FileUrl(40 * MiB, name: "conference-talk-2024.mp4", etag: "c", kbps: 4096)}");
    Console.WriteLine($"DEMO {server.FileUrl(3 * MiB, name: "annual-report-2024.pdf", etag: "r")}");
    Console.WriteLine($"DEMO {server.FileUrl(30 * MiB, name: "legacy-installer.exe", ranges: false, kbps: 1024)}");
    Console.WriteLine($"DEMO {server.BaseUrl}/status/404");
    Console.WriteLine("Ctrl+C to stop.");
    await Task.Delay(Timeout.Infinite);
    return 0;
}

static DownloadManager NewManager(string dir, int connections, string? dbPath = null) =>
    new(new EngineSettings
    {
        DownloadRoot = dir,
        UseCategoryFolders = false,
        DefaultConnections = connections,
        WriteMarkOfTheWeb = false,
    }, dbPath == null ? null : new ShadeNet.Core.Storage.JobStore(dbPath));

static async Task<DownloadJob> Download(DownloadManager manager, string url, ProbeResult probe, int connections, bool live)
{
    var done = new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    var sw = Stopwatch.StartNew();

    void OnUpdate(JobSnapshot s)
    {
        if (live && s.State == DownloadState.Downloading)
            Console.Write($"\r  [{SegmentMap(s, 40)}] {s.Fraction,4:P0} {Fmt((long)s.Speed),9}/s  conns {s.Connections}  splits {s.Splits}  takeovers {s.Takeovers}  ");
        if (s.State is DownloadState.Completed or DownloadState.Failed) done.TrySetResult(s);
    }

    manager.JobUpdated += OnUpdate;
    var job = manager.Add(new AddDownloadRequest { Url = url, MaxConnections = connections }, probe);
    await done.Task.WaitAsync(TimeSpan.FromMinutes(5));
    manager.JobUpdated -= OnUpdate;

    if (live)
    {
        Console.WriteLine();
        Console.WriteLine($"  {job.State} in {sw.Elapsed.TotalSeconds:0.0}s -> {job.TargetPath}");
        if (job.Error != null) Console.WriteLine($"  error: {job.Error}");
    }

    return job;
}

static void PrintProbe(ProbeResult r, string indent = "")
{
    Console.WriteLine($"{indent}status      {r.StatusCode}");
    Console.WriteLine($"{indent}file name   {r.FileName}");
    Console.WriteLine($"{indent}size        {(r.TotalSize is { } t ? $"{t:N0} bytes ({Fmt(t)})" : "unknown")}");
    Console.WriteLine($"{indent}resumable   {(r.AcceptsRanges ? "yes (206 + Content-Range)" : "no")}");
    Console.WriteLine($"{indent}type        {r.ContentType ?? "-"}");
    Console.WriteLine($"{indent}etag        {r.ETag ?? "-"}");
    Console.WriteLine($"{indent}modified    {r.LastModified?.ToString("u") ?? "-"}");
    Console.WriteLine($"{indent}final url   {r.FinalUrl}");
}

/// <summary>One character per slice of the file: █ complete, ▓ in progress, ░ untouched.</summary>
static string SegmentMap(JobSnapshot s, int width)
{
    if (s.Total is not > 0) return new string('░', width);
    var total = s.Total.Value;
    var chars = new char[width];
    for (var c = 0; c < width; c++)
    {
        long a = total * c / width, b = total * (c + 1) / width;
        long done = 0;
        var frontier = false;
        foreach (var seg in s.Segments)
        {
            var doneEnd = seg.Start + seg.Downloaded;
            done += Math.Max(0, Math.Min(b, doneEnd) - Math.Max(a, seg.Start));
            if (seg.State == SegmentState.Active && doneEnd >= a && doneEnd < b) frontier = true;
        }

        chars[c] = done >= b - a ? '█' : done > 0 || frontier ? '▓' : '░';
    }

    return new string(chars);
}

static async Task<int> SegTest()
{
    await using var server = await TestServer.StartAsync();
    var dir = Path.Combine(Path.GetTempPath(), "shadenet-segtest-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    Console.WriteLine($"Test server {server.BaseUrl}, output {dir}{Environment.NewLine}");
    var failures = 0;

    async Task<(DownloadJob Job, double Seconds, bool Sha)> Run(string url, long size, int conns, bool live = false)
    {
        var manager = NewManager(dir, conns);
        var probe = await manager.ProbeAsync(new RequestSpec(url));
        var sw = Stopwatch.StartNew();
        var job = await Download(manager, url, probe, conns, live);
        if (live) Console.WriteLine();
        var sha = job.State == DownloadState.Completed && ContentGen.Sha256HexOfFile(job.TargetPath) == ContentGen.Sha256Hex(size);
        return (job, sw.Elapsed.TotalSeconds, sha);
    }

    void Report(string title, bool ok, string detail)
    {
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"   {(ok ? "PASS" : "FAIL")}  {detail}");
        Console.ResetColor();
        Console.WriteLine();
        if (!ok) failures++;
    }

    string Stats(DownloadJob j, double t) =>
        $"{t:0.0}s  peak conns {j.Stats.PeakConnections}, splits {j.Stats.Splits}, takeovers {j.Stats.Takeovers}, retries {j.Stats.Retries}, fallbacks {j.Stats.Fallbacks}";

    const long MiB = 1024 * 1024;

    // A: the point of the whole exercise.
    Console.WriteLine("-- A. 8 MiB, server caps every connection at 512 KiB/s (one connection would need 16 s)");
    var a8 = await Run(server.FileUrl(8 * MiB, name: "a8.bin", kbps: 512), 8 * MiB, 8, live: true);
    const double singleConnection = 16.0;
    Console.WriteLine($"   8 connections: {Stats(a8.Job, a8.Seconds)}");
    Report("A", a8.Sha && a8.Job.Stats.PeakConnections >= 3 && a8.Seconds < singleConnection * 0.65,
        $"byte-exact={a8.Sha}, speed-up x{singleConnection / a8.Seconds:0.0} vs a single connection");

    // B: warm connection reuse.
    Console.WriteLine("-- B. chunk takeover: new connections cost 5 s (slow handshake), so a finished warm one should take over their range");
    var b = await Run(server.FileUrl(16 * MiB, name: "b.bin", kbps: 2048, connLatencyMs: 5000), 16 * MiB, 8, live: true);
    Console.WriteLine($"   {Stats(b.Job, b.Seconds)}");
    Report("B", b.Sha && b.Job.Stats.Takeovers >= 1, $"byte-exact={b.Sha}, takeovers={b.Job.Stats.Takeovers}");

    // C: data-integrity guard.
    Console.WriteLine("-- C. server returns wrong bytes to ranged requests: overlap check must catch it and fall back to 1 connection");
    var c = await Run(server.FileUrl(8 * MiB, name: "c.bin", kbps: 1024, corrupt: true), 8 * MiB, 8);
    Console.WriteLine($"   {Stats(c.Job, c.Seconds)}");
    Report("C", c.Sha && c.Job.Stats.Fallbacks == 1 && c.Job.MaxConnections == 1, $"byte-exact={c.Sha}, fallbacks={c.Job.Stats.Fallbacks}");

    // D: server lies about ranges.
    Console.WriteLine("-- D. server says 206 for the probe but ignores Range afterwards");
    var d = await Run(server.FileUrl(8 * MiB, name: "d.bin", kbps: 1024, lie: true), 8 * MiB, 8);
    Console.WriteLine($"   {Stats(d.Job, d.Seconds)}");
    Report("D", d.Sha && d.Job.Stats.Fallbacks == 1 && !d.Job.AcceptsRanges, $"byte-exact={d.Sha}, resumable now={d.Job.AcceptsRanges}");

    // E: no range support at all.
    Console.WriteLine("-- E. no range support, 8 connections requested");
    var e = await Run(server.FileUrl(8 * MiB, name: "e.bin", ranges: false), 8 * MiB, 8);
    Console.WriteLine($"   {Stats(e.Job, e.Seconds)}");
    Report("E", e.Sha && e.Job.Stats.PeakConnections == 1, $"byte-exact={e.Sha}, peak conns={e.Job.Stats.PeakConnections}");

    // F: tiny file is not split.
    Console.WriteLine("-- F. 400 KiB file, 8 connections requested (too small to split)");
    var f = await Run(server.FileUrl(400 * 1024, name: "f.bin", kbps: 1024), 400 * 1024, 8);
    Console.WriteLine($"   {Stats(f.Job, f.Seconds)}");
    Report("F", f.Sha && f.Job.Stats.Splits == 0, $"byte-exact={f.Sha}, splits={f.Job.Stats.Splits}");

    Console.WriteLine(failures == 0 ? "All segmentation checks passed." : $"{failures} scenario(s) failed.");
    try
    {
        Directory.Delete(dir, true);
    }
    catch
    {
    }

    return failures == 0 ? 0 : 3;
}

static async Task<int> ResumeTest()
{
    await using var server = await TestServer.StartAsync();
    var root = Path.Combine(Path.GetTempPath(), "shadenet-resumetest-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);
    Console.WriteLine($"Test server {server.BaseUrl}, work dir {root}{Environment.NewLine}");
    var failures = 0;
    const long MiB = 1024 * 1024;

    static async Task<bool> WaitUntil(Func<bool> cond, double seconds = 60)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (cond()) return true;
            await Task.Delay(50);
        }

        return false;
    }

    void Report(bool ok, string detail)
    {
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"   {(ok ? "PASS" : "FAIL")}  {detail}");
        Console.ResetColor();
        Console.WriteLine();
        if (!ok) failures++;
    }

    string Dir(string name)
    {
        var d = Path.Combine(root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    string Pct(DownloadJob j) => $"{j.DownloadedBytes * 100.0 / j.TotalSize!.Value:0}%";

    bool Sha(DownloadJob j, long size, int seed = 0) =>
        j.State == DownloadState.Completed && ContentGen.Sha256HexOfFile(j.TargetPath) == ContentGen.Sha256Hex(size, seed);

    async Task<DownloadJob> StartAt(DownloadManager m, string url, int conns, double fraction)
    {
        var probe = await m.ProbeAsync(new RequestSpec(url));
        var job = m.Add(new AddDownloadRequest { Url = url, MaxConnections = conns }, probe);
        await WaitUntil(() => job.State == DownloadState.Downloading && job.DownloadedBytes >= job.TotalSize * fraction);
        return job;
    }

    async Task PauseAndWait(DownloadManager m, DownloadJob j)
    {
        m.Pause(j.Id);
        await WaitUntil(() => j.State == DownloadState.Paused);
    }

    async Task FinishAsync(DownloadManager m, DownloadJob j)
    {
        m.Start(j.Id);
        await WaitUntil(() => j.State is DownloadState.Completed or DownloadState.Failed);
    }

    // R1
    Console.WriteLine("-- R1. pause at ~35 %, resume: only the missing part is downloaded");
    {
        var size = 8 * MiB;
        var m = NewManager(Dir("r1"), 4);
        var job = await StartAt(m, server.FileUrl(size, name: "r1.bin", etag: "r1", kbps: 1024), 4, 0.35);
        await PauseAndWait(m, job);
        var atPause = job.DownloadedBytes;
        var served = server.BytesServed;
        Console.WriteLine($"   paused at {Pct(job)} ({Fmt(atPause)}), state {job.State}");
        await Task.Delay(1500);
        var idle = server.BytesServed - served;
        await FinishAsync(m, job);
        var extra = server.BytesServed - served - (size - atPause);
        Console.WriteLine($"   idle while paused: {Fmt(idle)} served; after resume server sent {Fmt(server.BytesServed - served)} for {Fmt(size - atPause)} missing (overhead {Fmt(Math.Max(0, extra))})");
        Report(Sha(job, size) && idle < 64 * 1024 && extra < size * 0.10, $"byte-exact={Sha(job, size)}");
    }

    // R2
    Console.WriteLine("-- R2. graceful app restart: engine stopped, state loaded from SQLite by a new manager, resume");
    {
        var size = 8 * MiB;
        var dir = Dir("r2");
        var db = Path.Combine(dir, "jobs.db");
        var m = NewManager(dir, 4, db);
        var job = await StartAt(m, server.FileUrl(size, name: "r2.bin", etag: "r2", kbps: 1024), 4, 0.40);
        await m.StopAllAsync();
        m.Dispose();
        Console.WriteLine($"   engine stopped at {Pct(job)}");

        var m2 = NewManager(dir, 4, db);
        m2.Load();
        var loaded = m2.Jobs.Single();
        Console.WriteLine($"   after restart: state {loaded.State}, {Pct(loaded)} on record, {loaded.Segments.Count} segments");
        var served = server.BytesServed;
        var before = loaded.DownloadedBytes;
        await FinishAsync(m2, loaded);
        var extra = server.BytesServed - served - (size - before);
        Report(Sha(loaded, size) && before > 0 && extra < size * 0.10,
            $"byte-exact={Sha(loaded, size)}, re-downloaded overhead {Fmt(Math.Max(0, extra))}");
        m2.Dispose();
    }

    // R3
    Console.WriteLine("-- R3. HARD KILL: a separate process downloads, we kill it mid-transfer, then resume from disk");
    {
        var size = 12 * MiB;
        var dir = Dir("r3");
        var db = Path.Combine(dir, "jobs.db");
        var url = server.FileUrl(size, name: "r3.bin", etag: "r3", kbps: 1024);
        var cli = System.Reflection.Assembly.GetEntryAssembly()!.Location;
        var baseline = server.BytesServed;
        var child = Process.Start(new ProcessStartInfo("dotnet", $"\"{cli}\" get \"{url}\" -o \"{dir}\" -c 4 --db \"{db}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        _ = child.StandardOutput.ReadToEndAsync();

        await WaitUntil(() => server.BytesServed - baseline >= size * 0.45);
        await Task.Delay(500);
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        Console.WriteLine($"   killed child process (pid {child.Id}) after the server had sent it {Fmt(server.BytesServed - baseline)}");

        var m = NewManager(dir, 4, db);
        m.Load();
        var job = m.Jobs.Single();
        var onRecord = job.DownloadedBytes;
        Console.WriteLine($"   recovered: state {job.State}, {Pct(job)} on record, part file {(File.Exists(job.PartPath) ? "present" : "MISSING")}");
        var served = server.BytesServed;
        await FinishAsync(m, job);
        var sent = server.BytesServed - served;
        Console.WriteLine($"   resumed run transferred {Fmt(sent)} (file is {Fmt(size)}, {Fmt(onRecord)} was already on record)");
        Report(Sha(job, size) && onRecord > 0 && sent < size - onRecord + size * 0.25,
            $"byte-exact={Sha(job, size)}");
        m.Dispose();
    }

    // R4
    Console.WriteLine("-- R4. the file on the server changes while paused: resume must refuse, not stitch two versions together");
    {
        var size = 6 * MiB;
        var m = NewManager(Dir("r4"), 2);
        var job = await StartAt(m, server.FileUrl(size, name: "r4.bin", etag: "r4", kbps: 1024), 2, 0.35);
        await PauseAndWait(m, job);
        server.Epoch = 1;
        await FinishAsync(m, job);
        server.Epoch = 0;
        Console.WriteLine($"   state {job.State}, kind {job.ErrorKind}: {job.Error}");
        Report(job.State == DownloadState.Failed && job.ErrorKind == JobErrorKind.FileChanged && File.Exists(job.PartPath),
            "refused to continue, partial data kept for the user to decide");
    }

    // R5
    Console.WriteLine("-- R5. partial file deleted while paused: restart from zero, still correct");
    {
        var size = 4 * MiB;
        var m = NewManager(Dir("r5"), 2);
        var job = await StartAt(m, server.FileUrl(size, name: "r5.bin", etag: "r5", kbps: 1024), 2, 0.35);
        await PauseAndWait(m, job);
        File.Delete(job.PartPath);
        await FinishAsync(m, job);
        Report(Sha(job, size), $"byte-exact={Sha(job, size)}");
    }

    // R6
    Console.WriteLine("-- R6. server without Range support: pause/resume must restart cleanly, never corrupt");
    {
        var size = 4 * MiB;
        var m = NewManager(Dir("r6"), 4);
        var job = await StartAt(m, server.FileUrl(size, name: "r6.bin", ranges: false, kbps: 2048), 4, 0.40);
        await PauseAndWait(m, job);
        var served = server.BytesServed;
        await FinishAsync(m, job);
        var sent = server.BytesServed - served;
        Console.WriteLine($"   resumed run had to send {Fmt(sent)} (whole file, as expected without ranges)");
        Report(Sha(job, size) && sent >= size, $"byte-exact={Sha(job, size)}");
    }

    // R7
    Console.WriteLine("-- R7. link expires while paused (403): fail clearly, then refresh address continues the same file");
    {
        var size = 6 * MiB;
        var m = NewManager(Dir("r7"), 2);
        var job = await StartAt(m, server.FileUrl(size, name: "r7.bin", etag: "r7", kbps: 1024, token: "old"), 2, 0.35);
        await PauseAndWait(m, job);
        var before = job.DownloadedBytes;
        server.ValidTokens.Remove("old");
        await FinishAsync(m, job);
        Console.WriteLine($"   state {job.State}, kind {job.ErrorKind}: {job.Error}");
        var expired = job.State == DownloadState.Failed && job.ErrorKind == JobErrorKind.AddressExpired;

        var served = server.BytesServed;
        m.RefreshAddress(job.Id, server.FileUrl(size, name: "r7.bin", etag: "r7", kbps: 1024, token: "new"));
        await WaitUntil(() => job.State is DownloadState.Completed or DownloadState.Failed);
        var extra = server.BytesServed - served - (size - before);
        Console.WriteLine($"   refreshed: {job.State}, overhead {Fmt(Math.Max(0, extra))}");
        Report(expired && Sha(job, size) && extra < size * 0.10, $"expired detected={expired}, byte-exact={Sha(job, size)}");
    }

    Console.WriteLine(failures == 0 ? "All resume checks passed." : $"{failures} scenario(s) failed.");
    try
    {
        Directory.Delete(root, true);
    }
    catch
    {
    }

    return failures == 0 ? 0 : 3;
}

static async Task<int> LimitTest()
{
    await using var server = await TestServer.StartAsync();
    var root = Path.Combine(Path.GetTempPath(), "shadenet-limittest-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);
    Console.WriteLine($"Unthrottled test server {server.BaseUrl}, work dir {root}{Environment.NewLine}");
    var failures = 0;
    const long KiB = 1024, MiB = 1024 * 1024, Big = 1024 * MiB;

    void Report(bool ok, string detail)
    {
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"   {(ok ? "PASS" : "FAIL")}  {detail}");
        Console.ResetColor();
        Console.WriteLine();
        if (!ok) failures++;
    }

    string Rate(double bps) => bps >= MiB ? $"{bps / MiB:0.00} MiB/s" : $"{bps / KiB:0.0} KiB/s";
    string Dev(double measured, double target) => $"{(measured - target) / target * 100:+0.0;-0.0;0.0}%";
    bool Within(double measured, double target, double tol) => Math.Abs(measured - target) <= target * tol;

    string Dir(string n)
    {
        var d = Path.Combine(root, n);
        Directory.CreateDirectory(d);
        return d;
    }

    DownloadJob Start(DownloadManager m, string name, int conns, long? limit = null, long size = Big, int? kbps = null) =>
        m.Add(new AddDownloadRequest
        {
            Url = server.FileUrl(size, name: name, kbps: kbps),
            FileName = name,
            MaxConnections = conns,
            SpeedLimit = limit,
        });

    async Task<double> RateOf(Func<long> bytes, double warmup = 1.5, double window = 4.0)
    {
        await Task.Delay(TimeSpan.FromSeconds(warmup));
        var b0 = bytes();
        var sw = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(window));
        return (bytes() - b0) / sw.Elapsed.TotalSeconds;
    }

    async Task Stop(DownloadManager m, params DownloadJob[] jobs)
    {
        foreach (var j in jobs) await m.RemoveAsync(j.Id, true);
    }

    // L1 -------------------------------------------------------------------------------------------------------
    Console.WriteLine("-- L1. global limit, 4 connections: achieved rate vs target");
    {
        var ok = true;
        foreach (var target in new[] { 128 * KiB, 512 * KiB, 2 * MiB, 8 * MiB })
        {
            var m = NewManager(Dir("l1"), 4);
            m.SetGlobalSpeedLimit(target, true);
            var job = Start(m, $"l1-{target}.bin", 4);
            var measured = await RateOf(() => job.DownloadedBytes);
            Console.WriteLine($"   target {Rate(target),-11} measured {Rate(measured),-11} {Dev(measured, target)}   (conns peak {job.Stats.PeakConnections})");
            ok &= Within(measured, target, 0.08);
            await Stop(m, job);
        }

        Report(ok, "every level within ±8 % of the limit");
    }

    // L2 -------------------------------------------------------------------------------------------------------
    Console.WriteLine("-- L2. per-job limit: job A capped at 512 KiB/s next to a busy neighbour B");
    {
        var ok = true;
        foreach (var (label, bLimit) in new (string, long?)[] { ("B capped at 30 MiB/s", 30 * MiB), ("B completely unlimited (~200 MiB/s, CPU-bound)", null) })
        {
            var m = NewManager(Dir("l2"), 2);
            var a = Start(m, "l2-a.bin", 2, 512 * KiB);
            var b = Start(m, "l2-b.bin", 2, bLimit);
            var ra = RateOf(() => a.DownloadedBytes);
            var rb = RateOf(() => b.DownloadedBytes);
            var (xa, xb) = (await ra, await rb);
            Console.WriteLine($"   {label}: A {Rate(xa)} ({Dev(xa, 512 * KiB)} vs limit), B {Rate(xb)}");
            ok &= Within(xa, 512 * KiB, 0.08);
            await Stop(m, a, b);
        }

        Report(ok, "capped job holds its limit while another job runs");
    }

    // L3 -------------------------------------------------------------------------------------------------------
    Console.WriteLine("-- L3. global and per-job limit together: the stricter one wins");
    {
        var ok = true;
        foreach (var (global, perJob, expect) in new[] { (2 * MiB, 512 * KiB, 512 * KiB), (512 * KiB, 2 * MiB, 512 * KiB) })
        {
            var m = NewManager(Dir("l3"), 2);
            m.SetGlobalSpeedLimit(global, true);
            var job = Start(m, $"l3-{global}.bin", 2, perJob);
            var measured = await RateOf(() => job.DownloadedBytes);
            Console.WriteLine($"   global {Rate(global),-10} job {Rate(perJob),-10} -> {Rate(measured),-11} (expected ~{Rate(expect)}, {Dev(measured, expect)})");
            ok &= Within(measured, expect, 0.08);
            await Stop(m, job);
        }

        Report(ok, "min(global, job) is what you get");
    }

    // L4 -------------------------------------------------------------------------------------------------------
    Console.WriteLine("-- L4. change the limit while the download is running (sampled every 0.5 s)");
    {
        var m = NewManager(Dir("l4"), 4);
        var job = Start(m, "l4.bin", 4, size: 4 * Big);
        var samples = new List<(double T, double Bps)>();
        var sw = Stopwatch.StartNew();
        var last = 0L;
        var changes = new (double At, long Limit, string Label)[]
        {
            (2.0, 300 * KiB, "limit 300 KiB/s"), (6.0, 3 * MiB, "limit 3 MiB/s"), (10.0, 0, "limit off"),
        };
        var next = 0;
        var line = new System.Text.StringBuilder("   ");
        while (sw.Elapsed.TotalSeconds < 13)
        {
            await Task.Delay(500);
            var now = job.DownloadedBytes;
            var t = sw.Elapsed.TotalSeconds;
            samples.Add((t, (now - last) / 0.5));
            last = now;
            line.Append($"{t,4:0.0}s {Rate(samples[^1].Bps),-11}");
            if (samples.Count % 3 == 0) line.Append("\n   ");
            if (next < changes.Length && t >= changes[next].At)
            {
                m.SetGlobalSpeedLimit(changes[next].Limit, changes[next].Limit > 0);
                line.Append($"<- {changes[next].Label}  ");
                next++;
            }
        }

        Console.WriteLine(line.ToString().TrimEnd());
        double Mean(double from, double to) => samples.Where(s => s.T >= from && s.T < to).Average(s => s.Bps);
        var slow = Mean(3.5, 6.0);
        var mid = Mean(7.5, 10.0);
        var free = samples.Where(x => x.T >= 10.5).Max(x => x.Bps);
        Console.WriteLine($"   steady state: {Rate(slow)} (target 300 KiB/s), {Rate(mid)} (target 3 MiB/s), unlimited (peak) {Rate(free)}");
        Report(Within(slow, 300 * KiB, 0.10) && Within(mid, 3 * MiB, 0.10) && free > 20 * MiB,
            "limit changes take effect within about a second, in both directions");
        await Stop(m, job);
    }

    // L5 -------------------------------------------------------------------------------------------------------
    Console.WriteLine("-- L5. Pause must stay instant even when heavily throttled");
    {
        var ok = true;
        foreach (var limit in new[] { 20 * KiB, 2 * KiB })
        {
            var m = NewManager(Dir("l5"), 2);
            m.SetGlobalSpeedLimit(limit, true);
            var job = Start(m, $"l5-{limit}.bin", 2);
            await Task.Delay(1500);
            var sw = Stopwatch.StartNew();
            m.Pause(job.Id);
            while (job.State != DownloadState.Paused && sw.Elapsed.TotalSeconds < 10) await Task.Delay(5);
            Console.WriteLine($"   limit {Rate(limit),-10} -> paused after {sw.ElapsedMilliseconds} ms");
            ok &= job.State == DownloadState.Paused && sw.ElapsedMilliseconds < 500;
            await Stop(m, job);
        }

        Report(ok, "pause completes in under 500 ms at 20 KiB/s and 2 KiB/s");
    }

    // L6 -------------------------------------------------------------------------------------------------------
    Console.WriteLine("-- L6. tiny limit (8 KiB/s) with 8 connections requested: at most 2 are used");
    {
        var m = NewManager(Dir("l6"), 8);
        m.SetGlobalSpeedLimit(8 * KiB, true);
        var job = Start(m, "l6.bin", 8);
        var measured = await RateOf(() => job.DownloadedBytes, warmup: 2.0, window: 8.0);
        Console.WriteLine($"   rate {Rate(measured)} (target 8 KiB/s, {Dev(measured, 8 * KiB)}), peak connections {job.Stats.PeakConnections}");
        Report(job.Stats.PeakConnections <= 2 && Within(measured, 8 * KiB, 0.20), "connection cap and rate both honoured");
        await Stop(m, job);
    }

    // L6b ------------------------------------------------------------------------------------------------------
    Console.WriteLine("-- L6b. no point adding connections once the limit is saturated (2 MiB/s limit, 8 connections requested)");
    {
        var m = NewManager(Dir("l6b"), 8);
        m.SetGlobalSpeedLimit(2 * MiB, true);
        var job = Start(m, "l6b.bin", 8);
        await Task.Delay(9000);
        Console.WriteLine($"   peak connections after 9 s: {job.Stats.PeakConnections}");
        Report(job.Stats.PeakConnections <= 2, "ramp-up stops at >= 85 % of the limit instead of opening all 8");
        await Stop(m, job);
    }

    // L8 -------------------------------------------------------------------------------------------------------
    Console.WriteLine("-- L8. several connections sharing one global limit (server caps each connection at 512 KiB/s, limit 1.5 MiB/s)");
    {
        var m = NewManager(Dir("l8"), 8);
        m.SetGlobalSpeedLimit(3 * MiB / 2, true);
        var job = Start(m, "l8.bin", 8, kbps: 512);
        var measured = await RateOf(() => job.DownloadedBytes, warmup: 8.0, window: 6.0);
        Console.WriteLine($"   rate {Rate(measured)} (target 1.50 MiB/s, {Dev(measured, 1.5 * MiB)}), peak connections {job.Stats.PeakConnections}");
        Report(job.Stats.PeakConnections >= 3 && Within(measured, 1.5 * MiB, 0.08), "the aggregate of all connections is what gets limited");
        await Stop(m, job);
    }

    // L7 -------------------------------------------------------------------------------------------------------
    Console.WriteLine("-- L7. a limited download still ends up byte-exact and takes about size / limit");
    {
        var size = 3 * MiB;
        var m = NewManager(Dir("l7"), 4);
        m.SetGlobalSpeedLimit(2 * MiB, true);
        var sw = Stopwatch.StartNew();
        var job = Start(m, "l7.bin", 4, size: size);
        while (job.State is not (DownloadState.Completed or DownloadState.Failed) && sw.Elapsed.TotalSeconds < 30) await Task.Delay(20);
        var sha = job.State == DownloadState.Completed && ContentGen.Sha256HexOfFile(job.TargetPath) == ContentGen.Sha256Hex(size);
        Console.WriteLine($"   {Fmt(size)} at 2 MiB/s took {sw.Elapsed.TotalSeconds:0.00} s (ideal 1.50 s)");
        Report(sha && sw.Elapsed.TotalSeconds is > 1.1 and < 2.2, $"byte-exact={sha}");
    }

    Console.WriteLine(failures == 0 ? "All limiter checks passed." : $"{failures} scenario(s) failed.");
    try
    {
        Directory.Delete(root, true);
    }
    catch
    {
    }

    return failures == 0 ? 0 : 3;
}

static string Fmt(long bytes)
{
    string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
    double v = bytes;
    var i = 0;
    while (v >= 1024 && i < units.Length - 1)
    {
        v /= 1024;
        i++;
    }

    return i == 0 ? $"{v:0} {units[i]}" : $"{v:0.0} {units[i]}";
}
