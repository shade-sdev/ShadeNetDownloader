using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShadeNetDownloader.Services;

namespace ShadeNetDownloader.Controls;

/// <summary>
/// A colour, shown as a swatch, that opens a picker: ready-made colours, hue / saturation / brightness sliders, an optional
/// opacity slider and the colour as text ("#RRGGBB" or "#AARRGGBB"). <see cref="Value"/> is that text, and changes live
/// while a slider moves, so what it colours updates as the user picks.
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

    static readonly string[] Ready =
    {
        "#0078D4", "#2563EB", "#0EA5E9", "#06B6D4", "#14B8A6", "#22C55E", "#84CC16", "#EAB308",
        "#F59E0B", "#F97316", "#EF4444", "#E11D48", "#EC4899", "#D946EF", "#A855F7", "#7C5CFF",
        "#6366F1", "#94A3B8", "#64748B", "#FFFFFF", "#9CA3AF", "#3F3F46", "#1F1F1F", "#000000",
    };

    bool _syncing;
    bool _ready;   // the parts exist (a binding may set Value before the control counts as initialised)
    DateTime _closedAt;

    public ColorPickerButton()
    {
        InitializeComponent();
        foreach (var hex in Ready) Swatches.Children.Add(SwatchButton(hex));
        _ready = true;
        ShowValue();
    }

    Button SwatchButton(string hex)
    {
        var colour = ThemeColor.Parse(hex);
        var button = new Button
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(3),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = hex,
            Template = SwatchTemplate(),
            Background = new SolidColorBrush(colour),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, hex);
        button.Click += (_, _) =>
        {
            // a ready-made colour keeps the opacity chosen so far
            var alpha = AllowAlpha && ThemeColor.TryParse(Value, out var current) ? current.A : (byte)255;
            Value = ThemeColor.Format(ThemeColor.WithAlpha(colour, alpha));
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

    void OnOpen(object sender, RoutedEventArgs e)
    {
        // a click on the swatch while the picker is open closes it (StaysOpen=False) and must not open it again at once
        if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < 250) return;
        ShowValue();
        Popup.IsOpen = true;
        HueSlider.Focus();
    }

    void OnClosed(object? sender, EventArgs e)
    {
        _closedAt = DateTime.UtcNow;
        Commit(HexBox.Text);
    }

    /// <summary>Shows <see cref="Value"/> on the swatch, the sliders and the text.</summary>
    void ShowValue()
    {
        if (!_ready) return;
        OpacityPanel.Visibility = AllowAlpha ? Visibility.Visible : Visibility.Collapsed;
        if (!ThemeColor.TryParse(Value, out var colour)) return;

        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        Swatch.Background = brush;
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
        var alpha = AllowAlpha ? (byte)Math.Round(OpacitySlider.Value) : (byte)255;
        _syncing = true;
        try
        {
            Value = ThemeColor.Format(ThemeColor.FromHsv(HueSlider.Value, SaturationSlider.Value, BrightnessSlider.Value, alpha));
        }
        finally
        {
            _syncing = false;
        }
    }

    void OnHexKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Commit(HexBox.Text);
        e.Handled = true;
    }

    void OnHexCommit(object sender, KeyboardFocusChangedEventArgs e) => Commit(HexBox.Text);

    void Commit(string? text)
    {
        if (!ThemeColor.TryParse(text, out var colour))
        {
            ShowValue();   // not a colour: the text goes back to the current one
            return;
        }

        if (!AllowAlpha) colour = ThemeColor.WithAlpha(colour, 255);
        var formatted = ThemeColor.Format(colour);
        if (!string.Equals(formatted, Value, StringComparison.OrdinalIgnoreCase)) Value = formatted;
        else ShowValue();
    }
}

