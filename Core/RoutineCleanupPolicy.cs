namespace ClickClean.Core;

public sealed record RoutineCleanupPlan(MemoryCommand[] Commands, string Message)
{
    public bool Skipped => Commands.Length == 0;
}

public static class RoutineCleanupPolicy
{
    // 产品启发式，不是 Windows 官方健康阈值，也不证明存在可安全回收的内存。
    public const int DefaultPressurePercent = 55;
    public static RoutineCleanupPlan Evaluate(MemorySnapshot? snapshot, int threshold = DefaultPressurePercent, bool automatic = false)
    {
        if (threshold is < 55 or > 98) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (snapshot is null || snapshot.Total == 0 || snapshot.Total > long.MaxValue || snapshot.Available > snapshot.Total)
            return new([], "内存状态未知，未执行整理。");
        // 手动点击就是执行意图，门槛仅用于无人值守的自动触发。
        return automatic && snapshot.Load < threshold
            ? new([], $"当前占用 {snapshot.Load:0}% 未达到 {threshold}% 整理门槛，未调用系统整理接口。")
            : new([MemoryCommand.WorkingSets, MemoryCommand.ModifiedPages, MemoryCommand.StandbyCache],
                "原版三步：裁剪全系统工作集 → 刷新修改页 → 清理待机缓存；可能短暂卡顿或磁盘忙碌。");
    }
}
