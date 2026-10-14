using System.Diagnostics;
using System.Windows.Threading;

namespace ClickClean;

/// <summary>应用级整理生命周期；页面、托盘和岛共用执行门闩与取消语义。</summary>
internal sealed class CleanupCoordinator(Store store, IMemoryApi memory, AutomationPolicy automation, Dispatcher dispatcher, bool preview) : IDisposable
{
    private readonly MemoryEngine engine = new(memory);
    private readonly Stopwatch operationClock = new();
    private CancellationTokenSource? cancellation, observation;
    private CleanupResult? latest;
    private bool disposed, activeAutomatic, observing;
    private string currentStep = "准备整理";
    public bool Busy => cancellation is not null || engine.IsRunning;
    public Guid? PendingResult => observation is null ? null : latest?.Id;
    public bool CanCancel { get; private set; }
    public string CancelLabel => observing ? "结束复测" : "取消后续";
    public string Status { get; private set; } = "就绪";
    public event Action? Changed;
    public event Action<string>? DockOperation;
    public event Action<string>? DockResult;
    public event Action<CleanupResult, bool>? ResultAvailable;

    private void Report(string text, string? dock = null)
    {
        Status = text; if (dock is not null) DockOperation?.Invoke(dock); Changed?.Invoke();
    }
    public void RefreshTimeout()
    {
        if (Busy && !observing && operationClock.Elapsed.TotalSeconds >= 60)
            Report("仍在等待系统返回", "等待 · " + currentStep);
    }
    public async Task RunRoutineAsync(string trigger, bool automatic, AutomationEnvironment environment)
    {
        if (disposed || Busy || automatic && store.Settings.AutomationNeedsConfirmation) return;
        var a = store.Settings.Automation;
        MemorySnapshot? snapshot = null;
        bool? low = environment.LowMemory, high = null;
        try {
            if (memory is IMemoryPerformanceApi performance) { var pressure = performance.ReadPressure(); snapshot = pressure.Memory; low = pressure.LowMemory; high = pressure.HighMemory; }
            else snapshot = memory.Read();
        } catch (Exception ex) { low = high = null; store.Log("压力检查失败：" + ex.Message); }
        var plan = RoutineCleanupPolicy.Evaluate(snapshot, a.ThresholdPercent, automatic && a.TriggerMode == AutomaticTriggerMode.Percentage, low, high);
        if (plan.Skipped)
        {
            Report(plan.Message); DockResult?.Invoke(snapshot is null || low is null || high is null || low == true && high == true ? "状态暂不可用" : "无需整理");
            if (automatic) automation.RecordSkipped();
            return;
        }
        // 快捷操作保护正在进行的任务；高级手动操作另有显式风险确认。
        var deferred = environment.Fullscreen ? "全屏应用正在使用" :
            a.PauseDiskBusy && environment.DiskBusy != false ? "磁盘繁忙或状态暂不可用" : null;
        if (deferred is not null) {
            if (automatic) automation.DeferAttempt(trigger.Contains("定时", StringComparison.Ordinal), deferred);
            Report(deferred, "暂缓整理");
            return;
        }
        await RunAsync(plan.Commands, trigger, automatic);
    }
    public async Task RunAsync(IEnumerable<MemoryCommand> commands, string trigger = "手动", bool automatic = false)
    {
        if (disposed || Busy) return;
        var selected = commands.Distinct().OrderBy(c => (int)c).ToArray();
        if (selected.Length == 0) { Report("请至少选择一个整理项目。"); return; }
        StopObservation("新整理已开始，旧持续观测停止。");
        cancellation = new(); activeAutomatic = automatic; CanCancel = true; observing = false;
        operationClock.Restart(); Report("正在准备整理…", "准备整理…");
        var success = false; var executed = false; var skipped = false;
        try
        {
            var result = await engine.RunAsync(selected, command => dispatcher.Invoke(() => {
                currentStep = Labels.Name(command);
                var stepText = $"{Array.IndexOf(selected, command) + 1}/{selected.Length} · {currentStep}";
                Report(stepText, stepText);
            }), cancellation.Token, preview ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromSeconds(3), () => dispatcher.Invoke(() => {
                observing = true; Report("正在复测…", "复测内存…");
            }));
            result = result with { Trigger = preview ? "预览（模拟）" : trigger };
            latest = result; success = result.Success;
            executed = result.BackgroundCleanup is { } background ? background.Attempted > 0 : result.Steps.Count > 0;
            skipped = result.BackgroundCleanup is { Attempted: 0 } && result.Error is null && !result.Cancelled;
            try { store.Add(result); } catch (Exception ex) { store.Log("记录保存失败：" + ex); }
            ResultAvailable?.Invoke(result, true);
            Report(skipped ? "无需整理" : result.Success ? result.ObservationError ?? "步骤已完成" : result.Error ?? "已取消尚未开始的步骤。");
            if (executed && !result.Cancelled) BeginObservation(result);
        }
        catch (Exception ex) { Report("整理失败：" + ex.Message); store.Log(ex.ToString()); DockResult?.Invoke("整理失败"); }
        finally
        {
            if (skipped) automation.RecordSkipped(); else automation.RecordCompletion(success, automatic);
            if (!preview && executed) {
                store.Settings.LastCleanupUtc = DateTimeOffset.UtcNow;
                try { store.SaveSettings(); } catch (Exception ex) { store.Log("整理时间保存失败：" + ex); }
            }
            operationClock.Stop(); cancellation.Dispose(); cancellation = null; activeAutomatic = observing = CanCancel = false;
            Changed?.Invoke();
        }
    }
    public void Cancel()
    {
        if (!Busy || !CanCancel) return;
        cancellation?.Cancel(); CanCancel = false;
        Report(observing ? "已请求结束复测，保留已执行步骤。" : "已请求取消，等待当前步骤返回。", observing ? "结束复测…" : "取消后续 · 等待返回");
    }
    public void CancelAutomaticIfDisabled()
    { if (activeAutomatic && !store.Settings.Automation.TimerEnabled && !store.Settings.Automation.ThresholdEnabled) Cancel(); }
    private async void BeginObservation(CleanupResult result)
    {
        var source = new CancellationTokenSource(); observation = source;
        try {
            var delay = preview ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(Math.Max(0, 30 - (result.FollowUpSeconds ?? 0)));
            var observed = await MemoryObservations.ObserveAsync(result, memory, delay, source.Token);
            if (!disposed && !source.IsCancellationRequested && ReferenceEquals(observation, source)) {
                store.ReplaceObservation(observed);
                if (latest?.Id == result.Id) { latest = observed; ResultAvailable?.Invoke(observed, false); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) {
            store.Log("持续观测记录失败：" + ex);
            if (!disposed && !source.IsCancellationRequested && ReferenceEquals(observation, source) && latest?.Id == result.Id) {
                latest = result with { SustainedError = "持续观测记录未保存：" + ex.Message }; ResultAvailable?.Invoke(latest, false);
            }
        }
        finally {
            var current = ReferenceEquals(observation, source); if (current) observation = null; source.Dispose();
            if (current && !disposed) Changed?.Invoke();
        }
    }
    public void StopObservation(string? reason = null)
    {
        observation?.Cancel(); observation = null;
        if (reason is not null && latest is { Sustained: null, SustainedError: null } value) {
            latest = value with { SustainedError = reason };
            try { store.ReplaceObservation(latest); ResultAvailable?.Invoke(latest, false); } catch (Exception ex) { store.Log(ex.ToString()); }
        }
    }
    public void Dispose() { disposed = true; StopObservation(); cancellation?.Cancel(); }
}
