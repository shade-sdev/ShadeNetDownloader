using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShadeNet.Core.Hls;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNet.Core.Net;
using ShadeNet.Core.Storage;
using ShadeNet.Core.Torrents;
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

    /// <summary>Connection limits sites were seen to enforce: later downloads from them start within the limit.</summary>
    public HostConnectionMemory HostLimits { get; } = new();

    TorrentSession? _torrents;

    /// <summary>The torrent engine, made the first time a torrent is used (it opens a port: none is opened without a torrent).</summary>
    TorrentSession Torrents
    {
        get
        {
            lock (_gate)
            {
                return _torrents ??= new TorrentSession(Settings, () => GlobalLimiter.BytesPerSecond, _logger)
                {
                    Store = _store,
                    Changed = j => JobUpdated?.Invoke(Snapshot(j)),
                };
            }
        }
    }

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

        // finished torrents that were still being shared when ShadeNet closed go on being shared (until their limits)
        foreach (var job in loaded.Where(j => j is { Kind: DownloadKind.Torrent, State: DownloadState.Completed, Torrent.Seeding: true }))
        {
            if (Torrents.ShouldSeed(job)) _ = Torrents.ResumeSeedingAsync(job);
            else
            {
                job.Torrent!.Seeding = false;
                _store?.SaveJob(job);
            }
        }
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
        // a magnet link is the same torrent when it names the same torrent, whatever trackers it lists
        if (TorrentSession.IsMagnet(url))
        {
            try
            {
                return FindTorrent(TorrentSession.HashOf(TorrentSession.ParseMagnet(url)));
            }
            catch (DownloadException)
            {
                return null;
            }
        }

        lock (_gate)
        {
            return _jobs.FirstOrDefault(j =>
                string.Equals(j.Url, url, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(j.FinalUrl, url, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Stops every running download (and the sharing of finished torrents, which goes on at the next start).</summary>
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

        TorrentSession? torrents;
        lock (_gate)
        {
            torrents = _torrents;
            _torrents = null;
        }

        if (torrents != null) await torrents.DisposeAsync().ConfigureAwait(false);
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
        // a torrent: asked for as one, a magnet link, or a .torrent file on this computer (one on the web is a file to save unless the
        // request says it is a torrent: the browser's downloads of .torrent files stay files)
        var web = Uri.TryCreate(request.Url, UriKind.Absolute, out var asWeb) && asWeb.Scheme is "http" or "https";
        if (request.Torrent != null || TorrentSession.IsMagnet(request.Url) || (!web && IsTorrentAddress(request.Url)))
        {
            probe?.Discard();
            return AddTorrent(request);
        }

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
                    Pieces = stream.Pieces?.ToList(), InitUrl = stream.Init, AudioPieces = stream.AudioPieces?.ToList(), AudioInitUrl = stream.AudioInit,
                    Played = stream.Played?.ToList(),
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

    // ------------------------------------------------------------------------------------------------------------
    // Torrents
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>True for a magnet link, a .torrent file (on the disk or the web), or an answer of the BitTorrent file type.</summary>
    public static bool IsTorrentAddress(string? url, string? contentType = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (TorrentSession.IsMagnet(url)) return true;
        if (contentType != null && contentType.StartsWith("application/x-bittorrent", StringComparison.OrdinalIgnoreCase)) return true;
        var path = Uri.TryCreate(url.Trim().Trim('"'), UriKind.Absolute, out var u) ? (u.IsFile ? u.LocalPath : u.AbsolutePath) : url.Trim().Trim('"');
        return path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What a torrent holds: its name, size and files. A magnet link's file list comes from the peers sharing it, which can take a while
    /// (cancel to stop waiting); a .torrent file is read from the disk or the web.
    /// </summary>
    public Task<TorrentInfo> DescribeTorrentAsync(string source, CancellationToken ct = default) => DescribeTorrentAsync(source, null, ct);

    /// <param name="web">For a .torrent file on the web: the sign-in, referring page and identity to ask for it with (what the browser
    /// handed over).</param>
    public async Task<TorrentInfo> DescribeTorrentAsync(string source, RequestSpec? web, CancellationToken ct = default)
    {
        // (the peers are not asked for a torrent that is in the list already)
        if (TorrentSession.IsMagnet(source) && FindTorrent(TorrentSession.HashOf(TorrentSession.ParseMagnet(source))) is { } listed)
            throw new DownloadException(JobErrorKind.Other, $"This torrent is already in your list as “{listed.FileName}”.");

        var (torrent, bytes) = await Torrents.FetchAsync(source, null, ct, web).ConfigureAwait(false);
        if (FindTorrent(TorrentSession.HashOf(torrent)) is { } already)
            throw new DownloadException(JobErrorKind.Other, $"This torrent is already in your list as “{already.FileName}”.");
        return TorrentSession.InfoOf(torrent, bytes);
    }

    /// <summary>The download of the torrent with this identity (info hash), if it is in the list.</summary>
    public DownloadJob? FindTorrent(string infoHash)
    {
        lock (_gate) return _jobs.FirstOrDefault(j => j.Kind == DownloadKind.Torrent && string.Equals(j.Torrent?.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What the details drawer shows about a torrent: its peers, files, trackers and pieces. Live while it is in the engine, from what was
    /// saved otherwise; <see cref="TorrentDetails.Empty"/> for a download that is not a torrent.
    /// </summary>
    public async Task<TorrentDetails> GetTorrentDetailsAsync(Guid id)
    {
        if (Find(id) is not { Kind: DownloadKind.Torrent } job) return TorrentDetails.Empty;
        return await Torrents.DetailsAsync(job).ConfigureAwait(false);
    }

    /// <summary>
    /// Chooses the files of a torrent again, one priority per file (in the torrent's order). A running torrent follows at once; a finished
    /// one that is given a file it does not have yet starts downloading again (what it has is kept).
    /// </summary>
    public async Task SetTorrentFilesAsync(Guid id, IReadOnlyList<TorrentFilePriority> priorities)
    {
        if (Find(id) is not { Kind: DownloadKind.Torrent, Torrent.Files: { } files } job)
            throw new InvalidOperationException("The files of this torrent are not known yet.");
        if (priorities.Count != files.Count) throw new ArgumentException("One priority per file of the torrent.", nameof(priorities));
        if (priorities.All(p => p == TorrentFilePriority.Skip)) throw new ArgumentException("Choose at least one file of the torrent.", nameof(priorities));

        var added = false;
        for (var i = 0; i < files.Count; i++)
        {
            var want = priorities[i] != TorrentFilePriority.Skip;
            added |= want && !files[i].Selected;
            files[i].Selected = want;
            files[i].High = priorities[i] == TorrentFilePriority.High;
        }

        job.TotalSize = job.Torrent!.WantedBytes;
        if (Torrents.ManagerOf(id) != null) await Torrents.ApplyFilesAsync(job).ConfigureAwait(false);

        if (added && job.State == DownloadState.Completed)
        {
            // a finished torrent with a file it does not have: download that (stop sharing it first; the engine takes it back as a download)
            await Torrents.StopSeedingAsync(id).ConfigureAwait(false);
            job.State = DownloadState.Paused;
            job.CompletedAt = null;
            _store?.SaveJob(job);
            JobUpdated?.Invoke(Snapshot(job));
            StartNow(job);
            return;
        }

        _store?.SaveJob(job);
        JobUpdated?.Invoke(Snapshot(job));
    }

    /// <summary>Fetch a torrent's pieces in order (from the start of its files) or in the usual swarm-friendly way. A running torrent follows at once.</summary>
    public async Task SetTorrentSequentialAsync(Guid id, bool sequential)
    {
        if (Find(id) is not { Kind: DownloadKind.Torrent, Torrent: { } data } job) return;
        data.Sequential = sequential;
        _store?.SaveJob(job);
        await Torrents.ApplySequentialAsync(job).ConfigureAwait(false);
    }

    /// <summary>Whether a finished torrent is being shared with others right now.</summary>
    public bool IsSeeding(Guid id) => _torrents?.IsSeeding(id) == true;

    /// <summary>Stops sharing a finished torrent (its files stay).</summary>
    public async Task StopSeedingAsync(Guid id)
    {
        if (_torrents is { } t) await t.StopSeedingAsync(id).ConfigureAwait(false);
    }

    /// <summary>Shares a finished torrent again, until the sharing limits; false when its files are not all there any more.</summary>
    public async Task<bool> StartSeedingAsync(Guid id)
    {
        var job = Find(id);
        if (job is not { Kind: DownloadKind.Torrent, State: DownloadState.Completed }) return false;
        // asked for by hand: the limits count again from now
        job.Torrent!.SeedSeconds = 0;
        if (Settings.TorrentSeedRatio > 0 && TorrentSession.Ratio(job.Torrent) >= Settings.TorrentSeedRatio) job.Torrent.Uploaded = 0;
        return Settings.TorrentSeedRatio > 0 && await Torrents.ResumeSeedingAsync(job).ConfigureAwait(false);
    }

    DownloadJob TorrentJob(Guid id) =>
        Find(id) is { Kind: DownloadKind.Torrent, Torrent: not null } job ? job : throw new InvalidOperationException("That download is not a torrent.");

    /// <summary>
    /// Adds trackers to a torrent (one address per line, or several separated by spaces): each is asked on its own, at once if it runs,
    /// and they stay when it starts again. Returns how many were new; addresses that are not trackers are listed in the exception.
    /// </summary>
    public async Task<int> AddTorrentTrackersAsync(Guid id, string text)
    {
        var job = TorrentJob(id);
        var words = (text ?? "").Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var bad = words.Where(w => TorrentSession.ParseTracker(w) == null).ToList();
        if (words.Length == 0) throw new ArgumentException("Enter a tracker's address, e.g. udp://tracker.example.org:1337/announce.", nameof(text));
        if (bad.Count > 0) throw new ArgumentException($"Not a tracker address: {string.Join(", ", bad.Take(3))}. Trackers start with udp://, http:// or https://.", nameof(text));
        var added = await Torrents.AddTrackersAsync(job, words.Select(w => TorrentSession.ParseTracker(w)!).ToList()).ConfigureAwait(false);
        JobUpdated?.Invoke(Snapshot(job));
        return added;
    }

    /// <summary>Takes a tracker off a torrent (it stays off when the torrent starts again).</summary>
    public async Task RemoveTorrentTrackerAsync(Guid id, string address)
    {
        var job = TorrentJob(id);
        await Torrents.RemoveTrackerAsync(job, address).ConfigureAwait(false);
        JobUpdated?.Invoke(Snapshot(job));
    }

    /// <summary>"Ask for peers now": every tracker, the DHT and the local network are asked at once. False when the torrent is not running.</summary>
    public bool ReannounceTorrent(Guid id) => Torrents.Reannounce(TorrentJob(id));

    /// <summary>
    /// "Check the files again": every piece on the disk is checked against the torrent, and what is missing or damaged is downloaded again.
    /// A running torrent does it at once; a finished one starts for it; a paused one does it when it next starts.
    /// </summary>
    public async Task<TorrentRecheck> RecheckTorrentAsync(Guid id)
    {
        var job = TorrentJob(id);
        var data = job.Torrent!;
        if (string.IsNullOrEmpty(data.InfoHash) || data.Files == null)
            throw new InvalidOperationException("The torrent's file list is not known yet, so there is nothing to check.");

        if (Torrents.IsSeeding(id)) await Torrents.StopSeedingAsync(id).ConfigureAwait(false);
        else if (await Torrents.RecheckAsync(job).ConfigureAwait(false)) return TorrentRecheck.Checking;

        Torrents.ForgetResumeData(data.InfoHash);
        if (job.State != DownloadState.Completed) return TorrentRecheck.OnNextStart;

        // finished: it starts, checks everything, and downloads what is not right (then shares again, within the limits)
        job.State = DownloadState.Paused;
        job.CompletedAt = null;
        _store?.SaveJob(job);
        JobUpdated?.Invoke(Snapshot(job));
        StartNow(job);
        return TorrentRecheck.Restarted;
    }

    /// <summary>"Add a peer": connects a running torrent to someone known to share it ("address:port", "[IPv6 address]:port").</summary>
    public async Task AddTorrentPeerAsync(Guid id, string address)
    {
        var job = TorrentJob(id);
        var peer = TorrentSession.ParsePeer(address)
                   ?? throw new ArgumentException("Enter the peer as address:port, for example 203.0.113.7:51413 or [2001:db8::7]:51413.", nameof(address));
        if (!await Torrents.AddPeerAsync(job, peer).ConfigureAwait(false))
            throw new InvalidOperationException("Start the torrent first: peers are connected to only while it runs.");
    }

    /// <summary>Bans a peer's address from every torrent: it is disconnected now and never connected to again (until unbanned in Settings).</summary>
    public async Task BanTorrentPeerAsync(string address)
    {
        var peer = TorrentSession.ParsePeer(address, portRequired: false) ?? throw new ArgumentException("That is not a peer's address.", nameof(address));
        await Torrents.BanAsync(peer.Address).ConfigureAwait(false);
    }

    /// <summary>The banned peer addresses.</summary>
    public IReadOnlyList<string> BannedTorrentPeers => Torrents.BannedPeers;

    public void UnbanTorrentPeers() => Torrents.UnbanAll();

    /// <summary>
    /// How big a torrent's swarm is, before it is added: its trackers are asked how many have all of it and how many are downloading
    /// (for at most a few seconds).
    /// </summary>
    public Task<TorrentSwarmCount> CountTorrentSwarmAsync(TorrentInfo info, CancellationToken ct = default) =>
        Torrents.CountSwarmAsync(TorrentSession.Parse(info.Metadata), TimeSpan.FromSeconds(8), ct);

    /// <summary>How torrents reach the network now, in words: directly, through the proxy, bound to an adapter (and what is not used).</summary>
    public string DescribeTorrentNetwork() =>
        TorrentSession.DescribeRoute(_torrents?.Route ?? TorrentNetwork.RouteFor(Settings), Settings);

    /// <summary>The port the torrent engine listens on (0 before any torrent ran).</summary>
    public int TorrentListenPort => _torrents?.ListenPort ?? 0;

    DownloadJob AddTorrent(AddDownloadRequest request)
    {
        var choice = request.Torrent ?? new TorrentChoice();
        var url = request.Url.Trim();

        MonoTorrent.Torrent? torrent = null;
        byte[]? metadata = choice.Metadata;
        if (metadata == null && !TorrentSession.IsMagnet(url) && !(Uri.TryCreate(url, UriKind.Absolute, out var web) && web.Scheme is "http" or "https"))
        {
            var path = Uri.TryCreate(url, UriKind.Absolute, out var file) && file.IsFile ? file.LocalPath : url.Trim('"');
            if (!File.Exists(path)) throw new ArgumentException($"The .torrent file \"{path}\" was not found.", nameof(request));
            metadata = File.ReadAllBytes(path);
        }

        if (metadata != null) torrent = TorrentSession.Parse(metadata);

        var data = new TorrentJobData { Sequential = choice.Sequential };
        string suggested;
        if (torrent != null)
        {
            data.InfoHash = TorrentSession.HashOf(torrent);
            data.Magnet = TorrentSession.IsMagnet(url) ? url : TorrentSession.MagnetOf(torrent);
            data.Name = torrent.Name;
            data.IsFolder = torrent.Files.Count > 1;
            data.Files = TorrentSession.FilesOf(torrent, choice.Files);
            if (data.Files.All(f => !f.Selected)) throw new ArgumentException("Choose at least one file of the torrent.", nameof(request));
            suggested = TorrentNaming.NameOf(torrent);
        }
        else if (TorrentSession.IsMagnet(url))
        {
            var magnet = TorrentSession.ParseMagnet(url);
            data.InfoHash = TorrentSession.HashOf(magnet);
            data.Magnet = url;
            data.Name = magnet.Name;
            suggested = FileNameHelper.Sanitize(magnet.Name, "torrent " + data.InfoHash[..8].ToLowerInvariant());
        }
        else
        {
            // a .torrent file on the web, not fetched yet: the download fetches it first
            suggested = FileNameHelper.Sanitize(Path.GetFileNameWithoutExtension(GuessName(new Uri(url))), "torrent");
        }

        if (data.InfoHash.Length > 0 && FindTorrent(data.InfoHash) is { } already)
            throw new ArgumentException($"This torrent is already in your list as “{already.FileName}”.", nameof(request));

        var known = data.Files != null;
        var category = request.Category ?? (known ? TorrentNaming.CategoryOf(data, Settings) : CategoryResolver.General);
        var dir = request.SaveDirectory ?? Settings.ResolveCategoryFolder(category);
        Directory.CreateDirectory(dir);

        DownloadJob job;
        lock (_gate)
        {
            var name = TorrentNaming.Unique(dir, FileNameHelper.Sanitize(request.FileName ?? suggested, "torrent"),
                p => _jobs.Any(j => string.Equals(j.TargetPath, p, StringComparison.OrdinalIgnoreCase)));
            job = new DownloadJob
            {
                Url = url,
                // (a .torrent file on the web is fetched with what the browser handed it over with)
                Referer = request.Referer,
                Cookies = request.Cookies,
                UserAgent = request.UserAgent,
                Headers = request.Headers ?? new(),
                Kind = DownloadKind.Torrent,
                Torrent = data,
                FileName = name,
                SaveDirectory = dir,
                Category = category,
                MaxConnections = request.MaxConnections ?? Settings.DefaultConnections,
                SpeedLimit = request.SpeedLimit,
                State = request.Mode == AddMode.Hold ? DownloadState.Paused : DownloadState.Queued,
                Probed = known,
                AcceptsRanges = true,
                TotalSize = known ? data.WantedBytes : null,
                NameProvisional = request.FileName == null && !known,
                FolderProvisional = request.SaveDirectory == null && request.Category == null && !known,
            };
            _jobs.Add(job);
        }

        if (torrent != null && metadata != null) Torrents.SaveMetadataAsync(torrent, metadata).GetAwaiter().GetResult();

        _store?.SaveJob(job);
        JobAdded?.Invoke(job);
        JobUpdated?.Invoke(Snapshot(job));

        if (request.Mode == AddMode.Start) StartNow(job);
        else if (request.Mode == AddMode.Queue) Pump();

        return job;
    }

    /// <summary>A torrent's files, for removing them: each file of the torrent (not anything else that is in its folder).</summary>
    static IEnumerable<string> TorrentFiles(DownloadJob job)
    {
        var data = job.Torrent;
        if (data == null) yield break;
        if (!data.IsFolder)
        {
            yield return job.TargetPath;
            yield break;
        }

        var root = Path.GetFullPath(job.TargetPath) + Path.DirectorySeparatorChar;
        foreach (var f in data.Files ?? new List<TorrentFileEntry>())
        {
            var path = Path.GetFullPath(Path.Combine(root, f.Path));
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }

    static void DeleteTorrentFiles(DownloadJob job)
    {
        foreach (var path in TorrentFiles(job)) TryDelete(path);
        if (job.Torrent?.IsFolder != true || !Directory.Exists(job.TargetPath)) return;

        // the folders the torrent made, now empty (deepest first); a folder with anything else in it stays
        try
        {
            foreach (var dir in Directory.GetDirectories(job.TargetPath, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(job.TargetPath))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // in use: it stays
        }
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
            HostLimits = HostLimits,
            Torrents = job.Kind == DownloadKind.Torrent ? Torrents : null,
            IsPathTaken = (path, self) =>
            {
                lock (_gate) return _jobs.Any(j => j.Id != self && string.Equals(j.TargetPath, path, StringComparison.OrdinalIgnoreCase));
            },
        };

        IJobRunner runner = job.Kind switch
        {
            DownloadKind.Hls => job.Hls?.Record == true ? new RecordRunner(job, ctx) : new HlsRunner(job, ctx),
            DownloadKind.Torrent => new TorrentRunner(job, ctx),
            _ => new JobRunner(job, ctx),
        };
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

            if (outcome == RunOutcome.Completed && !rj.Removing)
            {
                JobCompleted?.Invoke(job);
                if (Settings.ExtractArchives) _ = ExtractWhenCompleteAsync(job);
            }

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
        NetworkLock.Invalidate();   // (the adapter may be another one now)
        Pump();
        if (_torrents is { } torrents) _ = torrents.ApplySettingsAsync();   // limits, port, DHT (sharing limits are read as it goes)
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

        if (job.Kind == DownloadKind.Torrent)
        {
            if (_torrents is { } torrents)
            {
                await torrents.StopSeedingAsync(id).ConfigureAwait(false);
                torrents.Forget(job.Torrent?.InfoHash);
            }
            else if (job.Torrent?.InfoHash is { Length: > 0 } hash)
            {
                Torrents.Forget(hash);
            }

            // unfinished pieces are useless without the download; the finished files go only if asked
            if (job.State != DownloadState.Completed || deleteFiles) DeleteTorrentFiles(job);
            JobRemoved?.Invoke(id);
            return;
        }

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
        if (_torrents is { } torrents) _ = torrents.ApplySettingsAsync();
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
        if (job.Kind == DownloadKind.Torrent) return TorrentRunner.Snapshot(job, 0, _torrents?.StatusOf(job));

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
            job.Stats.Retries, job.Stats.Fallbacks, job.Stats.PeakConnections, job.State == DownloadState.Completed ? job.Extraction : null);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Archives
    // ------------------------------------------------------------------------------------------------------------

    readonly HashSet<string> _extracting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A page is read this far for its links, no further (a page is not tens of megabytes).</summary>
    const int MaxPageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The links of a web page, for the link grabber: the page is fetched (with <paramref name="web"/>'s sign-in and referring page,
    /// through the proxy and the adapter like any download) and its links read. Throws a message for people when it is no page.
    /// </summary>
    public async Task<Links.GrabbedPage> GrabPageAsync(string url, RequestSpec? web = null, CancellationToken ct = default)
    {
        using var http = HttpFetcher.CreateClient(2, Settings);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var spec = (web ?? new RequestSpec(url)) with { Url = url };
        var (response, finalUrl) = await HttpFetcher.GetAsync(http, spec, Settings.UserAgent, null, timeout.Token).ConfigureAwait(false);
        using var _ = response;
        if (!response.IsSuccessStatusCode)
            throw new DownloadException(JobErrorKind.HttpStatus, $"The site answered {(int)response.StatusCode} ({response.ReasonPhrase}) for that page.");
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Length > 0 && !type.Contains("html", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            throw new DownloadException(JobErrorKind.Unsupported, "That address is a file, not a page with links: add it as a download.");

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while (copy.Length < MaxPageBytes && (read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0) copy.Write(buffer, 0, read);

        var charset = response.Content.Headers.ContentType?.CharSet;
        System.Text.Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(charset) ? System.Text.Encoding.UTF8 : System.Text.Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            encoding = System.Text.Encoding.UTF8;
        }

        return Links.LinkGrabber.FromHtml(encoding.GetString(copy.GetBuffer(), 0, (int)copy.Length), new Uri(finalUrl));
    }

    /// <summary>An archive was unpacked, or could not be (the app says so, and puts the archive in the Recycle Bin when asked to).</summary>
    public event Action<DownloadJob, Archives.ArchiveSet, Archives.ExtractResult>? ArchiveExtracted;

    /// <summary>Whether a download is an archive (or a part of one) that can be unpacked.</summary>
    public static bool IsArchive(DownloadJob job) =>
        job.Kind == DownloadKind.File && Archives.ArchiveExtractor.IsArchive(job.TargetPath);

    /// <summary>
    /// A finished download that is an archive is unpacked once all of it is here: a part waits for the other parts the list is still
    /// downloading (the last one to finish unpacks them all).
    /// </summary>
    async Task ExtractWhenCompleteAsync(DownloadJob job)
    {
        if (!IsArchive(job) || Archives.ArchiveExtractor.SetOf(job.TargetPath) is not { } set) return;
        lock (_gate)
        {
            if (_jobs.Any(j => j.Id != job.Id && j.Kind == DownloadKind.File && j.State != DownloadState.Completed && set.Includes(j.TargetPath)))
                return;   // another part is still coming
        }

        await ExtractAsync(job, set, null, automatic: true).ConfigureAwait(false);
    }

    /// <summary>"Extract": unpacks a finished archive now, with its password if it has one.</summary>
    public async Task<Archives.ExtractResult> ExtractAsync(Guid id, string? password = null)
    {
        if (Find(id) is not { State: DownloadState.Completed } job || !IsArchive(job) || Archives.ArchiveExtractor.SetOf(job.TargetPath) is not { } set)
            return new Archives.ExtractResult(Archives.ExtractStatus.Failed, Message: "That download is not a finished archive.");
        return await ExtractAsync(job, set, password, automatic: false).ConfigureAwait(false);
    }

    async Task<Archives.ExtractResult> ExtractAsync(DownloadJob job, Archives.ArchiveSet set, string? password, bool automatic)
    {
        lock (_gate)
        {
            if (!_extracting.Add(set.First)) return new Archives.ExtractResult(Archives.ExtractStatus.Failed, Message: "It is being extracted already.");
        }

        try
        {
            job.Extraction = "Extracting…";
            JobUpdated?.Invoke(Snapshot(job));
            Archives.ExtractResult result;
            try
            {
                result = await Task.Run(() => Archives.ArchiveExtractor.Extract(set, password, CancellationToken.None, Settings.FindSevenZip())).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Extracting {File} failed", job.FileName);
                result = new Archives.ExtractResult(Archives.ExtractStatus.Failed, Message: ex.Message);
            }

            job.Extraction = result.Status switch
            {
                Archives.ExtractStatus.Extracted => $"Extracted to {Path.GetFileName(result.Folder)}",
                Archives.ExtractStatus.NeedsPassword => "Needs a password to extract",
                Archives.ExtractStatus.WrongPassword => "Wrong password: not extracted",
                // (an automatic try while a part is missing says nothing: the part may come later)
                Archives.ExtractStatus.Incomplete => automatic ? null : "A part is missing: not extracted",
                _ => "Could not extract: " + result.Message,
            };
            JobUpdated?.Invoke(Snapshot(job));
            if (!(automatic && result.Status == Archives.ExtractStatus.Incomplete)) ArchiveExtracted?.Invoke(job, set, result);
            return result;
        }
        finally
        {
            lock (_gate) _extracting.Remove(set.First);
        }
    }
}
