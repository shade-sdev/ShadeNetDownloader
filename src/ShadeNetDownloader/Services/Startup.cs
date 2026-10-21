using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ShadeNetDownloader.Services;

/// <summary>
/// "Start with Windows": one value in the user's <c>Run</c> key (no administrator rights, nothing machine-wide). It starts
/// the app with <c>--minimized</c>, which goes straight to the tray, so the desktop is not interrupted at sign-in.
/// </summary>
public sealed class StartupRegistration
{
    public const string ValueName = "ShadeNetDownloader";

    readonly string _exePath;
    readonly string _runKeyPath;

    /// <param name="runKeyPath">Below HKCU; the real Run key in production, a throw-away key in tests.</param>
    public StartupRegistration(string? exePath = null, string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
    {
        _exePath = exePath ?? Environment.ProcessPath ?? "";
        _runKeyPath = runKeyPath;
    }

    public string Command => $"\"{_exePath}\" --minimized";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
            return key?.GetValue(ValueName) is string value
                   && value.StartsWith($"\"{_exePath}\"", StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (string.IsNullOrEmpty(_exePath)) throw new InvalidOperationException("The path of the program is unknown.");
            using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>The real "when finished" actions. Only <see cref="FinishWatcher"/> calls this, after its visible countdown.</summary>
public sealed class WindowsPowerService : IPowerService
{
    readonly Action _closeApp;

    public WindowsPowerService(Action closeApp) => _closeApp = closeApp;

    public void Perform(FinishAction action)
    {
        switch (action)
        {
            case FinishAction.CloseApp:
                _closeApp();
                break;
            case FinishAction.Sleep:
                SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false);
                break;
            case FinishAction.Hibernate:
                SetSuspendState(hibernate: true, forceCritical: false, disableWakeEvent: false);
                break;
            case FinishAction.Shutdown:
                // no /f: programs with unsaved work are asked, not killed
                Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
                break;
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
