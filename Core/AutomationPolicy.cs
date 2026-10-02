namespace ClickClean.Core;

public sealed record AutomationSettings
{
    public bool ThresholdEnabled { get; init; }
    public int ThresholdPercent { get; init; } = 85;
    public int SustainSeconds { get; init; } = 60;
    public int CooldownMinutes { get; init; } = 30;
    public bool TimerEnabled { get; init; }
    public int IntervalMinutes { get; init; } = 60;
    public bool FullCleanup { get; init; }
    public AutomationSettings Validated() => this with {
        ThresholdPercent = Math.Clamp(ThresholdPercent, 60, 98),
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
    public int ConsecutiveFailures { get; private set; }
    public bool Paused => ConsecutiveFailures >= 3;
    public TimeSpan? TimerRemaining => settings.TimerEnabled
        ? TimeSpan.FromSeconds(Math.Max(0, settings.IntervalMinutes * 60 - Elapsed(timerAnchor, clock.GetTimestamp()))) : null;
    private double Elapsed(long start, long end) => clock.GetElapsedTime(start, end).TotalSeconds;
    public void Configure(AutomationSettings value, bool recentCleanup = false)
    {
        settings = value.Validated(); highSince = lastSample = null; armed = true;
        ConsecutiveFailures = 0; timerAnchor = clock.GetTimestamp();
        if (recentCleanup) lastAttempt = timerAnchor;
    }
    public string? Evaluate(double? load, bool busy)
    {
        var now = clock.GetTimestamp();
        if (lastSample is { } previous && (now < previous || Elapsed(previous, now) > 120))
        {
            // 休眠恢复不补跑定时任务，不把无采样时间计入持续超限。
            highSince = null; timerAnchor = now;
        }
        lastSample = now;
        if (load is null || !double.IsFinite(load.Value) || load < 0 || load > 100)
        { highSince = null; if (Elapsed(timerAnchor, now) >= settings.IntervalMinutes * 60) timerAnchor = now; return null; }
        if (load < settings.ThresholdPercent) highSince = null;
        if (load <= settings.ThresholdPercent - 5) armed = true;
        if (settings.ThresholdEnabled && load >= settings.ThresholdPercent && armed) highSince ??= now;
        var timed = settings.TimerEnabled && Elapsed(timerAnchor, now) >= settings.IntervalMinutes * 60;
        if (timed) timerAnchor = now;
        if (busy || Paused || lastAttempt is { } attempted && Elapsed(attempted, now) < settings.CooldownMinutes * 60) return null;
        var threshold = settings.ThresholdEnabled && armed && highSince is { } high && Elapsed(high, now) >= settings.SustainSeconds;
        if (!timed && !threshold) return null;
        if (threshold) armed = false;
        highSince = null; lastAttempt = now;
        return threshold && timed ? "阈值 + 定时" : threshold ? "阈值" : "定时";
    }
    public void RecordCompletion(bool success, bool automatic)
    {
        lastAttempt = timerAnchor = clock.GetTimestamp(); highSince = null;
        if (automatic) ConsecutiveFailures = success ? 0 : ConsecutiveFailures + 1;
    }
}
