using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using ClickClean;

namespace ClickClean.Diagnostics;
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--system-check") { SystemCheck(args[1]); return; }
        if (args.Length == 2 && args[0] == "--acceptance") { RunUpdateCheck(() => AcceptanceCheck.Run(args[1]), Path.ChangeExtension(args[1], ".failure.txt")); return; }
        if (args.Length == 2 && args[0] == "--startup-probe") { File.WriteAllText(args[1], JsonSerializer.Serialize(new { Admin = WindowsMemoryApi.IsAdmin(), ProcessId = Environment.ProcessId })); return; }
        if (args.Length == 3 && args[0] == "--verify-updates") { RunUpdateCheck(() => UpdateVerification.Check(args[1], args[2]), Path.Combine(args[2], "failure.txt")); return; }
        if (args.Length == 3 && args[0] == "--update-installed") { RunUpdateCheck(() => UpdateVerification.ApplyInstalled(args[1], args[2]), Path.ChangeExtension(args[2], ".failure.txt")); return; }
        if (args.Length == 4 && args[0] == "--apply-update-fixture") { RunUpdateCheck(() => UpdateVerification.ApplyFixture(args[1], args[2], args[3]), args[3]); return; }
        var output = args.Length == 2 && args[0] == "--capture" ? Path.GetFullPath(args[1]) : null;
        var root = output is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickCleanPreview") : Path.Combine(output, "preview-data");
        Directory.CreateDirectory(root);
        var store = new Store(root); var app = new App(preview: true); app.InitializeComponent();
        var openedLinks = new List<Uri>();
        var window = new MainWindow(store, verification: true, projectLinkOpener: output is null ? null : openedLinks.Add); app.MainWindow = window;
        window.Loaded += async (_, _) => {
            window.ShowIsland();
            if (output is null) return;
            try { await Capture(window, store, output, openedLinks); app.Shutdown(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); app.Shutdown(1); }
        };
        app.Run(window);
    }
    private static void RunUpdateCheck(Func<Task> action, string failurePath)
    {
        try { action().GetAwaiter().GetResult(); }
        catch (Exception ex) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(failurePath))!); File.WriteAllText(failurePath, ex.ToString()); Environment.ExitCode = 1; }
    }
    private static async Task Capture(MainWindow window, Store store, string output, List<Uri> openedLinks)
    {
        Directory.CreateDirectory(output); var checks = new List<string>();
        void Assert(bool value, string text) { if (!value) throw new InvalidOperationException(text); checks.Add(text); }
        var defaultWidth = window.Width; var defaultHeight = window.Height;
        var buttons = new[] { "RepositoryLinkButton", "BlogLinkButton", "AuthorLinkButton" }.Select(name => (Button)window.FindName(name)).ToArray();
        foreach (var button in buttons) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert(openedLinks.Select(uri => uri.AbsoluteUri).SequenceEqual(new[] { "https://github.com/ice11123/Click-Clean", "https://ice11123.github.io/blog_test2/", "https://github.com/ice11123" }), "三个链接按钮分别打开准确的仓库、博客和作者首页（模拟打开器）");
        Assert(buttons.All(button => !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(button)) && button.ToolTip is not null), "三个链接按钮均提供文字替代、辅助名称和提示");
        var iconData = buttons.Select(button => Descendants(button).OfType<System.Windows.Shapes.Path>().FirstOrDefault()?.Data?.ToString()).ToArray();
        Assert(iconData.All(data => !string.IsNullOrWhiteSpace(data)) && iconData.Distinct().Count() == 3, "仓库、博客和作者按钮使用三种不同矢量图标");
        Assert(Descendants(window).OfType<TextBlock>().Any(text => text.Text.Contains("原作者：离子怪", StringComparison.Ordinal)), "原作者离子怪署名可见");
        var before = new MemorySnapshot(32UL << 30, 12UL << 30, 21UL << 30, 48UL << 30);
        window.DesktopIsland?.PreviewReveal(false);
        await DockVerification.Check(output, checks);
        if (window.DesktopIsland is { } liveDock) {
            var label = (TextBlock)liveDock.FindName("StatusLabel");
            liveDock.PreviewReveal(true);
            Assert(window.SamplingSeconds == 1 && label.Text.StartsWith("内存 ", StringComparison.Ordinal), "浮出即刷新内存，实时采样间隔为一秒");
            ((Button)window.FindName("CleanButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var step = 1; step <= 3; step++) {
                var deadline = Stopwatch.StartNew();
                while (!label.Text.StartsWith(step + "/3 ", StringComparison.Ordinal) && deadline.Elapsed.TotalSeconds < 2) await Task.Delay(10);
                Assert(label.Text.StartsWith(step + "/3 ", StringComparison.Ordinal), "主窗口模拟整理同步到底部岛步骤 " + step);
            }
            var finish = Stopwatch.StartNew();
            while (window.IsBusy && finish.Elapsed.TotalSeconds < 3) await Task.Delay(10);
            Assert(!window.IsBusy && label.Text.StartsWith("完成 · ", StringComparison.Ordinal), "主窗口整理完成展示成果，不被finally恢复覆盖");
            await Task.Delay(2150);
            Assert(label.Text.StartsWith("内存 ", StringComparison.Ordinal), "真实UI流程两秒后恢复实时内存，不需要额外点击");
            liveDock.PreviewReveal(false);
        }
        window.ShowResult(new(DateTimeOffset.Now, before, before with { Available = 13UL << 30 }, [new(MemoryCommand.StandbyCache, 0, 0.1)], false, null, 0.1) { Trigger = "预览（模拟）" });
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        foreach (var theme in new[] { "light", "dark" })
        {
            store.Settings.Theme = theme; window.ApplyTheme(); window.CloseOverlay();
            ((ScrollViewer)window.FindName("MainContent")).ScrollToTop();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(window.NativeThemeApplied, "原生标题栏成功应用主题属性：" + theme);
            Assert(((SolidColorBrush)Application.Current.Resources["AccentText"]).Color.ToString() == (theme == "light" ? "#FFFFFFFF" : "#FF101A2F"), "主题强调文字使用对应前景色：" + theme);
            foreach (var pair in new[] { ("Text", "Surface"), ("Muted", "Surface"), ("AccentText", "Accent") }) {
                var foreground = ((SolidColorBrush)Application.Current.Resources[pair.Item1]).Color;
                var background = ((SolidColorBrush)Application.Current.Resources[pair.Item2]).Color;
                Assert(Contrast(foreground, background) >= 4.5, "主题基础文本色对比度不少于4.5：" + theme + "/" + pair.Item1);
            }
            window.PreviewDockIdle(before); window.RenderTo(Path.Combine(output, theme + "-main.png"));
            var scroller = (ScrollViewer)window.FindName("MainContent");
            scroller.ScrollToBottom(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(scroller.VerticalOffset > 0 && Descendants(scroller).OfType<System.Windows.Controls.Primitives.ScrollBar>().Any(bar => bar.Orientation == Orientation.Vertical && bar.Width == 10), "主题滚动条存在，主体能够滚动到底部：" + theme);
            scroller.ScrollToTop(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.SetDockOperation("刷新修改页…"); window.RenderTo(Path.Combine(output, theme + "-running.png"));
            if (window.DesktopIsland is { } mini) { mini.PreviewReveal(true); mini.UpdateLayout(); MainWindow.Capture(mini, Path.Combine(output, theme + "-mini.png")); }
            foreach (var section in new[] { "appearance", "automation", "updates", "about" })
            {
                window.OpenSettings(); window.ShowSettingsSection(section);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var panelName = section switch { "appearance" => "AppearanceSection", "automation" => "AutomationSection", "updates" => "UpdatesSection", _ => "AboutSection" };
                Assert(Descendants((FrameworkElement)window.FindName(panelName)).OfType<TextBlock>().Where(text => text.FontSize >= 17).All(text => ((SolidColorBrush)text.Foreground).Color == ((SolidColorBrush)Application.Current.Resources["Text"]).Color), "设置区大标题实际前景色跟随主题：" + theme + "/" + section);
                var selectedTab = Descendants((DependencyObject)window.FindName("SettingsPanel")).OfType<Button>().First(button => button.Tag as string == section);
                Assert(((SolidColorBrush)selectedTab.Background).Color == ((SolidColorBrush)Application.Current.Resources["Tint"]).Color, "设置分段导航选中状态正确：" + theme + "/" + section);
                foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2d }) window.RenderTo(Path.Combine(output, $"{theme}-{section}-{scale * 100:0}.png"), scale);
            }
            window.OpenHistory(); window.RenderTo(Path.Combine(output, theme + "-history.png"));
            window.Width = 440; window.Height = 560; window.OpenSettings(); window.ShowSettingsSection("automation");
            window.RenderTo(Path.Combine(output, theme + "-small-automation.png")); window.CloseOverlay();
            ((ScrollViewer)window.FindName("MainContent")).ScrollToTop();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(buttons.All(button => button.IsVisible && button.ActualWidth > 55 && button.ActualHeight >= 30), "窄窗口下三个作者链接保持可见、可点击：" + theme);
            window.RenderTo(Path.Combine(output, theme + "-small-main.png")); window.Width = defaultWidth; window.Height = defaultHeight;
        }
        window.OpenSettings(); var startup = (CheckBox)window.FindName("StartupCheck"); startup.Focus(); Assert(startup.IsKeyboardFocused, "设置可键盘聚焦");
        for (var i = 0; i < 50; i++)
        {
            if (Keyboard.FocusedElement is UIElement focused) focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Assert(((Border)window.FindName("Overlay")).IsKeyboardFocusWithin, "Tab 焦点留在设置弹出层");
        }
        store.Settings.ReduceMotion = true; Assert(!window.MotionEnabled, "减少动画生效");
        window.CloseOverlay(); window.ShowResult(new(DateTimeOffset.Now, before, null, [new(MemoryCommand.WorkingSets, 0, 0.1)], false, "整理后快照不可用", 0.1));
        Assert(((TextBlock)window.FindName("ResultDelta")).Text.Contains("未知"), "快照失败不伪造零变化");
        window.RenderTo(Path.Combine(output, "unknown-result.png"));
        window.Hide(); window.DesktopIsland?.ResumePointerSampling();
        Assert(window.SamplingSeconds == 10, "底部岛收起时降低内存采样频率");
        Assert(window.DesktopIsland?.PointerPollingActive == true, "性能采样期间实际启用鼠标靠近监听");
        await Task.Delay(10000);
        var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
        await Task.Delay(30000); process.Refresh();
        var dockWasRevealed = window.DesktopIsland?.IsRevealed;
        var dockWasListening = window.DesktopIsland?.PointerPollingActive;
        window.DesktopIsland?.Hide();
        Assert(window.DesktopIsland?.PointerPollingActive == false && window.SamplingSeconds == 10, "完全关闭桌面入口停止鼠标采样");
        File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new {
            Checks = checks, WorkingSetMiB = process.WorkingSet64 / 1048576d, PrivateMiB = process.PrivateMemorySize64 / 1048576d,
            HiddenCpuMachinePercent = (process.TotalProcessorTime - cpu).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100,
            SampleSeconds = clock.Elapsed.TotalSeconds, DockListening = dockWasListening, DockRevealed = dockWasRevealed,
            Note = "CPU测量期间实际启用底部岛鼠标监听。截图倍率属于离屏渲染检查，不代替真实 Windows 显示缩放验收。"
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.DisposeResources();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte value) { var channel = value / 255d; return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4); }
        static double Light(Color value) => 0.2126 * Linear(value.R) + 0.7152 * Linear(value.G) + 0.0722 * Linear(value.B);
        var first = Light(foreground); var second = Light(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
    internal static void SystemCheck(string output)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(output))!; Directory.CreateDirectory(directory);
        try
        {
            if (!WindowsMemoryApi.IsAdmin()) throw new InvalidOperationException("实测必须通过 UAC 以管理员运行诊断程序。");
            var initial = WindowsMemoryApi.ProfilePrivilegeEnabled();
            var result = new MemoryEngine(new WindowsMemoryApi()).RunAsync(Enum.GetValues<MemoryCommand>()).GetAwaiter().GetResult();
            var restored = WindowsMemoryApi.ProfilePrivilegeEnabled() == initial;
            File.WriteAllText(output, JsonSerializer.Serialize(new { Result = result, PrivilegeRestored = restored }, new JsonSerializerOptions { WriteIndented = true }));
            if (!result.Success || !restored) Environment.ExitCode = 1;
        }
        catch (Exception ex) { File.WriteAllText(output, ex.ToString()); Environment.ExitCode = 1; }
    }
}
