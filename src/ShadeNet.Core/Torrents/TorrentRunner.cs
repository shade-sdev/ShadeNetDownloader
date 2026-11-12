using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;
using ShadeNet.Core.Engine;
using EngineSettings = ShadeNet.Core.Engine.EngineSettings;
using ShadeNet.Core.Models;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Torrents;

/// <summary>
/// Downloads one torrent with the shared <see cref="TorrentSession"/>: gets its file list (from the peers, for a magnet link), puts its files
/// in place (only the chosen ones), reports progress like any other download, and when the chosen files are complete hands the torrent to
/// the session to be shared for a while. Pausing stops it and saves where it was (the engine's resume data), so it carries on later without
/// checking everything again.
/// </summary>
internal sealed class TorrentRunner : IJobRunner
{
    readonly DownloadJob _job;
    readonly RunnerContext _ctx;
    string? _activity;
    TorrentManager? _manager;
    long _uploadedBase;
    readonly RateMeter _down = new();
    readonly RateMeter _up = new();

    public TorrentRunner(DownloadJob job, RunnerContext ctx)
    {
        _job = job;
        _ctx = ctx;
        JobLimiter = new SpeedLimiter(job.SpeedLimit ?? 0);
        _job.Torrent ??= new TorrentJobData();
    }

    public SpeedLimiter JobLimiter { get; }

    public void RequestFinish()
    {
        // only recordings finish early
    }

    TorrentJobData Data => _job.Torrent!;

    TorrentSession Session => _ctx.Torrents ?? throw new InvalidOperationException("No torrent session.");

    public async Task<RunOutcome> RunAsync(CancellationToken stop)
    {
        RunOutcome outcome;
        Exception? failure = null;
        var handedOver = false;
        try
        {
            _job.Error = null;
            _job.ErrorKind = JobErrorKind.None;
            _job.Stats.Reset();
            _activity = "Starting…";
            SetState(DownloadState.Probing);

            var torrent = Session.LoadMetadata(Data);
            if (torrent == null)
            {
                var source = !string.IsNullOrEmpty(Data.Magnet) ? Data.Magnet : _job.Url;
                // (a .torrent file on the web is asked for with what it was handed over with: the browser's sign-in, its page)
                var web = Uri.TryCreate(source, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                    ? new Net.RequestSpec(source, _job.Referer, _job.Cookies, _job.UserAgent, _job.Headers)
                    : null;
                var (fetched, _) = await Session.FetchAsync(source, a =>
                {
                    _activity = a;
                    Publish(0);
                }, stop, web, Data).ConfigureAwait(false);
                torrent = fetched;
            }

            ApplyMetadata(torrent);
            stop.ThrowIfCancellationRequested();

            _manager = await Session.OpenAsync(_job, torrent).ConfigureAwait(false);
            _uploadedBase = Data.Uploaded;
            await _manager.StartAsync().ConfigureAwait(false);

            var limit = _job.SpeedLimit ?? 0;
            var lastSaved = Stopwatch.GetTimestamp();
            long wanted;
            while (true)
            {
                stop.ThrowIfCancellationRequested();
                var m = _manager;
                if (m.State == TorrentState.Error)
                    throw new DiskException("The torrent's files could not be " + (m.Error?.Reason == Reason.ReadFailure ? "read" : "written") + ": "
                                            + (m.Error?.Exception.Message ?? "unknown error"), m.Error?.Exception ?? new IOException());

                // (the files can be chosen again while it runs: Details > Files)
                wanted = Data.WantedBytes;
                _job.TotalSize = wanted;
                Data.BytesDone = TorrentSession.BytesDone(m, wanted);
                Data.Uploaded = _uploadedBase + Session.SentOf(m);
                if (TorrentSession.WantedComplete(m)) break;

                if (m.State is TorrentState.Hashing or TorrentState.HashingPaused or TorrentState.Starting)
                {
                    _activity = m.State == TorrentState.Hashing ? $"Checking the files already on the disk ({m.Progress:0}%)…" : "Starting…";
                    if (_job.State != DownloadState.Probing) SetState(DownloadState.Probing);
                    else Publish(0);
                }
                else
                {
                    var route = Session.Route;
                    _activity = route.Offline ? $"Waiting for {route.AdapterName} to connect…" : m.OpenConnections == 0 ? "Looking for peers…" : null;
                    _job.Stats.NotePeak(m.OpenConnections);
                    if (_job.State != DownloadState.Downloading) SetState(DownloadState.Downloading);
                    else Publish(_down.Sample(Session.ReceivedOf(m)));
                }

                // a speed limit set on the card while it runs
                if ((_job.SpeedLimit ?? 0) != limit)
                {
                    limit = _job.SpeedLimit ?? 0;
                    await Session.RebalanceAsync().ConfigureAwait(false);
                }

                if ((Stopwatch.GetTimestamp() - lastSaved) / (double)Stopwatch.Frequency >= 3)
                {
                    lastSaved = Stopwatch.GetTimestamp();
                    Persist();
                }

                await Task.Delay(250, stop).ConfigureAwait(false);
            }

            wanted = Data.WantedBytes;
            Data.BytesDone = wanted;
            _job.TotalSize = wanted;
            _job.CompletedAt = DateTimeOffset.Now;
            _activity = null;
            outcome = RunOutcome.Completed;

            if (Session.ShouldSeed(_job))
            {
                // the session shares it from here on (it is out of the queue: a finished download never holds one up)
                _job.State = DownloadState.Completed;
                Session.Seed(_job, _manager, _uploadedBase);
                handedOver = true;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            outcome = RunOutcome.Paused;
        }
        catch (Exception ex)
        {
            failure = ex;
            outcome = RunOutcome.Failed;
        }

        // stopped (resume data saved) before the state says so: a download started again at once finds it out of the engine
        if (_manager != null && !handedOver)
        {
            Data.Uploaded = _uploadedBase + Session.SentOf(_manager);
            await Session.CloseAsync(_manager).ConfigureAwait(false);
        }

        switch (outcome)
        {
            case RunOutcome.Completed:
                SetState(DownloadState.Completed);
                break;
            case RunOutcome.Paused:
                _activity = null;
                SetState(DownloadState.Paused);
                break;
            default:
                _ctx.Logger.LogWarning(failure, "Torrent {File} failed", _job.FileName);
                _activity = null;
                _job.Error = failure!.Message;
                _job.ErrorKind = failure switch
                {
                    DownloadException d => d.Kind,
                    IOException or UnauthorizedAccessException => JobErrorKind.Disk,
                    HttpRequestException => JobErrorKind.Network,
                    _ => JobErrorKind.Other,
                };
                SetState(DownloadState.Failed);
                break;
        }

        return outcome;
    }

    /// <summary>The file list is known: the job learns its files, size and (unless the user chose them) its name and folder.</summary>
    void ApplyMetadata(Torrent torrent)
    {
        Data.InfoHash = TorrentSession.HashOf(torrent);
        if (string.IsNullOrEmpty(Data.Magnet)) Data.Magnet = TorrentSession.MagnetOf(torrent);
        Data.Name = torrent.Name;
        Data.IsFolder = torrent.Files.Count > 1;
        if (Data.Files == null || Data.Files.Count != torrent.Files.Count) Data.Files = TorrentSession.FilesOf(torrent);

        if (_job.FolderProvisional)
        {
            _job.Category = TorrentNaming.CategoryOf(Data, _ctx.Settings);
            _job.SaveDirectory = _ctx.Settings.ResolveCategoryFolder(_job.Category);
            _job.FolderProvisional = false;
        }

        if (_job.NameProvisional)
        {
            _job.FileName = TorrentNaming.Unique(_job.SaveDirectory, TorrentNaming.NameOf(torrent), p => _ctx.IsPathTaken?.Invoke(p, _job.Id) ?? false);
            _job.NameProvisional = false;
        }

        _job.TotalSize = Data.WantedBytes;
        _job.Probed = true;
        _job.AcceptsRanges = true;
        Persist();
    }

    void SetState(DownloadState state)
    {
        _job.State = state;
        Persist();
        Publish(state == DownloadState.Downloading && _manager != null ? _down.Sample(Session.ReceivedOf(_manager)) : 0);
    }

    void Publish(double speed)
    {
        var m = _manager;
        var (swarmSeeds, swarmLeechers) = m == null ? (null, null) : TorrentSession.SwarmOf(m);
        var status = new TorrentStatus(m?.OpenConnections ?? 0, m?.Peers.Seeds ?? 0, m == null ? 0 : _up.Sample(Session.SentOf(m)), Data.Uploaded,
            TorrentSession.Ratio(Data), false, _activity, swarmSeeds, swarmLeechers);
        _ctx.Publish(Snapshot(_job, speed, status));
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

    /// <summary>A torrent job's snapshot (also used while it is not running, and while it is shared).</summary>
    public static JobSnapshot Snapshot(DownloadJob job, double speed, TorrentStatus? status)
    {
        var data = job.Torrent ?? new TorrentJobData();
        var total = data.Files != null ? data.WantedBytes : job.TotalSize;
        var done = job.State == DownloadState.Completed && total is { } t ? t : data.BytesDone;
        TimeSpan? eta = job.State == DownloadState.Downloading && speed > 0 && total is { } all && all > done
            ? TimeSpan.FromSeconds((all - done) / speed)
            : null;
        status ??= new TorrentStatus(0, 0, 0, data.Uploaded, TorrentSession.Ratio(data), false, null);
        return new JobSnapshot(job.Id, job.State, job.FileName, job.SaveDirectory, done, total, speed, eta, status.Peers,
            Array.Empty<SegmentInfo>(), true, job.Error, job.ErrorKind, 0, 0, 0, 0, job.Stats.PeakConnections, null, 0, 0, false, 0, status);
    }
}

/// <summary>Names and places for torrents.</summary>
internal static class TorrentNaming
{
    /// <summary>A torrent of one file is saved as that file; one of several as a folder named after the torrent.</summary>
    public static string NameOf(Torrent torrent) =>
        FileNameHelper.Sanitize(torrent.Files.Count == 1 ? Path.GetFileName(torrent.Files[0].Path) : torrent.Name, "torrent");

    /// <summary>The category of the largest chosen file (a folder of episodes is Video, an album Music).</summary>
    public static string CategoryOf(TorrentJobData data, EngineSettings settings)
    {
        var largest = data.Files?.Where(f => f.Selected).OrderByDescending(f => f.Length).FirstOrDefault()
                      ?? data.Files?.OrderByDescending(f => f.Length).FirstOrDefault();
        return settings.CategoryOf(largest?.Path ?? data.Name ?? "");
    }

    /// <summary>A name not used yet in <paramref name="directory"/> by a file, a folder or another download: "Name", "Name (1)" ...</summary>
    public static string Unique(string directory, string name, Func<string, bool> isTaken)
    {
        bool Taken(string n)
        {
            var p = Path.Combine(directory, n);
            return File.Exists(p) || Directory.Exists(p) || File.Exists(p + ".sndpart") || isTaken(p);
        }

        if (!Taken(name)) return name;
        // a file keeps its extension at the end; a folder name may hold dots of its own ("Album 2.0")
        var ext = Path.GetExtension(name);
        var stem = ext.Length is > 1 and <= 6 && !ext.Contains(' ') ? name[..^ext.Length] : name;
        if (stem == name) ext = "";
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!Taken(candidate)) return candidate;
        }

        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }
}
