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

    /// <summary>Like a capture, the user already chose this (in Explorer, in a browser): it waits for a dialog that is open.</summary>
    public void ShowGrabber(ShadeNet.Core.Links.GrabbedPage page, IReadOnlyDictionary<string, string>? cookiesByHost, string? userAgent) =>
        Ui?.BeginInvoke(async () =>
        {
            try
            {
                await _dialogs.ShowGrabberAsync(page, null, null, cookiesByHost, userAgent);
            }
            catch (Exception)
            {
                // a dialog that could not open: the browser's links are simply not shown
            }
        });

    public void OpenLinks(IReadOnlyList<string> links) =>
        Ui?.BeginInvoke(async () =>
        {
            try
            {
                await _dialogs.ShowLinksAsync(links);
            }
            catch (Exception ex)
            {
                _toasts.Show($"Could not open {links[0]}: {ex.Message}", ToastKind.Error);
            }
        });

    /// <summary>Also brings the window back from the tray when it was hidden there.</summary>
    public void BringToFront() => _shell.BringToFront();

    public void Quit() => _shell.Exit();
}
