using System.IO;
using System.Windows;
using ShadeNet.Core;
using ShadeNet.Core.Engine;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Services.Browser;
using ShadeNetDownloader.Util;
using ShadeNetDownloader.ViewModels;
using ShadeNetDownloader.Views;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace ShadeNetDownloader.Services;

public interface IDialogService
{
    /// <summary>Opens the Add dialog, pre-filled with <paramref name="prefill"/> (or links found on the clipboard).</summary>
    Task ShowAddDownloadAsync(IReadOnlyList<string>? prefill = null);

    /// <summary>Opens the Add dialog for a download the browser handed over; waits if another dialog is open.</summary>
    Task ShowCaptureAsync(CaptureInfo capture);

    /// <summary>Opens the Add dialog with links that came from outside (Windows opened a magnet link with ShadeNet); waits if another dialog is open.</summary>
    Task ShowLinksAsync(IReadOnlyList<string> links);

    /// <summary>
    /// Opens the link grabber: with a page's links the browser sent (and its sign-in per site), or for a page ShadeNet reads itself
    /// (<paramref name="pageUrl"/>); waits if another dialog is open.
    /// </summary>
    Task ShowGrabberAsync(ShadeNet.Core.Links.GrabbedPage? page, string? pageUrl = null, ShadeNet.Core.Net.RequestSpec? web = null,
        IReadOnlyDictionary<string, string>? cookiesByHost = null, string? userAgent = null);

    /// <summary>Asks for an archive's password; null when the user cancels.</summary>
    Task<string?> AskPasswordAsync(string fileName, bool wrongBefore);
}

public sealed class DialogService : IDialogService
{
    readonly IContentDialogService _dialogs;
    readonly DownloadManager _manager;
    readonly ToastService _toasts;
    bool _open;

    public DialogService(IContentDialogService dialogs, DownloadManager manager, ToastService toasts)
    {
        _dialogs = dialogs;
        _manager = manager;
        _toasts = toasts;
    }

    public async Task ShowAddDownloadAsync(IReadOnlyList<string>? prefill = null)
    {
        if (_open) return; // never stack two dialogs
        await ShowAsync(prefill is { Count: > 0 } ? prefill : LinksFromClipboard(), null);
    }

    public async Task ShowCaptureAsync(CaptureInfo capture)
    {
        // Unlike a click on "Add download", a capture is something the user already did in the browser: do not drop it.
        while (_open) await Task.Delay(300);
        await ShowAsync(null, capture);
    }

    public async Task ShowLinksAsync(IReadOnlyList<string> links)
    {
        // like a capture: the user already chose this (in Explorer, in a browser), so it is not dropped while another dialog is open
        while (_open) await Task.Delay(300);
        await ShowAsync(links, null);
    }

    async Task ShowAsync(IReadOnlyList<string>? prefill, CaptureInfo? capture)
    {
        _open = true;

        using var vm = new AddDownloadViewModel(_manager, prefill, capture);
        ShadeNet.Core.Net.RequestSpec? grab = null;
        try
        {
            var dialog = new AddDownloadDialog(vm);
            var result = await _dialogs.ShowAsync(dialog, CancellationToken.None);
            grab = vm.GrabPage;
            if (result == ContentDialogResult.None || grab != null) return;

            var mode = result == ContentDialogResult.Primary ? AddMode.Start : AddMode.Queue;
            var added = 0;
            string? lastName = null;
            foreach (var (request, probe) in vm.Build(mode))
            {
                try
                {
                    lastName = _manager.Add(request, probe).FileName;
                    added++;
                }
                catch (Exception ex)
                {
                    _toasts.Show($"Could not add that link: {ex.Message}", ToastKind.Error);
                }
            }

            if (added == 1) _toasts.Show(mode == AddMode.Start ? $"Downloading {lastName}" : $"Queued {lastName}", ToastKind.Success);
            else if (added > 1) _toasts.Show(mode == AddMode.Start ? $"Started {added} downloads" : $"Queued {added} downloads", ToastKind.Success);
        }
        finally
        {
            _open = false;
            // "Grab the links on it" in the Add dialog: the grabber takes over, for that page
            if (grab != null) await ShowGrabberAsync(null, grab.Url, grab);
        }
    }

    public async Task ShowGrabberAsync(ShadeNet.Core.Links.GrabbedPage? page, string? pageUrl = null, ShadeNet.Core.Net.RequestSpec? web = null,
        IReadOnlyDictionary<string, string>? cookiesByHost = null, string? userAgent = null)
    {
        while (_open) await Task.Delay(300);
        _open = true;
        using var cts = new CancellationTokenSource();
        try
        {
            var vm = new LinkGrabberViewModel(_manager);
            if (page != null) vm.Show(page, cookiesByHost, userAgent);
            else if (pageUrl != null) _ = vm.LoadAsync(pageUrl, web, cts.Token);

            var result = await _dialogs.ShowAsync(new LinkGrabberDialog(vm), CancellationToken.None);
            cts.Cancel();
            if (result == ContentDialogResult.None) return;

            var mode = result == ContentDialogResult.Primary ? AddMode.Start : AddMode.Queue;
            var added = 0;
            var failed = 0;
            foreach (var request in vm.Build(mode))
            {
                try
                {
                    _manager.Add(request);
                    added++;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or DownloadException)
                {
                    failed++;
                }
            }

            var what = added == 1 ? "1 download" : $"{added} downloads";
            _toasts.Show((mode == AddMode.Start ? $"Started {what}" : $"Queued {what}") + (failed > 0 ? $" ({failed} could not be added)" : ""),
                added > 0 ? ToastKind.Success : ToastKind.Error);
        }
        finally
        {
            _open = false;
        }
    }

    public async Task<string?> AskPasswordAsync(string fileName, bool wrongBefore)
    {
        while (_open) await Task.Delay(300);
        _open = true;
        try
        {
            var box = new System.Windows.Controls.PasswordBox { MinWidth = 320, Margin = new Thickness(0, 10, 0, 0) };
            var panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = (wrongBefore ? "That password did not open it. " : "") + $"“{fileName}” is locked with a password.",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(box);
            var dialog = new ContentDialog
            {
                Title = "Extract",
                Content = panel,
                PrimaryButtonText = "Extract",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.Loaded += (_, _) => box.Focus();
            var result = await _dialogs.ShowAsync(dialog, CancellationToken.None);
            return result == ContentDialogResult.Primary ? box.Password : null;
        }
        finally
        {
            _open = false;
        }
    }

    static IReadOnlyList<string>? LinksFromClipboard()
    {
        try
        {
            return Clipboard.ContainsText() ? UrlParser.Extract(Clipboard.GetText()) : null;
        }
        catch
        {
            return null; // the clipboard can be locked by another app
        }
    }
}
