using System.Diagnostics;

namespace ClickClean.Core;

public enum MemoryCommand { WorkingSets = 2, ModifiedPages = 3, StandbyCache = 4, BackgroundWorkingSets = 100 }
public record MemorySnapshot(ulong Total, ulong Available, ulong Commit, ulong CommitLimit)
{
    public ulong? SystemCache { get; init; }
    public ulong? KernelPaged { get; init; }
    public ulong? KernelNonpaged { get; init; }
    public ulong Used => Total >= Available ? Total - Available : 0;
    public double Load => Total == 0 ? 0 : 100d * Used / Total;
}
public record StepResult(MemoryCommand Command, int Status, double Seconds)
{
    public bool Success => Status >= 0;
    public string StatusHex => $"0x{unchecked((uint)Status):X8}";
}
public record CleanupResult(DateTimeOffset Time, MemorySnapshot Before, MemorySnapshot? After,
    List<StepResult> Steps, bool Cancelled, string? Error, double Seconds)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public BackgroundCleanupSummary? BackgroundCleanup { get; init; }
    public long? AvailableChange => After is null ? null : checked((long)After.Available - (long)Before.Available);
    public string Trigger { get; init; } = "手动";
    public string? ObservationError { get; init; }
    public MemorySnapshot? FollowUp { get; init; }
    public double? FollowUpSeconds { get; init; }
    public MemorySnapshot? Sustained { get; init; }
    public double? SustainedSeconds { get; init; }
    public string? SustainedError { get; init; }
    public long? SustainedChange => Sustained is null ? null : checked((long)Sustained.Available - (long)Before.Available);
    public long? PreferredChange => FollowUp is null ? AvailableChange : FollowUpChange;
    public long? FollowUpChange => FollowUp is null ? null : checked((long)FollowUp.Available - (long)Before.Available);
    public long? CommitChange => After is null ? null : checked((long)After.Commit - (long)Before.Commit);
    public bool Success => Error is null && !Cancelled && Steps.Count > 0 && Steps.All(s => s.Success) &&
        (!Steps.Any(s => s.Command == MemoryCommand.BackgroundWorkingSets) || BackgroundCleanup is not null) &&
        (BackgroundCleanup is null || BackgroundCleanup.Completed > 0 && BackgroundCleanup.Failed == 0);
}
public interface IMemoryApi
{
    MemorySnapshot Read();
    IDisposable EnablePrivilege();
    int Execute(MemoryCommand command);
}

public sealed class MemoryEngine(IMemoryApi api)
{
    private int running;
    public bool IsRunning => Volatile.Read(ref running) != 0;

    public async Task<CleanupResult> RunAsync(IEnumerable<MemoryCommand> commands,
        Action<MemoryCommand>? progress = null, CancellationToken cancellation = default,
        TimeSpan? followUpDelay = null, Action? observing = null)
    {
        if (followUpDelay is { } delay && (delay < TimeSpan.Zero || delay > TimeSpan.FromMinutes(1)))
            throw new ArgumentOutOfRangeException(nameof(followUpDelay));
        var selected = commands.Distinct().OrderBy(c => (int)c).ToArray();
        if (selected.Length == 0 || selected.Any(c => !Enum.IsDefined(c)))
            throw new ArgumentException("请至少选择一个有效的整理步骤。");
        if (selected.Contains(MemoryCommand.BackgroundWorkingSets) && selected.Length != 1)
            throw new ArgumentException("日常整理不能与高级全系统步骤组合。");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("整理正在进行，请等待完成。");
        try
        {
            long instantTimestamp = 0;
            var result = await Task.Run(() =>
            {
                var started = DateTimeOffset.Now;
                var clock = Stopwatch.StartNew();
                var before = ReadSnapshot();
                var steps = new List<StepResult>();
                string? error = null;
                var cancelled = false;
                try
                {
                    // 当前用户的有界后台回收不需要开启系统特权；高级操作单独提权。
                    using var privilege = selected[0] == MemoryCommand.BackgroundWorkingSets ? null : api.EnablePrivilege();
                    foreach (var command in selected)
                    {
                        if (cancellation.IsCancellationRequested) { cancelled = true; break; }
                        progress?.Invoke(command);
                        var stepClock = Stopwatch.StartNew();
                        // 系统调用执行期间不可强行取消；取消只影响尚未开始的步骤。
                        var status = api.Execute(command);
                        var step = new StepResult(command, status, stepClock.Elapsed.TotalSeconds);
                        steps.Add(step);
                        if (!step.Success) { error = $"{Labels.Name(command)}失败（{step.StatusHex}）"; break; }
                    }
                }
                // UAC拒绝也通过OperationCanceledException报告，不要求应用取消令牌已取消。
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex) { error = ex.Message; }
                MemorySnapshot? after = null;
                string? observationError = null;
                try { after = ReadSnapshot(); }
                catch (Exception ex) { observationError = "读取整理后内存失败：" + ex.Message; }
                instantTimestamp = Stopwatch.GetTimestamp();
                return new CleanupResult(started, before, after, steps, cancelled, error, clock.Elapsed.TotalSeconds) {
                    ObservationError = observationError,
                    BackgroundCleanup = selected[0] == MemoryCommand.BackgroundWorkingSets ? (api as IMemoryPerformanceApi)?.LastBackgroundCleanup : null
                };
            });
            if (followUpDelay is null || result.Steps.Count == 0 || result.Cancelled || result.BackgroundCleanup is { Attempted: 0 }) return result;
            // 所有系统调用结束且权限恢复后才复测；运行门闩保留到观测结束。
            double ObservationElapsed() => Stopwatch.GetElapsedTime(instantTimestamp).TotalSeconds;
            try
            {
                observing?.Invoke();
                await Task.Delay(followUpDelay.Value, cancellation);
                var followUp = await Task.Run(ReadSnapshot, cancellation);
                var elapsed = ObservationElapsed();
                return result with { FollowUp = followUp, FollowUpSeconds = elapsed, Seconds = result.Seconds + elapsed };
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { return result with { ObservationError = Join(result.ObservationError, "已取消复测；已执行的步骤不会撤销。"), Seconds = result.Seconds + ObservationElapsed() }; }
            catch (Exception ex)
            { return result with { ObservationError = Join(result.ObservationError, "复测不可用：" + ex.Message), Seconds = result.Seconds + ObservationElapsed() }; }
        }
        finally { Volatile.Write(ref running, 0); }
    }
    private static string Join(string? first, string second) => first is null ? second : first + "；" + second;
    private MemorySnapshot ReadSnapshot()
    {
        var snapshot = api.Read();
        if (snapshot is null || snapshot.Total == 0 || snapshot.Total > long.MaxValue || snapshot.Available > snapshot.Total || snapshot.Commit > long.MaxValue)
            throw new InvalidDataException("系统返回的内存快照无效，不能计算真实变化。");
        return snapshot;
    }
}

public static class Labels
{
    public static string Name(MemoryCommand command) => command switch
    {
        MemoryCommand.WorkingSets => "裁剪工作集",
        MemoryCommand.ModifiedPages => "刷新修改页",
        MemoryCommand.StandbyCache => "清理待机缓存",
        MemoryCommand.BackgroundWorkingSets => "整理闲置后台",
        _ => "未知步骤"
    };
    public static string Bytes(ulong bytes) => $"{bytes / 1073741824d:0.00} GiB";
    public static string Delta(long? bytes) => bytes is null ? "变化未知" : $"{(bytes >= 0 ? "+" : "−")}{Math.Abs((double)bytes.Value) / 1073741824d:0.00} GiB";
}
