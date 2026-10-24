using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ShadeNetDownloader.Util;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Views;

public partial class DownloadsView : UserControl
{
    DownloadsViewModel? _watched;

    public DownloadsView() => InitializeComponent();

    DownloadsViewModel? Vm => DataContext as DownloadsViewModel;

    /// <summary>Small windows: drop the decorative extras first, then shrink the hero, so nothing is ever clipped.</summary>
    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var h = e.NewSize.Height;
        var extras = h >= 600 ? Visibility.Visible : Visibility.Collapsed;
        Chips.Visibility = extras;
        ShortcutHint.Visibility = extras;
        var hero = h >= 520 ? 190 : h >= 440 ? 150 : 120;
        Hero.Width = Hero.Height = hero;

        // The drawer takes a share of the page, never most of it. Where pushing the list aside would leave the cards too
        // narrow to read, it floats over the list instead (like any side sheet).
        var drawer = Math.Clamp(e.NewSize.Width * 0.4, 340, 420);
        Drawer.Width = drawer;
        if (DrawerHost.Visibility == Visibility.Visible) DrawerHost.Width = drawer;

        var overlay = e.NewSize.Width < OverlayBelowWidth;
        Grid.SetColumn(DrawerHost, overlay ? 0 : 1);
        Grid.SetColumnSpan(DrawerHost, overlay ? 2 : 1);
        DrawerHost.HorizontalAlignment = overlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        if (overlay) DrawerHost.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");   // opaque: the list is under it
        else DrawerHost.ClearValue(Border.BackgroundProperty);
    }

    /// <summary>Page width below which the drawer overlays the list instead of pushing it.</summary>
    const double OverlayBelowWidth = 880;

    /// <summary>The sort button opens its menu under it (a right click does too).</summary>
    void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (SortButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = SortButton;
        menu.IsOpen = true;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The slow orbit around the hero icon is decoration only: skip it when motion is off (or Windows asks for less).
        if (!Animate.Enabled) return;

        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(60)) { RepeatBehavior = RepeatBehavior.Forever };
        OrbitRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
    }

    // ---- details drawer ----

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_watched != null)
        {
            _watched.PropertyChanged -= OnVmPropertyChanged;
            _watched.ListReshuffled -= OnListReshuffled;
        }

        _watched = e.NewValue as DownloadsViewModel;
        if (_watched != null)
        {
            _watched.PropertyChanged += OnVmPropertyChanged;
            _watched.ListReshuffled += OnListReshuffled;
        }
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // the list just became empty: its picture floats in (after the layout shows it)
        if (e.PropertyName == nameof(DownloadsViewModel.IsEmpty) && _watched is { IsEmpty: true } && IsLoaded)
            Dispatcher.BeginInvoke(() => Animate.FadeSlideIn(EmptyState, offsetY: 18, duration: Animate.Slow),
                System.Windows.Threading.DispatcherPriority.Loaded);

        if (e.PropertyName != nameof(DownloadsViewModel.IsDetailsOpen)) return;

        if (_watched is { IsDetailsOpen: true })
        {
            // slide the drawer open: the host reveals it from the left edge while the page makes room
            var width = Drawer.Width;
            if (!Animate.Enabled)
            {
                DrawerHost.Width = width;
                return;
            }

            DrawerHost.BeginAnimation(WidthProperty, new DoubleAnimation(0, width, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
            DrawerHost.Width = width;
        }
    }

    void OnCloseDetails(object? sender, EventArgs e) => Vm?.CloseDetails();

    /// <summary>Another list or another order: the heading and the list slide in again.</summary>
    void OnListReshuffled()
    {
        if (!IsLoaded) return;
        Animate.FadeSlideIn(Heading, offsetY: 0, offsetX: -12, duration: Animate.Base);
        Animate.FadeSlideIn(List, offsetY: 16);
    }

    /// <summary>Esc closes the drawer wherever the focus is.</summary>
    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm is { IsDetailsOpen: true } vm && !IsEditingText(e.OriginalSource))
        {
            vm.CloseDetails();
            e.Handled = true;
        }
    }

    static bool IsEditingText(object source) => source is System.Windows.Controls.Primitives.TextBoxBase;

    // ---- list ----

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Vm?.SetSelection(List.SelectedItems.Cast<DownloadItemViewModel>());

    void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        // Alt+Enter arrives as a "system" key
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.Delete:
                vm.RemoveSelected();
                e.Handled = true;
                break;
            case Key.Space:
                vm.ToggleSelection();
                e.Handled = true;
                break;
            case Key.Enter when (Keyboard.Modifiers & ModifierKeys.Alt) != 0:
                vm.ToggleDetails();
                e.Handled = true;
                break;
            case Key.Enter:
                vm.OpenSelected();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Double-click a card: open it if finished, otherwise pause / resume.</summary>
    void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm) return;

        // Ignore double-clicks on the buttons inside a card.
        for (var d = e.OriginalSource as DependencyObject; d != null && d != List; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;
        }

        if (List.SelectedItem is DownloadItemViewModel item) vm.RunPrimary(item);
    }
}
