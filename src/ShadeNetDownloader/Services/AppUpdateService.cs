using System.Diagnostics;
using System.IO;
using ShadeNet.Core.Updates;

namespace ShadeNetDownloader.Services;

public enum AppUpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Starting,
    Failed,
}

/// <summary>Starts the replacement of this program by a newer one. The real one runs the setup program's update mode.</summary>
public interface IAppUpdateApplier
{
    /// <summary>This copy was put in place by the setup program, so the setup program can replace it (a copy run from a build folder cannot be).</summary>
    bool CanApply { get; }

    /// <summary>Starts the replacement; it waits for this program to end. The caller quits right after.</summary>
    void Start(string packagePath);
}

/// <summary>
/// Looks, about once a day, for a newer version of ShadeNet itself, and ASKS (a notification with an Update button) before
/// installing: the update package is downloaded and verified, then a copy of the uninstaller replaces the program's files while
/// the program is closed, and starts it again. Never installs anything by itself.
/// </summary>
public sealed class AppUpdateService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    readonly IAppUpdates _updates;
    readonly SettingsService _settings;
    readonly ToastService _toasts;
    readonly IAppUpdateApplier _applier;
    readonly Action _exit;
    readonly Action<Uri> _open;
    readonly Func<int> _activeDownloads;
    readonly Func<DateTimeOffset> _now;
    readonly object _gate = new();
    string _announced = "";

    public AppUpdateService(IAppUpdates updates, SettingsService settings, ToastService toasts, IAppUpdateApplier applier, Action exit,
        Action<Uri>? open = null, Func<int>? activeDownloads = null, Func<DateTimeOffset>? now = null)
    {
        _updates = updates;
        _settings = settings;
        _toasts = toasts;
        _applier = applier;
        _exit = exit;
        _open = open ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        _activeDownloads = activeDownloads ?? (() => 0);
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public AppUpdateState State { get; private set; } = AppUpdateState.Idle;
    public AppUpdate? Available { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public bool CanInstall => _applier.CanApply;
    public int ActiveDownloads => _activeDownloads();

    public event Action? Changed;

    void Set(AppUpdateState state, Action? more = null)
    {
        lock (_gate)
        {
            State = state;
            more?.Invoke();
        }

        Changed?.Invoke();
    }

    /// <summary>The automatic look: does nothing when switched off or when one ran within the last day.</summary>
    public async Task CheckDueAsync(CancellationToken ct = default)
    {
        var updates = _settings.Current.Updates;
        if (!updates.CheckForUpdates) return;
        if (State is AppUpdateState.Downloading or AppUpdateState.Starting) return;
        if (updates.LastCheck is { } last && _now() - last < Interval) return;

        updates.LastCheck = _now();
        _settings.Save();

        try
        {
            await LookAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;   // offline, GitHub down: the next look tries again; nobody wants an error for this
        }

        if (Available is { } found && _announced != found.LatestText)
        {
            _announced = found.LatestText;
            Announce(found);
        }
    }

    void Announce(AppUpdate found)
    {
        if (CanInstall)
        {
            var note = ActiveDownloads > 0 ? " Running downloads are paused and saved, and continue afterwards." : "";
            _toasts.Show($"ShadeNet {found.LatestText} is available (you have {found.Current.ToString(3)}). It installs in a moment and restarts the program.{note}",
                ToastKind.Info, "Update", () => _ = InstallAsync(), TimeSpan.FromSeconds(30));
        }
        else if (found.ReleasePage != null)
        {
            _toasts.Show($"ShadeNet {found.LatestText} is available. This copy was not installed with the setup program, so it cannot update itself.",
                ToastKind.Info, "What's new", () => _open(found.ReleasePage), TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>"Check for updates" in Settings: ignores the schedule and reports the result in <see cref="State"/>.</summary>
    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        try
        {
            await LookAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Set(AppUpdateState.Failed, () => Error = ex is UpdateException ? ex.Message : "Could not look for updates: " + ex.Message);
        }
    }

    async Task LookAsync(CancellationToken ct)
    {
        Set(AppUpdateState.Checking, () => Error = null);
        var found = await _updates.CheckAsync(ct).ConfigureAwait(false);
        Set(found == null ? AppUpdateState.UpToDate : AppUpdateState.Available, () => Available = found);
    }

    /// <summary>Downloads and verifies the update, hands over to the setup program's update mode and quits. Throws nothing: a failure is the <see cref="State"/>.</summary>
    public async Task InstallAsync(CancellationToken ct = default)
    {
        if (Available is not { } update) return;
        if (!CanInstall)
        {
            Set(AppUpdateState.Failed, () => Error = "This copy was not installed with the setup program, so it cannot update itself. Install the new setup instead.");
            return;
        }

        try
        {
            Set(AppUpdateState.Downloading, () => { Progress = 0; Error = null; });
            // Progress<T> delivers its reports on another thread, possibly after the download has ended: a late report must not
            // pull the state back from "starting" to "downloading".
            var progress = new Progress<double>(p =>
            {
                lock (_gate)
                {
                    if (State != AppUpdateState.Downloading) return;
                    Progress = p;
                }

                Changed?.Invoke();
            });
            var package = await _updates.DownloadAsync(update, progress, ct).ConfigureAwait(false);

            Set(AppUpdateState.Starting, () => Progress = 1);
            _applier.Start(package);   // from here the setup program waits for this program to end
            _exit();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex is UpdateException ? ex.Message : "The update could not be started: " + ex.Message;
            Set(AppUpdateState.Failed, () => Error = message);
            _toasts.Show("Update failed. " + message, ToastKind.Error);
        }
    }

    public void OpenReleasePage()
    {
        if (Available?.ReleasePage is { } page) _open(page);
    }
}

/// <summary>Runs a copy of the installed <c>Uninstall.exe</c> in its update mode (the original is one of the files that get replaced).</summary>
public sealed class UpdateApplier : IAppUpdateApplier
{
    readonly string _folder;

    public UpdateApplier(string? installFolder = null) => _folder = (installFolder ?? AppContext.BaseDirectory).TrimEnd('\\', '/');

    string Setup => Path.Combine(_folder, "Uninstall.exe");

    public bool CanApply => File.Exists(Path.Combine(_folder, "install.json")) && File.Exists(Setup);

    public void Start(string packagePath)
    {
        if (!CanApply) throw new InvalidOperationException("This copy was not installed with the setup program.");

        var copy = Path.Combine(Path.GetTempPath(), "ShadeNetUpdate-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        File.Copy(Setup, copy, overwrite: true);

        var info = new ProcessStartInfo(copy) { UseShellExecute = false };
        foreach (var argument in new[] { "--update", packagePath, "--wait-for", Environment.ProcessId.ToString(), "--dir", _folder, "--launch" })
            info.ArgumentList.Add(argument);
        Process.Start(info);
    }
}
