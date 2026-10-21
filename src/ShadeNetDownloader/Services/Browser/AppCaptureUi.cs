using System.Windows;

namespace ShadeNetDownloader.Services.Browser;

/// <summary>Shows captures to the user: the Add dialog, toasts, and bringing the window forward. Always on the UI thread.</summary>
public sealed class AppCaptureUi : ICaptureUi
{
    readonly IDialogService _dialogs;
    readonly ToastService _toasts;
    readonly IAppShell _shell;

    public AppCaptureUi(IDialogService dialogs, ToastService toasts, IAppShell shell)
    {
        _dialogs = dialogs;
        _toasts = toasts;
        _shell = shell;
    }

    static System.Windows.Threading.Dispatcher? Ui => Application.Current?.Dispatcher;

    public void ShowCapture(CaptureInfo capture) =>
        Ui?.BeginInvoke(async () =>
        {
            try
            {
                await _dialogs.ShowCaptureAsync(capture);
            }
            catch (Exception ex)
            {
                _toasts.Show($"Could not show the download from your browser: {ex.Message}", ToastKind.Error);
            }
        });

    public void Notify(string message, ToastKind kind) => Ui?.BeginInvoke(() => _toasts.Show(message, kind));

    /// <summary>Also brings the window back from the tray when it was hidden there.</summary>
    public void BringToFront() => _shell.BringToFront();
}
