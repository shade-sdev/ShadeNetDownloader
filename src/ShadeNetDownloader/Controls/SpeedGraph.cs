using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ShadeNetDownloader.Util;

namespace ShadeNetDownloader.Controls;

/// <summary>
/// The speed of a download over the last two minutes: a smooth line over a soft fill, newest sample at the right edge.
/// The vertical scale follows the peak, so a slow download is as readable as a fast one.
/// </summary>
public sealed class SpeedGraph : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(double[]), typeof(SpeedGraph),
        new FrameworkPropertyMetadata(Array.Empty<double>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(Brush), typeof(SpeedGraph),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridProperty = DependencyProperty.Register(
        nameof(Grid), typeof(Brush), typeof(SpeedGraph),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(Brush), typeof(SpeedGraph),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[] Samples
    {
        get => (double[])GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public Brush Line
    {
        get => (Brush)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public Brush Grid
    {
        get => (Brush)GetValue(GridProperty);
        set => SetValue(GridProperty, value);
    }

    public Brush Label
    {
        get => (Brush)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public SpeedGraph()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 8 || h < 8) return;

        var samples = Samples ?? Array.Empty<double>();
        const double padTop = 14, padBottom = 4;
        var plotHeight = h - padTop - padBottom;

        double peak = 0;
        foreach (var s in samples) peak = Math.Max(peak, s);
        var ceiling = GraphScale.Ceiling(peak);

        // faint guide lines at 50 % and 100 %, with the value they stand for
        var gridPen = new Pen(Grid, 1) { DashStyle = new DashStyle(new double[] { 2, 4 }, 0) };
        gridPen.Freeze();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var fraction in new[] { 1.0, 0.5 })
        {
            var y = padTop + plotHeight * (1 - fraction);
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            var text = new FormattedText(ByteFormat.Speed(ceiling * fraction), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, Label, dpi) { MaxTextWidth = Math.Max(1, w - 4) };
            dc.DrawText(text, new Point(2, Math.Max(0, y - text.Height - 1)));
        }

        if (samples.Length < 2)
        {
            // nothing to draw yet: a quiet baseline instead of an empty box
            var basePen = new Pen(Grid, 1.5);
            basePen.Freeze();
            dc.DrawLine(basePen, new Point(0, h - padBottom), new Point(w, h - padBottom));
            return;
        }

        // newest sample sits on the right edge; the window is SpeedHistory.Capacity samples wide
        var step = w / (SpeedHistory.Capacity - 1);
        var points = new Point[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var x = w - (samples.Length - 1 - i) * step;
            var y = padTop + plotHeight * (1 - Math.Min(1, samples[i] / ceiling));
            points[i] = new Point(x, y);
        }

        var line = Smooth(points, closeTo: null, top: padTop, bottom: h - padBottom);
        var area = Smooth(points, closeTo: h - padBottom, top: padTop, bottom: h - padBottom);

        var fill = Line is SolidColorBrush solid
            ? new LinearGradientBrush(
                Color.FromArgb(90, solid.Color.R, solid.Color.G, solid.Color.B),
                Color.FromArgb(0, solid.Color.R, solid.Color.G, solid.Color.B), 90)
            : null;
        if (fill != null)
        {
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        var pen = new Pen(Line, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        // the latest value, as a dot with a halo
        var last = points[^1];
        dc.DrawEllipse(Line, null, last, 3.5, 3.5);
        var halo = Line.Clone();
        halo.Opacity = 0.25;
        dc.DrawEllipse(halo, null, last, 7, 7);
    }

    /// <summary>A Catmull-Rom spline through the points, as cubic Béziers (so a steady download draws a calm curve, not a saw).</summary>
    static StreamGeometry Smooth(Point[] p, double? closeTo, double top, double bottom)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            if (closeTo is { } floor)
            {
                ctx.BeginFigure(new Point(p[0].X, floor), isFilled: true, isClosed: true);
                ctx.LineTo(p[0], isStroked: false, isSmoothJoin: false);
            }
            else
            {
                ctx.BeginFigure(p[0], isFilled: false, isClosed: false);
            }

            for (var i = 0; i < p.Length - 1; i++)
            {
                var p0 = p[Math.Max(0, i - 1)];
                var p1 = p[i];
                var p2 = p[i + 1];
                var p3 = p[Math.Min(p.Length - 1, i + 2)];
                // control points are kept inside the plot: after a sudden stop a free spline would swing below the axis
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, top, bottom));
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, top, bottom));
                ctx.BezierTo(c1, c2, p2, isStroked: true, isSmoothJoin: true);
            }

            if (closeTo is { } baseline) ctx.LineTo(new Point(p[^1].X, baseline), isStroked: false, isSmoothJoin: false);
        }

        g.Freeze();
        return g;
    }
}
