using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShadeNetDownloader.Services;

namespace ShadeNetDownloader.Controls;

/// <summary>
/// A colour, shown as a swatch, that opens a picker (<see cref="ColorPickerPanel"/>): ready-made colours, hue / saturation /
/// brightness sliders, an optional opacity slider and the colour as text ("#RRGGBB" or "#AARRGGBB"). <see cref="Value"/> is
/// that text, and changes live while a slider moves, so what it colours updates as the user picks. The picker itself is
/// made the first time it opens: a page shows many swatches and opens few of them.
/// </summary>
public partial class ColorPickerButton : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ColorPickerButton),
        new FrameworkPropertyMetadata("#0078D4", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorPickerButton)d).ShowValue()));

    public static readonly DependencyProperty AllowAlphaProperty = DependencyProperty.Register(
        nameof(AllowAlpha), typeof(bool), typeof(ColorPickerButton),
        new PropertyMetadata(false, (d, _) => ((ColorPickerButton)d).ShowValue()));

    /// <summary>The colour as text.</summary>
    public string? Value
    {
        get => (string?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Whether the colour may be see-through (an opacity slider is shown).</summary>
    public bool AllowAlpha
    {
        get => (bool)GetValue(AllowAlphaProperty);
        set => SetValue(AllowAlphaProperty, value);
    }

    bool _ready;   // the parts exist (a binding may set Value before the control counts as initialised)
    DateTime _closedAt;
    ColorPickerPanel? _panel;

    public ColorPickerButton()
    {
        InitializeComponent();
        _ready = true;
        ShowValue();
    }

    void OnOpen(object sender, RoutedEventArgs e)
    {
        // a click on the swatch while the picker is open closes it (StaysOpen=False) and must not open it again at once
        if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 250) return;
        if (_panel == null)
        {
            _panel = new ColorPickerPanel(this);
            Popup.Child = _panel;
        }

        _panel.Show();
        Popup.IsOpen = true;
        _panel.FocusFirst();
    }

    void OnClosed(object? sender, EventArgs e)
    {
        _closedAt = DateTime.UtcNow;
        _panel?.Commit();
    }

    /// <summary>Shows <see cref="Value"/> on the swatch (and on the picker, when it is open).</summary>
    void ShowValue()
    {
        if (!_ready || !ThemeColor.TryParse(Value, out var colour)) return;
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        Swatch.Background = brush;
        _panel?.Show();
    }
}
