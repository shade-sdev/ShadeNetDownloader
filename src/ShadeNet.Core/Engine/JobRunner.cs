using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Storage;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Engine;

public enum RunOutcome
{
    Completed,
    Paused,
    Failed,
}

internal sealed class RunnerContext
{
    public required EngineSettings Settings { get; init; }
    public required SpeedLimiter GlobalLimiter { get; init; }
    public JobStore? Store { get; init; }
    public required ILogger Logger { get; init; }
    public required Action<JobSnapshot> Publish { get; init; }

    /// <summary>Connection limits sites were seen to enforce, shared by the downloads of one manager.</summary>
    public HostConnectionMemory? HostLimits { get; init; }
}

/// <summary>
/// Drives one download from probe to finished file: preallocates the target, runs <see cref="ConnectionSlot"/> workers
/// under the <see cref="SegmentScheduler"/>, checkpoints progress, and survives pause / crash / server misbehaviour.
/// </summary>
internal sealed class JobRunner : IJobRunner
{
    readonly DownloadJob _job;
    readonly RunnerContext _ctx;
    readonly SegmentScheduler _scheduler;
    readonly SpeedMeter _jobMeter = new();
    readonly Random _rng = new();
    readonly object _firstLock = new();
    SessionCookies? _jar;

    public JobRunner(DownloadJob job, RunnerContext ctx)
    {
        _job = job;
        _ctx = ctx;
        _scheduler = new SegmentScheduler(job, ctx.Settings.MinSegmentBytes, ctx.Settings.LookaheadSeconds,
            ctx.Settings.TakeoverThresholdBytes);
        JobLimiter = new SpeedLimiter(job.SpeedLimit ?? 0);
    }

    public SpeedLimiter JobLimiter { get; }

    public void RequestFinish()
    {
        // a file has no "stop and save": it is finished or it is not
    }

    EngineSettings S => _ctx.Settings;

    /// <summary>The session's cookies: the captured ones plus everything the servers set while we talk to them.</summary>
    SessionCookies Jar => _jar ??= new SessionCookies(_job.Cookies, new Uri(_job.Url).Host);

    RequestSpec Spec() => RequestSpec.From(_job, null, Jar);

    // ------------------------------------------------------------------------------------------------------------
    // Top level
    // ------------------------------------------------------------------------------------------------------------

    public async Task<RunOutcome> RunAsync(CancellationToken stop)
    {
        try
        {
            _job.Error = null;
            _job.ErrorKind = JobErrorKind.None;
            _job.Stats.Reset();
            SetState(DownloadState.Probing);

            using var http = HttpFetcher.CreateClient(_job.MaxConnections, S);

            if (!_job.Probed) await ProbeAsync(http, stop).ConfigureAwait(false);

            EnsureSaveDirectory();
            await TransferAsync(http, stop).ConfigureAwait(false);
            return RunOutcome.Completed;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            _scheduler.ReleaseAll();
            SetState(DownloadState.Paused);
            return RunOutcome.Paused;
        }
        catch (Exception ex)
        {
            _scheduler.ReleaseAll();
            Fail(ex);
            return RunOutcome.Failed;
        }
        finally
        {
            // A kept first response that nobody read (paused early, empty file, failure) must not leak its connection.
            lock (_firstLock)
            {
                _job.FirstResponse?.Dispose();
                _job.FirstResponse = null;
            }
        }
    }

    void EnsureSaveDirectory()
    {
        try
        {
            Directory.CreateDirectory(_job.SaveDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DiskException($"Cannot use the save folder \"{_job.SaveDirectory}\": {ex.Message}", ex);
        }
    }

    void Fail(Exception ex)
    {
        _ctx.Logger.LogWarning(ex, "Download {File} failed", _job.FileName);
        _job.Error = ex.Message;
        _job.ErrorKind = ex switch
        {
            DownloadException d => d.Kind,
            HttpRequestException or IOException or TimeoutException => JobErrorKind.Network,
            _ => JobErrorKind.Other,
        };
        SetState(DownloadState.Failed);
    }

    void SetState(DownloadState state)
    {
        _job.State = state;
        Persist();
        _ctx.Publish(MakeSnapshot(0, 0));
    }

    void Persist()
    {
        try
        {
            _ctx.Store?.SaveJob(_job);
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogError(ex, "Could not persist job {Id}", _job.Id);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Probe
    // ------------------------------------------------------------------------------------------------------------

    async Task ProbeAsync(HttpClient http, CancellationToken ct)
    {
        var spec = Spec();

        // The first request is also the first download connection (IDM's "start downloading immediately", report §13.4):
        // a site that allows an address to be requested only once must not lose that request to a throw-away probe.
        // A resumed or refreshed job continues at its first missing byte.
        var startAt = FirstMissingPosition();

        // The first contact deserves the same patience as the transfer: 503/429 (honouring Retry-After) and dropped
        // connections are retried a few times before the job is declared failed.
        ProbeResult result;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                result = await HttpProbe.ProbeAsync(http, spec, S.UserAgent, keepOpen: true, clientToOwn: null, rangeStart: startAt, ct)
                    .ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (attempt <= 3 && IsTransientProbeFailure(ex))
            {
                _job.Stats.AddRetry();
                var delay = ex is HttpStatusException { RetryAfter: { } ra }
                    ? TimeSpan.FromSeconds(Math.Min(ra.TotalSeconds, 300))
                    : Backoff(attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        // Refreshed address / resumed job: the remote file must still be the same one.
        if (_job.TotalSize is { } known && result.TotalSize is { } now && known != now)
        {
            result.Discard();
            throw new FileChangedException($"The file size on the server changed ({known:N0} → {now:N0} bytes).");
        }

        if (_job.ETag != null && result.ETag != null && _job.ETag != result.ETag)
        {
            result.Discard();
            throw new FileChangedException();
        }

        lock (_firstLock)
        {
            _job.FirstResponse?.Dispose();
            _job.FirstResponse = result.DetachLive();
            _job.FirstResponseStart = result.AcceptsRanges ? startAt : 0;   // a server without ranges sends the whole file
        }

        _job.FinalUrl = result.FinalUrl;
        _job.TotalSize = result.TotalSize;
        _job.AcceptsRanges = result.AcceptsRanges;
        _job.ETag ??= result.ETag;
        _job.LastModified ??= result.LastModified;
        _job.ContentType ??= result.ContentType;
        _job.Probed = true;

        if (_job.NameProvisional && !File.Exists(_job.PartPath))
        {
            var name = FileNameHelper.Sanitize(result.FileName);
            if (_job.FolderProvisional)
            {
                _job.Category = S.CategoryOf(name);
                _job.SaveDirectory = S.ResolveCategoryFolder(_job.Category);
                EnsureSaveDirectory();
            }

            _job.FileName = FileNameHelper.MakeUnique(_job.SaveDirectory, name);
        }

        _job.NameProvisional = false;
        _job.FolderProvisional = false;
    }

    /// <summary>First byte not yet on disk according to the persisted segments (0 for a fresh job).</summary>
    long FirstMissingPosition()
    {
        lock (_job.Sync)
        {
            var open = _job.Segments.Where(s => !s.IsBounded || s.Downloaded < s.Length).Select(s => s.Position).ToList();
            return open.Count == 0 ? 0 : open.Min();
        }
    }

    /// <summary>The kept first response, when the connection about to start is exactly the one it was opened for.</summary>
    LiveResponse? TakeFirstResponse(long pos)
    {
        lock (_firstLock)
        {
            var live = _job.FirstResponse;
            if (live == null || pos != _job.FirstResponseStart) return null;

            _job.FirstResponse = null;
            if (live.Age > S.HeldProbeLifetime)
            {
                live.Dispose();
                return null;
            }

            return live;
        }
    }

    static bool IsTransientProbeFailure(Exception ex) =>
        ex is HttpStatusException { IsTransient: true }
            or TimeoutException
            or HttpRequestException { InnerException: IOException };

    // ------------------------------------------------------------------------------------------------------------
    // Transfer
    // ------------------------------------------------------------------------------------------------------------

    sealed class Attempt
    {
        public readonly List<ConnectionSlot> Slots = new();
        public readonly List<Task> Tasks = new();
        public readonly object Gate = new();
        public int Allowed;
        public int NextSlotId;
        public Exception? Fatal;
        public CancellationTokenSource Cts = null!;

        /// <summary>The server turned a connection away (429/503) during this attempt: it has a connection limit.</summary>
        public volatile bool Refused;

        /// <summary>The most connections that were receiving at the same time.</summary>
        public int PeakReceiving;

        TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes at the next <see cref="Wake"/>. Take it BEFORE looking at the state, so no change is missed.</summary>
        public Task WakeTask
        {
            get
            {
                lock (Gate) return _wake.Task;
            }
        }

        /// <summary>A connection ended or finished its part: the supervisor looks at once instead of at its next tick.</summary>
        public void Wake()
        {
            TaskCompletionSource woken;
            lock (Gate)
            {
                woken = _wake;
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            woken.TrySetResult();
        }
    }

    async Task TransferAsync(HttpClient http, CancellationToken stop)
    {
        PrepareSegments();

        using var file = OpenPartFile();
        try
        {
            while (true)
            {
                SetState(DownloadState.Downloading);
                var fallback = await RunAttemptAsync(http, file, stop).ConfigureAwait(false);
                if (!fallback) break;
            }

            // Success: finalize.
            RandomAccess.FlushToDisk(file);
            NativeFile.TrySetSparse(file, false);
        }
        catch
        {
            TryFlush(file);
            throw;
        }

        file.Dispose();
        FinalizeFile();
    }

    static void TryFlush(SafeFileHandle file)
    {
        try
        {
            if (!file.IsClosed) RandomAccess.FlushToDisk(file);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Normalises the persisted segment list for a (re)start.</summary>
    void PrepareSegments()
    {
        var canResume = _job.AcceptsRanges && _job.TotalSize is > 0;

        lock (_job.Sync)
        {
            if (!canResume && _job.Segments.Count > 0)
            {
                // Without range support the only safe move is to start over.
                _job.Segments.Clear();
            }

            foreach (var s in _job.Segments)
            {
                s.Owner = null;
                if (s.IsBounded && s.Downloaded >= s.Length)
                {
                    s.Downloaded = s.Length;
                    s.State = SegmentState.Done;
                }
                else
                {
                    s.State = SegmentState.Pending;
                }
            }
        }

        if (_job.Segments.Count == 0) _scheduler.ResetToSingleSegment();
    }

    SafeFileHandle OpenPartFile()
    {
        var path = _job.PartPath;
        var existed = File.Exists(path);

        if (!existed && _job.Segments.Any(s => s.Downloaded > 0))
        {
            // The partial file vanished - previous progress is meaningless.
            _scheduler.ResetToSingleSegment();
        }

        var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);

        if (_job.TotalSize is > 0 and var size)
        {
            if (RandomAccess.GetLength(handle) != size)
            {
                if (!existed) NativeFile.TrySetSparse(handle, true);
                RandomAccess.SetLength(handle, size);
            }
        }

        return handle;
    }

    /// <returns>true when the engine must restart in single-connection mode.</returns>
    async Task<bool> RunAttemptAsync(HttpClient http, SafeFileHandle file, CancellationToken stop)
    {
        var host = Host();
        var canSegment = _job.AcceptsRanges && _job.TotalSize is > 0;
        var allowed = canSegment ? S.EffectiveConnections(host, _job.MaxConnections) : 1;
        // a site that turned extra connections away before gets only as many as it allowed then
        if (canSegment && _ctx.HostLimits?.CapFor(host) is { } learned && learned < allowed)
        {
            _ctx.Logger.LogDebug("{Host} allows {Cap} connections (learned)", host, learned);
            allowed = learned;
        }

        var a = new Attempt { Allowed = allowed };
        a.Cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var ramp = new RampUpPolicy(S.RampUpInterval);

        void Spawn()
        {
            lock (a.Gate)
            {
                var slot = new ConnectionSlot(a.NextSlotId++);
                a.Slots.Add(slot);
                a.Tasks.Add(Task.Run(() => SlotMainAsync(slot, a, http, file, canSegment)));
                _job.Stats.NotePeak(a.Slots.Count(x => x.Alive));
            }
        }

        // An empty file needs no request at all.
        if (_job.TotalSize == 0)
        {
            lock (_job.Sync) _job.Segments.Clear();
            lock (_job.Sync) _job.Segments.Add(new Segment { Id = 0, Start = 0, End = -1, State = SegmentState.Done });
            return false;
        }

        _jobMeter.Reset();
        Volatile.Write(ref _bestConnectionSpeed, 0);
        _scheduler.BestConnectionSpeed = 0;
        var initial = canSegment ? RampUpPolicy.Initial(a.Allowed, _job.TotalSize, EffectiveLimit(), S.InitialPieceBytes) : 1;
        for (var i = 0; i < initial; i++) Spawn();

        using var timer = new PeriodicTimer(S.TickInterval);
        var lastCheckpoint = Stopwatch.GetTimestamp();
        var checkpointGap = S.CheckpointInterval;
        Task<bool>? tick = null;

        try
        {
            while (true)
            {
                // Taken before looking: a connection that ends after the look below still wakes the wait.
                var woken = a.WakeTask;
                if (a.Fatal != null || _scheduler.AllDone()) break;

                tick ??= timer.WaitForNextTickAsync(a.Cts.Token).AsTask();
                await Task.WhenAny(tick, woken).ConfigureAwait(false);
                if (!tick.IsCompleted) continue;   // a connection ended: only the completion check above, at once
                if (!await tick.ConfigureAwait(false)) break;
                tick = null;

                var speed = _jobMeter.Sample();
                int alive, receiving;
                lock (a.Gate)
                {
                    foreach (var s in a.Slots) s.Meter.Sample();
                    alive = a.Slots.Count(s => s.Alive);
                    receiving = a.Slots.Count(s => s.IsReceiving);
                    a.PeakReceiving = Math.Max(a.PeakReceiving, receiving);
                }

                _job.Stats.NotePeak(alive);

                if (a.Fatal != null || _scheduler.AllDone()) break;

                if (alive == 0)
                {
                    Spawn();
                }
                else if (canSegment && alive < a.Allowed && _scheduler.NeedNewConnection())
                {
                    var add = ramp.Plan(alive, receiving, speed, EffectiveLimit(), a.Allowed);
                    for (var i = 0; i < add; i++) Spawn();
                }

                if ((Stopwatch.GetTimestamp() - lastCheckpoint) / (double)Stopwatch.Frequency >= checkpointGap.TotalSeconds)
                {
                    var took = await CheckpointAsync(file).ConfigureAwait(false);
                    lastCheckpoint = Stopwatch.GetTimestamp();
                    // a disk that takes long to flush is flushed less often (each flush holds up every write to the file)
                    checkpointGap = took * 10 > S.CheckpointInterval ? took * 10 : S.CheckpointInterval;
                }

                _ctx.Publish(MakeSnapshot(speed, alive));
            }
        }
        catch (OperationCanceledException)
        {
            // stop requested or a slot reported a fatal error
        }

        if (a.Refused && a.PeakReceiving > 0)
        {
            // The server turned connections away while this many were at work: the next download starts with no more.
            _ctx.HostLimits?.Learn(host, a.PeakReceiving);
        }

        a.Cts.Cancel();
        Task[] tasks;
        lock (a.Gate) tasks = a.Tasks.ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // slots never throw; fatal errors are collected in a.Fatal
        }

        a.Cts.Dispose();

        if (a.Fatal is { } fatal)
        {
            if (fatal is RangeIgnoredException)
            {
                _ctx.Logger.LogWarning("Server ignores ranges for {File}; restarting with one connection", _job.FileName);
                _job.Stats.AddFallback();
                _job.AcceptsRanges = false;
                _job.MaxConnections = 1;
                _scheduler.ResetToSingleSegment();
                return true;
            }

            if (fatal is DataCorruptionException)
            {
                _ctx.Logger.LogWarning("Corrupt overlap for {File}; restarting with one connection", _job.FileName);
                _job.Stats.AddFallback();
                _job.MaxConnections = 1;
                _scheduler.ResetToSingleSegment();
                return true;
            }

            ExceptionDispatchInfo.Capture(fatal).Throw();
        }

        if (!_scheduler.AllDone())
        {
            stop.ThrowIfCancellationRequested();
            throw new DownloadException(JobErrorKind.Other, "The download stopped unexpectedly.");
        }

        return false;
    }

    /// <summary>
    /// capture progress → flush file data → persist the capture (never record bytes that are not on disk). Only written
    /// bytes count as downloaded (a connection commits a batch once its write has landed), so the capture is safe.
    /// </summary>
    /// <returns>How long the flush and the save took.</returns>
    async Task<TimeSpan> CheckpointAsync(SafeFileHandle file)
    {
        var captured = _job.CaptureSegments();
        var clock = Stopwatch.StartNew();
        await Task.Run(() =>
        {
            try
            {
                RandomAccess.FlushToDisk(file);
                _ctx.Store?.SaveJob(_job, captured);
            }
            catch (Exception ex)
            {
                _ctx.Logger.LogError(ex, "Checkpoint failed for {Id}", _job.Id);
            }
        }).ConfigureAwait(false);
        return clock.Elapsed;
    }

    string Host() => new Uri(_job.Url).Host;

    long EffectiveLimit()
    {
        var g = _ctx.GlobalLimiter.BytesPerSecond;
        var j = JobLimiter.BytesPerSecond;
        if (g > 0 && j > 0) return Math.Min(g, j);
        return Math.Max(g, j);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Slots
    // ------------------------------------------------------------------------------------------------------------

    async Task SlotMainAsync(ConnectionSlot slot, Attempt a, HttpClient http, SafeFileHandle file, bool canSegment)
    {
        var ct = a.Cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var seg = _scheduler.Acquire(slot, canSegment, out var revokedFrom, out var revokedSeg);
                revokedFrom?.Revoke(revokedSeg!);
                if (seg == null) break;

                var result = await DownloadSegmentAsync(slot, seg, a, http, file, canSegment).ConfigureAwait(false);
                if (result == SegResult.Completed) slot.Last = seg;
                else if (result == SegResult.Trimmed) break; // slot trimmed by a server back-off
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (a.Gate) a.Fatal ??= ex;
            a.Cts.Cancel();
        }
        finally
        {
            slot.Alive = false;
            a.Wake();
        }
    }

    enum SegResult
    {
        Completed,
        Lost,
        Trimmed,
    }

    async Task<SegResult> DownloadSegmentAsync(
        ConnectionSlot slot, Segment seg, Attempt a, HttpClient http, SafeFileHandle file, bool canSegment)
    {
        var attempt = 0;

        while (true)
        {
            var ct = slot.Begin(seg, a.Cts.Token);
            Exception? error = null;
            var before = seg.Downloaded;

            try
            {
                await TransferSegmentAsync(slot, seg, a, http, file, canSegment, ct).ConfigureAwait(false);
                return _scheduler.IsDone(seg) ? SegResult.Completed : SegResult.Lost;
            }
            catch (SlowConnectionException)
            {
                // Not a failure: what arrived is written, and the rest is asked for at once on a new connection.
                _job.Stats.AddReplacedConnection();
                _ctx.Logger.LogDebug("Slot {Slot} segment {Seg}: far slower than the other connections, asking again", slot.Id, seg.Id);
                continue;
            }
            catch (OperationCanceledException) when (a.Cts.Token.IsCancellationRequested)
            {
                _scheduler.Release(seg, slot);
                throw;
            }
            catch (OperationCanceledException)
            {
                // Revoked by the scheduler (chunk takeover) → just move on.
                if (_scheduler.Allowance(seg, slot) < 0) return SegResult.Lost;
                error = new TimeoutException("The connection stalled.");
            }
            catch (DownloadException ex) when (!(ex is HttpStatusException { IsTransient: true }))
            {
                _scheduler.Release(seg, slot);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or HttpStatusException)
            {
                error = ex;
            }
            finally
            {
                slot.End();
            }

            // ----- retry bookkeeping -----
            if (seg.Downloaded > before) attempt = 0;
            attempt++;

            TimeSpan delay;
            if (error is HttpStatusException hs)
            {
                // Server courtesy: fewer connections, honour Retry-After.
                lock (a.Gate)
                {
                    var alive = a.Slots.Count(s => s.Alive);
                    var receiving = a.Slots.Count(s => s != slot && s.IsReceiving);
                    // The server's limit is what it serves at once: keep to that many, and never fewer than it was seen to
                    // serve. A refusal right after a connection moved on to its next part (often all of them at the same
                    // moment) is the server still counting the old request, not a lower limit; taking one connection off for
                    // each such moment ended at one.
                    a.Allowed = receiving > 0
                        ? Math.Min(a.Allowed, Math.Max(receiving, a.PeakReceiving))
                        : Math.Max(Math.Max(1, a.PeakReceiving), Math.Min(a.Allowed, alive - 1));
                    // turned away while others work: the server limits connections (the next download keeps to it)
                    if (hs.StatusCode is 429 or 503 && a.Slots.Any(s => s != slot && s.IsWorking)) a.Refused = true;
                }

                // The server's Retry-After wins. Without one, the first try again is at once too: a refusal right after a
                // connection of ours closed is usually the server still counting it; repeated refusals wait as usual.
                delay = hs.RetryAfter is { } ra ? TimeSpan.FromSeconds(Math.Min(ra.TotalSeconds, 300))
                    : attempt == 1 ? TimeSpan.Zero : Backoff(attempt - 1);
            }
            else
            {
                // A dropped or stalled connection is asked again at once (the server is not saying "slow down");
                // waiting starts with the second try in a row.
                delay = attempt == 1 ? TimeSpan.Zero : Backoff(attempt - 1);
            }

            if (attempt > S.MaxRetries)
            {
                _scheduler.Release(seg, slot);
                throw new DownloadException(JobErrorKind.Network,
                    $"Giving up after {S.MaxRetries} retries: {error!.Message}", error);
            }

            _job.Stats.AddRetry();
            _ctx.Logger.LogDebug("Slot {Slot} segment {Seg} retry {N} in {Delay}: {Error}", slot.Id, seg.Id, attempt, delay, error.Message);

            // Trim this slot if the server asked us to use fewer connections: its part goes back to the pool, for one the
            // server serves. Whichever slot it is (with every connection opening at once, the first one may be the one
            // turned away; kept, it would hold its part through ever longer waits while the others finish the rest).
            lock (a.Gate)
            {
                var alive = a.Slots.Count(s => s.Alive);
                if (error is HttpStatusException && alive > a.Allowed)
                {
                    _scheduler.Release(seg, slot);
                    return SegResult.Trimmed;
                }
            }

            try
            {
                await Task.Delay(delay, a.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _scheduler.Release(seg, slot);
                throw;
            }
        }
    }

    TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(S.RetryMaxDelay.TotalSeconds, S.RetryBaseDelay.TotalSeconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds * (0.5 + _rng.NextDouble() * 0.5));
    }

    async Task TransferSegmentAsync(
        ConnectionSlot slot, Segment seg, Attempt a, HttpClient http, SafeFileHandle file, bool canSegment, CancellationToken ct)
    {
        long pos, reqEnd;
        lock (_job.Sync)
        {
            if (!ReferenceEquals(seg.Owner, slot)) return;
            pos = seg.Position;
            reqEnd = seg.End;
        }

        if (seg.IsBounded && pos > reqEnd)
        {
            _scheduler.MarkDone(seg, slot);
            return;
        }

        var ranged = canSegment;
        if (!ranged && pos > 0)
        {
            // Server cannot resume: restart this (single) segment from the top.
            lock (_job.Sync) seg.Downloaded = 0;
            pos = 0;
        }

        var validator = _job.ETag ?? (_job.LastModified?.ToString("R"));

        // The very first connection continues on the response the probe already opened (see ProbeAsync).
        var live = TakeFirstResponse(pos);
        using var liveGuard = live;

        HttpResponseMessage response;
        if (live != null)
        {
            response = live.Response;
        }
        else
        {
            (response, _) = await HttpFetcher.GetAsync(http, Spec(), S.UserAgent, req =>
            {
                if (!ranged) return;
                req.Headers.Range = new RangeHeaderValue(pos, reqEnd == Segment.Unbounded ? null : reqEnd);
                if (validator != null && pos > 0) req.Headers.TryAddWithoutValidation("If-Range", validator);
            }, ct).ConfigureAwait(false);
        }

        using (response)
        {
            ValidateResponse(response, ranged, pos, reqEnd);

            var readMax = S.ReadBufferBytes;
            using var batch = new BatchWriter(file, _scheduler, seg, slot, pos, Math.Max(readMax, S.WriteBatchBytes));
            var started = Stopwatch.GetTimestamp();
            var lastLook = started;
            long received = 0;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);

                var eof = false;
                while (true)
                {
                    var allowance = _scheduler.Allowance(seg, slot);
                    if (allowance < 0) return;   // taken over by another connection
                    if (allowance == 0) break;   // our (possibly trimmed) range is complete

                    var room = batch.Free;
                    var want = (int)Math.Min(Math.Min(
                        SpeedLimiter.PreferredReadSize(readMax, _ctx.GlobalLimiter, JobLimiter), room.Length), allowance);

                    var n = await ReadAsync(stream, room[..want], stall, S.StallTimeout, ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        eof = true;
                        break;
                    }

                    await SpeedLimiter.AcquireAsync(n, ct, _ctx.GlobalLimiter, JobLimiter).ConfigureAwait(false);

                    // claimed before it is written: a split meanwhile starts after these bytes
                    var mine = _scheduler.Reserve(seg, slot, n);
                    if (mine < 0) return;   // taken over meanwhile
                    batch.Add(mine);
                    received += mine;
                    slot.Meter.Add(mine);
                    _jobMeter.Add(mine);

                    var now = Stopwatch.GetTimestamp();
                    if (Seconds(now - lastLook) >= 0.5)
                    {
                        lastLook = now;
                        var onNet = Seconds(now - started - batch.DiskWaitTicks);
                        NoteConnectionSpeed(received, onNet);
                        if (IsFarTooSlow(a, slot, seg, received, onNet))
                        {
                            if (!await batch.FinishAsync().ConfigureAwait(false)) return;
                            throw new SlowConnectionException();
                        }
                    }

                    // a full batch, the end of the part, or a second gone by (progress shows; a pause keeps what came)
                    if (batch.Free.Length == 0 || mine < n || mine == allowance || Seconds(now - batch.LastWriteTicks) >= 1)
                        if (!await batch.WriteAsync().ConfigureAwait(false)) return;
                }

                if (!await batch.FinishAsync().ConfigureAwait(false)) return;
                NoteConnectionSpeed(received, Seconds(Stopwatch.GetTimestamp() - started - batch.DiskWaitTicks));

                if (eof)
                {
                    if (!seg.IsBounded)
                    {
                        _scheduler.CompleteUnbounded(seg, slot);
                        return;
                    }

                    throw new IOException("The server closed the connection before the range was complete.");
                }

                // Truncated by a split? The response still carries bytes of the neighbour - compare a few of them
                // with what the other connection wrote (cheap data-integrity guard).
                if (reqEnd != Segment.Unbounded && reqEnd > seg.End)
                    await VerifySeamAsync(stream, seg, reqEnd, file, stall, ct).ConfigureAwait(false);

                _scheduler.MarkDone(seg, slot);
            }
            catch (Exception ex) when (ex is not (DiskException or SlowConnectionException) && batch.Busy)
            {
                // Paused, cut off or gone quiet: what arrived is still written and counted.
                try
                {
                    await batch.FinishAsync().ConfigureAwait(false);
                }
                catch
                {
                    // the first error is the one to report
                }

                throw;
            }
            finally
            {
                // never leave a write running past this connection (the file may be closed next)
                await batch.DrainAsync().ConfigureAwait(false);
            }
        }
    }

    static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    /// <summary>
    /// A connection that should ask again on a new one: after <see cref="EngineSettings.SlowConnectionGrace"/> on its answer
    /// it gets less than 1/<see cref="EngineSettings.SlowConnectionRatio"/> of the yardstick, with more than that grace's
    /// worth of work left at its own pace. Never under a speed limit, which makes connections slow on purpose.
    /// <para>
    /// The yardstick is the larger of: the fastest OTHER connection right now, and the best one connection of this download
    /// sustained divided by the connections receiving now. Both allow for sharing: on a line that one connection could fill,
    /// N connections each get about 1/N of it, and none of them is "slow". The second catches what the first cannot: the
    /// connections still at work all stuck on slow routes (the last parts of a file), where each one alone looks no slower
    /// than the others.
    /// </para>
    /// </summary>
    bool IsFarTooSlow(Attempt a, ConnectionSlot slot, Segment seg, long received, double seconds)
    {
        if (!S.ReplaceSlowConnections || seconds < S.SlowConnectionGrace.TotalSeconds || EffectiveLimit() > 0) return false;

        var speed = received / seconds;
        double fastest = 0;
        var sharing = 1;
        lock (a.Gate)
            foreach (var s in a.Slots)
                if (s != slot && s.IsReceiving)
                {
                    sharing++;
                    fastest = Math.Max(fastest, s.Speed);
                }

        var yardstick = Math.Max(fastest, Volatile.Read(ref _bestConnectionSpeed) / sharing);
        if (yardstick <= 0 || speed * S.SlowConnectionRatio >= yardstick) return false;

        var left = _scheduler.Allowance(seg, slot);
        return left > 0 && left / Math.Max(1, speed) > S.SlowConnectionGrace.TotalSeconds;
    }

    /// <summary>The best speed one connection of this download sustained (over at least 256 KiB of one answer).</summary>
    double _bestConnectionSpeed;

    void NoteConnectionSpeed(long received, double seconds)
    {
        if (received < 256 * 1024 || seconds <= 0) return;
        var speed = received / seconds;
        double seen;
        while (speed > (seen = Volatile.Read(ref _bestConnectionSpeed)))
            if (Interlocked.CompareExchange(ref _bestConnectionSpeed, speed, seen) == seen)
            {
                _scheduler.BestConnectionSpeed = speed;
                break;
            }
    }

    sealed class SlowConnectionException() : Exception("Far slower than this download's other connections: asking again on a new one.");

    /// <summary>
    /// One connection's writes. Received bytes are collected and written a batch at a time while the next batch is
    /// received, so the network is not held up by the disk (and the disk gets fewer, larger writes). A batch counts as
    /// downloaded (<see cref="SegmentScheduler.Commit"/>) only once its write has landed, so a checkpoint never records bytes
    /// that are not in the file.
    /// </summary>
    sealed class BatchWriter : IDisposable
    {
        readonly SafeFileHandle _file;
        readonly SegmentScheduler _scheduler;
        readonly Segment _seg;
        readonly ConnectionSlot _slot;
        readonly int _size;
        byte[] _fill, _spare;
        long _fillAt;
        Task? _writing;
        int _writingLength;

        public BatchWriter(SafeFileHandle file, SegmentScheduler scheduler, Segment seg, ConnectionSlot slot, long at, int size)
        {
            _file = file;
            _scheduler = scheduler;
            _seg = seg;
            _slot = slot;
            _fillAt = at;
            _size = size;
            _fill = ArrayPool<byte>.Shared.Rent(size);
            _spare = ArrayPool<byte>.Shared.Rent(size);
        }

        public int Filled { get; private set; }

        /// <summary>Where the next bytes are received.</summary>
        public Memory<byte> Free => _fill.AsMemory(Filled, _size - Filled);

        public long LastWriteTicks { get; private set; } = Stopwatch.GetTimestamp();

        /// <summary>Time spent waiting for the disk: a connection is not slow for that.</summary>
        public long DiskWaitTicks { get; private set; }

        public bool Busy => Filled > 0 || _writing != null;

        /// <summary><paramref name="n"/> bytes were received into <see cref="Free"/> (and claimed).</summary>
        public void Add(int n) => Filled += n;

        /// <summary>Starts writing what is collected, once the batch before it has landed. False: the part is no longer ours.</summary>
        public async Task<bool> WriteAsync()
        {
            if (Filled == 0) return true;
            if (!await LandAsync().ConfigureAwait(false)) return false;

            _writing = RandomAccess.WriteAsync(_file, _fill.AsMemory(0, Filled), _fillAt, CancellationToken.None).AsTask();
            _writingLength = Filled;
            _fillAt += Filled;
            (_fill, _spare) = (_spare, _fill);
            Filled = 0;
            LastWriteTicks = Stopwatch.GetTimestamp();
            return true;
        }

        /// <summary>Waits for the batch on its way to the disk and counts it as downloaded.</summary>
        public async Task<bool> LandAsync()
        {
            if (_writing == null) return true;
            var from = Stopwatch.GetTimestamp();
            try
            {
                await _writing.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DiskException($"Could not write to disk: {ex.Message}", ex);
            }
            finally
            {
                _writing = null;
                DiskWaitTicks += Stopwatch.GetTimestamp() - from;
            }

            return _scheduler.Commit(_seg, _slot, _writingLength);
        }

        /// <summary>Everything collected written and counted (the end of an answer, a pause, a replaced connection).</summary>
        public async Task<bool> FinishAsync() =>
            await WriteAsync().ConfigureAwait(false) && await LandAsync().ConfigureAwait(false);

        /// <summary>A write still running is waited for, whatever happened.</summary>
        public async Task DrainAsync()
        {
            if (_writing == null) return;
            try
            {
                await _writing.ConfigureAwait(false);
            }
            catch
            {
                // reported where it mattered, or the connection is ending anyway
            }

            _writing = null;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_fill);
            ArrayPool<byte>.Shared.Return(_spare);
        }
    }

    async Task<int> ReadAsync(Stream stream, Memory<byte> into, CancellationTokenSource stall, TimeSpan timeout, CancellationToken ct)
    {
        stall.CancelAfter(timeout);
        try
        {
            var n = await stream.ReadAsync(into, stall.Token).ConfigureAwait(false);
            return n;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("No data received for " + timeout.TotalSeconds.ToString("0") + " s.");
        }
        finally
        {
            stall.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    async Task VerifySeamAsync(
        Stream stream, Segment seg, long reqEnd, SafeFileHandle file, CancellationTokenSource stall, CancellationToken ct)
    {
        const int Probe = 4096;
        var next = _scheduler.NextOf(seg);
        if (next == null) return;

        var toRead = (int)Math.Min(Probe, reqEnd - seg.End);
        var got = 0;
        var scratch = new byte[toRead];
        while (got < toRead)
        {
            stall.CancelAfter(S.StallTimeout);
            int n;
            try
            {
                n = await stream.ReadAsync(scratch.AsMemory(got, toRead - got), stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return; // inconclusive
            }
            finally
            {
                stall.CancelAfter(Timeout.InfiniteTimeSpan);
            }

            if (n == 0) break;
            got += n;
        }

        long written;
        lock (_job.Sync) written = next.State == SegmentState.Done ? long.MaxValue : next.Downloaded;
        var compare = (int)Math.Min(got, written);
        if (compare <= 0) return;

        var onDisk = new byte[compare];
        var read = 0;
        while (read < compare)
        {
            var r = RandomAccess.Read(file, onDisk.AsSpan(read), next.Start + read);
            if (r == 0) return;
            read += r;
        }

        if (!onDisk.AsSpan().SequenceEqual(scratch.AsSpan(0, compare)))
            throw new DataCorruptionException();
    }

    void ValidateResponse(HttpResponseMessage response, bool ranged, long pos, long reqEnd)
    {
        var status = (int)response.StatusCode;
        if (status >= 400)
        {
            if (status == 416) throw new FileChangedException("The server no longer has the expected byte range.");
            throw new HttpStatusException(status, HttpFetcher.RetryAfter(response));
        }

        // A web page where bytes of a file should be (login wall, link that may be used only once): never write it
        // into the file. Without this check a restart would save the page as if it were the download.
        if (HttpProbe.IsWebPageInsteadOfFile(response, response.Content.Headers.ContentType?.MediaType, _job.Url, _job.FileName))
        {
            var host = response.RequestMessage?.RequestUri?.Host;
            throw new WebPageException(host != null && !host.Equals(new Uri(_job.Url).Host, StringComparison.OrdinalIgnoreCase) ? host : null);
        }

        // A strong ETag that differs from the one recorded at the start means a different file.
        var etag = response.Headers.ETag;
        if (_job.ETag != null && etag is { IsWeak: false } && etag.Tag != _job.ETag)
            throw new FileChangedException();

        if (!ranged) return;

        if (status == (int)HttpStatusCode.PartialContent)
        {
            var cr = response.Content.Headers.ContentRange;
            if (cr == null || cr.From != pos) throw new RangeIgnoredException();
            if (cr.Length is { } len && _job.TotalSize is { } total && len != total)
                throw new FileChangedException();
            return;
        }

        if (status == (int)HttpStatusCode.OK)
        {
            // Acceptable only when we asked for the whole file from byte 0.
            var total = _job.TotalSize;
            var wholeFile = pos == 0 && (reqEnd == Segment.Unbounded || (total != null && reqEnd >= total.Value - 1));
            var length = response.Content.Headers.ContentLength;
            if (wholeFile && (length == null || total == null || length == total)) return;

            // A whole-file answer to a ranged request is either "the file changed" (If-Range mismatch) or a server
            // that simply ignores Range. Same size and same Last-Modified → it is the same file: range ignored.
            var lastModified = response.Content.Headers.LastModified;
            var changed = (length != null && total != null && length != total)
                          || (_job.LastModified != null && lastModified != null && lastModified != _job.LastModified);
            if (changed) throw new FileChangedException();
            throw new RangeIgnoredException();
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Completion
    // ------------------------------------------------------------------------------------------------------------

    void FinalizeFile()
    {
        var part = _job.PartPath;
        var target = _job.TargetPath;

        if (File.Exists(target))
        {
            _job.FileName = FileNameHelper.MakeUnique(_job.SaveDirectory, _job.FileName);
            target = _job.TargetPath;
        }

        try
        {
            File.Move(part, target);
        }
        catch (IOException ex)
        {
            throw new DiskException($"Could not move the finished file into place: {ex.Message}", ex);
        }

        if (_job.LastModified is { } lm)
        {
            try
            {
                File.SetLastWriteTimeUtc(target, lm.UtcDateTime);
            }
            catch
            {
                // cosmetic
            }
        }

        if (S.WriteMarkOfTheWeb) NativeFile.TryWriteMarkOfTheWeb(target, _job.Referer, _job.FinalUrl ?? _job.Url);

        _job.State = DownloadState.Completed;
        _job.CompletedAt = DateTimeOffset.Now;
        _job.Cookies = null; // session data is only needed while the transfer runs
        _job.Headers = new Dictionary<string, string>();
        Persist();
        _ctx.Publish(MakeSnapshot(0, 0));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Snapshots
    // ------------------------------------------------------------------------------------------------------------

    public JobSnapshot MakeSnapshot(double speed, int connections)
    {
        long downloaded;
        SegmentInfo[] infos;
        lock (_job.Sync)
        {
            downloaded = 0;
            infos = new SegmentInfo[_job.Segments.Count];
            for (var i = 0; i < infos.Length; i++)
            {
                var s = _job.Segments[i];
                downloaded += s.Received;   // (what is on its way to the disk too: progress follows the network)
                infos[i] = s.ToInfo();
            }
        }

        if (_job.State == DownloadState.Completed && _job.TotalSize is { } t) downloaded = t;

        TimeSpan? eta = null;
        if (speed > 1 && _job.TotalSize is { } total && total > downloaded)
            eta = TimeSpan.FromSeconds((total - downloaded) / speed);

        return new JobSnapshot(_job.Id, _job.State, _job.FileName, _job.SaveDirectory, downloaded, _job.TotalSize,
            speed, eta, connections, infos, _job.AcceptsRanges, _job.Error, _job.ErrorKind,
            _job.Stats.Splits, _job.Stats.Takeovers, _job.Stats.Retries, _job.Stats.Fallbacks, _job.Stats.PeakConnections);
    }
}
