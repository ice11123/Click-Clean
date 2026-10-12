using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClickClean.ViewModels;

namespace ClickClean.Diagnostics;

internal static class RebuiltUiCheck
{
    public static async Task Run(MainWindow window, Store store, string output, List<Uri> links, FakeApplicationUpdates update, TrackingMemoryApi memory, FakeEnvironment environment, FakeProcesses processes)
    {
        Directory.CreateDirectory(output); var checks = new List<string>();
        using var bindingErrors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
        T Control<T>(string name) where T : class => window.FindUiElement(name) as T ?? throw new InvalidOperationException("未找到控件：" + name);
        void Click(string name) { var button = Control<Button>(name); button.Focus(); button.Command.Execute(button.CommandParameter); }
        void Command(object value) => window.Presentation.Command.Execute(value);
        async Task Settle() { await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
        async Task Wait(Func<bool> condition) { var timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed.TotalSeconds < 8) await Task.Delay(20); Assert(condition(), "异步状态在期限内完成"); }
        var state = window.Presentation; window.DesktopIsland?.PreviewReveal(false);
        Assert(store.Settings.Theme == "light" && store.Settings.MiniIsland && !store.Settings.Startup, "新安装浅色、岛开启、登录启动关闭");
        Assert(!store.Settings.Automation.ThresholdEnabled && !store.Settings.Automation.TimerEnabled && store.Settings.Automation.ThresholdPercent == 55, "自动默认关闭，门槛55保留");
        memory.FailRead = true; await Task.Delay(1150);
        Assert(state.Load == "—" && state.Available == "—" && state.MemoryFailed, "读数失败清除旧数字");
        memory.FailRead = false; await Task.Delay(1150);
        Assert(state.Load != "—" && !state.MemoryFailed, "读数恢复原位显示真实数据");
        memory.Available = 28UL << 30; var history = store.History.Count; var last = store.Settings.LastCleanupUtc;
        Click("CleanButton"); Click("CleanButton"); await Wait(() => !window.IsBusy);
        Assert(memory.Calls.SequenceEqual(Enum.GetValues<MemoryCommand>()) && store.History.Count == history + 1 && store.Settings.LastCleanupUtc == last, "低占用手动仍执行2/3/4，重复点击无重复任务，预览不更改实机整理时间");
        await Task.Delay(300);
        Assert(store.History[0].Sustained is not null && state.Recent?.Delta.StartsWith("复测可用") == true, "3秒复测优先、30秒观测只补充同条模拟记录");
        memory.Calls.Clear(); memory.FailureCommand = MemoryCommand.ModifiedPages;
        Click("CleanButton"); await Wait(() => !window.IsBusy);
        Assert(memory.Calls.SequenceEqual(new[] { MemoryCommand.WorkingSets, MemoryCommand.ModifiedPages }) && state.Recent?.Outcome == "部分完成", "第二步失败停止第三步，呈现部分完成");
        Assert(state.Recent!.Details.Contains("0xC0000022"), "结果详情保留真实模拟返回码");
        memory.FailureCommand = null; memory.Calls.Clear(); Click("CleanButton"); await Task.Delay(80); Click("CancelButton"); await Wait(() => !window.IsBusy);
        Assert(memory.Calls.SequenceEqual(new[] { MemoryCommand.WorkingSets }) && state.Recent?.Outcome == "已取消", "取消等待当前步骤返回，不执行后续步骤");
        Click("CleanButton"); await Wait(() => !window.IsBusy);
        var blocker = Path.Combine(store.Root, "history.json.tmp");
        Directory.CreateDirectory(blocker);
        try {
            await Task.Delay(300);
            Assert(state.Recent?.Outcome == "步骤完成" && state.Recent.Observation.Contains("未保存"), "持续观测写入失败不改步骤成功，也不永远显示观测中");
        }
        finally { Directory.Delete(blocker); }
        var routine = typeof(MainWindow).GetMethod("RoutineCleanup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Task Automatic() => (Task)routine.Invoke(window, new object[] { "阈值（模拟）", true })!;
        memory.Calls.Clear(); environment.State = new(Fullscreen: true); await Automatic(); Assert(memory.Calls.Count == 0 && state.Automatic.Contains("全屏"), "自动入口在全屏时暂缓");
        environment.State = new(DiskBusy: true); await Automatic(); Assert(memory.Calls.Count == 0, "自动入口磁盘忙碌不执行");
        environment.State = new(DiskBusy: null); await Automatic(); Assert(memory.Calls.Count == 0, "自动入口未知磁盘状态不误认空闲");
        environment.State = new(); memory.Available = 28UL << 30; await Automatic(); Assert(memory.Calls.Count == 0, "自动低于55门槛不新增假整理");
        memory.Available = (32UL << 30) * 450 / 1000; await Automatic(); Assert(memory.Calls.SequenceEqual(Enum.GetValues<MemoryCommand>()), "自动达到55门槛仍执行原版三步");
        memory.Calls.Clear(); store.Settings.AutomationNeedsConfirmation = true; await Automatic(); Assert(memory.Calls.Count == 0, "旧自动规则待确认时安全暂停"); store.Settings.AutomationNeedsConfirmation = false;
        store.Settings.Automation = store.Settings.Automation with { TriggerMode = AutomaticTriggerMode.SystemPressure }; environment.State = new(LowMemory: true); memory.Available = 28UL << 30;
        await Automatic(); Assert(memory.Calls.SequenceEqual(Enum.GetValues<MemoryCommand>()), "系统压力模式不套用百分比门槛");
        store.Settings.Automation = new(); environment.State = new(); state.Settings.Draft.Load(store.Settings.Automation);
        foreach (var name in new[] { "RepositoryLinkButton", "BlogLinkButton", "AuthorLinkButton" }) Click(name);
        Assert(links.Select(u => u.AbsoluteUri).SequenceEqual(new[] { "https://github.com/ice11123/Click-Clean", "https://ice11123.github.io/blog_test2/", "https://github.com/ice11123" }), "三个外链分别打开已确认地址（注入打开器）");
        var icons = new[] { "RepositoryLinkButton", "BlogLinkButton", "AuthorLinkButton" }.Select(n => Descendants(Control<Button>(n)).OfType<System.Windows.Shapes.Path>().Single().Data.ToString()).ToArray();
        Assert(icons.Distinct().Count() == 3, "作者三个入口使用不同矢量图标");
        Command("diagnostics"); await Wait(() => state.Processes.Count > 0); await Settle();
        Assert(state.Diagnostics && !state.SettingsPage, "诊断为独立一级页面");
        Command("history"); await Settle(); var scans = processes.Calls; await Task.Delay(150); Assert(processes.Calls == scans && state.Records, "离开诊断停止进程采样");
        Command("automation-shortcut"); await Settle(); var draft = state.Settings.Draft;
        Assert(state.Settings.Automation && !draft.Dirty, "概览快捷入口直接进入自动规则页");
        Control<CheckBox>("TimerCheck").IsChecked = true; await Settle();
        Assert(draft.TimerEnabled && draft.PercentageEnabled && Control<TextBox>("ThresholdInput").IsEnabled && Control<StackPanel>("ThresholdGroup").IsVisible, "仅定时开启时共用门槛仍可编辑");
        Assert(draft.Dirty && !store.Settings.Automation.TimerEnabled, "编辑规则为草稿，不立即生效");
        window.DraftDecisionOverride = () => MessageBoxResult.Cancel; Command("history"); Assert(state.SettingsPage && draft.Dirty, "继续编辑阻止离开未保存规则");
        window.DraftDecisionOverride = () => MessageBoxResult.No; Command("history"); await Settle(); Assert(state.Records && !draft.Dirty && !store.Settings.Automation.TimerEnabled, "放弃草稿保留已保存规则");
        Command("automation-shortcut"); await Settle(); Control<CheckBox>("TimerCheck").IsChecked = true; Control<TextBox>("ThresholdInput").Text = "abc"; Click("SaveAutomationButton"); await Settle();
        Assert(draft.Dirty && draft.Feedback.Contains("占用门槛") && !store.Settings.Automation.TimerEnabled, "非法活动字段不保存并给出就地错误");
        Control<TextBox>("ThresholdInput").Text = "55"; Click("SaveAutomationButton"); await Settle(); Assert(!draft.Dirty && store.Settings.Automation.TimerEnabled && store.Settings.Automation.ThresholdPercent == 55, "合法草稿保存后生效");
        Control<CheckBox>("TimerCheck").IsChecked = false; Control<TextBox>("ThresholdInput").Text = ""; Click("SaveAutomationButton"); await Settle(); Assert(!draft.Dirty && !store.Settings.Automation.TimerEnabled && store.Settings.Automation.ThresholdPercent == 55, "关闭自动时忽略不适用空字段并保留合法门槛");
        draft.TimerEnabled = true; draft.Mode = 1; await Settle(); Assert(!Control<StackPanel>("ThresholdGroup").IsVisible && draft.SystemPressureEnabled, "系统压力模式隐藏无效百分比字段");
        window.DraftDecisionOverride = () => MessageBoxResult.No; Command("settings"); await Settle();
        Assert(state.Settings.Index && !Descendants(Control<UserControl>("SettingsPane")).OfType<Button>().Any(b => b.Content as string == "诊断"), "设置为四组索引，不再重复诊断入口");
        Command("settings-appearance"); await Settle(); Control<CheckBox>("IslandCheck").IsChecked = false; await Settle(); Assert(!store.Settings.MiniIsland && window.DesktopIsland?.IsVisible == false, "关闭岛即时保存并停止可见入口");
        Control<CheckBox>("IslandCheck").IsChecked = true; await Settle(); window.DesktopIsland?.PreviewReveal(false); Assert(store.Settings.MiniIsland && window.DesktopIsland?.IsVisible == true, "重新开启岛与主窗口共用状态");
        var before = new MemorySnapshot(32UL << 30, (12UL << 30) + (768UL << 20), 21UL << 30, 48UL << 30);
        var sample = new CleanupResult(DateTimeOffset.Now, before, before with { Available = before.Available + (1UL << 30) }, Enum.GetValues<MemoryCommand>().Select(c => new StepResult(c, 0, 0.1)).ToList(), false, null, 3.1) {
            Trigger = "预览（模拟）", FollowUp = before with { Available = before.Available + (768UL << 20) }, FollowUpSeconds = 3, Sustained = before with { Available = before.Available + (512UL << 20) }, SustainedSeconds = 30
        };
        store.Add(sample); memory.Available = before.Available; window.ShowResult(sample, false); Command("overview"); await Task.Delay(1100); await Settle();
        var width = window.Width; var height = window.Height;
        foreach (var theme in new[] { "light", "dark" }) {
            store.Settings.Theme = theme; window.ApplyTheme(); await Settle();
            Assert(window.NativeThemeApplied, "原生标题栏主题应用成功：" + theme);
            Assert(Descendants(window).OfType<TextBlock>().Where(t => t.IsVisible && (t.Style == window.FindResource("PageTitle") || t.Style == window.FindResource("GroupTitle"))).All(t => t.Foreground is SolidColorBrush brush && brush.Color == ColorOf("Text")), "实际首页标题继承主题前景色：" + theme);
            Assert(Contrast(ColorOf("Muted"), ColorOf("Bg")) >= 4.5 && Contrast(ColorOf("AccentText"), ColorOf("Accent")) >= 4.5, "正文和主按钮基础对比度合格：" + theme);
            Command("overview"); update.Set(UpdatePhase.Current, "当前已是最新版本"); await Settle();
            Keyboard.ClearFocus();
            window.RenderTo(Path.Combine(output, theme + "-main.png"));
            var scroller = Control<ScrollViewer>("MainContent");
            Assert(scroller.ScrollableHeight < 1, $"默认窗口读数、操作、成功结果首屏完整：{theme}，内容{scroller.ExtentHeight:0.0}，视口{scroller.ViewportHeight:0.0}");
            var primary = Control<Button>("CleanButton"); var primaryText = Descendants(primary).OfType<TextBlock>().First(t => t.Text == "一键整理");
            Assert(((SolidColorBrush)primaryText.Foreground).Color == ColorOf("AccentText"), "主按钮实际文字使用白色而非正文色：" + theme);
            window.RenderTo(Path.Combine(output, theme + "-main.png"));
            Click("RecentDetailsButton"); await Settle(); Assert(Control<System.Windows.Controls.Primitives.Popup>("DetailsPopup").IsOpen && state.Detail?.Id == sample.Id, "首页打开统一详情：" + theme);
            CaptureElement(Control<Border>("PopupFrame"), Path.Combine(output, theme + "-result.png")); Command("close-popup"); await Settle();
            Command("history"); await Settle(); window.RenderTo(Path.Combine(output, theme + "-history.png"));
            Assert(state.History.Count == store.History.Count, "记录页读取同一历史模型：" + theme);
            Command(state.History[0]); await Settle(); Assert(state.Detail?.Id == state.History[0].Id, "历史与首页复用详情：" + theme); Command("close-popup");
            Command("diagnostics"); await Wait(() => state.Processes.Count > 0); await Settle(); window.RenderTo(Path.Combine(output, theme + "-diagnostics.png"));
            Command(state.Processes[0]); await Settle(); Assert(state.ProcessDetail.Contains("PID 100"), "进程详情提供键盘可达的PID：" + theme); Command("close-popup");
            foreach (var section in new[] { "index", "appearance", "automation", "updates", "about" }) {
                Command(section == "index" ? "settings" : "settings-" + section); await Settle(); window.RenderTo(Path.Combine(output, theme + "-" + section + ".png"));
                Assert(state.SettingsPage && state.Settings.Section == section, "设置目的地与标题一致：" + theme + "/" + section);
                Assert(((SolidColorBrush)Control<TextBlock>("SettingsHeading").Foreground).Color == ColorOf("Text"), "实际设置标题可见且颜色跟随主题：" + theme + "/" + section);
            }
            Command("overview"); update.Set(UpdatePhase.Available, "模拟：发现正式新版本"); await Settle(); var downloads = update.Downloads; var applies = update.Applies;
            Assert(Control<Border>("HomeUpdateBanner").IsVisible && update.Downloads == downloads && update.Applies == applies, "首页醒目提示且未自动下载/应用：" + theme);
            window.RenderTo(Path.Combine(output, theme + "-update-found.png")); Click("HomeUpdateButton"); Assert(state.Downloading && !Control<Button>("HomeUpdateButton").IsEnabled, "更新下载显示真实注入进度并防重复点击：" + theme); await Wait(() => update.Ready);
            Assert(update.Downloads == downloads + 1 && update.Applies == applies && state.UpdateButton == "重启并更新", "下载完成仍等待用户重启确认：" + theme);
            Click("HomeUpdateButton"); await Wait(() => update.Applies == applies + 1); Assert(window.SamplingSeconds == 1, "模拟手动重启返回后恢复采样：" + theme);
            update.Set(UpdatePhase.Failed, "模拟：离线"); await Settle(); Assert(state.UpdateVisible && state.UpdateButton == "重试检查", "失败提示保留对应重试入口：" + theme);
            update.Set(UpdatePhase.Current, "模拟：已是最新");
            Click("CustomButton"); await Settle(); Assert(Control<System.Windows.Controls.Primitives.Popup>("DetailsPopup").IsOpen, "自定义步骤使用局部面板：" + theme);
            for (var n = 0; n < 10; n++) { (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); Assert(Control<Border>("PopupFrame").IsKeyboardFocusWithin, "详情面板Tab焦点不逃逸：" + theme); }
            Command("close-popup"); await Settle(); Assert(Control<Button>("CustomButton").IsKeyboardFocused, "关闭面板返回触发控件焦点：" + theme);
            foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2d }) window.RenderTo(Path.Combine(output, $"{theme}-main-{scale * 100:0}.png"), scale);
            window.Width = 440; window.Height = 540; await Settle(); window.RenderTo(Path.Combine(output, theme + "-small-main.png"));
            Assert(primary.ActualWidth >= 100 && primary.IsVisible && Control<ScrollViewer>("MainContent").ExtentWidth <= Control<ScrollViewer>("MainContent").ViewportWidth + 1, "最小窗口主按钮可达、无横向滚动：" + theme);
            window.Width = width; window.Height = height; Command("settings-automation"); draft.TimerEnabled = true; draft.ThresholdEnabled = true; await Settle(); window.RenderTo(Path.Combine(output, theme + "-automation-expanded.png")); draft.Load(store.Settings.Automation); Command("overview");
            if (window.DesktopIsland is { } island) {
                window.PreviewDockIdle(before); island.PreviewReveal(true); await Settle(); MainWindow.Capture(island, Path.Combine(output, theme + "-island-idle.png"));
                Assert(island.BreathingActive && window.SamplingSeconds == 1, "岛浮出呼吸并使用1秒采样：" + theme);
                window.SetDockOperation("2/3 刷新修改页"); island.IsWorking = true; await Settle(); MainWindow.Capture(island, Path.Combine(output, theme + "-island-working.png")); Assert(island.BackgroundMotionActive, "运行态岛有局部背景动画：" + theme);
                island.IsWorking = false; window.ShowResult(sample); await Settle(); Assert(((TextBlock)island.FindName("StatusLabel")).Text.StartsWith("完成"), "岛展示真实模拟成果：" + theme); await Task.Delay(2100); Assert(((TextBlock)island.FindName("StatusLabel")).Text.StartsWith("内存"), "岛成果2秒后恢复实时读数：" + theme);
                store.Settings.ReduceMotion = true; window.ApplyTheme(); await Settle(); Assert(!island.BreathingActive && !island.BackgroundMotionActive, "减少动画立即停慢循环：" + theme);
                store.Settings.ReduceMotion = false; window.ApplyTheme(); island.PreviewReveal(false); Assert(!island.BreathingActive && !island.BackgroundMotionActive, "岛收起无动画时钟：" + theme);
            }
        }
        window.ShowResult(new(DateTimeOffset.Now, before, null, [new(MemoryCommand.WorkingSets, 0, 0.1)], false, "模拟：快照不可用", 0.1));
        Assert(state.Recent!.Delta.Contains("未知") && state.Recent.Observation.Contains("无记录"), "缺失观测不伪造0或持续观测中");
        window.ShowResult(sample with { FollowUp = before with { Available = before.Available - (256UL << 20) } }); Assert(state.Recent!.Delta.Contains("−"), "负值观测照实呈现");
        Assert(bindingErrors.Messages.Count == 0, "新页面绑定无运行时错误");
        window.Hide(); window.DesktopIsland?.ResumePointerSampling(); Assert(window.SamplingSeconds == 10 && window.DesktopIsland?.PointerPollingActive == true, "主窗口隐藏、岛收起时10秒读数且接近监听有效");
        window.DesktopIsland?.PreviewReveal(true); Assert(window.SamplingSeconds == 1 && window.DesktopIsland?.BreathingActive == true, "主窗口隐藏但岛浮出仍按岛状态采样和呼吸");
        window.DesktopIsland?.Hide(); Assert(window.DesktopIsland?.PointerPollingActive == false && window.SamplingSeconds == 10, "岛完全隐藏停止接近监听");
        File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new { Version = BuildInfo.Version, Checks = checks, BindingErrors = bindingErrors.Messages, Note = "使用隔离配置与模拟系统/更新/进程。离屏倍率不代替真实Windows DPI缩放；不执行实机整理、安装或升级。" }, new JsonSerializerOptions { WriteIndented = true }));
        PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors); window.DisposeResources();
    }
    private static Color ColorOf(string name) => ((SolidColorBrush)Application.Current.Resources[name]).Color;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static double Contrast(Color fg, Color bg) {
        double Linear(byte value) { var x = value / 255d; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        double Light(Color value) => 0.2126 * Linear(value.R) + 0.7152 * Linear(value.G) + 0.0722 * Linear(value.B);
        var a = Light(fg); var b = Light(bg); return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static void CaptureElement(FrameworkElement element, string path) {
        element.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class BindingErrors : TraceListener {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (message?.Contains("Error:") == true) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
