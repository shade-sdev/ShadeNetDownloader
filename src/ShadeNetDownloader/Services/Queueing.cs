using CommunityToolkit.Mvvm.ComponentModel;
using ShadeNet.Core;
using ShadeNet.Core.Models;

namespace ShadeNetDownloader.Services;

/// <summary>When may the queue run? Pure arithmetic, so it can be tested without a clock.</summary>
public static class ScheduleMath
{
    /// <summary>
    /// True when <paramref name="now"/> is inside the daily window from <paramref name="start"/> to <paramref name="stop"/>.
    /// A window that ends before it starts runs overnight (22:00 → 06:00); the day list names the day it <em>starts</em> on.
    /// Equal start and stop mean the whole day.
    /// </summary>
    public static bool IsInWindow(DateTime now, TimeOnly start, TimeOnly stop, IReadOnlyCollection<DayOfWeek> days)
    {
        var time = TimeOnly.FromDateTime(now);

        if (start == stop) return days.Contains(now.DayOfWeek);

        if (start < stop)
            return time >= start && time < stop && days.Contains(now.DayOfWeek);

        // overnight: [start, midnight) belongs to today's window, [midnight, stop) to the window that began yesterday
        if (time >= start) return days.Contains(now.DayOfWeek);
        if (time < stop) return days.Contains(now.AddDays(-1).DayOfWeek);
        return false;
    }
}

/// <summary>
/// Starts and stops the queue at the edges of the schedule window. Acts only when the answer <em>changes</em>, so a user
/// who starts the queue by hand outside the window is not overruled a moment later; the next edge applies again.
/// </summary>
public sealed class QueueScheduler
{
    readonly DownloadManager _manager;
    readonly Func<ScheduleSettings> _settings;
    readonly Func<DateTime> _now;
    bool? _lastInWindow;

    public QueueScheduler(DownloadManager manager, Func<ScheduleSettings> settings, Func<DateTime>? now = null)
    {
        _manager = manager;
        _settings = settings;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>Forget the last answer: the next <see cref="Evaluate"/> applies the schedule even if nothing changed (settings were edited).</summary>
    public void Reset() => _lastInWindow = null;

    /// <summary>Call about every half minute, and after the schedule was edited.</summary>
    public void Evaluate()
    {
        var s = _settings();
        if (!s.Enabled)
        {
            _lastInWindow = null;
            return;
        }

        var inWindow = ScheduleMath.IsInWindow(_now(), s.Start, s.Stop, s.Days);
        if (_lastInWindow == inWindow) return;
        _lastInWindow = inWindow;

        // leaving the window pauses what the queue started; downloads the user started by hand keep going
        _manager.SetQueueRunning(inWindow, pauseQueueDownloads: !inWindow);
    }
}

public enum FinishAction
{
    None,
    CloseApp,
    Sleep,
    Hibernate,
    Shutdown,
}

/// <summary>What happens to the computer when the downloads are done. Faked in tests: a test must never sleep the machine.</summary>
public interface IPowerService
{
    void Perform(FinishAction action);
}

/// <summary>
/// "When all downloads are finished": waits until nothing is queued or running (after at least one download finished since
/// the choice was made), then counts down with a cancel button, then acts. The choice is deliberately <b>not</b> saved: a
/// forgotten "shut down" from last week must never switch the computer off today.
/// </summary>
public sealed partial class FinishWatcher : ObservableObject
{
    readonly DownloadManager _manager;
    readonly IPowerService _power;
    readonly ToastService _toasts;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    CancellationTokenSource? _countdown;
    bool _finishedSinceArmed;

    public FinishWatcher(DownloadManager manager, IPowerService power, ToastService toasts, int countdownSeconds = 60,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _manager = manager;
        _power = power;
        _toasts = toasts;
        CountdownSeconds = countdownSeconds;
        _delay = delay ?? Task.Delay;

        _manager.JobCompleted += _ => OnActivity(completed: true);
        _manager.JobUpdated += _ => OnActivity(completed: false);
        _manager.JobAdded += _ => CancelCountdown();   // new work arrived: the plan is off
    }

    public int CountdownSeconds { get; }

    [ObservableProperty] FinishAction _action;
    [ObservableProperty] bool _isCountingDown;
    [ObservableProperty] int _secondsLeft;

    partial void OnActionChanged(FinishAction value)
    {
        _finishedSinceArmed = false;
        CancelCountdown();
        OnPropertyChanged(nameof(IsArmed));
        // armed while everything already is idle and something finished earlier: nothing to wait for... but only a
        // download that finishes AFTER choosing counts, so choosing it on an idle list does nothing by itself
    }

    public bool IsArmed => Action != FinishAction.None;

    static bool IsBusy(DownloadState s) => s is DownloadState.Queued or DownloadState.Probing or DownloadState.Downloading or DownloadState.Stopping;

    void OnActivity(bool completed)
    {
        if (Action == FinishAction.None) return;
        if (completed) _finishedSinceArmed = true;

        if (_manager.Jobs.Any(j => IsBusy(j.State)))
        {
            CancelCountdown();   // something is running again
            return;
        }

        if (_finishedSinceArmed && !IsCountingDown) _ = CountDownAsync();
    }

    /// <summary>Stops the countdown and the plan; the user changed their mind.</summary>
    public void Cancel()
    {
        Action = FinishAction.None;
        CancelCountdown();
    }

    void CancelCountdown()
    {
        var cts = Interlocked.Exchange(ref _countdown, null);
        if (cts == null) return;
        cts.Cancel();
        IsCountingDown = false;
    }

    async Task CountDownAsync()
    {
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _countdown, cts, null) != null) return;

        IsCountingDown = true;
        var action = Action;

        try
        {
            for (var left = CountdownSeconds; left > 0; left--)
            {
                SecondsLeft = left;
                _toasts.Show($"{Describe(action)} in {left} s", ToastKind.Info, "Cancel", Cancel);
                await _delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
            }

            // last look before acting: nothing may have started in the meantime
            if (_manager.Jobs.Any(j => IsBusy(j.State)) || Action != action) return;

            Action = FinishAction.None;
            _power.Perform(action);
        }
        catch (OperationCanceledException)
        {
            // cancelled: the plan is off or work started again
        }
        finally
        {
            IsCountingDown = false;
            Interlocked.CompareExchange(ref _countdown, null, cts);
        }
    }

    /// <summary>"shut down", "sleep"...: the action as it reads in a short label.</summary>
    public static string ShortName(FinishAction action) => action switch
    {
        FinishAction.CloseApp => "close ShadeNet",
        FinishAction.Sleep => "sleep",
        FinishAction.Hibernate => "hibernate",
        FinishAction.Shutdown => "shut down",
        _ => "nothing",
    };

    public static string Describe(FinishAction action) => action switch
    {
        FinishAction.CloseApp => "Closing ShadeNet",
        FinishAction.Sleep => "Going to sleep",
        FinishAction.Hibernate => "Hibernating",
        FinishAction.Shutdown => "Shutting down",
        _ => "",
    };
}
