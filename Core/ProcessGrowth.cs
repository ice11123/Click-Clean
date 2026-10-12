namespace ClickClean.Core;

public sealed record ProcessMemorySample(int Id, long StartedUtcTicks, string Name, long PrivateBytes, long WorkingSetBytes);
public sealed record ProcessGrowthRow(ProcessMemorySample Current, long? PrivateGrowth);

public sealed class ProcessGrowthTracker
{
    private Dictionary<(int Id, long Start), long>? baseline;
    public IReadOnlyList<ProcessGrowthRow> Compare(IEnumerable<ProcessMemorySample> samples)
    {
        var valid = samples.Where(s => s.Id > 0 && s.StartedUtcTicks > 0 && s.PrivateBytes >= 0 && s.WorkingSetBytes >= 0).ToArray();
        var rows = valid.Select(s => new ProcessGrowthRow(s,
            baseline is not null && baseline.TryGetValue((s.Id, s.StartedUtcTicks), out var before) ? s.PrivateBytes - before : null))
            .OrderByDescending(s => s.PrivateGrowth ?? long.MinValue).ThenByDescending(s => s.Current.PrivateBytes).Take(12).ToArray();
        baseline ??= valid.GroupBy(s => (s.Id, s.StartedUtcTicks)).ToDictionary(g => g.Key, g => g.First().PrivateBytes);
        return rows;
    }
    public void Reset() => baseline = null;
}
