using ShadeNet.Core.Tools;

namespace ShadeNetDownloader.Services;

/// <summary>
/// Looks, about once a day, for newer versions of yt-dlp and ffmpeg and ASKS the user (a notification with an Update button)
/// before installing anything. A copy the user chose in Settings, or one found on the PATH, is never touched.
/// </summary>
public sealed class ToolUpdateService
{
    readonly IToolUpdates _tools;
    readonly SettingsService _settings;
    readonly ToastService _toasts;
    readonly Func<DateTimeOffset> _now;
    readonly object _gate = new();
    List<ToolUpdate> _pending = new();

    public ToolUpdateService(IToolUpdates tools, SettingsService settings, ToastService toasts, Func<DateTimeOffset>? now = null)
    {
        _tools = tools;
        _settings = settings;
        _toasts = toasts;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Updates that were found and not installed yet (only for tools that are installed).</summary>
    public IReadOnlyList<ToolUpdate> Pending
    {
        get
        {
            lock (_gate) return _pending.ToList();
        }
    }

    public event Action? PendingChanged;

    /// <summary>The automatic check: does nothing when switched off or when one ran within the last day.</summary>
    public async Task CheckDueAsync(CancellationToken ct = default)
    {
        var tools = _settings.Current.Tools;
        if (!tools.CheckForUpdates) return;
        if (tools.LastCheck is { } last && _now() - last < Interval) return;

        tools.LastCheck = _now();
        _settings.Save();

        var found = new List<ToolUpdate>();
        foreach (var tool in Enum.GetValues<ManagedTool>())
        {
            try
            {
                if (await _tools.CheckAsync(tool, ct).ConfigureAwait(false) is { Current: not null } update) found.Add(update);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // offline, rate-limited, GitHub down: the next check tries again; nobody wants an error for this
            }
        }

        SetPending(found);
        if (found.Count == 0) return;

        var message = found.Count == 1
            ? $"{found[0].Name} {found[0].Latest} is available (you have {found[0].Current})."
            : $"Updates for {string.Join(" and ", found.Select(u => u.Name))} are available.";
        _toasts.Show(message, ToastKind.Info, "Update", () => _ = UpdateAllAsync(), TimeSpan.FromSeconds(20));
    }

    /// <summary>"Check now" in Settings: ignores the schedule; also reports tools that are not installed (they can be downloaded).</summary>
    public async Task<IReadOnlyList<ToolUpdate>> CheckNowAsync(CancellationToken ct = default)
    {
        var found = new List<ToolUpdate>();
        foreach (var tool in Enum.GetValues<ManagedTool>())
            if (await _tools.CheckAsync(tool, ct).ConfigureAwait(false) is { } update) found.Add(update);

        SetPending(found.Where(u => u.Current != null).ToList());
        return found;
    }

    public async Task UpdateAllAsync(CancellationToken ct = default)
    {
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var update in Pending)
        {
            try
            {
                var version = await UpdateCoreAsync(update, ct).ConfigureAwait(false);
                done.Add($"{update.Name} {version}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add($"{update.Name}: {ex.Message}");
            }
        }

        if (failed.Count > 0) _toasts.Show("Update failed. " + string.Join(" ", failed), ToastKind.Error);
        else if (done.Count > 0) _toasts.Show("Updated " + string.Join(" and ", done) + ".", ToastKind.Success);
    }

    /// <summary>Installs one update (from Settings). Throws with a message the user can read.</summary>
    public async Task<string> UpdateAsync(ToolUpdate update, CancellationToken ct = default)
    {
        var version = await UpdateCoreAsync(update, ct).ConfigureAwait(false);
        _toasts.Show($"{update.Name} {version} is installed.", ToastKind.Success);
        return version;
    }

    async Task<string> UpdateCoreAsync(ToolUpdate update, CancellationToken ct)
    {
        var version = await _tools.UpdateAsync(update, ct).ConfigureAwait(false);
        lock (_gate) _pending = _pending.Where(p => p.Tool != update.Tool).ToList();
        PendingChanged?.Invoke();
        return version;
    }

    void SetPending(List<ToolUpdate> found)
    {
        lock (_gate) _pending = found;
        PendingChanged?.Invoke();
    }
}
