using System.Diagnostics;

namespace ClickClean;
public partial class IslandView : UserControl
{
    private double position, velocity, target;
    private long last;
    private bool animating;
    public IslandView()
    {
        InitializeComponent();
        Unloaded += (_, _) => Stop();
    }
    public void SetState(string title, string? detail, bool expanded, bool animate)
    {
        TitleText.Text = title; DetailText.Text = detail;
        DetailText.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        target = expanded ? 1 : 0;
        if (!animate || !SystemParameters.ClientAreaAnimation) { Stop(); position = target; velocity = 0; Paint(); return; }
        if (Math.Abs(position - target) < 0.001 && Math.Abs(velocity) < 0.001) return;
        if (!animating) { animating = true; last = Stopwatch.GetTimestamp(); CompositionTarget.Rendering += Frame; }
    }
    private void Frame(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = Math.Clamp((now - last) / (double)Stopwatch.Frequency, 0, 0.032); last = now;
        // 临界阻尼解析积分；中断时保留位置和速度，无闲置动画循环。
        const double omega = 20;
        var offset = position - target; var decay = Math.Exp(-omega * dt);
        var term = velocity + omega * offset;
        position = target + (offset + term * dt) * decay;
        velocity = (velocity - omega * term * dt) * decay;
        if (Math.Abs(position - target) < 0.001 && Math.Abs(velocity) < 0.005) { position = target; velocity = 0; Stop(); }
        Paint();
    }
    private void Paint() { PillScale.ScaleX = 0.82 + position * 0.18; PillScale.ScaleY = 0.64 + position * 0.36; }
    private void Stop() { if (animating) CompositionTarget.Rendering -= Frame; animating = false; }
}
