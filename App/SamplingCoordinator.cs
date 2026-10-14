using System.Diagnostics;
using System.Windows.Threading;

namespace ClickClean;

/// <summary>同一份整机读数供所有入口使用；可见性只改变频率，不创建重复采样器。</summary>
internal sealed class SamplingCoordinator : IDisposable
{
    private readonly Store store;
    private readonly IMemoryApi memory;
    private readonly CleanupCoordinator cleanup;
    private readonly AutomationPolicy automation;
    private readonly IApplicationUpdates updates;
    private readonly IAutomationEnvironmentReader? environmentReader;
    private readonly bool preview;
    private readonly Func<bool> applying;
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private bool ticking, disposed, failed;
    private double lastEnvironment = -10, lastTheme = -30, lastBackground = -15, nextUpdate = 21600;
    public AutomationEnvironment Environment { get; private set; } = new();
    public MemoryPressureSnapshot? Pressure { get; private set; }
    public MemorySnapshot? Snapshot { get; private set; }
    public string Error { get; private set; } = "";
    public double Seconds => timer.Interval.TotalSeconds;
    public event Action<MemorySnapshot?, string>? MemoryChanged;
    public event Action<MemoryPressureSnapshot?>? PressureChanged;
    public event Action? AutomaticChanged;
    public event Action? ThemePoll;
    public SamplingCoordinator(Store store, IMemoryApi memory, CleanupCoordinator cleanup, AutomationPolicy automation, IApplicationUpdates updates,
        IAutomationEnvironmentReader? environmentReader, bool preview, Func<bool> applying)
    {
        this.store = store; this.memory = memory; this.cleanup = cleanup; this.automation = automation; this.updates = updates;
        this.environmentReader = environmentReader; this.preview = preview; this.applying = applying;
        timer.Interval = TimeSpan.FromSeconds(1); timer.Tick += async (_, _) => await Tick();
    }
    public void Start() => timer.Start();
    public void Stop() => timer.Stop();
    public void SetVisible(bool mainVisible, bool islandRevealed) => timer.Interval = TimeSpan.FromSeconds(mainVisible || islandRevealed ? 1 : 10);
    public MemorySnapshot? Read()
    {
        if (disposed) return null;
        try {
            var pressure = memory is IMemoryPerformanceApi performance ? performance.ReadPressure() : null;
            var value = pressure?.Memory ?? memory.Read();
            if (value.Total == 0 || value.Total > long.MaxValue || value.Available > value.Total || value.Commit > long.MaxValue) throw new InvalidDataException("系统内存读数无效。");
            Pressure = pressure ?? new(value, Environment.LowMemory, null); Snapshot = value; Error = "";
            failed = false; PressureChanged?.Invoke(Pressure); MemoryChanged?.Invoke(value, ""); return value;
        }
        catch (Exception ex) {
            if (!failed) store.Log("实时内存读取失败：" + ex); failed = true;
            Pressure = null; Snapshot = null; Error = "内存读数暂不可用：" + ex.Message;
            PressureChanged?.Invoke(null); MemoryChanged?.Invoke(null, Error); return null;
        }
    }
    public async Task<AutomationEnvironment> ReadEnvironment()
    {
        if (environmentReader is null) return Environment;
        try { var value = await Task.Run(environmentReader.Read); if (!disposed) Environment = value; }
        catch (Exception ex) { Environment = new(DiskBusy: null); store.Log("自动避让读数失败：" + ex); }
        return Environment;
    }
    private async Task Tick()
    {
        if (ticking || disposed || applying()) return;
        ticking = true;
        try {
            if (clock.Elapsed.TotalSeconds - lastTheme >= 30) { ThemePoll?.Invoke(); lastTheme = clock.Elapsed.TotalSeconds; }
            var snapshot = Read(); cleanup.RefreshTimeout();
            if (!preview) {
                // 闲置基线独立于自动开关；低频只读观察，不把一次CPU静止当作可整理证据。
                if (!cleanup.Busy && memory is IMemoryPerformanceApi performance && clock.Elapsed.TotalSeconds - lastBackground >= 15) {
                    lastBackground = clock.Elapsed.TotalSeconds;
                    try { await Task.Run(performance.ObserveBackgroundProcesses); }
                    catch (Exception ex) { store.Log("后台闲置观察不可用：" + ex.Message); }
                    if (disposed || applying()) return;
                }
                var enabled = store.Settings.Automation.ThresholdEnabled || store.Settings.Automation.TimerEnabled;
                if ((Pressure?.LowMemory == true || enabled && !store.Settings.AutomationNeedsConfirmation) && clock.Elapsed.TotalSeconds - lastEnvironment >= 5) {
                    lastEnvironment = clock.Elapsed.TotalSeconds; await ReadEnvironment(); if (disposed || applying()) return;
                }
                var currentEnvironment = Pressure is { } value ? Environment with { LowMemory = value.LowMemory } : Environment;
                var reason = automation.Evaluate(snapshot?.Load, cleanup.Busy || updates.Busy || store.Settings.AutomationNeedsConfirmation, currentEnvironment);
                if (reason is not null) {
                    var current = currentEnvironment with { Fullscreen = ForegroundActivity.IsFullscreen() };
                    _ = cleanup.RunRoutineAsync(reason, true, current);
                }
                if (store.Settings.CheckUpdates && clock.Elapsed.TotalSeconds >= nextUpdate) {
                    nextUpdate = clock.Elapsed.TotalSeconds + 21600; _ = updates.CheckAsync();
                }
            }
            AutomaticChanged?.Invoke();
        }
        catch (Exception ex) { store.Log("采样循环异常：" + ex); }
        finally { ticking = false; }
    }
    public void Dispose() { disposed = true; timer.Stop(); environmentReader?.Dispose(); }
}
