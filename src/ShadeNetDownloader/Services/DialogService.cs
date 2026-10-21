using System.Windows;
using ShadeNet.Core;
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

    async Task ShowAsync(IReadOnlyList<string>? prefill, CaptureInfo? capture)
    {
        _open = true;

        using var vm = new AddDownloadViewModel(_manager, prefill, capture);
        try
        {
            var dialog = new AddDownloadDialog(vm);
            var result = await _dialogs.ShowAsync(dialog, CancellationToken.None);
            if (result == ContentDialogResult.None) return;

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
