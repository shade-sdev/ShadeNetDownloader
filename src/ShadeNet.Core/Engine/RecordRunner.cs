using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using ShadeNet.Core.Hls;
using ShadeNet.Core.Models;
using ShadeNet.Core.Util;

namespace ShadeNet.Core.Engine;

/// <summary>
/// Records a live stream that is one endless HTTP response (an FLV pull: TikTok, Bilibili, Douyin ...) with ffmpeg. There are no
/// pieces to fetch in parallel and nothing to resume, so the job is simple: ffmpeg reads the stream with the headers the browser's player
/// sent and copies picture and sound into a Matroska file as they arrive (no re-encoding), until the stream ends, the recording length
/// limit is reached or the user presses "Stop and save". Pausing a recording saves it too: a stopped recording is a finished one.
/// </summary>
internal sealed class RecordRunner : IJobRunner
{
    readonly DownloadJob _job;
    readonly RunnerContext _ctx;
    readonly CancellationTokenSource _finish = new();   // "Stop and save"

    // progress, written by ffmpeg's output thread
    long _bytes;
    double _seconds;
    long _lastBytes;
    long _lastTicks = Stopwatch.GetTimestamp();
    double _speed;
    long _lastSaved = Stopwatch.GetTimestamp();
    string? _note;

    public RecordRunner(DownloadJob job, RunnerContext ctx)
    {
        _job = job;
        _ctx = ctx;
        JobLimiter = new SpeedLimiter(job.SpeedLimit ?? 0);   // (a recording is copied as it comes; there is nothing to slow down)
        _job.Hls ??= new HlsJobData();
    }

    public SpeedLimiter JobLimiter { get; }

    HlsJobData Data => _job.Hls!;

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

    public async Task<RunOutcome> RunAsync(CancellationToken stop)
    {
        string? part = null;
        try
        {
            _job.Error = null;
            _job.ErrorKind = JobErrorKind.None;
            _job.Stats.Reset();
            Data.Live = true;
            Data.Record = true;
            Data.BytesDone = 0;
            Data.SecondsDone = 0;
            SetState(DownloadState.Probing);

            var ffmpeg = _ctx.Settings.FindFfmpeg()
                         ?? throw new DownloadException(JobErrorKind.Tool,
                             "ffmpeg was not found, and a live stream cannot be recorded without it. Run tools\\get-tools.ps1, or choose its location in Settings > Downloads.");

            try
            {
                Directory.CreateDirectory(_job.SaveDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DiskException($"Cannot use the save folder \"{_job.SaveDirectory}\": {ex.Message}", ex);
            }

            // every run records into a file of its own (a recording that was stopped before is a finished one)
            if (!string.Equals(Path.GetExtension(_job.FileName), ".mkv", StringComparison.OrdinalIgnoreCase)) _job.FileName = Path.ChangeExtension(_job.FileName, ".mkv");
            if (File.Exists(_job.TargetPath)) _job.FileName = FileNameHelper.MakeUnique(_job.SaveDirectory, _job.FileName);
            part = _job.PartPath;

            var info = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in Ffmpeg.RecordArguments(_job.FinalUrl ?? _job.Url, part, _job.UserAgent ?? _ctx.Settings.UserAgent, _job.Referer, _job.Cookies, _job.Headers))
                info.ArgumentList.Add(a);

            using var process = new Process { StartInfo = info };
            var log = new StringBuilder();
            var quitting = 0;

            void Quit()
            {
                if (Interlocked.Exchange(ref quitting, 1) == 1) return;
                try
                {
                    process.StandardInput.WriteLine("q");   // ffmpeg finishes the file properly when it is told to quit
                    process.StandardInput.Flush();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    // it is gone already
                }

                // a stream that does not let go gets cut off after a few seconds
                _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ =>
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // it ended by itself
                    }
                }, TaskScheduler.Default);
            }

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line && OnProgress(line)) Quit();
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (log) { if (log.Length < 8000) log.AppendLine(e.Data); }
            };

            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new DownloadException(JobErrorKind.Tool, "ffmpeg could not be started: " + ex.Message, ex);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var stopped = stop.Register(Quit);
            using var finished = _finish.Token.Register(Quit);

            await process.WaitForExitAsync().ConfigureAwait(false);
            process.WaitForExit();   // flushes the redirected output

            var size = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (size == 0)
            {
                TryDelete(part);
                if (stop.IsCancellationRequested)
                {
                    SetState(DownloadState.Paused);
                    return RunOutcome.Paused;
                }

                string reason;
                lock (log) reason = log.ToString().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "the stream did not deliver any video";
                throw new DownloadException(JobErrorKind.Network, "Nothing was recorded: " + reason);
            }

            // the stream ended or was stopped: what was recorded is the video
            if (File.Exists(_job.TargetPath)) _job.FileName = FileNameHelper.MakeUnique(_job.SaveDirectory, _job.FileName);
            try
            {
                File.Move(part, _job.TargetPath);
            }
            catch (IOException ex)
            {
                throw new DiskException($"Could not move the finished recording into place: {ex.Message}", ex);
            }

            _job.TotalSize = size;
            _job.CompletedAt = DateTimeOffset.Now;
            Data.BytesDone = size;
            _note = null;
            SetState(DownloadState.Completed);
            return RunOutcome.Completed;
        }
        catch (Exception ex)
        {
            _ctx.Logger.LogWarning(ex, "Recording {File} failed", _job.FileName);
            if (part != null && File.Exists(part) && new FileInfo(part).Length == 0) TryDelete(part);
            _job.Error = ex.Message;
            _job.ErrorKind = ex switch
            {
                DownloadException d => d.Kind,
                IOException => JobErrorKind.Disk,
                _ => JobErrorKind.Other,
            };
            SetState(DownloadState.Failed);
            return RunOutcome.Failed;
        }
    }

    /// <summary>
    /// One line of ffmpeg's <c>-progress</c> output (<c>total_size=…</c>, <c>out_time_us=…</c>, and <c>progress=continue</c> ends each report).
    /// </summary>
    /// <returns><c>true</c> when the recording has reached the length the user limited recordings to.</returns>
    bool OnProgress(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return false;
        var key = line[..eq];
        var value = line[(eq + 1)..].Trim();

        if (key == "total_size" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)) _bytes = bytes;
        else if (key == "out_time_us" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micro)) _seconds = micro / 1_000_000.0;
        else if (key == "progress")
        {
            var now = Stopwatch.GetTimestamp();
            var span = (now - _lastTicks) / (double)Stopwatch.Frequency;
            if (span >= 0.9)
            {
                var rate = (_bytes - _lastBytes) / span;
                _speed = _speed <= 0 ? rate : _speed * 0.6 + rate * 0.4;
                _lastBytes = _bytes;
                _lastTicks = now;
            }

            Data.BytesDone = _bytes;
            Data.SecondsDone = _seconds;

            if (_job.State != DownloadState.Downloading && _bytes > 0) SetState(DownloadState.Downloading);
            else _ctx.Publish(MakeSnapshot(_speed));

            if ((now - _lastSaved) / (double)Stopwatch.Frequency >= 5)
            {
                _lastSaved = now;
                Persist();
            }

            var limit = _ctx.Settings.MaxRecordingLength;
            return limit > TimeSpan.Zero && _seconds >= limit.TotalSeconds;
        }

        return false;
    }

    void SetState(DownloadState state)
    {
        _job.State = state;
        Persist();
        _ctx.Publish(MakeSnapshot(state == DownloadState.Downloading ? _speed : 0));
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

    JobSnapshot MakeSnapshot(double speed)
    {
        var done = _job.State == DownloadState.Completed && _job.TotalSize is { } total ? total : Math.Max(_bytes, Data.BytesDone);
        return new JobSnapshot(_job.Id, _job.State, _job.FileName, _job.SaveDirectory, done, null, speed, null,
            _job.State == DownloadState.Downloading ? 1 : 0, Array.Empty<SegmentInfo>(), false, _job.Error, _job.ErrorKind, 0, 0, 0, 0, 0,
            _note, 0, 0, true, Math.Max(_seconds, Data.SecondsDone));
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover empty file is harmless
        }
    }
}
