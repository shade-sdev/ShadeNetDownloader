using System.Diagnostics;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Storage;
using ShadeNet.TestServer;
using Srv = ShadeNet.TestServer.TestServer;

// Timing-sensitive tests (rates, ramp-up) must not compete for CPU with each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ShadeNet.Core.Tests;

public static class Cat
{
    public const string Category = "Category";
    public const string Slow = "Slow";
}

/// <summary>A test server, a scratch directory and the managers created against it - all cleaned up on dispose.</summary>
public sealed class Harness : IAsyncDisposable
{
    public const long KiB = 1024;
    public const long MiB = 1024 * 1024;

    readonly List<DownloadManager> _managers = new();

    Harness(Srv server, string root)
    {
        Server = server;
        Root = root;
    }

    public Srv Server { get; }
    public string Root { get; }

    public static async Task<Harness> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "shadenet-tests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(root);
        return new Harness(await Srv.StartAsync(), root);
    }

    public string Dir(string name = "dl")
    {
        var d = Path.Combine(Root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Settings tuned for tests: no category sub-folders, quick retries, no Mark-of-the-Web noise.</summary>
    public EngineSettings Settings(string? dir = null, int connections = 4, Action<EngineSettings>? tweak = null)
    {
        var s = new EngineSettings
        {
            DownloadRoot = dir ?? Dir(),
            UseCategoryFolders = false,
            DefaultConnections = connections,
            WriteMarkOfTheWeb = false,
            RetryBaseDelay = TimeSpan.FromMilliseconds(100),
            RetryMaxDelay = TimeSpan.FromMilliseconds(600),
        };
        tweak?.Invoke(s);
        return s;
    }

    public DownloadManager NewManager(string? dir = null, int connections = 4, Action<EngineSettings>? tweak = null, string? db = null)
    {
        var m = new DownloadManager(Settings(dir, connections, tweak), db == null ? null : new JobStore(db));
        _managers.Add(m);
        return m;
    }

    public DownloadJob Add(DownloadManager m, string url, int connections = 4, long? limit = null, string? fileName = null,
        AddMode mode = AddMode.Start, Dictionary<string, string>? headers = null, string? cookies = null, string? referer = null,
        string? userAgent = null) =>
        m.Add(new AddDownloadRequest
        {
            Url = url,
            FileName = fileName,
            MaxConnections = connections,
            SpeedLimit = limit,
            Mode = mode,
            Headers = headers,
            Cookies = cookies,
            Referer = referer,
            UserAgent = userAgent,
        });

    public static async Task WaitUntil(Func<bool> condition, double seconds = 60, string? what = null, Func<string>? diagnostics = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new TimeoutException($"Timed out after {seconds:0}s waiting for: {what ?? "condition"}"
                                   + (diagnostics != null ? Environment.NewLine + diagnostics() : ""));
    }

    /// <summary>Everything useful to know about a job that did not finish.</summary>
    public static string Describe(DownloadJob job)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"state {job.State}, error {job.ErrorKind} {job.Error}");
        sb.AppendLine($"downloaded {job.DownloadedBytes:N0} of {job.TotalSize:N0}; splits {job.Stats.Splits}, takeovers {job.Stats.Takeovers}, " +
                      $"retries {job.Stats.Retries}, fallbacks {job.Stats.Fallbacks}, peak conns {job.Stats.PeakConnections}");
        lock (job.Sync)
        {
            foreach (var s in job.Segments)
                sb.AppendLine($"  seg {s.Id,4} [{s.Start:N0}..{s.End:N0}] done {s.Downloaded:N0}/{s.Length:N0} {s.State} owner {(s.Owner == null ? "-" : s.Owner.Id.ToString())}");
        }

        return sb.ToString();
    }

    public static Task Finished(DownloadJob job, double seconds = 60) =>
        WaitUntil(() => job.State is DownloadState.Completed or DownloadState.Failed, seconds,
            $"job '{job.FileName}' to finish", () => Describe(job));

    public static async Task<DownloadJob> Download(Harness h, DownloadManager m, string url, int connections = 4, double seconds = 60)
    {
        var job = h.Add(m, url, connections);
        await Finished(job, seconds);
        return job;
    }

    public static Task PauseAsync(DownloadManager m, DownloadJob job)
    {
        m.Pause(job.Id);
        return WaitUntil(() => job.State == DownloadState.Paused, 15, "job to pause");
    }

    public static Task ReachAsync(DownloadJob job, double fraction) =>
        WaitUntil(() => job.State == DownloadState.Downloading && job.TotalSize is > 0
                        && job.DownloadedBytes >= job.TotalSize * fraction, 60, $"job to reach {fraction:P0}");

    public static bool IsByteExact(DownloadJob job, long size, int seed = 0) =>
        job.State == DownloadState.Completed && File.Exists(job.TargetPath)
        && new FileInfo(job.TargetPath).Length == size
        && ContentGen.Sha256HexOfFile(job.TargetPath) == ContentGen.Sha256Hex(size, seed);

    public static void AssertByteExact(DownloadJob job, long size, int seed = 0)
    {
        Assert.True(job.State == DownloadState.Completed, $"job ended {job.State} ({job.ErrorKind}): {job.Error}");
        Assert.True(IsByteExact(job, size, seed), "downloaded file differs from the expected content");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var m in _managers)
        {
            try
            {
                await m.StopAllAsync();
                m.Dispose();
            }
            catch
            {
                // ignore shutdown noise
            }
        }

        await Server.DisposeAsync();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(Root, true);
                break;
            }
            catch
            {
                await Task.Delay(100);
            }
        }
    }
}
