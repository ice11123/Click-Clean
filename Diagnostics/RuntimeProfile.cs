using System.Diagnostics;
using System.Text.Json;
using ClickClean;

namespace ClickClean.Diagnostics;

internal static class RuntimeProfile
{
    public static void Run(string output, bool pointerOnly = false, bool allStates = false)
    {
        Directory.CreateDirectory(output);
        var store = new Store(Path.Combine(output, "isolated-data")); store.Settings.CheckUpdates = false;
        store.Settings.Automation = new(); store.Settings.AutomationNeedsConfirmation = false;
        var app = new App(preview: true); app.InitializeComponent();
        // 不初始化托盘或登录任务；整理接口拒绝调用，仅内存读数使用真实系统API。
        var window = new MainWindow(store, verification: true, memoryApi: new ReadOnlyMemory(), observeInVerification: true); app.MainWindow = window;
        window.Loaded += async (_, _) => {
            var metrics = new List<object>();
            try {
                var phases = allStates ? new[] { ("隐藏窗口与岛收起", 30), ("主窗口空闲", 30), ("真实鼠标监听", 30), ("底部岛浮出", 30), ("诊断页面", 30) } :
                    pointerOnly ? new[] { ("真实鼠标监听", 60), ("底部岛浮出", 60) } : new[] { ("隐藏窗口与岛收起", 600), ("主窗口空闲", 60), ("底部岛浮出", 60), ("隐藏且关闭底部岛", 60) };
                foreach (var phase in phases) {
                    window.DesktopIsland?.ResumePointerSampling();
                    window.Navigate(phase.Item1 == "诊断页面" ? "diagnostics" : "overview");
                    if (phase.Item1 is "主窗口空闲" or "诊断页面") window.Show(); else window.Hide();
                    if (phase.Item1 == "底部岛浮出") window.DesktopIsland?.PreviewReveal(true);
                    if (phase.Item1 == "隐藏且关闭底部岛") window.DesktopIsland?.Hide();
                    using var process = Process.GetCurrentProcess(); process.Refresh(); var cpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
                    var samples = new List<object>();
                    for (var elapsed = 0; elapsed < phase.Item2; elapsed += 30) {
                        await Task.Delay(30000); process.Refresh();
                        samples.Add(new { Seconds = clock.Elapsed.TotalSeconds, WorkingSetMiB = process.WorkingSet64 / 1048576d, PrivateMiB = process.PrivateMemorySize64 / 1048576d });
                    }
                    process.Refresh();
                    metrics.Add(new { Phase = phase.Item1, Seconds = clock.Elapsed.TotalSeconds, CpuMachinePercent = (process.TotalProcessorTime - cpu).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100,
                        Samples = samples, MemorySamplingSeconds = window.SamplingSeconds, PointerSamplingMilliseconds = window.DesktopIsland?.PointerPollingMilliseconds,
                        PointerListening = window.DesktopIsland?.PointerPollingActive, IslandRevealed = window.DesktopIsland?.IsRevealed });
                    File.WriteAllText(Path.Combine(output, "metrics.json"), JsonSerializer.Serialize(new { Version = BuildInfo.Version, Metrics = metrics, Note = "隔离诊断宿主使用正式UI代码，内存与进程来自真实只读API；无托盘、登录任务、整理或更新，不是正式安装进程测量。" }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); }
            finally { window.DisposeResources(); app.Shutdown(); }
        };
        app.Run(window);
    }
    private sealed class ReadOnlyMemory : IMemoryApi, IMemoryPerformanceApi, IDisposable
    {
        private readonly WindowsMemoryApi native = new();
        public MemorySnapshot Read() => native.Read();
        public MemoryPressureSnapshot ReadPressure() => native.ReadPressure();
        public BackgroundScanSummary ObserveBackgroundProcesses() => native.ObserveBackgroundProcesses();
        public BackgroundCleanupSummary? LastBackgroundCleanup => null;
        public void Dispose() => native.Dispose();
        public IDisposable EnablePrivilege() => throw new InvalidOperationException("性能测量不允许整理系统内存。");
        public int Execute(MemoryCommand command) => throw new InvalidOperationException("性能测量不允许整理系统内存。");
    }
}
