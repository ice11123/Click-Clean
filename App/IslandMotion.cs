using System.Windows.Media.Animation;

namespace ClickClean;

internal static class IslandMotion
{
    // 每次从当前呈现值重定向，不跳回动画起点。只用于变换与透明度。
    public static void To(Animatable target, DependencyProperty property, double value, int milliseconds, bool animate)
    {
        var current = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        if (!animate || Math.Abs(current - value) < 0.001) { target.SetValue(property, value); return; }
        // 新时钟首帧尚未开始时仍呈现 current，避免读取或重绘跳到终点。
        target.SetValue(property, current);
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)), new KeySpline(0.23, 1, 0.32, 1)));
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    public static void Opacity(UIElement target, double value, int milliseconds, bool animate)
    {
        var current = target.Opacity;
        target.BeginAnimation(UIElement.OpacityProperty, null);
        if (!animate || Math.Abs(current - value) < 0.001) { target.Opacity = value; return; }
        target.Opacity = current;
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)), new KeySpline(0.23, 1, 0.32, 1)));
        target.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
