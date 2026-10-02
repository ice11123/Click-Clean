using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClickClean;

public partial class MiniIsland : Window
{
    private readonly Func<Task> clean;
    private readonly DockRevealPolicy reveal = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer pointerTimer;
    private bool shown, invoking, disposed, verificationOverride;
    private IntPtr handle;
    public bool MotionAllowed { get; set; } = true;
    public bool IsRevealed => shown;
    public bool PointerPollingActive => pointerTimer.IsEnabled;
    public event Action? RevealChanged;

    public MiniIsland(Func<Task> clean)
    {
        this.clean = clean; InitializeComponent();
        pointerTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        pointerTimer.Tick += (_, _) => PollPointer();
        ResetPosition();
        SourceInitialized += (_, _) => {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessages);
        };
        IsVisibleChanged += (_, _) => {
            if (IsVisible && !disposed) { ResetPosition(); pointerTimer.Start(); }
            else { pointerTimer.Stop(); reveal.Reset(); Reveal(false, false); }
        };
        CleanButton.LostMouseCapture += (_, _) => SetPressed(false, false);
        PreviewKeyDown += (_, e) => {
            if (e.Key == Key.Escape) { reveal.Reset(); Reveal(false, false); e.Handled = true; }
            else if (e.Key is Key.Enter or Key.Space) SetPressed(false, false);
        };
        Closed += (_, _) => DisposeDock();
    }
    private IntPtr WindowMessages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 不抢当前应用焦点；透明窗口收起时不截获鼠标。
        if (message == 0x0021) { handled = true; return new IntPtr(3); }
        if (message == 0x0084 && !shown) { handled = true; return new IntPtr(-1); }
        if (message is 0x007E or 0x001A or 0x02E0) Dispatcher.BeginInvoke(ResetPosition);
        return IntPtr.Zero;
    }
    public void ResetPosition()
    {
        if (disposed) return;
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height;
    }
    private void PollPointer()
    {
        if (disposed || verificationOverride || !IsVisible || handle == IntPtr.Zero) return;
        var fullscreen = Native.PrimaryFullscreen(handle);
        if (!Native.GetCursorPos(out var screen)) { reveal.Reset(); Reveal(false, false); return; }
        var point = PointFromScreen(new Point(screen.X, screen.Y));
        // 仅在任务栏上边缘附近触发；展开后使用较大保持区域，避免边界来回跳动。
        UpdatePointer(point, fullscreen, clock.Elapsed.TotalSeconds);
    }
    private void UpdatePointer(Point point, bool fullscreen, double seconds)
    {
        Topmost = !fullscreen;
        var near = new Rect(-16, Height - 18, Width + 32, 18).Contains(point);
        var inside = new Rect(-16, 0, Width + 32, Height + 8).Contains(point) || CleanButton.IsKeyboardFocusWithin;
        Reveal(reveal.Evaluate(near, inside, fullscreen, seconds), MotionAllowed);
    }
    private void Reveal(bool value, bool animate)
    {
        if (shown == value && animate) return;
        var changed = shown != value;
        shown = value; CleanButton.IsHitTestVisible = value; CleanButton.Focusable = value;
        var motion = animate && MotionAllowed && SystemParameters.ClientAreaAnimation;
        // 减少动画时不位移，仅保留轻量淡入淡出；透明区域不覆盖任务栏。
        IslandMotion.To(Slide, TranslateTransform.YProperty, value ? 0 : 48, 150, motion);
        IslandMotion.Opacity(CleanButton, value ? 1 : 0, 150, animate);
        if (!value) SetPressed(false, false);
        if (changed) RevealChanged?.Invoke();
    }
    public void SetState(string title, string detail, bool running, bool animate)
    {
        StatusLabel.Text = running ? "整理中…" : title.StartsWith("即清 · 内存 ", StringComparison.Ordinal) ? title[5..] :
            title.Contains("失败", StringComparison.Ordinal) ? "整理失败" : title.Contains("完成", StringComparison.Ordinal) ?
            detail.Replace("可用内存 ", "已整理 · ", StringComparison.Ordinal) : "点击整理";
        Dot.Fill = new SolidColorBrush(running ? Color.FromRgb(238, 177, 84) : Color.FromRgb(131, 173, 249));
    }
    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (!shown || invoking || !CleanButton.IsEnabled) return;
        invoking = true; CleanButton.IsEnabled = false;
        try { await clean(); }
        finally { invoking = false; CleanButton.IsEnabled = true; }
    }
    private void Press(object sender, MouseButtonEventArgs e) => SetPressed(true, MotionAllowed);
    private void Release(object sender, MouseButtonEventArgs e) => SetPressed(false, MotionAllowed);
    private void SetPressed(bool value, bool animate)
    {
        var motion = animate && MotionAllowed && SystemParameters.ClientAreaAnimation;
        IslandMotion.To(PressScale, ScaleTransform.ScaleXProperty, value ? 0.97 : 1, 120, motion);
        IslandMotion.To(PressScale, ScaleTransform.ScaleYProperty, value ? 0.97 : 1, 120, motion);
    }
    public void PreviewReveal(bool value)
    {
        verificationOverride = true; pointerTimer.Stop(); Reveal(value, false);
    }
    public void PreviewPointer(Point point, bool fullscreen, double seconds)
    {
        verificationOverride = true; pointerTimer.Stop(); UpdatePointer(point, fullscreen, seconds);
    }
    public void ResumePointerSampling()
    {
        verificationOverride = false; reveal.Reset(); Reveal(false, false);
        if (IsVisible && !disposed) pointerTimer.Start();
    }
    public void DisposeDock()
    {
        if (disposed) return;
        disposed = true; pointerTimer.Stop();
        if (handle != IntPtr.Zero) HwndSource.FromHwnd(handle)?.RemoveHook(WindowMessages);
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        internal static bool PrimaryFullscreen(IntPtr own)
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == own) return false;
            GetWindowThreadProcessId(foreground, out var process);
            GetWindowThreadProcessId(GetShellWindow(), out var shellProcess);
            if (process == 0 || process == shellProcess || process == Environment.ProcessId) return false;
            if ((GetWindowLongPtr(foreground, -16).ToInt64() & 0x00C00000) != 0 || !GetWindowRect(foreground, out var rect)) return false;
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen is null) return false;
            var bounds = screen.Bounds;
            return rect.Left <= bounds.Left && rect.Top <= bounds.Top && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;
        }
    }
}
