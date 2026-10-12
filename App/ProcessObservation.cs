using System.Diagnostics;
using System.Windows.Threading;

namespace ClickClean;

internal sealed class ProcessObservation(IProcessMemoryReader reader) : IDisposable
{
    private readonly ProcessGrowthTracker tracker = new();
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private bool active, visible, reading, disposed;
    private int generation;
    public int Samples { get; private set; }
    public event Action<IReadOnlyList<ProcessGrowthRow>, string>? Changed;
    public event Action<string>? Failed;
    public void Initialize() => timer.Tick += async (_, _) => await Read();
    public void SetActive(bool value, bool windowVisible)
    {
        if (active != value) {
            active = value; generation++;
            if (value) { tracker.Reset(); clock.Restart(); }
        }
        SetVisible(windowVisible);
    }
    public void SetVisible(bool value)
    {
        var resume = value && active && (!visible || !timer.IsEnabled);
        visible = value;
        if (active && visible && !disposed) { timer.Start(); if (resume) _ = Read(); }
        else { timer.Stop(); generation++; }
    }
    public async Task Read()
    {
        if (!active || !visible || reading || disposed) return;
        reading = true; var expected = generation;
        try {
            var batch = await Task.Run(reader.Read);
            if (disposed || !active || !visible || expected != generation) return;
            Samples++; Changed?.Invoke(tracker.Compare(batch.Samples), $"本次观察 {clock.Elapsed.TotalSeconds:0} 秒 · 读取 {batch.Samples.Count} 个进程 · {batch.Unavailable} 个暂不可读");
        }
        catch (Exception ex) { if (!disposed && expected == generation) Failed?.Invoke("进程读数暂不可用：" + ex.Message); }
        finally { reading = false; }
    }
    public void Dispose() { disposed = true; generation++; timer.Stop(); }
}
