using ShadeNet.Core.Engine;

namespace ShadeNetDownloader.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class WindowGeometry
{
    /// <summary>null = never saved yet (let Windows place the window).</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 1180;
    public double Height { get; set; } = 760;
    public bool Maximized { get; set; }
}

/// <summary>Everything the user can configure; stored as JSON in <c>%LOCALAPPDATA%\ShadeNet\settings.json</c>.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public WindowGeometry Window { get; set; } = new();
    public EngineSettings Engine { get; set; } = new();
    public BrowserSettings Browser { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public ScheduleSettings Schedule { get; set; } = new();
    public ToolSettings Tools { get; set; } = new();
}

/// <summary>yt-dlp and ffmpeg: whether ShadeNet looks for newer versions (it always asks before installing one).</summary>
public sealed class ToolSettings
{
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the last automatic check ran: at most one a day.</summary>
    public DateTimeOffset? LastCheck { get; set; }
}

/// <summary>What the app does with a download the browser extension hands over.</summary>
public enum CaptureMode
{
    /// <summary>Show the Add dialog, pre-filled with the browser's session (like IDM's "Download File Info").</summary>
    ShowDialog,

    /// <summary>Start straight away.</summary>
    StartImmediately,
}

public sealed class BrowserSettings
{
    /// <summary>Master switch: when off the extension is told to leave every download to the browser.</summary>
    public bool Enabled { get; set; } = true;

    public CaptureMode Mode { get; set; } = CaptureMode.ShowDialog;

    /// <summary>Hosts (and their sub-domains) whose downloads stay with the browser.</summary>
    public List<string> ExcludedHosts { get; set; } = new();

    /// <summary>Downloads known to be smaller than this stay with the browser (0 = take everything).</summary>
    public long MinSizeBytes { get; set; }
}

/// <summary>Window, tray and notification behaviour.</summary>
public sealed class GeneralSettings
{
    /// <summary>Show ShadeNet's icon in the notification area.</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>The window's close button hides it to the tray instead of quitting (needs the tray icon).</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Remembered for the settings page; the Windows start-up entry itself is the truth.</summary>
    public bool StartWithWindows { get; set; }

    public bool NotifyOnComplete { get; set; } = true;
    public bool NotifyOnFailure { get; set; } = true;

    /// <summary>Only tell the user when ShadeNet is not the window they are looking at.</summary>
    public bool NotifyOnlyInBackground { get; set; } = true;

    public bool NotificationSound { get; set; } = true;

    /// <summary>The one-time "still running in the tray" hint has been shown.</summary>
    public bool TrayHintShown { get; set; }
}

/// <summary>Run the queue only inside a daily time window (e.g. overnight, when bandwidth is free).</summary>
public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }
    public TimeOnly Start { get; set; } = new(1, 0);
    public TimeOnly Stop { get; set; } = new(7, 0);

    /// <summary>The days a window <em>starts</em> on.</summary>
    public List<DayOfWeek> Days { get; set; } = Enum.GetValues<DayOfWeek>().ToList();
}
