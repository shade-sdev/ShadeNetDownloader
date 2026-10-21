using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ShadeNet.Core.Hls;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Engine;

/// <summary>What the manager needs from something that runs a job: a limiter to adjust and a way to run and stop it.</summary>
internal interface IJobRunner
{
    SpeedLimiter JobLimiter { get; }
    Task<RunOutcome> RunAsync(CancellationToken stop);

    /// <summary>"Stop and save" for a recording: stop fetching new pieces and finish with what has been recorded. No-op for files.</summary>
    void RequestFinish();
}

/// <summary>
/// Downloads an HLS or DASH stream. It reads the playlist (or manifest), fetches the media segments in parallel
/// (decrypting AES-128 ones), keeps each finished segment as a file in the job's work folder - which is what makes
/// pause, resume and crashes cheap - and finally joins them into one video file, with ffmpeg when it is available
/// (<c>-c copy</c>: no re-encoding). A live stream is recorded the same way: the playlist is read again and again and
/// new segments are queued, until the stream ends or the user stops it. Subtitle tracks are saved as files next to the video.
/// </summary>
internal sealed class HlsRunner : IJobRunner
{
    sealed class Part
    {
        public required Track Track { get; init; }
        public required HlsSegment Segment { get; init; }
        public required string FinalPath { get; init; }
        public bool Done { get; set; }
    }

    /// <summary>One media track (picture, or sound kept apart). Its segments are known by media sequence number.</summary>
    sealed class Track
    {
        public required string Id { get; init; }
        public required HlsMediaPlaylist First { get; init; }

        /// <summary>Fetches the track's current segment list again (live only).</summary>
        public Func<CancellationToken, Task<HlsMediaPlaylist>>? Reload { get; init; }

        /// <summary>A subtitle track: fetched like media, but saved as a file of its own and never the reason a download fails.</summary>
        public bool IsSubtitle { get; init; }
        public string Language { get; init; } = "";
        public string Extension { get; init; } = ".vtt";

        /// <summary>The track whose duration is "how much has been downloaded / recorded" (the first media track).</summary>
        public bool CountsTime { get; set; }

        public List<Part> Parts { get; } = new();
        public HashSet<long> Known { get; } = new();
    }

    sealed class Plan
    {
        /// <summary>Media tracks first (picture, then sound kept apart), subtitle tracks after.</summary>
        public required List<Track> Tracks { get; init; }
        public bool Live { get; init; }
        public bool IsDash { get; init; }
        public double PollSeconds { get; init; } = 2;
    }

    readonly DownloadJob _job;
    readonly RunnerContext _ctx;
    readonly SpeedMeter _meter = new();
    readonly Random _rng = new();
    readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _keys = new();
    readonly ConcurrentDictionary<string, Lazy<Task>> _inits = new();
    readonly ConcurrentQueue<Part> _queue = new();
    readonly CancellationTokenSource _finish = new();
    readonly List<string> _subtitleFiles = new();
    readonly object _lock = new();
    SessionCookies? _jar;
    volatile bool _pollingDone;
    bool _limitReached;

    // progress; guarded by _lock
    long _completedBytes;
    double _completedSeconds;
    int _partsDone;
    int _partsTotal;
    long _carriedBytes;       // bytes of segments finished before this run began
    int _active;
    string? _note;

    public HlsRunner(DownloadJob job, RunnerContext ctx)
    {
        _job = job;
        _ctx = ctx;
        JobLimiter = new SpeedLimiter(job.SpeedLimit ?? 0);
        _job.Hls ??= new HlsJobData();
    }

    public SpeedLimiter JobLimiter { get; }

    public void RequestFinish()
    {
        try
        {
            _finish.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already over
        }
    }

    EngineSettings S => _ctx.Settings;
    HlsJobData Data => _job.Hls!;
    string Work => _job.WorkFolder;

    SessionCookies Jar => _jar ??= new SessionCookies(_job.Cookies, new Uri(_job.Url).Host);

    RequestSpec Spec(string url) =>
        new(url, _job.Referer, _job.Cookies, _job.UserAgent, _job.Headers, new Uri(_job.Url).Host) { Jar = Jar };

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
            _note = null;
            SetState(DownloadState.Probing);

            var workers = Math.Clamp(S.EffectiveConnections(new Uri(_job.Url).Host, _job.MaxConnections), 1, 16);
            using var http = HttpFetcher.CreateClient(workers, S);

            var plan = await PlanAsync(http, stop).ConfigureAwait(false);
            Data.Live = plan.Live;
            EnsureWorkFolder();
            StartCounting(plan);

            SetState(DownloadState.Downloading);
            await TransferAsync(http, plan, workers, stop).ConfigureAwait(false);
            await JoinAsync(plan, stop).ConfigureAwait(false);
            return RunOutcome.Completed;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            SetState(DownloadState.Paused);
            return RunOutcome.Paused;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return RunOutcome.Failed;
        }
    }

    void Fail(Exception ex)
    {
        _ctx.Logger.LogWarning(ex, "Stream download {File} failed", _job.FileName);
        _job.Error = ex.Message;
        _job.ErrorKind = ex switch
        {
            DownloadException d => d.Kind,
            HlsFormatException => JobErrorKind.Other,
            HttpRequestException or IOException or TimeoutException => JobErrorKind.Network,
            _ => JobErrorKind.Other,
        };
        _note = null;
        SetState(DownloadState.Failed);
    }

    void SetState(DownloadState state)
    {
        _job.State = state;
        Persist();
        _ctx.Publish(MakeSnapshot(0));
    }

    void Persist()
    {
        try
        {
            SyncData();
            _ctx.Store?.SaveJob(_job);
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogError(ex, "Could not persist job {Id}", _job.Id);
        }
    }

    void EnsureWorkFolder()
    {
        try
        {
            Directory.CreateDirectory(_job.SaveDirectory);
            Directory.CreateDirectory(Work);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DiskException($"Cannot use the save folder \"{_job.SaveDirectory}\": {ex.Message}", ex);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Planning: what the stream consists of
    // ------------------------------------------------------------------------------------------------------------

    async Task<Plan> PlanAsync(HttpClient http, CancellationToken ct)
    {
        Plan plan;
        if (Data.Direct)
        {
            plan = PlanDirect();
        }
        else
        {
            var (text, url) = await FetchPlaylistAsync(http, _job.Url, ct).ConfigureAwait(false);
            plan = DashParser.LooksLikeManifest(text)
                ? PlanDash(http, DashParser.Parse(text, url))
                : await PlanHlsAsync(http, HlsParser.Parse(text, url), ct).ConfigureAwait(false);
        }

        foreach (var track in plan.Tracks)
        {
            var media = track.First;
            if (media.Encryption == HlsEncryption.Unsupported)
            {
                var kind = media.Segments.Select(s => s.Key).First(k => k?.Method == HlsEncryption.Unsupported)?.Description;
                throw new DownloadException(JobErrorKind.Unsupported, $"This stream is protected ({kind ?? "DRM"}) and cannot be downloaded.");
            }
        }

        await ChunkWholeFilesAsync(http, plan, ct).ConfigureAwait(false);
        Data.SecondsTotal = plan.Live ? 0 : plan.Tracks.First(t => t.CountsTime).First.TotalDuration;
        return plan;
    }

    async Task<Plan> PlanHlsAsync(HttpClient http, HlsPlaylist playlist, CancellationToken ct)
    {
        HlsMediaPlaylist video;
        Uri videoUrl;
        HlsMediaPlaylist? audio = null;
        Uri? audioUrl = null;
        var subtitles = new List<(string Name, string Language, Uri Url)>();

        if (playlist is HlsMasterPlaylist master)
        {
            // The quality is remembered by height and bit rate, not by address: after a refreshed link the same
            // qualities may live at other URLs.
            var variant = Data.Height > 0 || Data.Bandwidth > 0
                ? HlsChoice.Match(master.Variants, Data.Height, Data.Bandwidth)
                : HlsChoice.Best(master.Variants);
            Data.Height = variant.Height;
            Data.Bandwidth = variant.Bandwidth;

            videoUrl = variant.Url;
            video = await LoadMediaAsync(http, videoUrl, ct).ConfigureAwait(false);

            if (HlsChoice.AudioFor(master, variant, Data.AudioName) is { Url: { } found } rendition)
            {
                Data.AudioName = rendition.Name;
                audioUrl = found;
                audio = await LoadMediaAsync(http, found, ct).ConfigureAwait(false);
            }

            if (Data.Subtitles)
                foreach (var r in HlsChoice.SubtitlesFor(master, variant))
                    subtitles.Add((r.Name, r.Language ?? r.Name, r.Url!));
        }
        else
        {
            video = (HlsMediaPlaylist)playlist;
            videoUrl = video.Url;
        }

        var live = video.IsLive;
        var tracks = new List<Track>
        {
            new() { Id = "v", First = video, CountsTime = true, Reload = live ? c => LoadMediaAsync(http, videoUrl, c) : null },
        };
        if (audio != null)
            tracks.Add(new Track { Id = "a", First = audio, Reload = audio.IsLive ? c => LoadMediaAsync(http, audioUrl!, c) : null });

        // Subtitle tracks are a bonus: one that cannot be read is left out, it never stops the video.
        foreach (var (name, language, url) in subtitles)
        {
            try
            {
                var uri = url;
                var first = await LoadMediaAsync(http, uri, ct).ConfigureAwait(false);
                tracks.Add(new Track
                {
                    Id = $"s{tracks.Count:D2}", First = first, IsSubtitle = true, Language = language, Extension = ".vtt",
                    Reload = live ? c => LoadMediaAsync(http, uri, c) : null,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ctx.Logger.LogWarning(ex, "Subtitles {Name} of {File} could not be read", name, _job.FileName);
            }
        }

        return new Plan
        {
            Tracks = tracks,
            Live = live,
            PollSeconds = Math.Clamp(video.TargetDuration / 2, 1, 6),
        };
    }

    /// <summary>
    /// The picture and sound a video site's player already requested, as files: no playlist to read. Each is one whole file,
    /// which <see cref="ChunkWholeFilesAsync"/> cuts into parallel byte ranges; ffmpeg joins them.
    /// </summary>
    Plan PlanDirect()
    {
        static HlsMediaPlaylist Single(string url, double seconds)
        {
            var playlist = new HlsMediaPlaylist { Url = new Uri(url), IsEnded = true, PlaylistType = "VOD" };
            playlist.Segments.Add(new HlsSegment { Index = 0, Sequence = 0, Url = new Uri(url), Duration = seconds, IsWholeFile = true });
            return playlist;
        }

        var tracks = new List<Track> { new() { Id = "v", First = Single(_job.Url, Data.Seconds), CountsTime = true } };
        if (!string.IsNullOrEmpty(Data.AudioUrl)) tracks.Add(new Track { Id = "a", First = Single(Data.AudioUrl, Data.Seconds) });
        return new Plan { Tracks = tracks, IsDash = true };
    }

    Plan PlanDash(HttpClient http, DashManifest dash)
    {
        if (dash.Protection != null)
            throw new DownloadException(JobErrorKind.Unsupported, $"This stream is protected ({dash.Protection}) and cannot be downloaded.");

        var live = dash.IsDynamic;
        var tracks = new List<Track>();

        DashRepresentation? video = null;
        var variants = dash.VideoVariants();
        if (variants.Count > 0)
        {
            var chosen = Data.Height > 0 || Data.Bandwidth > 0
                ? HlsChoice.Match(variants.Select(v => v.Variant).ToList(), Data.Height, Data.Bandwidth)
                : HlsChoice.Best(variants.Select(v => v.Variant));
            Data.Height = chosen.Height;
            Data.Bandwidth = chosen.Bandwidth;
            video = variants.First(v => v.Variant == chosen).Representation;
        }

        var audio = dash.PickAudio(Data.AudioName);
        if (audio != null) Data.AudioName = audio.Name;

        // A live manifest is read again each time: the list of segments grows with the wall clock.
        async Task<HlsMediaPlaylist> Again(DashRepresentation rep, CancellationToken c)
        {
            var (text, url) = await FetchPlaylistAsync(http, _job.Url, c).ConfigureAwait(false);
            var fresh = DashParser.Parse(text, url);
            var all = fresh.Video.Concat(fresh.Audio).Concat(fresh.Text);
            return (all.FirstOrDefault(r => r.Id == rep.Id && r.Kind == rep.Kind) ?? rep).Build(DateTimeOffset.UtcNow);
        }

        foreach (var (rep, id) in new[] { (video, "v"), (audio, "a") })
        {
            if (rep == null) continue;
            var captured = rep;
            var first = rep.Build(DateTimeOffset.UtcNow);
            tracks.Add(new Track { Id = id, First = first, Reload = live ? c => Again(captured, c) : null });
        }

        if (tracks.Count == 0) throw new HlsFormatException("The manifest lists nothing to download.");
        foreach (var t in tracks)
            if (t.First.Segments.Count == 0 && !live) throw new HlsFormatException("The manifest lists no segments.");

        tracks[0].CountsTime = true;

        if (Data.Subtitles)
            foreach (var t in dash.Text)
            {
                var rep = t;
                var first = rep.Build(DateTimeOffset.UtcNow);
                if (first.Segments.Count == 0 && !live) continue;
                tracks.Add(new Track
                {
                    Id = $"s{tracks.Count:D2}", First = first, IsSubtitle = true, Language = rep.Language ?? rep.Id, Extension = rep.TextExtension,
                    Reload = live ? c => Again(rep, c) : null,
                });
            }

        return new Plan { Tracks = tracks, Live = live, IsDash = true, PollSeconds = Math.Clamp(dash.UpdateSeconds, 1, 6) };
    }

    async Task<HlsMediaPlaylist> LoadMediaAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        var (text, finalUrl) = await FetchPlaylistAsync(http, url.AbsoluteUri, ct).ConfigureAwait(false);
        return HlsParser.Parse(text, finalUrl) as HlsMediaPlaylist
               ?? throw new HlsFormatException("Expected a list of media segments but found a list of streams.");
    }

    async Task<(string Text, Uri Url)> FetchPlaylistAsync(HttpClient http, string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await HlsClient.FetchTextAsync(http, Spec(url), S.UserAgent, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt <= 3 && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                _job.Stats.AddRetry();
                await Task.Delay(DelayFor(ex, attempt), ct).ConfigureAwait(false);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Parts
    // ------------------------------------------------------------------------------------------------------------

    string PartPath(Track track, long sequence) => Path.Combine(Work, $"{track.Id}{sequence:D16}.seg");

    /// <summary>Registers the segments of a playlist that are new to the track; finished ones are on disk already, the rest are queued.</summary>
    int AddSegments(Track track, HlsMediaPlaylist playlist)
    {
        var added = 0;
        foreach (var segment in playlist.Segments)
        {
            Part part;
            lock (track.Known)
            {
                if (!track.Known.Add(segment.Sequence)) continue;
                part = new Part { Track = track, Segment = segment, FinalPath = PartPath(track, segment.Sequence) };
                track.Parts.Add(part);
            }

            var onDisk = File.Exists(part.FinalPath) ? new FileInfo(part.FinalPath).Length : 0;
            lock (_lock)
            {
                _partsTotal++;
                if (onDisk > 0)
                {
                    part.Done = true;
                    _partsDone++;
                    _completedBytes += onDisk;
                    _carriedBytes += onDisk;
                    if (track.CountsTime) _completedSeconds += segment.Duration;
                }
            }

            if (onDisk == 0)
            {
                TryDelete(part.FinalPath + ".tmp");   // a half-written file of an earlier run
                _queue.Enqueue(part);
            }

            added++;
        }

        return added;
    }

    /// <summary>Pieces finished by an earlier run that the playlist no longer lists (a recording that was paused): they still belong in the file.</summary>
    void AdoptRecordedPieces(Track track)
    {
        foreach (var file in Directory.GetFiles(Work, track.Id + "*.seg"))
        {
            if (!long.TryParse(Path.GetFileNameWithoutExtension(file)[track.Id.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)) continue;
            bool known;
            lock (track.Known) known = track.Known.Contains(sequence);
            if (known) continue;

            var length = new FileInfo(file).Length;
            if (length == 0) continue;

            var stub = new HlsSegment
            {
                Index = track.Parts.Count, Sequence = sequence, Url = new Uri(_job.Url), Duration = Math.Max(track.First.TargetDuration, 1),
                Map = track.First.Segments.FirstOrDefault()?.Map,
            };
            lock (track.Known)
            {
                track.Known.Add(sequence);
                track.Parts.Add(new Part { Track = track, Segment = stub, FinalPath = file, Done = true });
            }

            lock (_lock)
            {
                _partsTotal++;
                _partsDone++;
                _completedBytes += length;
                _carriedBytes += length;
                if (track.CountsTime) _completedSeconds += stub.Duration;
            }
        }
    }

    void StartCounting(Plan plan)
    {
        foreach (var track in plan.Tracks)
        {
            AddSegments(track, track.First);
            if (plan.Live) AdoptRecordedPieces(track);
        }

        _meter.Reset();
    }

    async Task EnsureInitAsync(HttpClient http, Part part, CancellationToken ct)
    {
        if (part.Segment.Map is not { } map) return;
        var path = InitPath(part.Track, map);
        if (File.Exists(path) && new FileInfo(path).Length > 0) return;

        var lazy = _inits.GetOrAdd(path, _ => new Lazy<Task>(() =>
            WithRetryAsync(() => DownloadToFileAsync(http, map.Url, map.Offset, map.Length, part.Segment.Key, part.Segment.Sequence, path, ct), ct)));
        try
        {
            await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            _inits.TryRemove(path, out _);
            throw;
        }
    }

    string InitPath(Track track, HlsMap map) =>
        Path.Combine(Work, $"init-{track.Id}-{Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{map.Url}|{map.Offset}|{map.Length}")))[..12]}.bin");

    // ------------------------------------------------------------------------------------------------------------
    // Transfer: workers fetch queued parts; pollers keep adding parts of a live stream
    // ------------------------------------------------------------------------------------------------------------

    async Task TransferAsync(HttpClient http, Plan plan, int workers, CancellationToken stop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Exception? fatal = null;
        _pollingDone = false;

        var pollers = plan.Tracks.Select(t => Task.Run(() => PollAsync(plan, t, linked.Token))).ToArray();
        var pollersTask = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(pollers).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref fatal, ex, null);
                linked.Cancel();
            }
            finally
            {
                _pollingDone = true;
            }
        });

        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            while (!linked.IsCancellationRequested)
            {
                if (!_queue.TryDequeue(out var part))
                {
                    if (_pollingDone && _queue.IsEmpty) return;
                    try
                    {
                        await Task.Delay(40, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                try
                {
                    Interlocked.Increment(ref _active);
                    await EnsureInitAsync(http, part, linked.Token).ConfigureAwait(false);
                    await WithRetryAsync(() => DownloadPartOnceAsync(http, part, linked.Token), linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when ((plan.Live || part.Track.IsSubtitle) && (ex is HttpStatusException or TimeoutException or HttpRequestException || ex is IOException and not DirectoryNotFoundException))
                {
                    // a recording goes on without a piece the server no longer has (the window moved on): a gap, not the end
                    _ctx.Logger.LogWarning(ex, "Skipping a live segment of {File}", _job.FileName);
                    _job.Stats.AddRetry();
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref fatal, ex, null);
                    linked.Cancel();
                    return;
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }
        })).ToArray();

        var all = Task.WhenAll(tasks);
        using var timer = new PeriodicTimer(S.TickInterval);
        var lastCheckpoint = DateTime.UtcNow;

        while (true)
        {
            var tick = timer.WaitForNextTickAsync(CancellationToken.None).AsTask();
            var first = await Task.WhenAny(all, tick).ConfigureAwait(false);

            var speed = _meter.Sample();
            _job.Stats.NotePeak(Volatile.Read(ref _active));
            _ctx.Publish(MakeSnapshot(speed));

            if (plan.Live && S.MaxRecordingLength > TimeSpan.Zero && !_limitReached)
            {
                double recorded;
                lock (_lock) recorded = _completedSeconds;
                if (recorded >= S.MaxRecordingLength.TotalSeconds)
                {
                    _limitReached = true;   // the same as pressing "Stop and save"
                    RequestFinish();
                }
            }

            if (DateTime.UtcNow - lastCheckpoint >= S.CheckpointInterval)
            {
                Persist();
                lastCheckpoint = DateTime.UtcNow;
            }

            if (first == all) break;
        }

        await all.ConfigureAwait(false);
        await pollersTask.ConfigureAwait(false);
        stop.ThrowIfCancellationRequested();
        if (fatal != null) ExceptionDispatchInfo.Capture(fatal).Throw();
    }

    /// <summary>For a live stream: ask for the segment list again and again until it ends, the user stops it, or the server goes away.</summary>
    async Task PollAsync(Plan plan, Track track, CancellationToken ct)
    {
        if (!plan.Live || track.Reload == null) return;

        var failures = 0;
        while (!ct.IsCancellationRequested && !_finish.IsCancellationRequested)
        {
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _finish.Token);
                await Task.Delay(TimeSpan.FromSeconds(plan.PollSeconds), wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;   // stopped, or the user finished the recording
            }

            try
            {
                var fresh = await track.Reload(ct).ConfigureAwait(false);
                AddSegments(track, fresh);
                failures = 0;
                if (fresh.IsEnded) return;   // the broadcast is over
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpStatusException or TimeoutException or HttpRequestException or IOException or HlsFormatException)
            {
                // The playlist disappearing is how most live streams end. Give up after a few misses; if
                // something was recorded that is the result, otherwise the stream never worked.
                if (++failures < 3) continue;
                if (track.IsSubtitle) return;   // a bonus: never the reason a recording fails
                lock (track.Known)
                    if (track.Parts.Count == 0) throw;
                return;
            }
        }
    }

    async Task DownloadPartOnceAsync(HttpClient http, Part part, CancellationToken ct)
    {
        var s = part.Segment;
        await DownloadToFileAsync(http, s.Url, s.ByteOffset, s.ByteLength, s.Key, s.Sequence, part.FinalPath, ct).ConfigureAwait(false);

        var size = new FileInfo(part.FinalPath).Length;
        part.Done = true;
        lock (_lock)
        {
            _completedBytes += size;
            if (part.Track.CountsTime) _completedSeconds += s.Duration;
            _partsDone++;
        }
    }

    /// <summary>Segments are small, so a failed one is simply fetched again (a few times) before the download is given up.</summary>
    async Task WithRetryAsync(Func<Task> action, CancellationToken ct)
    {
        var limit = Math.Clamp(S.MaxRetries, 1, 6);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt <= limit && IsTransient(ex))
            {
                _job.Stats.AddRetry();
                await Task.Delay(DelayFor(ex, attempt), ct).ConfigureAwait(false);
            }
        }
    }

    static bool IsTransient(Exception ex) =>
        ex is HttpStatusException { IsTransient: true } or TimeoutException or HttpRequestException
        || ex is IOException and not DirectoryNotFoundException;

    TimeSpan DelayFor(Exception ex, int attempt)
    {
        if (ex is HttpStatusException { RetryAfter: { } ra }) return TimeSpan.FromSeconds(Math.Min(ra.TotalSeconds, 60));
        var seconds = Math.Min(S.RetryMaxDelay.TotalSeconds, S.RetryBaseDelay.TotalSeconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds * (0.5 + _rng.NextDouble() * 0.5));
    }

    // ------------------------------------------------------------------------------------------------------------
    // One segment
    // ------------------------------------------------------------------------------------------------------------

    async Task DownloadToFileAsync(
        HttpClient http, Uri url, long? offset, long? length, HlsKey? key, long sequence, string finalPath, CancellationToken ct)
    {
        var temp = finalPath + ".tmp";

        // The key first: a worker that already holds a connection for its segment must not wait for one to fetch the key.
        byte[]? keyBytes = null;
        if (key is { Method: HlsEncryption.Aes128, Url: { } keyUrl })
            keyBytes = await GetKeyAsync(http, keyUrl, ct).ConfigureAwait(false);

        var (response, _) = await HttpFetcher.GetAsync(http, Spec(url.AbsoluteUri), S.UserAgent, r =>
        {
            if (length != null) r.Headers.Range = new RangeHeaderValue(offset ?? 0, (offset ?? 0) + length.Value - 1);
        }, ct).ConfigureAwait(false);

        using (response)
        {
            if ((int)response.StatusCode >= 400) throw new HttpStatusException((int)response.StatusCode, HttpFetcher.RetryAfter(response));
            if (length != null && (int)response.StatusCode != 206)
                throw new DownloadException(JobErrorKind.RangeIgnored, "The server ignored the byte range of a stream segment.");

            await using var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            FileStream file;
            try
            {
                file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DiskException($"Cannot write to \"{Work}\": {ex.Message}", ex);
            }

            long received = 0;
            try
            {
                await using (file)
                {
                    Aes? aes = null;
                    CryptoStream? crypto = null;
                    try
                    {
                        Stream sink = file;
                        if (keyBytes != null)
                        {
                            aes = Aes.Create();
                            aes.Key = keyBytes;
                            aes.IV = key!.Iv ?? HlsParser.DefaultIv(sequence);
                            aes.Mode = CipherMode.CBC;
                            aes.Padding = PaddingMode.PKCS7;
                            sink = crypto = new CryptoStream(file, aes.CreateDecryptor(), CryptoStreamMode.Write, leaveOpen: true);
                        }

                        received = await CopyAsync(network, sink, ct).ConfigureAwait(false);
                        if (crypto != null) await crypto.FlushFinalBlockAsync(ct).ConfigureAwait(false);
                    }
                    catch (CryptographicException ex)
                    {
                        throw new DownloadException(JobErrorKind.DataCorruption,
                            "A piece of the stream could not be decrypted (the key does not fit).", ex);
                    }
                    finally
                    {
                        if (crypto != null) await crypto.DisposeAsync().ConfigureAwait(false);
                        aes?.Dispose();
                    }
                }
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            if (response.Content.Headers.ContentLength is { } expected && received < expected)
            {
                TryDelete(temp);
                throw new IOException("The server closed the connection before the segment was complete.");
            }
        }

        try
        {
            File.Move(temp, finalPath, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new DiskException($"Could not store a downloaded piece: {ex.Message}", ex);
        }
    }

    /// <returns>The number of bytes read from the network.</returns>
    async Task<long> CopyAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(16 * 1024, S.ReadBufferBytes));
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long total = 0;
        try
        {
            while (true)
            {
                var want = SpeedLimiter.PreferredReadSize(buffer.Length, _ctx.GlobalLimiter, JobLimiter);

                stall.CancelAfter(S.StallTimeout);
                int n;
                try
                {
                    n = await from.ReadAsync(buffer.AsMemory(0, want), stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"No data received for {S.StallTimeout.TotalSeconds:0} s.");
                }
                finally
                {
                    stall.CancelAfter(Timeout.InfiniteTimeSpan);
                }

                if (n == 0) break;

                await SpeedLimiter.AcquireAsync(n, ct, _ctx.GlobalLimiter, JobLimiter).ConfigureAwait(false);

                try
                {
                    await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                }
                catch (IOException ex) when (to is FileStream or CryptoStream)
                {
                    throw new DiskException($"Could not write to disk: {ex.Message}", ex);
                }

                total += n;
                _meter.Add(n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    /// <summary>One key fetch for all segments that share it; a failed fetch is forgotten so the next segment tries again.</summary>
    async Task<byte[]> GetKeyAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        // Lazy: workers starting together must not each send their own request before one of them is stored
        var lazy = _keys.GetOrAdd(url.AbsoluteUri, _ => new Lazy<Task<byte[]>>(() => FetchKeyAsync(http, url, ct)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            _keys.TryRemove(url.AbsoluteUri, out _);
            throw;
        }
    }

    async Task<byte[]> FetchKeyAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        var (response, _) = await HttpFetcher.GetAsync(http, Spec(url.AbsoluteUri), S.UserAgent, null, ct).ConfigureAwait(false);
        using (response)
        {
            if ((int)response.StatusCode >= 400) throw new HttpStatusException((int)response.StatusCode, HttpFetcher.RetryAfter(response));
            var key = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (key.Length != 16)
                throw new DownloadException(JobErrorKind.Unsupported, $"The stream's key is {key.Length} bytes long instead of 16: it is not a plain AES-128 key (the site probably protects it).");
            return key;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Subtitles and big single files
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes each subtitle track as one file in the work folder; they move next to the video when it is finished (or go
    /// inside it). One file is kept as it is; segmented WebVTT is joined; segmented anything else cannot be joined.
    /// </summary>
    async Task WriteSubtitleFilesAsync(Plan plan, CancellationToken ct)
    {
        var index = 0;
        foreach (var track in plan.Tracks.Where(t => t.IsSubtitle))
        {
            var done = track.Parts.Where(p => p.Done).OrderBy(p => p.Segment.Sequence).ToList();
            if (done.Count == 0) continue;

            try
            {
                string content;
                if (done.Count == 1) content = await File.ReadAllTextAsync(done[0].FinalPath, ct).ConfigureAwait(false);
                else if (track.Extension == ".vtt") content = WebVtt.Merge(await Task.WhenAll(done.Select(p => File.ReadAllTextAsync(p.FinalPath, ct))).ConfigureAwait(false));
                else continue;

                var path = Path.Combine(Work, $"sub{++index:D2}-{FileNameHelper.Sanitize(track.Language)}{track.Extension}");
                await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
                _subtitleFiles.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ctx.Logger.LogWarning(ex, "Subtitles {Language} of {File} could not be saved", track.Language, _job.FileName);
            }
        }
    }

    /// <summary>
    /// A presentation that is one big file (DASH on-demand) becomes pieces of <see cref="EngineSettings.StreamChunkBytes"/>
    /// when the server can serve byte ranges, so it is fetched in parallel and can be resumed. Otherwise it stays one piece.
    /// </summary>
    async Task ChunkWholeFilesAsync(HttpClient http, Plan plan, CancellationToken ct)
    {
        if (plan.Live) return;
        var chunk = Math.Max(1024, S.StreamChunkBytes);

        foreach (var track in plan.Tracks.Where(t => !t.IsSubtitle))
        {
            var segments = track.First.Segments;
            if (segments.Count != 1 || !segments[0].IsWholeFile) continue;
            var whole = segments[0];

            long? total = null;
            try
            {
                var (response, _) = await HttpFetcher.GetAsync(http, Spec(whole.Url.AbsoluteUri), S.UserAgent,
                    r => r.Headers.Range = new RangeHeaderValue(0, 0), ct).ConfigureAwait(false);
                using (response)
                    if ((int)response.StatusCode == 206) total = response.Content.Headers.ContentRange?.Length;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                // the transfer will report it
            }

            if (total is not { } size || size <= 2 * chunk) continue;

            var pieces = new List<HlsSegment>();
            for (long offset = 0; offset < size; offset += chunk)
            {
                var length = Math.Min(chunk, size - offset);
                pieces.Add(new HlsSegment
                {
                    Index = pieces.Count, Sequence = pieces.Count, Url = whole.Url, Duration = whole.Duration * length / size,
                    ByteOffset = offset, ByteLength = length,
                });
            }

            segments.Clear();
            segments.AddRange(pieces);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Joining
    // ------------------------------------------------------------------------------------------------------------

    async Task JoinAsync(Plan plan, CancellationToken ct)
    {
        var media = plan.Tracks.Where(t => !t.IsSubtitle).ToList();
        if (!media.Any(t => t.Parts.Any(p => p.Done)))
            throw new DownloadException(JobErrorKind.Network, "Nothing was recorded: the stream did not deliver any media.");

        _note = plan.Live ? "Saving the recording…" : "Joining the pieces…";
        _ctx.Publish(MakeSnapshot(0));

        var inputs = new List<string>();
        foreach (var track in media)
        {
            var file = Path.Combine(Work, track.Id == "v" ? "video.bin" : "audio.bin");
            await ConcatAsync(track, file, ct).ConfigureAwait(false);
            inputs.Add(file);
        }

        _subtitleFiles.Clear();
        await WriteSubtitleFilesAsync(plan, ct).ConfigureAwait(false);

        var wanted = Path.GetExtension(_job.FileName).ToLowerInvariant();
        if (wanted is not (".mp4" or ".mkv" or ".ts" or ".m4v" or ".mov")) wanted = ".mp4";

        // DASH pieces and fragmented HLS are MP4 inside; transport-stream pieces are not
        var isoBmff = plan.IsDash || media[0].Parts.Any(p => p.Segment.Map != null);
        var ffmpeg = S.FindFfmpeg();
        string output;
        string extension;

        if (ffmpeg == null)
        {
            if (inputs.Count > 1)
                throw new DownloadException(JobErrorKind.Tool,
                    "This stream keeps picture and sound apart, and joining them needs ffmpeg. Install ffmpeg or set its location in Settings > Downloads, then try again; nothing has to be downloaded again.");

            // Without ffmpeg a stream can still be saved as it is: transport-stream pieces make a playable .ts,
            // MP4 pieces make a playable .mp4.
            extension = isoBmff ? ".mp4" : ".ts";
            output = inputs[0];
        }
        else if (inputs.Count == 1 && !isoBmff && wanted == ".ts")
        {
            extension = ".ts";
            output = inputs[0];   // already what was asked for
        }
        else
        {
            extension = wanted;
            output = Path.Combine(Work, "output" + extension);
            TryDelete(output);

            // WebVTT can go inside MP4/MKV; anything else (TTML) stays a file beside the video
            var embeddable = Data.EmbedSubtitles && (extension is ".mp4" or ".m4v" or ".mov" or ".mkv")
                ? _subtitleFiles.Where(f => f.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<string>();

            var (code, log) = await Ffmpeg.RunAsync(ffmpeg,
                Ffmpeg.RemuxArguments(inputs, output, fastStart: true, embeddable.Select(f => (f, LanguageCode(f))).ToList()), ct).ConfigureAwait(false);

            if ((code != 0 || !File.Exists(output)) && embeddable.Count > 0)
            {
                // the subtitles were the problem, not the video: join without them and keep them as files
                _ctx.Logger.LogWarning("ffmpeg could not put the subtitles inside {File}: {Log}", _job.FileName, log);
                embeddable.Clear();
                TryDelete(output);
                (code, log) = await Ffmpeg.RunAsync(ffmpeg, Ffmpeg.RemuxArguments(inputs, output, fastStart: true), ct).ConfigureAwait(false);
            }

            if (code != 0 || !File.Exists(output))
            {
                var reason = log.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? $"exit code {code}";
                throw new DownloadException(JobErrorKind.Tool, $"ffmpeg could not join the stream: {reason}");
            }

            foreach (var inside in embeddable) _subtitleFiles.Remove(inside);   // they are in the video now
        }

        FinalizeFile(output, extension);
    }

    /// <summary>"sub01-en.vtt" → "eng" (what MP4 and MKV want for a track's language); "und" when it is not a language.</summary>
    static string LanguageCode(string subtitleFile)
    {
        var name = Path.GetFileNameWithoutExtension(subtitleFile);
        var language = name.Length > 6 ? name[6..] : name;
        try
        {
            var three = new CultureInfo(language).ThreeLetterISOLanguageName;
            return three.Length == 3 ? three : "und";
        }
        catch (CultureNotFoundException)
        {
            return "und";
        }
    }

    async Task ConcatAsync(Track track, string destination, CancellationToken ct)
    {
        try
        {
            await using var outFile = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, useAsync: true);
            HlsMap? lastMap = null;
            foreach (var part in track.Parts.Where(p => p.Done).OrderBy(p => p.Segment.Sequence))
            {
                if (part.Segment.Map is { } map && !Equals(map, lastMap))
                {
                    await AppendAsync(InitPath(track, map), outFile, ct).ConfigureAwait(false);
                    lastMap = map;
                }

                await AppendAsync(part.FinalPath, outFile, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DiskException($"Could not join the downloaded pieces: {ex.Message}", ex);
        }
    }

    static async Task AppendAsync(string source, Stream destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, useAsync: true);
        await input.CopyToAsync(destination, 256 * 1024, ct).ConfigureAwait(false);
    }

    void FinalizeFile(string output, string extension)
    {
        if (!string.Equals(Path.GetExtension(_job.FileName), extension, StringComparison.OrdinalIgnoreCase))
            _job.FileName = Path.ChangeExtension(_job.FileName, extension);

        if (File.Exists(_job.TargetPath)) _job.FileName = FileNameHelper.MakeUnique(_job.SaveDirectory, _job.FileName);

        try
        {
            File.Move(output, _job.TargetPath);
        }
        catch (IOException ex)
        {
            throw new DiskException($"Could not move the finished file into place: {ex.Message}", ex);
        }

        var size = new FileInfo(_job.TargetPath).Length;
        MoveSubtitles();

        try
        {
            Directory.Delete(Work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Logger.LogWarning(ex, "Could not remove the work folder {Folder}", Work);
        }

        if (S.WriteMarkOfTheWeb) NativeFile.TryWriteMarkOfTheWeb(_job.TargetPath, _job.Referer, _job.Url);

        _note = null;
        _job.TotalSize = size;
        _job.State = DownloadState.Completed;
        _job.CompletedAt = DateTimeOffset.Now;
        _job.Cookies = null;
        _job.Headers = new Dictionary<string, string>();
        Persist();
        _ctx.Publish(MakeSnapshot(0));
    }

    /// <summary>"Movie.mp4" gets "Movie.en.vtt" beside it: players such as VLC and Plex pick that up on their own.</summary>
    void MoveSubtitles()
    {
        var baseName = Path.GetFileNameWithoutExtension(_job.FileName);
        foreach (var file in _subtitleFiles.Where(File.Exists))
        {
            // "sub01-en.vtt" -> language "en"
            var name = Path.GetFileNameWithoutExtension(file);
            var language = name.Length > 6 ? name[6..] : name;
            var target = Path.Combine(_job.SaveDirectory, $"{baseName}.{language}{Path.GetExtension(file)}");
            for (var n = 2; File.Exists(target); n++)
                target = Path.Combine(_job.SaveDirectory, $"{baseName}.{language}-{n}{Path.GetExtension(file)}");

            try
            {
                File.Move(file, target);
            }
            catch (IOException ex)
            {
                _ctx.Logger.LogWarning(ex, "Could not store the subtitles {File}", target);
            }
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
    // Progress
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Copies the live counters into the persisted data.</summary>
    void SyncData()
    {
        lock (_lock)
        {
            Data.BytesDone = _completedBytes;
            Data.PartsDone = _partsDone;
            Data.Parts = _partsTotal;
            Data.SecondsDone = _completedSeconds;
            Data.EstimatedTotal = Estimate();
        }
    }

    /// <summary>The finished size: from the bit rate at first, then from what the segments so far weighed. Call under the lock.</summary>
    long? Estimate()
    {
        if (Data.Live) return null;   // a recording has no known end
        var total = Data.SecondsTotal;
        if (total <= 0) return null;
        if (_completedSeconds > 0 && _completedBytes > 0) return Math.Max(_completedBytes, (long)(_completedBytes / _completedSeconds * total));
        return Data.Bandwidth > 0 ? (long)(Data.Bandwidth / 8.0 * total) : null;
    }

    JobSnapshot MakeSnapshot(double speed)
    {
        long downloaded;
        long? total;
        int done, parts, active;
        double seconds;
        lock (_lock)
        {
            parts = _partsTotal > 0 ? _partsTotal : Data.Parts;
            done = _partsTotal > 0 ? _partsDone : Data.PartsDone;
            seconds = _partsTotal > 0 ? _completedSeconds : Data.SecondsDone;
            // finished pieces, plus what pieces still in flight have received
            downloaded = _partsTotal > 0 ? Math.Max(_completedBytes, _carriedBytes + _meter.TotalBytes) : Data.BytesDone;
            total = _partsTotal > 0 ? Estimate() : Data.EstimatedTotal;
        }

        active = Volatile.Read(ref _active);
        if (_job.State == DownloadState.Completed && _job.TotalSize is { } t)
        {
            downloaded = t;
            total = t;
        }
        else if (total is { } e && e < downloaded)
        {
            total = downloaded;
        }

        TimeSpan? eta = null;
        if (speed > 1 && total is { } tot && tot > downloaded) eta = TimeSpan.FromSeconds((tot - downloaded) / speed);

        return new JobSnapshot(_job.Id, _job.State, _job.FileName, _job.SaveDirectory, downloaded, total, speed, eta, active,
            Array.Empty<SegmentInfo>(), true, _job.Error, _job.ErrorKind, 0, 0, _job.Stats.Retries, 0, _job.Stats.PeakConnections,
            _note, parts, done, Data.Live, seconds);
    }
}
