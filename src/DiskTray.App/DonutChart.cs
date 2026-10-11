using System.Windows;
using System.Windows.Media;
using DiskTray.Core.Tray;

namespace DiskTray.App;

/// <summary>Draws a <see cref="PieChart"/> as a ring, clockwise from 12 o'clock, with small gaps between slices.</summary>
internal sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty ChartProperty = DependencyProperty.Register(
        nameof(Chart), typeof(PieChart), typeof(DonutChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingFractionProperty = DependencyProperty.Register(
        nameof(RingFraction), typeof(double), typeof(DonutChart), new FrameworkPropertyMetadata(0.34, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(DonutChart), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public PieChart? Chart
    {
        get => (PieChart?)GetValue(ChartProperty);
        set => SetValue(ChartProperty, value);
    }

    /// <summary>How wide the ring is, as a fraction of the radius.</summary>
    public double RingFraction
    {
        get => (double)GetValue(RingFractionProperty);
        set => SetValue(RingFractionProperty, value);
    }

    /// <summary>The gap between slices, in DIPs, measured along the middle of the ring.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        if (Chart is not { Slices.Count: > 0 } chart)
        {
            return;
        }

        double outer = Math.Min(ActualWidth, ActualHeight) / 2;
        double inner = outer * (1 - RingFraction);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (chart.Slices.Count == 1)
        {
            var pen = new Pen(RgbBrushConverter.ToBrush(chart.Slices[0].Color), outer - inner);
            context.DrawEllipse(null, pen, center, (outer + inner) / 2, (outer + inner) / 2);
            return;
        }

        double gap = Gap / ((outer + inner) / 2);
        double start = 0;
        foreach (var slice in chart.Slices)
        {
            double sweep = slice.Fraction * 2 * Math.PI;
            // A sliver smaller than the gap still shows as a thin line.
            double trimmed = Math.Max(sweep - gap, Math.Min(sweep, 0.01));
            double from = start + ((sweep - trimmed) / 2);
            context.DrawGeometry(RgbBrushConverter.ToBrush(slice.Color), null, Segment(center, outer, inner, from, from + trimmed));
            start += sweep;
        }
    }

    private static StreamGeometry Segment(Point center, double outer, double inner, double from, double to)
    {
        Point At(double radius, double angle) =>
            new(center.X + (radius * Math.Sin(angle)), center.Y - (radius * Math.Cos(angle)));

        bool large = to - from > Math.PI;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(outer, from), isFilled: true, isClosed: true);
            ctx.ArcTo(At(outer, to), new Size(outer, outer), 0, large, SweepDirection.Clockwise, isStroked: false, isSmoothJoin: false);
            ctx.LineTo(At(inner, to), isStroked: false, isSmoothJoin: false);
            ctx.ArcTo(At(inner, from), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }
}
