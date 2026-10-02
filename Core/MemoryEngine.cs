using System.Diagnostics;

namespace ClickClean.Core;

public enum MemoryCommand { WorkingSets = 2, ModifiedPages = 3, StandbyCache = 4 }
public record MemorySnapshot(ulong Total, ulong Available, ulong Commit, ulong CommitLimit)
{
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
    public long? AvailableChange => After is null ? null : checked((long)After.Available - (long)Before.Available);
    public string Trigger { get; init; } = "手动";
    public bool Success => Error is null && !Cancelled && Steps.Count > 0 && Steps.All(s => s.Success);
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
        Action<MemoryCommand>? progress = null, CancellationToken cancellation = default)
    {
        var selected = commands.Distinct().OrderBy(c => (int)c).ToArray();
        if (selected.Length == 0 || selected.Any(c => !Enum.IsDefined(c)))
            throw new ArgumentException("请至少选择一个有效的整理步骤。");
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("整理正在进行，请等待完成。");
        try
        {
            return await Task.Run(() =>
            {
                var started = DateTimeOffset.Now;
                var clock = Stopwatch.StartNew();
                var before = api.Read();
                var steps = new List<StepResult>();
                string? error = null;
                var cancelled = false;
                try
                {
                    using var privilege = api.EnablePrivilege();
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
                catch (Exception ex) { error = ex.Message; }
                MemorySnapshot? after = null;
                try { after = api.Read(); }
                catch (Exception ex) { error = (error is null ? "" : error + "；") + "读取整理后内存失败：" + ex.Message; }
                return new CleanupResult(started, before, after, steps, cancelled, error, clock.Elapsed.TotalSeconds);
            });
        }
        finally { Volatile.Write(ref running, 0); }
    }
}

public static class Labels
{
    public static string Name(MemoryCommand command) => command switch
    {
        MemoryCommand.WorkingSets => "裁剪工作集",
        MemoryCommand.ModifiedPages => "刷新修改页",
        MemoryCommand.StandbyCache => "清理待机缓存",
        _ => "未知步骤"
    };
    public static string Bytes(ulong bytes) => $"{bytes / 1073741824d:0.00} GiB";
    public static string Delta(long? bytes) => bytes is null ? "变化未知" : $"{(bytes >= 0 ? "+" : "−")}{Math.Abs((double)bytes.Value) / 1073741824d:0.00} GiB";
}
