using System.Windows.Media;

namespace ClickClean;

/// <summary>绘制实际占比，不引入评分、补间或独立刷新时钟。</summary>
public sealed class MemoryGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(MemoryGauge), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(MemoryGauge), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(MemoryGauge), new FrameworkPropertyMetadata(Brushes.DarkRed, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        const double stroke = 6;
        var radius = Math.Max(0, (Math.Min(ActualWidth, ActualHeight) - stroke) / 2);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawing.DrawEllipse(null, new Pen(Track, stroke), center, radius, radius);
        var value = double.IsFinite(Value) ? Math.Clamp(Value, 0, 100) : 0;
        if (value <= 0 || radius <= 0) return;
        var pen = new Pen(Fill, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 100) { drawing.DrawEllipse(null, pen, center, radius, radius); return; }
        var angle = value / 100 * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open()) {
            context.BeginFigure(new(center.X, center.Y - radius), false, false);
            context.ArcTo(new(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)), new(radius, radius), 0, value > 50, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze(); drawing.DrawGeometry(null, pen, geometry);
    }
}
