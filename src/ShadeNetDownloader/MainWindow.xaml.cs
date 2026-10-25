using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShadeNetDownloader.Services;
using ShadeNetDownloader.Services.Tray;
using ShadeNetDownloader.Util;
using ShadeNetDownloader.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace ShadeNetDownloader;

public partial class MainWindow : FluentWindow
{
    readonly MainViewModel _vm;
    readonly SettingsService _settings;
    readonly IDialogService _dialogService;
    readonly ToastService _toasts;
    readonly AppController _app;

    public MainWindow(MainViewModel vm, SettingsService settings, AppThemeService theme,
        IContentDialogService dialogs, IDialogService dialogService, ToastService toasts, AppController app, AppIconService icons)
    {
        _vm = vm;
        _settings = settings;
        _dialogService = dialogService;
        _toasts = toasts;
        _app = app;

        DataContext = vm;
        InitializeComponent();

        dialogs.SetDialogHost(DialogHost);

        // (the title bar's left slot is placed by WPF-UI: measured once everything is laid out, and again when the sidebar changes)
        Loaded += (_, _) => Dispatcher.BeginInvoke(AlignTitleBar, System.Windows.Threading.DispatcherPriority.Loaded);
        Sidebar.SizeChanged += (_, _) => AlignTitleBar();
        Sidebar.Width = vm.SidebarWidth.Value;
        ShowPage(vm.CurrentPage);
        // the settings page is the heaviest: it is made while nothing else happens after start-up, so its first opening is instant
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => Prepare(vm.SettingsPage), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSidebarExpanded)) AnimateSidebar();
            else if (e.PropertyName == nameof(MainViewModel.CurrentPage))
            {
                ShowPage(vm.CurrentPage);
                Animate.FadeSlideIn(PageHost, 14);
            }
        };
        StateChanged += (_, _) => Dispatcher.BeginInvoke(AlignTitleBar, System.Windows.Threading.DispatcherPriority.Loaded);   // (maximised, the slot moves)

        // Window position/size from last time (only if it still fits on a screen).
        RestoreGeometry(settings.Current.Window);

        BackgroundLayerHost.DataContext = theme.Layer;
        BrandLogo.DataContext = icons;
        icons.Attach(this);
        icons.Apply(settings.Current.AppIcon);
        theme.Switching += CrossfadeTheme;
        theme.Attach(this);
        theme.Apply(settings.Current.Theme, settings.Current.Appearance);
        theme.ApplyLayout(settings.Current.Layout);

        // The limit flyout sits above its button with the right edges aligned (the button is at the window's right edge,
        // so a centred popup would hang outside the window). 14 = the transparent margin around the popup's card.
        LimitPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[]
        {
            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 14, -popupSize.Height + 8), PopupPrimaryAxis.None),
        };

        // Ctrl+F → search, Ctrl+, → settings
        InputBindings.Add(new KeyBinding(new RelayAction(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayAction(() => vm.SelectCommand.Execute(vm.SettingsItem)),
            Key.OemComma, ModifierKeys.Control));
    }

    void OnThemeClick(object sender, RoutedEventArgs e)
    {
        Animate.Pop(ThemeButton, 1.15);
        _vm.SettingsPage.CycleTheme();
    }

    /// <summary>
    /// The search box starts where the content does: the brand column of the title bar is as wide as the sidebar, less where
    /// the title bar's left slot begins (WPF-UI places it a little in from the window's edge).
    /// </summary>
    void AlignTitleBar()
    {
        if (!TitleBarHeader.IsLoaded || !Sidebar.IsLoaded) return;
        // (measured inside the zoomed content, so the width is in the same units as the column it sets)
        var slot = TitleBarHeader.TranslatePoint(new Point(0, 0), Root).X;
        var content = Sidebar.TranslatePoint(new Point(Sidebar.ActualWidth, 0), Root).X;
        var width = Math.Max(0, content - slot);
        if (Math.Abs(BrandColumn.Width.Value - width) > 0.5) BrandColumn.Width = new GridLength(width);
    }

    readonly Dictionary<object, FrameworkElement> _pages = new();

    /// <summary>The view of a page, made the first time and kept (a page made again on every visit made switching slow).</summary>
    FrameworkElement? PageFor(object? page)
    {
        if (page == null) return null;
        if (_pages.TryGetValue(page, out var view)) return view;
        view = page switch
        {
            DownloadsViewModel => new Views.DownloadsView(),
            SettingsViewModel => new Views.SettingsView(),
            _ => null,
        };
        if (view == null) return null;
        view.DataContext = page;
        _pages[page] = view;
        return view;
    }

    void ShowPage(object? page) => PageHost.Content = PageFor(page);

    /// <summary>A page is made and laid out once off screen, at the size it will show at: its lists and controls are built then.</summary>
    void Prepare(object? page)
    {
        if (PageFor(page) is not { Parent: null } view || ReferenceEquals(PageHost.Content, view)) return;
        var size = PageHost.RenderSize;
        if (size.Width < 1 || size.Height < 1) return;
        view.Measure(size);
        view.Arrange(new Rect(size));
    }

    /// <summary>The sidebar glides to its new width; the brand fades back in beside it when the names return.</summary>
    void AnimateSidebar()
    {
        Animate.To(Sidebar, WidthProperty, _vm.SidebarWidth.Value, Animate.Base);
        if (_vm.IsSidebarExpanded) Animate.FadeSlideIn(Brand, offsetY: 0, offsetX: -10);
    }

    bool _crossfading;

    /// <summary>
    /// The theme is about to change: a picture of the window as it looks now is laid over it and fades out, so light and dark,
    /// a preset or an imported theme blend into each other instead of jumping.
    /// </summary>
    void CrossfadeTheme()
    {
        if (!Animate.Enabled || !IsLoaded || !IsVisible || _crossfading || Root.ActualWidth < 1 || Root.ActualHeight < 1) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(Root);
            var picture = new RenderTargetBitmap(
                (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            picture.Render(Root);
            picture.Freeze();
            ThemeSnapshot.Source = picture;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or InvalidOperationException)
        {
            return;   // no picture: the theme simply changes
        }

        _crossfading = true;
        ThemeSnapshot.Opacity = 1;
        ThemeSnapshot.Visibility = Visibility.Visible;
        // (once the new look has been drawn under it)
        Dispatcher.BeginInvoke(() => Animate.To(ThemeSnapshot, OpacityProperty, 0, Animate.Slow, done: () =>
        {
            ThemeSnapshot.Visibility = Visibility.Collapsed;
            ThemeSnapshot.Source = null;
            _crossfading = false;
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The brand area moves the window (and a double click maximises it), as the rest of the title bar does.</summary>
    void OnBrandMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
        e.Handled = true;
    }

    long _limitPopupClosedAt;

    /// <summary>
    /// Opens the speed-limit flyout. A click on the button while the flyout is open first closes it (it is not "sticky"),
    /// and would then reopen it: ignore a click that arrives right after the flyout closed.
    /// </summary>
    void OnLimitButtonClick(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _limitPopupClosedAt < 250) return;
        _vm.Limit.Refresh();
        _vm.Limit.IsOpen = true;
    }

    void OnLimitPopupClosed(object? sender, EventArgs e) => _limitPopupClosedAt = Environment.TickCount64;

    // ---- paste: Ctrl+V anywhere (outside a text box) with a link on the clipboard opens the Add dialog ----

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (Keyboard.FocusedElement is TextBoxBase or System.Windows.Controls.PasswordBox) return;

        string? text = null;
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
        }
        catch
        {
            // the clipboard can be locked by another application for a moment
        }

        var links = UrlParser.Extract(text);
        if (links.Count == 0) return;

        e.Handled = true;
        _ = _dialogService.ShowAddDownloadAsync(links);
    }

    // ---- drag and drop ----

    static bool CanTake(IDataObject data) =>
        data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text)
        || data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent("UniformResourceLocatorW");

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (CanTake(e.Data))
        {
            e.Effects = DragDropEffects.Copy;
            DropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;

        var links = LinksFrom(e.Data);
        if (links.Count == 0)
        {
            _toasts.Show("No download links found in what you dropped.", ToastKind.Error);
            return;
        }

        await _dialogService.ShowAddDownloadAsync(links);
    }

    /// <summary>Links from dragged text, a browser link, or text-like files (.txt, .url, .html, …).</summary>
    static IReadOnlyList<string> LinksFrom(IDataObject data)
    {
        var all = new List<string>();

        foreach (var format in new[] { DataFormats.UnicodeText, DataFormats.Text, "UniformResourceLocatorW" })
        {
            if (!data.GetDataPresent(format)) continue;
            try
            {
                if (data.GetData(format) is string s) all.AddRange(UrlParser.Extract(s));
            }
            catch
            {
                // exotic data object: skip this format
            }
        }

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files)
            {
                try
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is ".txt" or ".url" or ".html" or ".htm" or ".md" or ".csv" or ".lst" or ".list"
                        && new FileInfo(file).Length <= 2 * 1024 * 1024)
                        all.AddRange(UrlParser.Extract(File.ReadAllText(file)));
                }
                catch
                {
                    // unreadable file: ignore
                }
            }
        }

        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- window geometry ----

    void RestoreGeometry(WindowGeometry g)
    {
        if (g.Left is { } left && g.Top is { } top
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
            && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100
            && left + g.Width > SystemParameters.VirtualScreenLeft + 100
            && top + g.Height > SystemParameters.VirtualScreenTop + 40)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        Width = Math.Max(MinWidth, g.Width);
        Height = Math.Max(MinHeight, g.Height);
        if (g.Maximized) WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var g = _settings.Current.Window;
        g.Maximized = WindowState == WindowState.Maximized;
        var b = RestoreBounds;
        if (!b.IsEmpty)
        {
            g.Left = b.Left;
            g.Top = b.Top;
            g.Width = b.Width;
            g.Height = b.Height;
        }

        base.OnClosing(e);

        // With a tray icon the close button only hides the window; downloads carry on. "Exit" in the tray menu really quits.
        if (!e.Cancel && _app.HideToTrayIfWanted()) e.Cancel = true;
    }

    /// <summary>The window really closed (no tray, or Exit): the program ends.</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    // ---- "when finished" menu in the status bar ----

    void OnFinishButtonClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Finish is not { } finish || sender is not UIElement button) return;

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Top, StaysOpen = false };
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "When all downloads are finished", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        foreach (var (action, label) in TrayMenu.FinishChoices)
        {
            var item = new System.Windows.Controls.MenuItem { Header = label, IsChecked = finish.Action == action };
            item.Click += (_, _) => finish.Action = action;
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>Minimal ICommand for key bindings that just run an action.</summary>
    sealed class RelayAction : ICommand
    {
        readonly Action _action;

        public RelayAction(Action action) => _action = action;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
    }
}
