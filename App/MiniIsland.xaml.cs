using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using System.Windows.Automation;

namespace ClickClean;

public partial class MiniIsland : Window
{
    private readonly Func<Task> clean;
    private readonly DockRevealPolicy reveal = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer pointerTimer;
    private readonly DispatcherTimer resultTimer;
    private readonly DockStatus status;
    private bool motionAllowed = true, breathing;
    private bool working;
    private int backgroundState;
    private bool shown, invoking, disposed, verificationOverride;
    private IntPtr handle;
    public bool MotionAllowed { get => motionAllowed; set { motionAllowed = value; RefreshBreathing(); RefreshBackground(); } }
    public bool IsWorking { get => working; set { working = value; RefreshBackground(); } }
    public bool BreathingActive => breathing;
    public bool BackgroundMotionActive => AmbientGlow.HasAnimatedProperties || AmbientShift.HasAnimatedProperties;
    public bool ResultTimerActive => resultTimer.IsEnabled;
    public bool IsRevealed => shown;
    public bool PointerPollingActive => pointerTimer.IsEnabled;
    public event Action? RevealChanged;

    public MiniIsland(Func<Task> clean, DockStatus? status = null)
    {
        this.clean = clean; this.status = status ?? new DockStatus(); InitializeComponent();
        resultTimer = new DispatcherTimer(DispatcherPriority.Normal);
        resultTimer.Tick += (_, _) => RefreshStatus();
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
        if (message is 0x007E or 0x001A or 0x02E0) Dispatcher.BeginInvoke(() => { ResetPosition(); RefreshBreathing(); RefreshBackground(); });
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
        var motion = animate && MotionAllowed && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        // 减少动画时不位移，仅保留轻量淡入淡出；透明区域不覆盖任务栏。
        IslandMotion.To(Slide, TranslateTransform.YProperty, value ? 0 : 48, 150, motion);
        IslandMotion.Opacity(CleanButton, value ? 1 : 0, 150, motion);
        if (!value) SetPressed(false, false);
        RefreshStatus();
        if (changed) RevealChanged?.Invoke();
    }
    public void RefreshStatus()
    {
        if (disposed) return;
        StatusLabel.Text = status.Text;
        AutomationProperties.SetName(CleanButton, status.Text + "；点击检查内存压力并按需整理待机缓存");
        var color = status.Pressure switch {
            MemoryPressureBand.Low => Color.FromRgb(112, 207, 164),
            MemoryPressureBand.Moderate => Color.FromRgb(238, 190, 104),
            MemoryPressureBand.High => Color.FromRgb(242, 125, 124),
            _ => Color.FromRgb(151, 160, 175)
        };
        if (Dot.Fill is not SolidColorBrush brush || brush.Color != color) Dot.Fill = new SolidColorBrush(color);
        resultTimer.Stop();
        var remaining = status.ResultRemaining;
        if (IsVisible && shown && remaining > TimeSpan.Zero) {
            resultTimer.Interval = remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining; resultTimer.Start();
        }
        RefreshBreathing();
        RefreshBackground();
    }
    private void RefreshBreathing()
    {
        var enabled = !disposed && IsVisible && shown && MotionAllowed && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast && status.Pressure != MemoryPressureBand.Unknown;
        if (enabled == breathing) return;
        breathing = enabled;
        Dot.BeginAnimation(OpacityProperty, null); Dot.Opacity = 1;
        if (enabled) Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(1200)) {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
        });
    }
    private void RefreshBackground()
    {
        var visible = !disposed && IsVisible && shown && !SystemParameters.HighContrast;
        var motion = visible && MotionAllowed && SystemParameters.ClientAreaAnimation;
        var next = !visible ? 0 : !motion ? 1 : working || invoking ? 3 : 2;
        if (next == backgroundState) return;
        backgroundState = next;
        // 局部柔光只表达浮出和整理状态；数字始终固定，后台不保留动画时钟。
        AmbientGlow.BeginAnimation(OpacityProperty, null);
        AmbientShift.BeginAnimation(TranslateTransform.XProperty, null);
        AmbientShift.X = 0;
        AmbientGlow.Opacity = visible ? 0.16 : 0;
        if (next == 2) {
            var revealGlow = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            revealGlow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            revealGlow.KeyFrames.Add(new SplineDoubleKeyFrame(0.16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), new KeySpline(0.23, 1, 0.32, 1)));
            revealGlow.Completed += (_, _) => { if (backgroundState == 2) AmbientGlow.BeginAnimation(OpacityProperty, null); };
            AmbientGlow.BeginAnimation(OpacityProperty, revealGlow);
        } else if (next == 3) {
            AmbientGlow.Opacity = 0.24;
            var progressGlow = new DoubleAnimationUsingKeyFrames { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            progressGlow.KeyFrames.Add(new DiscreteDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            progressGlow.KeyFrames.Add(new SplineDoubleKeyFrame(10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200)), new KeySpline(0.77, 0, 0.175, 1)));
            AmbientShift.BeginAnimation(TranslateTransform.XProperty, progressGlow);
        }
    }
    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (!shown || invoking || !CleanButton.IsEnabled) return;
        invoking = true; CleanButton.IsEnabled = false; RefreshBackground();
        try { await clean(); }
        finally { invoking = false; CleanButton.IsEnabled = true; RefreshBackground(); }
    }
    private void Press(object sender, MouseButtonEventArgs e) => SetPressed(true, MotionAllowed);
    private void Release(object sender, MouseButtonEventArgs e) => SetPressed(false, MotionAllowed);
    private void SetPressed(bool value, bool animate)
    {
        var motion = animate && MotionAllowed && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
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
        disposed = true; pointerTimer.Stop(); resultTimer.Stop(); RefreshBreathing(); RefreshBackground();
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
