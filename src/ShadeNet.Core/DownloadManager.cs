using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShadeNet.Core.Hls;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Storage;
using ShadeNet.Core.Util;

namespace ShadeNet.Core;

/// <summary>
/// The public face of the engine: owns the job list, the queue, the global speed limiter and one
/// <see cref="JobRunner"/> per running download. UI-agnostic; all events fire on thread-pool threads.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    sealed class RunningJob
    {
        public required IJobRunner Runner { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public Task Task { get; set; } = Task.CompletedTask;
        public volatile bool Removing;

        /// <summary>Started by the queue (not by the user pressing Start): "stop the queue" pauses only these.</summary>
        public bool FromQueue { get; init; }
    }

    readonly object _gate = new();
    readonly List<DownloadJob> _jobs = new();
    readonly Dictionary<Guid, RunningJob> _running = new();
    readonly JobStore? _store;
    readonly ILogger _logger;

    public DownloadManager(EngineSettings settings, JobStore? store = null, ILogger? logger = null)
    {
        Settings = settings;
        _store = store;
        _logger = logger ?? NullLogger.Instance;
        GlobalLimiter.BytesPerSecond = Math.Max(0, settings.GlobalSpeedLimit);   // the limit chosen last time
    }

    public EngineSettings Settings { get; }
    public SpeedLimiter GlobalLimiter { get; } = new();

    public event Action<DownloadJob>? JobAdded;
    public event Action<Guid>? JobRemoved;
    public event Action<JobSnapshot>? JobUpdated;
    public event Action<DownloadJob>? JobCompleted;

    // ------------------------------------------------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Loads persisted jobs. Anything that was mid-transfer when the app died comes back paused (or queued).</summary>
    public void Load()
    {
        if (_store == null) return;

        var loaded = _store.LoadAll();
        lock (_gate)
        {
            foreach (var job in loaded)
            {
                if (job.State is DownloadState.Probing or DownloadState.Downloading or DownloadState.Stopping)
                    job.State = Settings.AutoResumeOnStartup ? DownloadState.Queued : DownloadState.Paused;

                foreach (var s in job.Segments)
                {
                    if (s.State == SegmentState.Active) s.State = SegmentState.Pending;
                }

                _jobs.Add(job);
            }
        }

        foreach (var job in loaded) JobAdded?.Invoke(job);
        Pump();
    }

    public IReadOnlyList<DownloadJob> Jobs
    {
        get
        {
            lock (_gate) return _jobs.ToArray();
        }
    }

    public DownloadJob? Find(Guid id)
    {
        lock (_gate) return _jobs.FirstOrDefault(j => j.Id == id);
    }

    /// <summary>Same URL (original or final) already in the list and not finished with errors → ask before adding twice.</summary>
    public DownloadJob? FindDuplicate(string url)
    {
        lock (_gate)
        {
            return _jobs.FirstOrDefault(j =>
                string.Equals(j.Url, url, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.FinalUrl, url, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task StopAllAsync()
    {
        List<RunningJob> running;
        lock (_gate)
        {
            running = _running.Values.ToList();
            foreach (var rj in running) rj.Cts.Cancel();
        }

        try
        {
            await Task.WhenAll(running.Select(r => r.Task)).ConfigureAwait(false);
        }
        catch
        {
            // runners never throw
        }
    }

    public void Dispose()
    {
        StopAllAsync().GetAwaiter().GetResult();
        _store?.Dispose();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Probe + add
    // ------------------------------------------------------------------------------------------------------------

    public async Task<ProbeResult> ProbeAsync(RequestSpec spec, CancellationToken ct = default)
    {
        using var http = HttpFetcher.CreateClient(2, Settings);
        return await HttpProbe.ProbeAsync(http, spec, Settings.UserAgent, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Like <see cref="ProbeAsync"/>, but the response stays open and becomes the first download connection when the
    /// result is passed to <see cref="Add"/>, so the address is requested only once (sites with one-time links need this).
    /// Call <see cref="ProbeResult.Discard"/> when the result will not be used. A kept response goes stale after about
    /// <see cref="EngineSettings.HeldProbeLifetime"/>; the download then simply requests the address again.
    /// </summary>
    public async Task<ProbeResult> ProbeAndHoldAsync(RequestSpec spec, CancellationToken ct = default)
    {
        var http = HttpFetcher.CreateClient(Settings.DefaultConnections, Settings);
        ProbeResult? result = null;
        try
        {
            result = await HttpProbe.ProbeAsync(http, spec, Settings.UserAgent, keepOpen: true, clientToOwn: http, rangeStart: 0, ct)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (result?.Live == null) http.Dispose();
        }
    }

    /// <summary>True when an address (or the content type it answered with) is an HLS playlist.</summary>
    public static bool IsStreamAddress(string url, string? contentType = null) => HlsClient.LooksLikeHls(url, contentType);

    /// <summary>
    /// Reads an HLS playlist and reports what it offers: the qualities, the length, and whether it can be downloaded at all
    /// (live and DRM streams cannot). Throws when the address is not a playlist.
    /// </summary>
    public async Task<HlsStreamInfo> DescribeStreamAsync(RequestSpec spec, CancellationToken ct = default)
    {
        using var http = HttpFetcher.CreateClient(2, Settings);
        return await HlsClient.DescribeAsync(http, spec, Settings.UserAgent, ct).ConfigureAwait(false);
    }

    public DownloadJob Add(AddDownloadRequest request, ProbeResult? probe = null)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Only http:// and https:// addresses are supported.", nameof(request));

        var name = FileNameHelper.Sanitize(request.FileName
                                           ?? (request.Stream != null ? HlsClient.SuggestName(uri) + ".mp4" : null)
                                           ?? probe?.FileName ?? GuessName(uri));
        if (request.Stream != null) name = request.Stream.Record ? RecordingFileName(name) : StreamFileName(name);
        var category = request.Category ?? Settings.CategoryOf(name);
        var dir = request.SaveDirectory ?? Settings.ResolveCategoryFolder(category);
        Directory.CreateDirectory(dir);

        DownloadJob job;
        lock (_gate)
        {
            name = FileNameHelper.MakeUnique(dir, name,
                p => _jobs.Any(j => string.Equals(j.TargetPath, p, StringComparison.OrdinalIgnoreCase)));

            job = new DownloadJob
            {
                Url = request.Url,
                Referer = request.Referer,
                Cookies = request.Cookies,
                UserAgent = request.UserAgent,
                Headers = request.Headers ?? new(),
                FileName = name,
                SaveDirectory = dir,
                Category = category,
                MaxConnections = request.MaxConnections ?? Settings.DefaultConnections,
                SpeedLimit = request.SpeedLimit,
                State = request.Mode == AddMode.Hold ? DownloadState.Paused : DownloadState.Queued,
                NameProvisional = request.FileName == null && probe == null && request.Stream == null,
                FolderProvisional = request.SaveDirectory == null && probe == null && request.Stream == null,
            };

            if (request.Stream is { } stream)
            {
                job.Kind = DownloadKind.Hls;
                job.Probed = true;
                job.Hls = new HlsJobData { Height = stream.Height, Bandwidth = stream.Bandwidth, AudioName = stream.AudioName, Subtitles = stream.Subtitles, EmbedSubtitles = stream.EmbedSubtitles,
                    Direct = stream.Direct, AudioUrl = stream.AudioUrl, Seconds = stream.Seconds, Record = stream.Record, Live = stream.Record,
                };
            }

            if (probe != null)
            {
                job.Probed = true;
                job.FinalUrl = probe.FinalUrl;
                job.TotalSize = probe.TotalSize;
                job.AcceptsRanges = probe.AcceptsRanges;
                job.ContentType = probe.ContentType;
                job.ETag = probe.ETag;
                job.LastModified = probe.LastModified;
                // Only a download that starts right now can use the kept connection; queued or held ones would sit on it.
                if (request.Mode == AddMode.Start)
                {
                    job.FirstResponse = probe.DetachLive();
                    job.FirstResponseStart = 0;
                }
                else
                {
                    probe.Discard();
                }
            }

            _jobs.Add(job);
        }

        _store?.SaveJob(job);
        JobAdded?.Invoke(job);
        JobUpdated?.Invoke(Snapshot(job));

        if (request.Mode == AddMode.Start) StartNow(job);
        else if (request.Mode == AddMode.Queue) Pump();

        return job;
    }

    /// <summary>A stream is saved as a video: keep a video extension the user chose, anything else (or ".m3u8") becomes ".mp4".</summary>
    static string StreamFileName(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".mp4" or ".mkv" or ".ts" or ".mov" or ".m4v") return name;
        return (ext is ".m3u8" or ".m3u" ? Path.ChangeExtension(name, null) : name) + ".mp4";
    }

    /// <summary>A recorded live stream is a Matroska file (it takes whatever the stream carries, and survives being cut off).</summary>
    static string RecordingFileName(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext == ".mkv") return name;
        return (ext is ".mp4" or ".flv" or ".ts" or ".mov" or ".m4v" or ".m3u8" ? Path.ChangeExtension(name, null) : name) + ".mkv";
    }

    static string GuessName(Uri uri)
    {
        var last = uri.Segments.Length > 0 ? uri.Segments[^1].TrimEnd('/') : "";
        return last.Length > 0 ? Uri.UnescapeDataString(last) : uri.Host;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Control
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Starts (or resumes) a job immediately, bypassing the concurrent-jobs limit.</summary>
    public void Start(Guid id)
    {
        if (Find(id) is { } job) StartNow(job);
    }

    void StartNow(DownloadJob job) => StartNow(job, fromQueue: false);

    void StartNow(DownloadJob job, bool fromQueue)
    {
        lock (_gate)
        {
            if (_running.ContainsKey(job.Id) || job.State == DownloadState.Completed) return;
            LaunchLocked(job, fromQueue);
        }
    }

    /// <summary>Puts a job at the back of the queue; it starts when a slot is free.</summary>
    public void Enqueue(Guid id)
    {
        var job = Find(id);
        if (job == null || job.State is DownloadState.Completed || job.IsActive) return;
        job.State = DownloadState.Queued;
        job.Error = null;
        job.ErrorKind = JobErrorKind.None;
        _store?.SaveJob(job);
        JobUpdated?.Invoke(Snapshot(job));
        Pump();
    }

    void LaunchLocked(DownloadJob job, bool fromQueue)
    {
        var ctx = new RunnerContext
        {
            Settings = Settings,
            GlobalLimiter = GlobalLimiter,
            Store = _store,
            Logger = _logger,
            Publish = s => JobUpdated?.Invoke(s),
        };

        IJobRunner runner = job.Kind == DownloadKind.Hls
            ? job.Hls?.Record == true ? new RecordRunner(job, ctx) : new HlsRunner(job, ctx)
            : new JobRunner(job, ctx);
        var cts = new CancellationTokenSource();
        var rj = new RunningJob { Runner = runner, Cts = cts, FromQueue = fromQueue };
        _running[job.Id] = rj;
        job.State = DownloadState.Probing;

        rj.Task = Task.Run(async () =>
        {
            RunOutcome outcome;
            try
            {
                outcome = await runner.RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Runner crashed for {Id}", job.Id);
                job.State = DownloadState.Failed;
                job.Error = ex.Message;
                outcome = RunOutcome.Failed;
            }

            lock (_gate) _running.Remove(job.Id);
            cts.Dispose();

            if (outcome == RunOutcome.Completed && !rj.Removing) JobCompleted?.Invoke(job);
            Pump();
        });
    }

    // ------------------------------------------------------------------------------------------------------------
    // The queue: start / stop, order
    // ------------------------------------------------------------------------------------------------------------

    volatile bool _queueRunning = true;

    /// <summary>
    /// Whether queued downloads are started as slots free up. When stopped, queued downloads wait (a download the user
    /// starts by hand still runs).
    /// </summary>
    public bool QueueRunning => _queueRunning;

    public event Action? QueueStateChanged;

    /// <summary>Raised when the order of the queued downloads changed.</summary>
    public event Action? QueueOrderChanged;

    /// <param name="running">Start (<c>true</c>) or stop (<c>false</c>) taking downloads from the queue.</param>
    /// <param name="pauseQueueDownloads">When stopping: also pause the downloads the queue started (not the ones started by hand).</param>
    public void SetQueueRunning(bool running, bool pauseQueueDownloads = false)
    {
        var changed = _queueRunning != running;
        _queueRunning = running;

        if (!running && pauseQueueDownloads)
        {
            List<Guid> fromQueue;
            lock (_gate) fromQueue = _running.Where(kv => kv.Value.FromQueue).Select(kv => kv.Key).ToList();
            foreach (var id in fromQueue) Pause(id);
        }

        if (running) Pump();
        if (changed) QueueStateChanged?.Invoke();
    }

    /// <summary>Call after changing <see cref="Settings"/> that the manager acts on at once (e.g. how many downloads run together).</summary>
    public void ApplySettings()
    {
        Settings.Normalize();
        Pump();
    }

    /// <summary>The queued downloads in the order they will start.</summary>
    public IReadOnlyList<DownloadJob> QueuedInOrder()
    {
        lock (_gate)
        {
            return _jobs.Where(j => j.State == DownloadState.Queued && !_running.ContainsKey(j.Id))
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.CreatedAt)
                .ToList();
        }
    }

    /// <summary>Moves a queued download within the queue.</summary>
    /// <returns><c>false</c> when the download is not queued or is already where it should go.</returns>
    public bool MoveInQueue(Guid id, QueueMove move)
    {
        List<DownloadJob> order;
        int from;
        lock (_gate)
        {
            order = _jobs.Where(j => j.State == DownloadState.Queued && !_running.ContainsKey(j.Id))
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.CreatedAt)
                .ToList();
            from = order.FindIndex(j => j.Id == id);
            if (from < 0) return false;

            var to = move switch
            {
                QueueMove.Top => 0,
                QueueMove.Up => Math.Max(0, from - 1),
                QueueMove.Down => Math.Min(order.Count - 1, from + 1),
                QueueMove.Bottom => order.Count - 1,
                _ => from,
            };
            if (to == from) return false;

            var job = order[from];
            order.RemoveAt(from);
            order.Insert(to, job);

            // The queue is a list; priorities are how the order is stored: the first gets the highest number.
            for (var i = 0; i < order.Count; i++) order[i].Priority = order.Count - i;
        }

        foreach (var job in order)
        {
            _store?.SaveJob(job);
            JobUpdated?.Invoke(Snapshot(job));
        }

        QueueOrderChanged?.Invoke();
        return true;
    }

    void Pump()
    {
        lock (_gate)
        {
            if (!QueueRunning) return;   // the queue is stopped: queued downloads wait until it is started
            var slots = Settings.MaxConcurrentJobs - _running.Count;
            if (slots <= 0) return;

            foreach (var job in _jobs
                         .Where(j => j.State == DownloadState.Queued && !_running.ContainsKey(j.Id))
                         .OrderByDescending(j => j.Priority)
                         .ThenBy(j => j.CreatedAt)
                         .ToList())
            {
                if (slots-- <= 0) break;
                LaunchLocked(job, fromQueue: true);
            }
        }
    }

    public void Pause(Guid id)
    {
        var job = Find(id);
        if (job == null) return;

        RunningJob? rj;
        lock (_gate) _running.TryGetValue(id, out rj);

        if (rj != null)
        {
            job.State = DownloadState.Stopping;
            JobUpdated?.Invoke(Snapshot(job));
            rj.Cts.Cancel();
        }
        else if (job.State == DownloadState.Queued)
        {
            job.State = DownloadState.Paused;
            _store?.SaveJob(job);
            JobUpdated?.Invoke(Snapshot(job));
        }
    }

    /// <summary>
    /// "Stop and save" for a recording of a live stream: stop taking new pieces, join what has been recorded and finish.
    /// (Pause would keep the job open to continue later.)
    /// </summary>
    public bool FinishRecording(Guid id)
    {
        RunningJob? rj;
        lock (_gate) _running.TryGetValue(id, out rj);
        if (rj == null) return false;
        rj.Runner.RequestFinish();
        return true;
    }

    public void PauseAll()
    {
        foreach (var j in Jobs)
            if (j.State is DownloadState.Queued or DownloadState.Probing or DownloadState.Downloading)
                Pause(j.Id);
    }

    public void ResumeAll()
    {
        foreach (var j in Jobs)
            if (j.State is DownloadState.Paused or DownloadState.Failed)
                Enqueue(j.Id);
    }

    public async Task RemoveAsync(Guid id, bool deleteFiles)
    {
        DownloadJob? job;
        RunningJob? rj;
        lock (_gate)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job == null) return;
            _jobs.Remove(job);
            _running.TryGetValue(id, out rj);
        }

        if (rj != null)
        {
            rj.Removing = true;
            rj.Cts.Cancel();
            await rj.Task.ConfigureAwait(false);
        }

        _store?.Delete(id);

        // A partial file is useless without its job, so it always goes; the finished file only if asked.
        if (job.State != DownloadState.Completed)
        {
            TryDelete(job.PartPath);
            TryDeleteFolder(job.WorkFolder);   // the finished pieces of a stream
        }

        if (deleteFiles && job.State == DownloadState.Completed) TryDelete(job.TargetPath);

        JobRemoved?.Invoke(id);
    }

    static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // locked or already gone
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // locked or already gone
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Settings applied to jobs
    // ------------------------------------------------------------------------------------------------------------

    public void SetGlobalSpeedLimit(long bytesPerSecond, bool enabled)
    {
        var limit = enabled ? Math.Max(0, bytesPerSecond) : 0;
        GlobalLimiter.BytesPerSecond = limit;
        Settings.GlobalSpeedLimit = limit;   // remembered for the next start (the app saves its settings)
    }

    public void SetJobSpeedLimit(Guid id, long? bytesPerSecond)
    {
        var job = Find(id);
        if (job == null) return;
        job.SpeedLimit = bytesPerSecond is > 0 ? bytesPerSecond : null;

        lock (_gate)
        {
            if (_running.TryGetValue(id, out var rj)) rj.Runner.JobLimiter.BytesPerSecond = job.SpeedLimit ?? 0;
        }

        _store?.SaveJob(job);
    }

    public void SetMaxConnections(Guid id, int connections)
    {
        var job = Find(id);
        if (job == null) return;
        job.MaxConnections = Math.Clamp(connections, 1, 32);
        _store?.SaveJob(job);
    }

    /// <summary>
    /// Looks for the same file on the page the download came from (<see cref="LinkRefresher"/>) and, when exactly one
    /// link qualifies, continues the download from that address.
    /// </summary>
    public async Task<RefreshResult> RefreshFromOwnerPageAsync(Guid id, CancellationToken ct = default)
    {
        var job = Find(id);
        if (job == null || job.IsActive || job.State == DownloadState.Completed)
            return new RefreshResult(RefreshOutcome.NotFound, null, "This download cannot be refreshed right now.");

        var result = await LinkRefresher.FindNewAddressAsync(job, Settings, ct).ConfigureAwait(false);
        if (result.Outcome == RefreshOutcome.Found && result.NewUrl != null) RefreshAddress(id, result.NewUrl);
        return result;
    }

    /// <summary>
    /// "Refresh address": the old link expired, so continue the same file from a freshly captured URL.
    /// The probe on restart verifies size and validators still match.
    /// </summary>
    public void RefreshAddress(Guid id, string newUrl, string? cookies = null, string? referer = null,
        string? userAgent = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var job = Find(id);
        if (job == null || job.IsActive) return;

        job.Url = newUrl;
        job.FinalUrl = null;
        job.Probed = false;
        if (cookies != null) job.Cookies = cookies;
        if (referer != null) job.Referer = referer;
        if (userAgent != null) job.UserAgent = userAgent;
        if (headers != null) job.Headers = new Dictionary<string, string>(headers);
        _store?.SaveJob(job);
        StartNow(job);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Snapshots
    // ------------------------------------------------------------------------------------------------------------

    public JobSnapshot Snapshot(DownloadJob job)
    {
        if (job.Kind == DownloadKind.Hls && job.Hls is { } hls)
        {
            var finished = job.State == DownloadState.Completed && job.TotalSize is not null;
            return new JobSnapshot(job.Id, job.State, job.FileName, job.SaveDirectory,
                finished ? job.TotalSize!.Value : hls.BytesDone, finished ? job.TotalSize : hls.EstimatedTotal,
                0, null, 0, Array.Empty<SegmentInfo>(), true, job.Error, job.ErrorKind, 0, 0, job.Stats.Retries, 0,
                job.Stats.PeakConnections, null, hls.Parts, hls.PartsDone, hls.Live, hls.SecondsDone);
        }

        long downloaded;
        SegmentInfo[] infos;
        lock (job.Sync)
        {
            downloaded = 0;
            infos = new SegmentInfo[job.Segments.Count];
            for (var i = 0; i < infos.Length; i++)
            {
                downloaded += job.Segments[i].Downloaded;
                infos[i] = job.Segments[i].ToInfo();
            }
        }

        if (job.State == DownloadState.Completed && job.TotalSize is { } t) downloaded = t;

        return new JobSnapshot(job.Id, job.State, job.FileName, job.SaveDirectory, downloaded, job.TotalSize, 0, null, 0,
            infos, job.AcceptsRanges, job.Error, job.ErrorKind, job.Stats.Splits, job.Stats.Takeovers,
            job.Stats.Retries, job.Stats.Fallbacks, job.Stats.PeakConnections);
    }
}
