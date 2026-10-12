using System.Diagnostics;
using System.Text.Json;
using ClickClean;

namespace ClickClean.Diagnostics;

internal static class RuntimeProfile
{
    public static void Run(string output, bool pointerOnly = false)
    {
        Directory.CreateDirectory(output);
        var store = new Store(Path.Combine(output, "isolated-data")); store.Settings.CheckUpdates = false;
        store.Settings.Automation = new(); store.Settings.AutomationNeedsConfirmation = false;
        var app = new App(preview: true); app.InitializeComponent();
        // 使用正式MainWindow代码和真实只读系统API，独立数据，不取得单实例锁或执行整理。
        var window = new MainWindow(store, verification: false); app.MainWindow = window;
        window.Loaded += async (_, _) => {
            var metrics = new List<object>();
            try {
                var phases = pointerOnly ? new[] { ("真实鼠标监听", 60), ("底部岛浮出", 60) } : new[] { ("隐藏窗口与岛收起", 600), ("主窗口空闲", 60), ("底部岛浮出", 60), ("隐藏且关闭底部岛", 60) };
                foreach (var phase in phases) {
                    window.DesktopIsland?.ResumePointerSampling();
                    if (phase.Item1 == "主窗口空闲") window.Show(); else window.Hide();
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
                    File.WriteAllText(Path.Combine(output, "metrics.json"), JsonSerializer.Serialize(new { Version = BuildInfo.Version, Metrics = metrics, Note = "独立诊断宿主使用正式UI代码和真实只读API；不是正式安装进程测量，无截图分配、无整理或更新操作。" }, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); }
            finally { window.DisposeResources(); app.Shutdown(); }
        };
        app.Run(window);
    }
}
