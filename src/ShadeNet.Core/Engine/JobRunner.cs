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
        var host = new Uri(_job.Url).Host;
        var canSegment = _job.AcceptsRanges && _job.TotalSize is > 0;
        var allowed = canSegment ? S.EffectiveConnections(host, _job.MaxConnections) : 1;

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
        Spawn();

        using var timer = new PeriodicTimer(S.TickInterval);
        var lastCheckpoint = Stopwatch.GetTimestamp();

        try
        {
            while (await timer.WaitForNextTickAsync(a.Cts.Token).ConfigureAwait(false))
            {
                var speed = _jobMeter.Sample();
                int alive, receiving;
                lock (a.Gate)
                {
                    foreach (var s in a.Slots) s.Meter.Sample();
                    alive = a.Slots.Count(s => s.Alive);
                    receiving = a.Slots.Count(s => s.IsReceiving);
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

                if ((Stopwatch.GetTimestamp() - lastCheckpoint) / (double)Stopwatch.Frequency >= S.CheckpointInterval.TotalSeconds)
                {
                    await CheckpointAsync(file).ConfigureAwait(false);
                    lastCheckpoint = Stopwatch.GetTimestamp();
                }

                _ctx.Publish(MakeSnapshot(speed, alive));
            }
        }
        catch (OperationCanceledException)
        {
            // stop requested or a slot reported a fatal error
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

    /// <summary>capture progress → flush file data → persist the capture (never record bytes that are not on disk).</summary>
    async Task CheckpointAsync(SafeFileHandle file)
    {
        var captured = _job.CaptureSegments();
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
    }

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
                await TransferSegmentAsync(slot, seg, http, file, canSegment, ct).ConfigureAwait(false);
                return _scheduler.IsDone(seg) ? SegResult.Completed : SegResult.Lost;
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
                    a.Allowed = Math.Max(1, Math.Min(a.Allowed, alive - 1));
                }

                delay = hs.RetryAfter is { } ra ? TimeSpan.FromSeconds(Math.Min(ra.TotalSeconds, 300)) : Backoff(attempt);
            }
            else
            {
                delay = Backoff(attempt);
            }

            if (attempt > S.MaxRetries)
            {
                _scheduler.Release(seg, slot);
                throw new DownloadException(JobErrorKind.Network,
                    $"Giving up after {S.MaxRetries} retries: {error!.Message}", error);
            }

            _job.Stats.AddRetry();
            _ctx.Logger.LogDebug("Slot {Slot} segment {Seg} retry {N} in {Delay}: {Error}", slot.Id, seg.Id, attempt, delay, error.Message);

            // Trim this slot if the server asked us to use fewer connections.
            lock (a.Gate)
            {
                var alive = a.Slots.Count(s => s.Alive);
                if (error is HttpStatusException && alive > a.Allowed && slot.Id != 0)
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
        ConnectionSlot slot, Segment seg, HttpClient http, SafeFileHandle file, bool canSegment, CancellationToken ct)
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

            var chunkMax = S.ReadBufferBytes;
            var buffer = ArrayPool<byte>.Shared.Rent(chunkMax);
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

                    var want = (int)Math.Min(
                        SpeedLimiter.PreferredReadSize(chunkMax, _ctx.GlobalLimiter, JobLimiter), allowance);

                    var n = await ReadAsync(stream, buffer, want, stall, S.StallTimeout, ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        eof = true;
                        break;
                    }

                    await SpeedLimiter.AcquireAsync(n, ct, _ctx.GlobalLimiter, JobLimiter).ConfigureAwait(false);

                    try
                    {
                        await RandomAccess.WriteAsync(file, buffer.AsMemory(0, n), pos, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new DiskException($"Could not write to disk: {ex.Message}", ex);
                    }

                    if (!_scheduler.Commit(seg, slot, n)) return;
                    pos += n;
                    slot.Meter.Add(n);
                    _jobMeter.Add(n);
                }

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
                    await VerifySeamAsync(stream, seg, reqEnd, buffer, file, stall, ct).ConfigureAwait(false);

                _scheduler.MarkDone(seg, slot);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    async Task<int> ReadAsync(Stream stream, byte[] buffer, int want, CancellationTokenSource stall, TimeSpan timeout, CancellationToken ct)
    {
        stall.CancelAfter(timeout);
        try
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, want), stall.Token).ConfigureAwait(false);
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
        Stream stream, Segment seg, long reqEnd, byte[] buffer, SafeFileHandle file, CancellationTokenSource stall, CancellationToken ct)
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
                downloaded += s.Downloaded;
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
