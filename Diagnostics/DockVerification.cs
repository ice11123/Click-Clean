using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClickClean.Diagnostics;

internal static class DockVerification
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    internal static async Task Check(string output, List<string> checks)
    {
        void Assert(bool value, string text) { if (!value) throw new InvalidOperationException(text); checks.Add(text); }
        var clicks = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var island = new MiniIsland(async () => { clicks++; await completion.Task; }) { MotionAllowed = false };
        var button = (Button)island.FindName("CleanButton");
        try {
            island.Show(); await island.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var area = SystemParameters.WorkArea;
            Assert(Math.Abs(island.Left + island.Width / 2 - (area.Left + area.Width / 2)) < 1 && Math.Abs(island.Top + island.Height - area.Bottom) < 1, "底部岛固定在主屏工作区底部中央");
            Assert(button.Height == 36 && island.Width == 216 && island.Height == 72, "胶囊可见高度为36DIP，无大尺寸扩展面板");
            Assert(island.PointerPollingActive, "底部入口启用时启动低频鼠标采样");
            island.Hide(); Assert(!island.PointerPollingActive, "关闭底部入口停止鼠标采样");
            island.Show();
            var handle = new WindowInteropHelper(island).Handle;
            Assert(SendMessageW(handle, 0x0021, IntPtr.Zero, IntPtr.Zero).ToInt64() == 3, "鼠标操作不抢走当前应用焦点");
            Assert(!island.IsRevealed && !button.IsHitTestVisible && SendMessageW(handle, 0x0084, IntPtr.Zero, IntPtr.Zero).ToInt64() == -1, "收起状态不拦截鼠标");
            island.PreviewPointer(new Point(island.Width / 2, island.Height - 5), false, 1);
            Assert(island.IsRevealed && clicks == 0, "靠近底部浮出但不执行整理");
            Assert(button.ContextMenu is null && CountButtons(island) == 1, "底部岛仅一个点击入口，无附加按钮或菜单");
            island.SetState("即清 · 内存 63%", "", false, false);
            await island.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            MainWindow.Capture(island, Path.Combine(output, "dock-ready.png"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(clicks == 1 && !button.IsEnabled, "点击即触发一次整理，忙碌时拒绝重复点击");
            island.SetState("正在整理", "", true, false);
            await island.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            MainWindow.Capture(island, Path.Combine(output, "dock-running.png"));
            completion.SetResult();
            for (var i = 0; i < 20 && !button.IsEnabled; i++) await Task.Delay(10);
            Assert(button.IsEnabled, "整理完成后恢复点击入口");
            island.PreviewPointer(new Point(island.Width / 2, 30), false, 1.1);
            Assert(island.IsRevealed, "指针移入浮出胶囊时持续显示");
            island.PreviewPointer(new Point(-100, -100), false, 1.2);
            Assert(island.IsRevealed, "离开后短暂迟滞避免边界抖动");
            island.PreviewPointer(new Point(-100, -100), false, 1.4);
            Assert(!island.IsRevealed && button.Opacity == 0, "离开后完全收回且没有持续动画");
            island.PreviewPointer(new Point(island.Width / 2, island.Height - 5), true, 2);
            Assert(!island.IsRevealed && !island.Topmost, "全屏状态禁止浮出并取消置顶");
            island.PreviewPointer(new Point(island.Width / 2, island.Height - 5), false, 3);
            Assert(island.IsRevealed && island.Topmost, "退出全屏后可以再次靠近浮出");
            island.ResetPosition();
            Assert(Math.Abs(island.Top + island.Height - SystemParameters.WorkArea.Bottom) < 1, "显示布局刷新后重新停靠工作区底部");
            if (SystemParameters.ClientAreaAnimation) {
                island.PreviewReveal(false); island.MotionAllowed = true;
                island.PreviewPointer(new Point(island.Width / 2, island.Height - 5), false, 10);
                await Task.Delay(40);
                var slide = (TranslateTransform)island.FindName("Slide");
                Assert(slide.Y > 0 && slide.Y < 48 && button.Opacity > 0, "正常动画从底部向上浮出而不是原地出现");
                var offset = slide.Y;
                island.PreviewPointer(new Point(-100, -100), false, 10.4);
                Assert(Math.Abs(slide.Y - offset) < 1, "上浮中途移开，从当前位置反向收回不跳变");
                await Task.Delay(220);
                Assert(button.Opacity == 0 && slide.Y == 48, "收回动画在短时长内结束");
            } else {
                island.MotionAllowed = false; island.PreviewReveal(false);
                island.PreviewPointer(new Point(island.Width / 2, island.Height - 5), false, 10);
                Assert(((TranslateTransform)island.FindName("Slide")).Y == 0, "系统减少动画时立即呈现，不播放位移动画");
            }
        } finally { completion.TrySetResult(); island.DisposeDock(); island.Close(); }
    }
    private static int CountButtons(DependencyObject root)
    {
        var count = root is Button ? 1 : 0;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) count += CountButtons(VisualTreeHelper.GetChild(root, i));
        return count;
    }
}
