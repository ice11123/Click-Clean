using System.Windows.Input;

namespace ClickClean;

public static class PressFeedback
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(PressFeedback), new(false, OnChanged));
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Button button) return;
        if ((bool)args.NewValue) { button.PreviewMouseLeftButtonDown += Down; button.PreviewMouseLeftButtonUp += Up; button.LostMouseCapture += Up; button.Unloaded += Up; }
        else { button.PreviewMouseLeftButtonDown -= Down; button.PreviewMouseLeftButtonUp -= Up; button.LostMouseCapture -= Up; button.Unloaded -= Up; }
    }
    private static void Down(object sender, MouseButtonEventArgs e) => Animate((Button)sender, 0.98);
    private static void Up(object sender, RoutedEventArgs e) => Animate((Button)sender, 1);
    private static void Animate(Button button, double value)
    {
        var allowed = Application.Current.Resources["AnimationsAllowed"] is true && button.IsEnabled;
        if (button.RenderTransform is not ScaleTransform) { button.RenderTransform = new ScaleTransform(); button.RenderTransformOrigin = new(0.5, 0.5); }
        var transform = (ScaleTransform)button.RenderTransform;
        IslandMotion.To(transform, ScaleTransform.ScaleXProperty, value, 120, allowed);
        IslandMotion.To(transform, ScaleTransform.ScaleYProperty, value, 120, allowed);
    }
}
