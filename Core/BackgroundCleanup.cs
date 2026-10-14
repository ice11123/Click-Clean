namespace ClickClean.Core;

public sealed record MemoryPressureSnapshot(MemorySnapshot Memory, bool? LowMemory, bool? HighMemory);
public sealed record BackgroundScanSummary(int Examined, int Eligible, int IdleCandidates);
public enum BackgroundStopReason { Completed, PressureUnavailable, NoPressure, NoIdleCandidates, PressureRelieved, SamplingUnavailable, ActivityChanged }
public sealed record BackgroundCleanupSummary(int Examined, int Eligible, int Attempted, int Completed, int Failed,
    long? WorkingSetChange, BackgroundStopReason StopReason)
{
    public IReadOnlyList<BackgroundProcessFailure> Failures { get; init; } = [];
}
public sealed record BackgroundProcessFailure(int ProcessId, int Win32Error);
public interface IMemoryPerformanceApi
{
    MemoryPressureSnapshot ReadPressure();
    BackgroundScanSummary ObserveBackgroundProcesses();
    BackgroundCleanupSummary? LastBackgroundCleanup { get; }
}

// 所有准入标志由原生层核实；缺失权限、未知身份或退出的进程不应产生样本。
public sealed record BackgroundProcessSample(int Id, long CreationTime, string ImagePath, ulong WorkingSet,
    ulong CpuTicks, ulong IoBytes, uint PageFaults, bool CurrentUser = true, bool Critical = false,
    bool Interactive = false, bool Foreground = false);

/// <summary>有界闲置启发式，不证明所有驻留页面都是冷页，也不宣称普遍提速。</summary>
public sealed class BackgroundProcessTracker(TimeProvider? time = null)
{
    public const ulong MinimumWorkingSet = 128 * 1024 * 1024;
    public const ulong MaximumWorkingSet = 512 * 1024 * 1024;
    public const ulong MaximumRoundWorkingSet = 768 * 1024 * 1024;
    public const int MaximumTargets = 3;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private sealed record State(BackgroundProcessSample Last, long LastSeen, long IdleSince, ulong IdleCpu,
        ulong IdleIo, uint IdleFaults, long? LastTrim = null, ulong TrimWorkingSet = 0, uint TrimFaults = 0,
        long? BackoffSince = null);
    private readonly Dictionary<int, State> states = [];
    private readonly Dictionary<(int Id, long Creation), State> trimmed = [];
    private readonly Dictionary<string, long> recentForeground = new(StringComparer.OrdinalIgnoreCase);
    private static bool Allowed(BackgroundProcessSample sample) => sample.Id > 0 && sample.CreationTime > 0 &&
        !string.IsNullOrWhiteSpace(sample.ImagePath) && sample.CurrentUser && !sample.Critical && !sample.Interactive && !sample.Foreground &&
        sample.WorkingSet is >= MinimumWorkingSet and <= MaximumWorkingSet;
    private double Elapsed(long started, long now) => clock.GetElapsedTime(started, now).TotalSeconds;
    public bool RequiresObservation(int id) => states.ContainsKey(id);
    public BackgroundScanSummary Observe(IReadOnlyList<BackgroundProcessSample> samples, string? foregroundPath = null)
    {
        var now = clock.GetTimestamp();
        foreach (var key in trimmed.Where(pair => pair.Value.LastTrim is null ||
            Elapsed(pair.Value.LastTrim.Value, now) > 86400 && (pair.Value.BackoffSince is null || Elapsed(pair.Value.BackoffSince.Value, now) > 86400))
            .Select(pair => pair.Key).ToArray()) trimmed.Remove(key);
        if (!string.IsNullOrWhiteSpace(foregroundPath)) recentForeground[foregroundPath] = now;
        foreach (var sample in samples.Where(s => s.Foreground)) recentForeground[sample.ImagePath] = now;
        foreach (var key in recentForeground.Where(p => Elapsed(p.Value, now) > 90).Select(p => p.Key).ToArray()) recentForeground.Remove(key);
        var seen = new HashSet<int>();
        foreach (var sample in samples)
        {
            if (!seen.Add(sample.Id)) continue;
            states.TryGetValue(sample.Id, out var previous);
            if (previous is null || previous.Last.CreationTime != sample.CreationTime ||
                !string.Equals(previous.Last.ImagePath, sample.ImagePath, StringComparison.OrdinalIgnoreCase) ||
                Elapsed(previous.LastSeen, now) > 30 || now < previous.LastSeen ||
                sample.CpuTicks < previous.Last.CpuTicks || sample.IoBytes < previous.Last.IoBytes || sample.PageFaults < previous.Last.PageFaults)
            {
                trimmed.TryGetValue((sample.Id, sample.CreationTime), out var priorTrim);
                states[sample.Id] = new(sample, now, now, sample.CpuTicks, sample.IoBytes, sample.PageFaults,
                    priorTrim?.LastTrim, priorTrim?.TrimWorkingSet ?? 0, priorTrim?.TrimFaults ?? 0, priorTrim?.BackoffSince);
                continue;
            }
            var state = previous with { Last = sample, LastSeen = now };
            if (state.LastTrim is { } trimTimestamp && Elapsed(trimTimestamp, now) <= 60 &&
                (sample.WorkingSet >= state.TrimWorkingSet + 64 * 1024 * 1024 || sample.PageFaults >= state.TrimFaults && sample.PageFaults - state.TrimFaults >= 256))
                state = state with { BackoffSince = now };
            // 累计 CPU 大于 100ms、任何 I/O、明显缺页或者重新交互，都重置完整闲置窗口。
            if (!Allowed(sample) || sample.CpuTicks - state.IdleCpu > 1_000_000 || sample.IoBytes != state.IdleIo ||
                sample.PageFaults - state.IdleFaults > 8 || recentForeground.ContainsKey(sample.ImagePath))
                state = state with { IdleSince = now, IdleCpu = sample.CpuTicks, IdleIo = sample.IoBytes, IdleFaults = sample.PageFaults };
            states[sample.Id] = state;
            if (state.LastTrim is not null) trimmed[(sample.Id, sample.CreationTime)] = state;
        }
        // 未能读取的进程直接丢弃基线；再次可读时必须重新积累，而非沿用陈旧的闲置证明。
        foreach (var id in states.Keys.Where(id => !seen.Contains(id)).ToArray()) states.Remove(id);
        return new(samples.Count, samples.Count(Allowed), Candidates().Count);
    }
    public IReadOnlyList<BackgroundProcessSample> Candidates()
    {
        var now = clock.GetTimestamp();
        var ordered = states.Values.Where(s => Allowed(s.Last) && Elapsed(s.LastSeen, now) <= 5 &&
            Elapsed(s.IdleSince, now) >= 60 && !recentForeground.ContainsKey(s.Last.ImagePath) &&
            (s.LastTrim is null || Elapsed(s.LastTrim.Value, now) >= 1800) &&
            (s.BackoffSince is null || Elapsed(s.BackoffSince.Value, now) >= 86400))
            .OrderByDescending(s => s.Last.WorkingSet).ThenBy(s => s.Last.Id);
        var result = new List<BackgroundProcessSample>(); ulong budget = 0;
        foreach (var state in ordered)
        {
            if (result.Count == MaximumTargets) break;
            if (budget + state.Last.WorkingSet > MaximumRoundWorkingSet) continue;
            budget += state.Last.WorkingSet; result.Add(state.Last);
        }
        return result;
    }
    public bool Revalidate(BackgroundProcessSample expected, BackgroundProcessSample actual) =>
        Allowed(actual) && expected.Id == actual.Id && expected.CreationTime == actual.CreationTime &&
        string.Equals(expected.ImagePath, actual.ImagePath, StringComparison.OrdinalIgnoreCase) &&
        expected.CpuTicks == actual.CpuTicks && expected.IoBytes == actual.IoBytes && expected.PageFaults == actual.PageFaults &&
        Candidates().Any(s => s.Id == expected.Id && s.CreationTime == expected.CreationTime);
    public void RecordTrim(BackgroundProcessSample before, BackgroundProcessSample? after)
    {
        if (!states.TryGetValue(before.Id, out var state) || state.Last.CreationTime != before.CreationTime) return;
        var now = clock.GetTimestamp();
        states[before.Id] = state with { LastTrim = now, TrimWorkingSet = after?.WorkingSet ?? before.WorkingSet,
            TrimFaults = after?.PageFaults ?? before.PageFaults, IdleSince = now };
        trimmed[(before.Id, before.CreationTime)] = states[before.Id];
    }
}
