using ShadeNet.Core;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Util;

namespace ShadeNetDownloader.Services;

/// <summary>Shows a Windows notification (a tray balloon) and reports clicks on it.</summary>
public interface ITrayNotifier
{
    /// <param name="tag">Handed back to <see cref="BalloonClicked"/>.</param>
    void ShowBalloon(string title, string text, bool silent, object? tag);

    event Action<object?>? BalloonClicked;
}

/// <summary>The window and the shell, as the notification logic needs them.</summary>
public interface IAppShell
{
    /// <summary>ShadeNet's window is visible and is the window the user is working in.</summary>
    bool IsForeground { get; }

    void BringToFront();

    /// <summary>Open Explorer with the file selected. Never <em>runs</em> the file: a download is not trusted.</summary>
    void ShowInFolder(string path);
}

/// <summary>
/// Tells the user, through Windows, about downloads that finished or failed while they were doing something else.
/// Events are collected for a moment and sent together, so twenty finished files make one notification, not twenty.
/// </summary>
public sealed class NotificationService
{
    readonly Func<GeneralSettings> _settings;
    readonly ITrayNotifier _notifier;
    readonly IAppShell _shell;
    readonly object _gate = new();
    readonly List<DownloadJob> _completed = new();
    readonly List<(string Name, string Reason)> _failed = new();
    readonly HashSet<Guid> _failedAlready = new();

    public NotificationService(DownloadManager manager, Func<GeneralSettings> settings, ITrayNotifier notifier, IAppShell shell)
    {
        _settings = settings;
        _notifier = notifier;
        _shell = shell;

        manager.JobCompleted += OnCompleted;
        manager.JobUpdated += OnUpdated;
        _notifier.BalloonClicked += OnBalloonClicked;
    }

    void OnCompleted(DownloadJob job)
    {
        lock (_gate) _completed.Add(job);
    }

    void OnUpdated(JobSnapshot s)
    {
        lock (_gate)
        {
            if (s.State == DownloadState.Failed)
            {
                // one notification per failure, however many snapshots follow
                if (_failedAlready.Add(s.Id)) _failed.Add((s.FileName, s.Error ?? "The download failed."));
            }
            else
            {
                _failedAlready.Remove(s.Id);   // a retry that fails again deserves a new notification
            }
        }
    }

    /// <summary>Sends what has collected. Called every second or two by the app (and directly by tests).</summary>
    public void Flush()
    {
        List<DownloadJob> completed;
        List<(string Name, string Reason)> failed;
        lock (_gate)
        {
            completed = _completed.ToList();
            failed = _failed.ToList();
            _completed.Clear();
            _failed.Clear();
        }

        var settings = _settings();
        if (settings.NotifyOnlyInBackground && _shell.IsForeground) return;   // they are looking at it: the card says it all

        var silent = !settings.NotificationSound;

        if (settings.NotifyOnComplete && completed.Count > 0)
        {
            if (completed.Count == 1)
            {
                var job = completed[0];
                var size = job.TotalSize is { } total ? $" · {ByteFormat.Size(total)}" : "";
                _notifier.ShowBalloon("Download finished", $"{job.FileName}{size}\nClick to show it in its folder.", silent, job.TargetPath);
            }
            else
            {
                var names = string.Join(", ", completed.Take(3).Select(j => j.FileName)) + (completed.Count > 3 ? $" and {completed.Count - 3} more" : "");
                _notifier.ShowBalloon($"{completed.Count} downloads finished", names, silent, null);
            }
        }

        if (settings.NotifyOnFailure && failed.Count > 0)
        {
            if (failed.Count == 1)
                _notifier.ShowBalloon("Download failed", $"{failed[0].Name}\n{failed[0].Reason}", silent, null);
            else
                _notifier.ShowBalloon($"{failed.Count} downloads failed", string.Join(", ", failed.Take(3).Select(f => f.Name)), silent, null);
        }
    }

    void OnBalloonClicked(object? tag)
    {
        if (tag is string path && !string.IsNullOrEmpty(path)) _shell.ShowInFolder(path);
        else _shell.BringToFront();
    }
}
