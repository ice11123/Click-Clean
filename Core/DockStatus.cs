namespace ClickClean.Core;

public enum MemoryPressureBand { Unknown, Low, Moderate, High }

/// <summary>底部岛状态独立于主窗口、更新提示和内存采样周期。</summary>
public sealed class DockStatus(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private long resultStarted;
    private string? operation, result;
    public MemorySnapshot? Memory { get; private set; }
    public static MemoryPressureBand Band(double? load) => load is null || !double.IsFinite(load.Value) || load < 0 || load > 100
        ? MemoryPressureBand.Unknown : load < 60 ? MemoryPressureBand.Low : load < 85 ? MemoryPressureBand.Moderate : MemoryPressureBand.High;
    public MemoryPressureBand Pressure => Band(Memory is { Total: > 0 } value ? value.Load : null);
    public TimeSpan ResultRemaining {
        get {
            if (result is null) return TimeSpan.Zero;
            var remaining = TimeSpan.FromSeconds(2) - time.GetElapsedTime(resultStarted);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    public string Text => operation ?? (ResultRemaining > TimeSpan.Zero ? result! :
        Pressure == MemoryPressureBand.Unknown ? "内存 —" : $"内存 {Memory!.Load:0}%");
    public void SetMemory(MemorySnapshot? snapshot) => Memory = snapshot is { Total: > 0 } && snapshot.Available <= snapshot.Total ? snapshot : null;
    public void SetOperation(string text) { operation = text; result = null; }
    public void SetResult(string text) { operation = null; result = text; resultStarted = time.GetTimestamp(); }
    public void SetIdle() { operation = result = null; }
}
