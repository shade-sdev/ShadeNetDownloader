using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShadeNet.Core;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Util;

namespace ShadeNetDownloader.ViewModels;

/// <summary>One preset in the flyout. <see cref="BytesPerSecond"/> 0 means "no limit".</summary>
public sealed partial class LimitChoiceViewModel : ObservableObject
{
    public LimitChoiceViewModel(string label, long bytesPerSecond)
    {
        Label = label;
        BytesPerSecond = bytesPerSecond;
    }

    public string Label { get; }
    public long BytesPerSecond { get; }

    [ObservableProperty] bool _isSelected;
}

/// <summary>
/// The total-speed limit of all downloads, set from the status bar. The choice is remembered (settings file) and applied
/// at the next start. Per-download limits are set in the details drawer and the lower of the two always wins.
/// </summary>
public sealed partial class SpeedLimitViewModel : ObservableObject
{
    const double MaxMegabytesPerSecond = 100_000;

    readonly DownloadManager _manager;
    readonly SettingsService _settings;

    public SpeedLimitViewModel(DownloadManager manager, SettingsService settings)
    {
        _manager = manager;
        _settings = settings;

        Presets = new[]
        {
            new LimitChoiceViewModel("No limit", 0),
            new LimitChoiceViewModel("512 KB/s", 512 * 1024),
            new LimitChoiceViewModel("1 MB/s", 1024 * 1024),
            new LimitChoiceViewModel("2 MB/s", 2 * 1024 * 1024),
            new LimitChoiceViewModel("5 MB/s", 5 * 1024 * 1024),
            new LimitChoiceViewModel("10 MB/s", 10 * 1024 * 1024),
            new LimitChoiceViewModel("25 MB/s", 25 * 1024 * 1024),
        };

        Refresh();
    }

    public IReadOnlyList<LimitChoiceViewModel> Presets { get; }

    /// <summary>Raised after the limit changed, so the status bar updates at once.</summary>
    public event Action? Changed;

    [ObservableProperty] bool _isOpen;
    [ObservableProperty] string _customText = "";
    [ObservableProperty] bool _customInMegabytes = true;
    [ObservableProperty] string _error = "";
    [ObservableProperty] string _statusText = "Limit: off";
    [ObservableProperty] string _summary = "Downloads use all the speed they can get.";
    [ObservableProperty] bool _isActive;

    public bool CustomInKilobytes
    {
        get => !CustomInMegabytes;
        set { if (value) CustomInMegabytes = false; }
    }

    partial void OnCustomInMegabytesChanged(bool value) => OnPropertyChanged(nameof(CustomInKilobytes));

    public long Current => _manager.GlobalLimiter.BytesPerSecond;

    /// <summary>Re-reads the limit from the engine (it may have been changed elsewhere).</summary>
    public void Refresh()
    {
        var limit = Current;
        IsActive = limit > 0;
        StatusText = limit > 0 ? $"Limit: {ByteFormat.Speed(limit)}" : "Limit: off";
        Summary = limit > 0
            ? $"All downloads together stay under {ByteFormat.Speed(limit)}."
            : "Downloads use all the speed they can get.";
        foreach (var p in Presets) p.IsSelected = p.BytesPerSecond == limit;
    }

    [RelayCommand]
    void Choose(LimitChoiceViewModel? choice)
    {
        if (choice == null) return;
        Apply(choice.BytesPerSecond);
        Error = "";
        CustomText = "";
    }

    [RelayCommand]
    void ApplyCustom()
    {
        var text = (CustomText ?? "").Trim();
        if (text.Length == 0)
        {
            Error = "Type a speed, or pick one above.";
            return;
        }

        // "3.5" and "3,5" both mean three and a half, whatever the language of Windows
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !(text.Count(c => c == ',') == 1 && !text.Contains('.')
                 && double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            || double.IsNaN(value) || double.IsInfinity(value))
        {
            Error = "That is not a number.";
            return;
        }

        var megabytes = CustomInMegabytes ? value : value / 1024;
        if (value <= 0 || megabytes > MaxMegabytesPerSecond)
        {
            Error = "Use a speed above zero. For no limit, choose \"No limit\".";
            return;
        }

        Apply((long)Math.Round(megabytes * 1024 * 1024));
        Error = "";
        IsOpen = false;
    }

    void Apply(long bytesPerSecond)
    {
        _manager.SetGlobalSpeedLimit(bytesPerSecond, enabled: bytesPerSecond > 0);
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // the limit still applies for this session; it just will not be remembered
        }

        Refresh();
        Changed?.Invoke();
    }
}
