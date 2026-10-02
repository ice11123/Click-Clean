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
        if (args.Length == 3 && args[0] == "--verify-updates") { RunUpdateCheck(() => UpdateVerification.Check(args[1], args[2]), Path.Combine(args[2], "failure.txt")); return; }
        if (args.Length == 4 && args[0] == "--apply-update-fixture") { RunUpdateCheck(() => UpdateVerification.ApplyFixture(args[1], args[2], args[3]), args[3]); return; }
        var output = args.Length == 2 && args[0] == "--capture" ? Path.GetFullPath(args[1]) : null;
        var root = output is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickCleanPreview") : Path.Combine(output, "preview-data");
        Directory.CreateDirectory(root);
        var store = new Store(root); var app = new App(preview: true); app.InitializeComponent();
        var window = new MainWindow(store, verification: true); app.MainWindow = window;
        window.Loaded += async (_, _) => {
            window.ShowIsland();
            if (output is null) return;
            try { await Capture(window, store, output); app.Shutdown(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); app.Shutdown(1); }
        };
        app.Run(window);
    }
    private static void RunUpdateCheck(Func<Task> action, string failurePath)
    {
        try { action().GetAwaiter().GetResult(); }
        catch (Exception ex) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(failurePath))!); File.WriteAllText(failurePath, ex.ToString()); Environment.ExitCode = 1; }
    }
    private static async Task Capture(MainWindow window, Store store, string output)
    {
        Directory.CreateDirectory(output); var checks = new List<string>();
        void Assert(bool value, string text) { if (!value) throw new InvalidOperationException(text); checks.Add(text); }
        var before = new MemorySnapshot(32UL << 30, 12UL << 30, 21UL << 30, 48UL << 30);
        window.ShowResult(new(DateTimeOffset.Now, before, before with { Available = 13UL << 30 }, [new(MemoryCommand.StandbyCache, 0, 0.1)], false, null, 0.1) { Trigger = "预览（模拟）" });
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        foreach (var theme in new[] { "light", "dark" })
        {
            store.Settings.Theme = theme; window.ApplyTheme(); window.CloseOverlay();
            window.SetIsland($"即清 · 内存 {before.Load:0}%", "", false, false); window.RenderTo(Path.Combine(output, theme + "-main.png"));
            window.SetIsland("正在刷新修改页", "阈值整理 · 等待系统返回", true, false); window.RenderTo(Path.Combine(output, theme + "-running.png"));
            if (window.DesktopIsland is { } mini) { mini.UpdateLayout(); MainWindow.Capture(mini, Path.Combine(output, theme + "-mini.png")); }
            foreach (var section in new[] { "appearance", "automation", "updates", "about" })
            {
                window.OpenSettings(); window.ShowSettingsSection(section);
                foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2d }) window.RenderTo(Path.Combine(output, $"{theme}-{section}-{scale * 100:0}.png"), scale);
            }
            window.OpenHistory(); window.RenderTo(Path.Combine(output, theme + "-history.png"));
            window.Width = 440; window.Height = 560; window.OpenSettings(); window.ShowSettingsSection("automation");
            window.RenderTo(Path.Combine(output, theme + "-small-automation.png")); window.Width = 560; window.Height = 740;
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
        window.DesktopIsland?.Hide(); window.Hide(); Assert(window.SamplingSeconds == 10, "仅托盘时降低采样频率");
        await Task.Delay(10000);
        var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
        await Task.Delay(30000); process.Refresh();
        File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new {
            Checks = checks, WorkingSetMiB = process.WorkingSet64 / 1048576d, PrivateMiB = process.PrivateMemorySize64 / 1048576d,
            HiddenCpuMachinePercent = (process.TotalProcessorTime - cpu).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100,
            SampleSeconds = clock.Elapsed.TotalSeconds, Note = "截图倍率属于离屏渲染检查，不代替真实 Windows 显示缩放验收。"
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.DisposeResources();
    }
    private static void SystemCheck(string output)
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
