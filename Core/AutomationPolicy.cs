namespace ClickClean.Core;

public enum AutomaticTriggerMode { Percentage, SystemPressure }
public sealed record AutomationEnvironment(bool? LowMemory = null, bool Fullscreen = false, bool? DiskBusy = false, bool? HighMemory = null);

public sealed record AutomationSettings
{
    public AutomaticTriggerMode TriggerMode { get; init; } = AutomaticTriggerMode.SystemPressure;
    public bool PauseFullscreen { get; init; } = true;
    public bool PauseDiskBusy { get; init; } = true;
    public bool ThresholdEnabled { get; init; }
    public int ThresholdPercent { get; init; } = RoutineCleanupPolicy.DefaultPressurePercent;
    public int SustainSeconds { get; init; } = 60;
    public int CooldownMinutes { get; init; } = 30;
    public bool TimerEnabled { get; init; }
    public int IntervalMinutes { get; init; } = 60;
    public bool FullCleanup { get; init; }
    public AutomationSettings Validated() => this with {
        TriggerMode = Enum.IsDefined(TriggerMode) ? TriggerMode : AutomaticTriggerMode.SystemPressure,
        ThresholdPercent = Math.Clamp(ThresholdPercent, 55, 98),
        // 保留旧字段的读取兼容性；任何自动触发都不能开启全系统三步。
        FullCleanup = false,
        SustainSeconds = Math.Clamp(SustainSeconds, 15, 600),
        CooldownMinutes = Math.Clamp(CooldownMinutes, 5, 240),
        IntervalMinutes = Math.Clamp(IntervalMinutes, 15, 1440)
    };
}

// 单调时钟负责持续时间，墙上时钟仅用于展示；所有触发回到同一执行入口。
public sealed class AutomationPolicy(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private AutomationSettings settings = new();
    private long? highSince, lastSample, lastAttempt;
    private long timerAnchor;
    private bool armed = true;
    private bool timerPending;
    public string? DeferredReason { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public bool Paused => ConsecutiveFailures >= 3;
    public TimeSpan? TimerRemaining => settings.TimerEnabled
        ? TimeSpan.FromSeconds(timerPending ? 0 : Math.Max(0, settings.IntervalMinutes * 60 - Elapsed(timerAnchor, clock.GetTimestamp()))) : null;
    private double Elapsed(long start, long end) => clock.GetElapsedTime(start, end).TotalSeconds;
    public void Configure(AutomationSettings value, bool recentCleanup = false)
    {
        settings = value.Validated(); highSince = lastSample = null; armed = true;
        ConsecutiveFailures = 0; timerAnchor = clock.GetTimestamp();
        timerPending = false; DeferredReason = null;
        if (recentCleanup) lastAttempt = timerAnchor;
    }
    public string? Evaluate(double? load, bool busy, AutomationEnvironment? environment = null)
    {
        environment ??= new();
        DeferredReason = null;
        var now = clock.GetTimestamp();
        if (lastSample is { } previous && (now < previous || Elapsed(previous, now) > 120))
        {
            // 休眠恢复不补跑定时任务，不把无采样时间计入持续超限。
            highSince = null; timerAnchor = now; timerPending = false;
        }
        lastSample = now;
        if (load is null || !double.IsFinite(load.Value) || load < 0 || load > 100)
        { highSince = null; if (Elapsed(timerAnchor, now) >= settings.IntervalMinutes * 60) timerAnchor = now; return null; }
        var systemMode = settings.TriggerMode == AutomaticTriggerMode.SystemPressure;
        var highPressure = systemMode ? environment.LowMemory == true : load >= settings.ThresholdPercent;
        if (!highPressure) highSince = null;
        if (systemMode ? environment.LowMemory == false : load <= settings.ThresholdPercent - 5) armed = true;
        if (settings.ThresholdEnabled && highPressure && armed) highSince ??= now;
        var timed = settings.TimerEnabled && (timerPending || Elapsed(timerAnchor, now) >= settings.IntervalMinutes * 60);
        if (timed)
        {
            // 忙碌期间不积压补跑；只有场景避让会保留一个待执行周期。
            timerPending = !busy;
            if (busy) timerAnchor = now;
        }
        if ((settings.ThresholdEnabled || settings.TimerEnabled) && !busy && !Paused)
        {
            DeferredReason = settings.PauseFullscreen && environment.Fullscreen ? "全屏应用正在使用" :
                settings.PauseDiskBusy && environment.DiskBusy is null ? "磁盘状态暂不可用" :
                settings.PauseDiskBusy && environment.DiskBusy == true ? "磁盘繁忙" :
                systemMode && environment.LowMemory is null ? "系统压力状态暂不可用" : null;
            if (DeferredReason is not null) { highSince = null; return null; }
        }
        if (busy || Paused || lastAttempt is { } attempted && Elapsed(attempted, now) < settings.CooldownMinutes * 60) return null;
        var threshold = settings.ThresholdEnabled && armed && highSince is { } high && Elapsed(high, now) >= settings.SustainSeconds;
        if (!timed && !threshold) return null;
        if (threshold) armed = false;
        highSince = null; lastAttempt = now; timerAnchor = now; timerPending = false;
        var reason = systemMode ? "系统压力" : "阈值";
        return threshold && timed ? reason + " + 定时" : threshold ? reason : "定时";
    }
    public void RecordCompletion(bool success, bool automatic)
    {
        lastAttempt = timerAnchor = clock.GetTimestamp(); highSince = null; timerPending = false;
        DeferredReason = null;
        if (automatic) ConsecutiveFailures = success ? 0 : ConsecutiveFailures + 1;
    }
    public void RecordSkipped()
    {
        // 跳过推进周期与冷却，但不计失败，也不标记为一次真实整理。
        lastAttempt = timerAnchor = clock.GetTimestamp(); highSince = null; timerPending = false;
        DeferredReason = null;
    }
    public void DeferAttempt(bool timed, string reason)
    {
        // 执行入口再次检查发现条件变化：未调用系统，不能当作完成或失败。
        highSince = lastAttempt = null; armed = true; timerPending |= timed; DeferredReason = reason;
    }
}
