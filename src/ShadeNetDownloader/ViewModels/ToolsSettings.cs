using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShadeNet.Core.Tools;
using ShadeNetDownloader.Services;

namespace ShadeNetDownloader.ViewModels;

/// <summary>One helper program (yt-dlp or ffmpeg) in Settings: where it is, which version, a different location, an update.</summary>
public sealed partial class ToolRowViewModel : ObservableObject
{
    readonly ManagedTool _tool;
    readonly SettingsService _settings;
    readonly IToolUpdates _tools;
    readonly ToolUpdateService _updates;
    readonly Action _changed;
    bool _loading;
    ToolUpdate? _offer;

    public ToolRowViewModel(ManagedTool tool, SettingsService settings, IToolUpdates tools, ToolUpdateService updates, Action changed)
    {
        _tool = tool;
        _settings = settings;
        _tools = tools;
        _updates = updates;
        _changed = changed;

        _loading = true;
        PathText = Configured;
        _loading = false;

        updates.PendingChanged += OnPendingChanged;
        OnPendingChanged();
        _ = RefreshAsync();
    }

    public string Title => _tool == ManagedTool.YtDlp ? "yt-dlp" : "ffmpeg";

    public string Description => _tool == ManagedTool.YtDlp
        ? "Finds the videos behind YouTube pages. ShadeNet downloads them itself."
        : "Joins video streams into one file, without re-encoding.";

    string Configured => (_tool == ManagedTool.YtDlp ? _settings.Current.Engine.YtDlpPath : _settings.Current.Engine.FfmpegPath) ?? "";

    [ObservableProperty] string _pathText = "";
    [ObservableProperty] string _status = "";
    [ObservableProperty] string _updateText = "";
    [ObservableProperty] bool _hasUpdate;
    [ObservableProperty] bool _isBusy;

    /// <summary>The buttons are off while something is being looked up or installed.</summary>
    public bool IsIdle => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    /// <summary>"Update" for an installed tool, "Download" when there is none.</summary>
    public string ActionText => _offer is { Current: null } ? "Download" : "Update";

    partial void OnPathTextChanged(string value)
    {
        if (_loading) return;
        var path = (value ?? "").Trim().Trim('"');
        if (_tool == ManagedTool.YtDlp) _settings.Current.Engine.YtDlpPath = path;
        else _settings.Current.Engine.FfmpegPath = path;
        _settings.Save();
        _changed();
        _ = RefreshAsync();
    }

    /// <summary>Where it is and what version, as one sentence.</summary>
    public async Task RefreshAsync()
    {
        var source = _tools.SourceOf(_tool);
        var found = _tools.Find(_tool);
        var version = found != null ? await _tools.VersionAsync(_tool, CancellationToken.None) : null;
        var shown = version != null ? $" (version {Shorten(version)})" : "";

        Status = source switch
        {
            ToolSource.None => !string.IsNullOrWhiteSpace(Configured) ? "There is nothing at that location." : "Not installed.",
            ToolSource.Custom => $"Your choice: {found}{shown}. ShadeNet will not update it.",
            ToolSource.User => $"Updated copy: {found}{shown}",
            ToolSource.Bundled => $"Default, with the program{shown}",
            _ => $"Found on the PATH: {found}{shown}. ShadeNet will not update it.",
        };
    }

    static string Shorten(string version) => version.Length > 48 ? version[..48] + "…" : version;

    void OnPendingChanged()
    {
        var offer = _updates.Pending.FirstOrDefault(u => u.Tool == _tool);
        ShowOffer(offer);
    }

    void ShowOffer(ToolUpdate? offer)
    {
        _offer = offer;
        HasUpdate = offer != null;
        UpdateText = offer == null ? ""
            : offer.Current == null ? $"{offer.Name} {offer.Latest} can be downloaded."
            : $"Update available: {offer.Current} → {offer.Latest}";
        OnPropertyChanged(nameof(ActionText));
    }

    [RelayCommand]
    void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choose {Title}",
            Filter = $"{Title}|{Title}.exe|Programs|*.exe",
        };
        if (dialog.ShowDialog() == true) PathText = dialog.FileName;
    }

    /// <summary>Back to the default: the copy that came with the program (or its downloaded update).</summary>
    [RelayCommand]
    void Reset() => PathText = "";

    [RelayCommand]
    async Task CheckAsync()
    {
        IsBusy = true;
        UpdateText = "Looking…";
        try
        {
            var update = await _tools.CheckAsync(_tool, CancellationToken.None);
            if (update != null)
            {
                ShowOffer(update);
            }
            else
            {
                ShowOffer(null);
                UpdateText = _tools.SourceOf(_tool) is ToolSource.Custom or ToolSource.Path
                    ? "ShadeNet only updates its own copy; this one is yours."
                    : "Up to date.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateText = "Could not look for updates: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    async Task UpdateAsync()
    {
        if (_offer is not { } offer) return;
        IsBusy = true;
        UpdateText = "Downloading and checking…";
        try
        {
            await _updates.UpdateAsync(offer);
            ShowOffer(null);
            UpdateText = "Updated.";
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateText = "Could not update: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>The helper programs section: a row per program and whether to look for updates automatically.</summary>
public sealed partial class ToolsSettingsViewModel : ObservableObject
{
    readonly SettingsService _settings;

    public ToolsSettingsViewModel(SettingsService settings, IToolUpdates tools, ToolUpdateService updates, Action engineChanged)
    {
        _settings = settings;
        Rows = new ObservableCollection<ToolRowViewModel>(
            Enum.GetValues<ManagedTool>().Select(t => new ToolRowViewModel(t, settings, tools, updates, engineChanged)));
    }

    public ObservableCollection<ToolRowViewModel> Rows { get; }

    /// <summary>Look for newer versions about once a day and ask before installing one.</summary>
    public bool CheckForUpdates
    {
        get => _settings.Current.Tools.CheckForUpdates;
        set
        {
            if (_settings.Current.Tools.CheckForUpdates == value) return;
            _settings.Current.Tools.CheckForUpdates = value;
            OnPropertyChanged();
            _settings.Save();
        }
    }
}
