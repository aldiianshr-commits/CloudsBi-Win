using System.Windows;
using System.Windows.Media;

namespace CloudSBiian.Desktop.Controls;

/// <summary>Minimal line chart for a series of real samples. Draws nothing until data exists.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryValuesProperty = DependencyProperty.Register(
        nameof(SecondaryValues), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryStrokeProperty = DependencyProperty.Register(
        nameof(SecondaryStroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<double>? SecondaryValues
    {
        get => (IReadOnlyList<double>?)GetValue(SecondaryValuesProperty);
        set => SetValue(SecondaryValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush SecondaryStroke
    {
        get => (Brush)GetValue(SecondaryStrokeProperty);
        set => SetValue(SecondaryStrokeProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2)
        {
            return;
        }

        var gridPen = new Pen(GridBrush, 1) { DashStyle = DashStyles.Dot };
        for (var i = 1; i < 4; i++)
        {
            var y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        var max = Math.Max(Max(Values), Max(SecondaryValues));
        if (max <= 0)
        {
            max = 1;
        }

        Draw(dc, SecondaryValues, SecondaryStroke, max, w, h);
        Draw(dc, Values, Stroke, max, w, h);
    }

    private static double Max(IReadOnlyList<double>? v) => v == null || v.Count == 0 ? 0 : v.Max();

    private static void Draw(DrawingContext dc, IReadOnlyList<double>? values, Brush brush, double max, double w, double h)
    {
        if (values == null || values.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var x = w * i / (values.Count - 1);
                var y = h - (values[i] / max * (h - 4)) - 2;
                if (i == 0)
                {
                    ctx.BeginFigure(new Point(x, y), false, false);
                }
                else
                {
                    ctx.LineTo(new Point(x, y), true, true);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round }, geometry);
    }
}
