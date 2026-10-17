namespace ClickClean.Core;

public sealed record RoutineCleanupPlan(MemoryCommand[] Commands, string Message)
{
    public bool Skipped => Commands.Length == 0;
}

public static class RoutineCleanupPolicy
{
    public static IReadOnlyList<MemoryCommand> ManualSteps { get; } = Array.AsReadOnly(new[] {
        MemoryCommand.WorkingSets, MemoryCommand.ModifiedPages, MemoryCommand.StandbyCache
    });
    // 产品启发式，不是 Windows 官方健康阈值，也不证明存在可安全回收的内存。
    public const int DefaultPressurePercent = 55;
    public static RoutineCleanupPlan Evaluate(MemorySnapshot? snapshot, int threshold = DefaultPressurePercent, bool percentageMode = false,
        bool? lowMemory = null, bool? highMemory = null)
    {
        if (threshold is < 55 or > 98) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (snapshot is null || snapshot.Total == 0 || snapshot.Total > long.MaxValue || snapshot.Available > snapshot.Total)
            return new([], "内存状态未知，未执行整理。");
        // 百分比模式遵从用户门槛，不与系统低内存通知叠加；收益仍需实际任务验证。
        if (percentageMode) return snapshot.Load >= threshold
            ? new(ManualSteps.ToArray(), "执行全系统三步。")
            : new([], "未达到检查门槛。");
        if (lowMemory is null || highMemory is null || lowMemory == true && highMemory == true)
            return new([], "系统压力状态暂不可用。");
        if (lowMemory != true) return new([], "当前无需整理。");
        return new([MemoryCommand.BackgroundWorkingSets], "整理闲置后台。");
    }
}
