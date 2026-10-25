using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShadeNetDownloader.Services;

namespace ShadeNetDownloader.Controls;

/// <summary>
/// The open picker of a <see cref="ColorPickerButton"/>: ready-made colours, hue / saturation / brightness sliders, an
/// optional opacity slider and the colour as text. It reads and writes its button's <see cref="ColorPickerButton.Value"/>,
/// and is made only when the button is first opened (a settings page holds a couple of dozen pickers).
/// </summary>
public partial class ColorPickerPanel : UserControl
{
    static readonly string[] Ready =
    {
        "#0078D4", "#2563EB", "#0EA5E9", "#06B6D4", "#14B8A6", "#22C55E", "#84CC16", "#EAB308",
        "#F59E0B", "#F97316", "#EF4444", "#E11D48", "#EC4899", "#D946EF", "#A855F7", "#7C5CFF",
        "#6366F1", "#94A3B8", "#64748B", "#FFFFFF", "#9CA3AF", "#3F3F46", "#1F1F1F", "#000000",
    };

    readonly ColorPickerButton _owner;
    bool _syncing;
    bool _ready;

    public ColorPickerPanel(ColorPickerButton owner)
    {
        _owner = owner;
        InitializeComponent();
        foreach (var hex in Ready) Swatches.Children.Add(SwatchButton(hex));
        _ready = true;
        Show();
    }

    Button SwatchButton(string hex)
    {
        var colour = ThemeColor.Parse(hex);
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        var button = new Button
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(3),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = hex,
            Template = SwatchTemplate(),
            Background = brush,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, hex);
        button.Click += (_, _) =>
        {
            // a ready-made colour keeps the opacity chosen so far
            var alpha = _owner.AllowAlpha && ThemeColor.TryParse(_owner.Value, out var current) ? current.A : (byte)255;
            _owner.Value = ThemeColor.Format(ThemeColor.WithAlpha(colour, alpha));
        };
        return button;
    }

    static ControlTemplate? _swatchTemplate;

    static ControlTemplate SwatchTemplate()
    {
        if (_swatchTemplate != null) return _swatchTemplate;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        return _swatchTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    public void FocusFirst() => HueSlider.Focus();

    /// <summary>Shows the button's colour on the sliders, the preview and the text.</summary>
    public void Show()
    {
        if (!_ready) return;
        OpacityPanel.Visibility = _owner.AllowAlpha ? Visibility.Visible : Visibility.Collapsed;
        if (!ThemeColor.TryParse(_owner.Value, out var colour)) return;

        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        Preview.Background = brush;
        if (!HexBox.IsKeyboardFocusWithin) HexBox.Text = ThemeColor.Format(colour);
        if (_syncing) return;

        _syncing = true;
        try
        {
            var (h, s, v) = ThemeColor.ToHsv(colour);
            if (s > 0 && v > 0) HueSlider.Value = h;   // grey and black have no hue: the slider stays where it was
            if (v > 0) SaturationSlider.Value = s;
            BrightnessSlider.Value = v;
            OpacitySlider.Value = colour.A;
        }
        finally
        {
            _syncing = false;
        }
    }

    void OnSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !_ready) return;
        var alpha = _owner.AllowAlpha ? (byte)Math.Round(OpacitySlider.Value) : (byte)255;
        _syncing = true;
        try
        {
            _owner.Value = ThemeColor.Format(ThemeColor.FromHsv(HueSlider.Value, SaturationSlider.Value, BrightnessSlider.Value, alpha));
        }
        finally
        {
            _syncing = false;
        }
    }

    void OnHexKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Commit();
        e.Handled = true;
    }

    void OnHexCommit(object sender, KeyboardFocusChangedEventArgs e) => Commit();

    /// <summary>The colour typed as text becomes the button's (text that is no colour goes back to the current one).</summary>
    public void Commit()
    {
        if (!ThemeColor.TryParse(HexBox.Text, out var colour))
        {
            Show();
            return;
        }

        if (!_owner.AllowAlpha) colour = ThemeColor.WithAlpha(colour, 255);
        var formatted = ThemeColor.Format(colour);
        if (!string.Equals(formatted, _owner.Value, StringComparison.OrdinalIgnoreCase)) _owner.Value = formatted;
        else Show();
    }
}
