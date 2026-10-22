using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShadeNetDownloader.Services;

namespace ShadeNetDownloader.ViewModels;

/// <summary>The "Updates" part of Settings > About: the state of the look for a newer ShadeNet, and the buttons.</summary>
public sealed partial class UpdatesSettingsViewModel : ObservableObject
{
    readonly SettingsService _settings;
    readonly AppUpdateService _service;

    public UpdatesSettingsViewModel(SettingsService settings, AppUpdateService service)
    {
        _settings = settings;
        _service = service;
        service.Changed += () =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) Refresh();
            else dispatcher.BeginInvoke(Refresh);
        };
    }

    /// <summary>Look for a newer version about once a day and ask before installing one.</summary>
    public bool CheckForUpdates
    {
        get => _settings.Current.Updates.CheckForUpdates;
        set
        {
            if (_settings.Current.Updates.CheckForUpdates == value) return;
            _settings.Current.Updates.CheckForUpdates = value;
            OnPropertyChanged();
            _settings.Save();
        }
    }

    public AppUpdateState State => _service.State;
    public bool IsBusy => State is AppUpdateState.Checking or AppUpdateState.Downloading or AppUpdateState.Starting;
    public bool CanCheck => !IsBusy;

    /// <summary>"Update to 1.0.5": there is a newer version and this copy can replace itself.</summary>
    public bool CanInstall => State == AppUpdateState.Available && _service.CanInstall;

    /// <summary>A newer version exists but this copy was not installed with the setup program.</summary>
    public bool CanOpenPage => State == AppUpdateState.Available && !_service.CanInstall && _service.Available?.ReleasePage != null;

    public string InstallText => _service.Available is { } found ? $"Update to {found.LatestText}" : "Update";
    public bool ShowProgress => State == AppUpdateState.Downloading;
    public double ProgressValue => _service.Progress;

    public string StatusText => State switch
    {
        AppUpdateState.Checking => "Looking for a newer version…",
        AppUpdateState.UpToDate => "You have the newest version.",
        AppUpdateState.Available => _service.Available is { } found
            ? $"Version {found.LatestText} is available." + (_service.CanInstall
                ? " It installs in a moment and restarts the program." + (_service.ActiveDownloads > 0 ? $" {_service.ActiveDownloads} running download(s) are paused and saved, and continue afterwards." : "")
                : " This copy was not installed with the setup program, so it cannot update itself.")
            : "",
        AppUpdateState.Downloading => $"Downloading the update… {_service.Progress:P0}",
        AppUpdateState.Starting => "Starting the update…",
        AppUpdateState.Failed => _service.Error ?? "The update did not work.",
        _ => _settings.Current.Updates.LastCheck is { } last ? $"Last checked {last.LocalDateTime:g}." : "Not checked yet.",
    };

    public void Refresh()
    {
        foreach (var name in new[] { nameof(State), nameof(IsBusy), nameof(CanCheck), nameof(CanInstall), nameof(CanOpenPage), nameof(InstallText), nameof(ShowProgress), nameof(ProgressValue), nameof(StatusText) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    async Task CheckNowAsync() => await _service.CheckNowAsync();

    [RelayCommand]
    async Task InstallAsync() => await _service.InstallAsync();

    [RelayCommand]
    void OpenPage() => _service.OpenReleasePage();
}
