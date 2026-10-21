using System.Windows;
using System.Windows.Media;
using ShadeNet.Core.Models;
using ShadeNetDownloader.Util;
using ShadeNetDownloader.ViewModels;

namespace ShadeNetDownloader.Controls;

/// <summary>
/// The file as a grid of squares, each an equal slice of it. A square fills as its slice arrives; the square where a
/// connection is writing gets a ring in that connection's colour (the same colour as in the Connections tab).
/// </summary>
public sealed class SegmentMap : FrameworkElement
{
    const double Cell = 12, Gap = 3, Radius = 3;
    const int Rows = 5;

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SegmentInfo>), typeof(SegmentMap),
        new FrameworkPropertyMetadata(Array.Empty<SegmentInfo>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
        nameof(Total), typeof(long), typeof(SegmentMap),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DoneBrushProperty = DependencyProperty.Register(
        nameof(DoneBrush), typeof(Brush), typeof(SegmentMap),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyBrushProperty = DependencyProperty.Register(
        nameof(EmptyBrush), typeof(Brush), typeof(SegmentMap),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<SegmentInfo> Segments
    {
        get => (IReadOnlyList<SegmentInfo>)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public long Total
    {
        get => (long)GetValue(TotalProperty);
        set => SetValue(TotalProperty, value);
    }

    public Brush DoneBrush
    {
        get => (Brush)GetValue(DoneBrushProperty);
        set => SetValue(DoneBrushProperty, value);
    }

    public Brush EmptyBrush
    {
        get => (Brush)GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    public SegmentMap() => IsHitTestVisible = false;

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 320 : available.Width;
        return new Size(width, Rows * (Cell + Gap) - Gap);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var columns = Math.Max(1, (int)((ActualWidth + Gap) / (Cell + Gap)));
        var cells = BlockMap.Compute(Segments ?? Array.Empty<SegmentInfo>(), Total, columns * Rows);

        // centre the grid so the leftover pixels are split evenly at both sides
        var used = columns * (Cell + Gap) - Gap;
        var x0 = Math.Max(0, (ActualWidth - used) / 2);

        for (var i = 0; i < columns * Rows; i++)
        {
            var col = i % columns;
            var row = i / columns;
            var rect = new Rect(x0 + col * (Cell + Gap), row * (Cell + Gap), Cell, Cell);
            var cell = i < cells.Length ? cells[i] : default;

            dc.DrawRoundedRectangle(EmptyBrush, null, rect, Radius, Radius);

            if (cell.Done >= 0.995f)
            {
                dc.DrawRoundedRectangle(DoneBrush, null, rect, Radius, Radius);
            }
            else if (cell.Done > 0.01f)
            {
                // a slice that is partly there: filled from the left, like a tiny progress bar
                dc.PushClip(new RectangleGeometry(rect, Radius, Radius));
                dc.DrawRectangle(DoneBrush, null, new Rect(rect.X, rect.Y, Cell * cell.Done, Cell));
                dc.Pop();
            }

            if (cell.ActiveConnection >= 0)
            {
                var ring = new Pen(ConnectionPalette.BrushFor(cell.ActiveConnection), 2);
                ring.Freeze();
                var outer = new Rect(rect.X - 1.5, rect.Y - 1.5, Cell + 3, Cell + 3);
                dc.DrawRoundedRectangle(null, ring, outer, Radius + 1, Radius + 1);
            }
        }
    }
}
