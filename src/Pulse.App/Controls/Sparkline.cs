using System.Windows;
using System.Windows.Media;

namespace Pulse.App.Controls;

/// <summary>Küçük çizgi grafik. Düz çizgi, gradyan yok; altı çok açık dolgu.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        var w = ActualWidth;
        var h = ActualHeight;
        if (values is null || values.Count < 2 || w < 4 || h < 4) return;

        var min = double.IsNaN(Minimum) ? values.Min() : Minimum;
        var max = double.IsNaN(Maximum) ? values.Max() : Maximum;
        if (max - min < 1e-6) { max = min + 1; }
        var pad = 2.0;

        Point P(int i) => new(
            i * (w - 2 * pad) / (values.Count - 1) + pad,
            h - pad - (Math.Clamp(values[i], min, max) - min) / (max - min) * (h - 2 * pad));

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false, false);
            for (var i = 1; i < values.Count; i++) g.LineTo(P(i), true, true);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(P(0).X, h), true, true);
            for (var i = 0; i < values.Count; i++) g.LineTo(P(i), true, false);
            g.LineTo(new Point(P(values.Count - 1).X, h), true, false);
        }
        area.Freeze();

        var fill = Stroke.Clone();
        fill.Opacity = 0.07;
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
    }
}
