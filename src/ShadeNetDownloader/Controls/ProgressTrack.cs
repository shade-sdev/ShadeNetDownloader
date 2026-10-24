using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ShadeNetDownloader.Controls;

/// <summary>
/// A slim rounded progress bar drawn in one pass. Progress glides between updates (the engine reports 4×/s) and an
/// indeterminate mode sweeps a highlight for downloads of unknown size. Animation is skipped when Windows asks for
/// reduced motion (or the user turned motion off in Settings > Appearance).
/// </summary>
public sealed class ProgressTrack : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressTrack),
        new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ProgressTrack),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(ProgressTrack),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(ProgressTrack),
        new PropertyMetadata(false, OnIndeterminateChanged));

    // The animated, on-screen value (0..1) and the sweep position (-0.4..1) for indeterminate mode.
    static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(double), typeof(ProgressTrack),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    static readonly DependencyProperty SweepProperty = DependencyProperty.Register(
        nameof(Sweep), typeof(double), typeof(ProgressTrack),
        new FrameworkPropertyMetadata(-0.4, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..1</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    double Shown
    {
        get => (double)GetValue(ShownProperty);
        set => SetValue(ShownProperty, value);
    }

    double Sweep
    {
        get => (double)GetValue(SweepProperty);
        set => SetValue(SweepProperty, value);
    }

    public ProgressTrack()
    {
        SnapsToDevicePixels = true;
        IsVisibleChanged += (_, _) => UpdateSweep();
        Unloaded += (_, _) => BeginAnimation(SweepProperty, null);
        Loaded += (_, _) => UpdateSweep();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 100 : availableSize.Width, double.IsNaN(Height) ? 6 : Height);

    static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (ProgressTrack)d;
        var target = Math.Clamp((double)e.NewValue, 0, 1);

        // Jumps backwards (restart) and first paint are instant; forward progress glides.
        if (!bar.IsLoaded || target < bar.Shown || !ShadeNetDownloader.Services.Motion.Allowed)
        {
            bar.BeginAnimation(ShownProperty, null);
            bar.Shown = target;
            return;
        }

        bar.BeginAnimation(ShownProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    static void OnIndeterminateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ProgressTrack)d).UpdateSweep();

    void UpdateSweep()
    {
        if (IsIndeterminate && IsVisible && IsLoaded && ShadeNetDownloader.Services.Motion.Allowed)
        {
            BeginAnimation(SweepProperty, new DoubleAnimation(-0.4, 1.0, TimeSpan.FromSeconds(1.4))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            BeginAnimation(SweepProperty, null);
            Sweep = -0.4;
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var radius = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), radius, radius);

        if (IsIndeterminate)
        {
            // A soft capsule that sweeps across, clipped to the track.
            var clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
            dc.PushClip(clip);
            var bw = w * 0.4;
            dc.DrawRoundedRectangle(Fill, null, new Rect(Sweep * w, 0, bw, h), radius, radius);
            dc.Pop();
            return;
        }

        var filled = Math.Clamp(Shown, 0, 1) * w;
        if (filled <= 0) return;

        // Never narrower than a dot, so tiny progress is still visible.
        filled = Math.Max(filled, Math.Min(h, w));
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, filled, h), radius, radius);
    }
}
