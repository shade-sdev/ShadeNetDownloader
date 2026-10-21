using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using ShadeNet.Core;
using ShadeNet.Core.Models;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Services.Tray;

/// <summary>The menu of the tray icon. It is built anew every time it opens, so it always shows the current state.</summary>
public sealed class TrayMenu
{
    readonly AppController _app;
    readonly DownloadManager _manager;
    readonly MainViewModel _main;
    readonly IDialogService _dialogs;
    readonly FinishWatcher _finish;

    public TrayMenu(AppController app, DownloadManager manager, MainViewModel main, IDialogService dialogs, FinishWatcher finish)
    {
        _app = app;
        _manager = manager;
        _main = main;
        _dialogs = dialogs;
        _finish = finish;
    }

    /// <summary>Opens the menu at a screen position given in device pixels (where the icon was clicked).</summary>
    public void ShowAt(int xPixels, int yPixels)
    {
        var menu = Build();

        // the menu is placed in device-independent units
        var source = Application.Current.MainWindow is { } w ? PresentationSource.FromVisual(w) : null;
        var toDips = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var point = toDips.Transform(new Point(xPixels, yPixels));

        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = point.X;
        menu.VerticalOffset = point.Y;
        menu.IsOpen = true;

        // Without this the menu would not close when the user clicks elsewhere: it must belong to the foreground window.
        if (PresentationSource.FromVisual(menu) is HwndSource menuSource) SetForegroundWindow(menuSource.Handle);
    }

    public ContextMenu Build()
    {
        var menu = new ContextMenu { StaysOpen = false };
        var jobs = _manager.Jobs;
        var anyActive = jobs.Any(j => j.IsActive || j.State == DownloadState.Queued);
        var anyPaused = jobs.Any(j => j.State is DownloadState.Paused or DownloadState.Failed);

        menu.Items.Add(Item("Open ShadeNet", _app.BringToFront, bold: true));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Add download…", () =>
        {
            _app.BringToFront();
            _ = _dialogs.ShowAddDownloadAsync();
        }));
        menu.Items.Add(Item("Pause all", () => _main.PauseAllCommand.Execute(null), enabled: anyActive));
        menu.Items.Add(Item("Resume all", () => _main.ResumeAllCommand.Execute(null), enabled: anyPaused));
        menu.Items.Add(new Separator());

        // speed limit
        var limit = new MenuItem { Header = _main.Limit.IsActive ? $"Speed limit ({_main.Limit.StatusText.Replace("Limit: ", "")})" : "Speed limit" };
        foreach (var preset in _main.Limit.Presets)
        {
            var item = Item(preset.Label, () => _main.Limit.ChooseCommand.Execute(preset));
            item.IsChecked = preset.IsSelected;
            limit.Items.Add(item);
        }

        menu.Items.Add(limit);

        // queue
        menu.Items.Add(Item(_manager.QueueRunning ? "Stop the queue" : "Start the queue", () => _main.ToggleQueueCommand.Execute(null)));

        // when finished
        var finish = new MenuItem { Header = _finish.IsArmed ? $"When finished ({Short(_finish.Action)})" : "When finished" };
        foreach (var (action, label) in FinishChoices)
        {
            var item = Item(label, () => _finish.Action = action);
            item.IsChecked = _finish.Action == action;
            finish.Items.Add(item);
        }

        menu.Items.Add(finish);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit ShadeNet", _app.Exit));
        return menu;
    }

    public static readonly (FinishAction Action, string Label)[] FinishChoices =
    {
        (FinishAction.None, "Do nothing"),
        (FinishAction.CloseApp, "Close ShadeNet"),
        (FinishAction.Sleep, "Put the computer to sleep"),
        (FinishAction.Hibernate, "Hibernate"),
        (FinishAction.Shutdown, "Shut down"),
    };

    static string Short(FinishAction a) => a switch
    {
        FinishAction.CloseApp => "close",
        FinishAction.Sleep => "sleep",
        FinishAction.Hibernate => "hibernate",
        FinishAction.Shutdown => "shut down",
        _ => "",
    };

    static MenuItem Item(string header, Action click, bool enabled = true, bool bold = false)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => click();
        return item;
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
}
