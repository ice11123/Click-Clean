using System.Windows.Input;
using System.Windows.Interop;
namespace ClickClean;

public partial class MiniIsland : Window
{
    private readonly Action open, clean, hide;
    private readonly Action<Point> moved;
    private bool expanded;
    private string title = "即清 · 随时就绪", detail = "";
    private bool running;
    public bool MotionAllowed { get; set; } = true;
    public MiniIsland(Action open, Action clean, Action hide, Action<Point> moved)
    {
        this.open = open; this.clean = clean; this.hide = hide; this.moved = moved; InitializeComponent();
        ResetPosition();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessages);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SetExpanded(false, false); e.Handled = true; } };
    }
    private IntPtr WindowMessages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x007E) Dispatcher.BeginInvoke(() => SetPosition(Left, Top));
        return IntPtr.Zero;
    }
    public void SetPosition(double x, double y)
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }
    public void ResetPosition()
    {
        var area = SystemParameters.WorkArea; SetPosition(area.Left + (area.Width - Width) / 2, area.Top + 8);
    }
    public void SetState(string title, string detail, bool running, bool animate)
    {
        this.title = title; this.detail = detail; this.running = running;
        if (running) expanded = true;
        Actions.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        Island.SetState(title, detail, running || expanded, animate && MotionAllowed);
    }
    private void SetExpanded(bool value, bool animate = true)
    { expanded = value; Actions.Visibility = value ? Visibility.Visible : Visibility.Collapsed; Island.SetState(title, detail, running || expanded, animate && MotionAllowed); }
    private void Open_Click(object sender, RoutedEventArgs e) => open();
    private void Clean_Click(object sender, RoutedEventArgs e) => clean();
    private void Hide_Click(object sender, RoutedEventArgs e) => hide();
    private Point? pressed;
    private void Drag_Click(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is Button) return;
            source = source is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(source);
        }
        pressed = e.GetPosition(this);
    }
    private void Drag_Move(object sender, MouseEventArgs e)
    {
        if (pressed is not { } point || e.LeftButton != MouseButtonState.Pressed) return;
        if ((e.GetPosition(this) - point).Length < 7) return;
        pressed = null; DragMove(); SetPosition(Left, Top); moved(new Point(Left, Top));
    }
    private void Drag_Release(object sender, MouseButtonEventArgs e)
    { if (pressed is null) return; pressed = null; SetExpanded(!expanded); }
}
