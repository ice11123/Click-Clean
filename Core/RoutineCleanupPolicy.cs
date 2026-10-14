namespace ClickClean.Core;

public sealed record RoutineCleanupPlan(MemoryCommand[] Commands, string Message)
{
    public bool Skipped => Commands.Length == 0;
}

public static class RoutineCleanupPolicy
{
    // 产品启发式，不是 Windows 官方健康阈值，也不证明存在可安全回收的内存。
    public const int DefaultPressurePercent = 55;
    public static RoutineCleanupPlan Evaluate(MemorySnapshot? snapshot, int threshold = DefaultPressurePercent, bool automatic = false,
        bool? lowMemory = null, bool? highMemory = null)
    {
        if (threshold is < 55 or > 98) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (snapshot is null || snapshot.Total == 0 || snapshot.Total > long.MaxValue || snapshot.Available > snapshot.Total)
            return new([], "内存状态未知，未执行整理。");
        // 占用百分比只用于触发检查；待机内存已可用，不能用数字高低证明回收有益。
        if (lowMemory is null || highMemory is null || lowMemory == true && highMemory == true)
            return new([], "系统压力状态暂不可用。");
        if (lowMemory != true) return new([], "当前无需整理。");
        if (automatic && snapshot.Load < threshold)
            return new([], "未达到检查门槛。");
        return new([MemoryCommand.BackgroundWorkingSets], "整理闲置后台。");
    }
}
