namespace ClickClean.Core;

public sealed record RoutineCleanupPlan(MemoryCommand[] Commands, string Message)
{
    public bool Skipped => Commands.Length == 0;
}

public static class RoutineCleanupPolicy
{
    // 产品启发式，不是 Windows 官方健康阈值，也不证明存在可安全回收的内存。
    public const int DefaultPressurePercent = 85;
    public static RoutineCleanupPlan Evaluate(MemorySnapshot? snapshot, int threshold = DefaultPressurePercent)
    {
        if (threshold is < 60 or > 98) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (snapshot is null || snapshot.Total == 0 || snapshot.Total > long.MaxValue || snapshot.Available > snapshot.Total)
            return new([], "内存状态未知，未执行整理。");
        return snapshot.Load < threshold
            ? new([], $"当前占用 {snapshot.Load:0}% 未达到 {threshold}% 整理门槛，未调用系统整理接口。")
            : new([MemoryCommand.StandbyCache], "尝试整理待机缓存；缓存本已计入可用内存，不保证降低占用率或提速。");
    }
}
