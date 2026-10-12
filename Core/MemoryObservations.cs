using System.Diagnostics;

namespace ClickClean.Core;

/// <summary>只补充观测，不持有整理门闩；新整理开始后应取消旧观测。</summary>
public static class MemoryObservations
{
    public static async Task<CleanupResult> ObserveAsync(CleanupResult result, IMemoryApi api,
        TimeSpan delay, CancellationToken cancellation = default)
    {
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(delay));
        var started = Stopwatch.GetTimestamp();
        await Task.Delay(delay, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var elapsed = (result.FollowUpSeconds ?? 0) + Stopwatch.GetElapsedTime(started).TotalSeconds;
        try
        {
            var sample = await Task.Run(api.Read, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (sample.Total == 0 || sample.Total > long.MaxValue || sample.Available > sample.Total || sample.Commit > long.MaxValue)
                throw new InvalidDataException("持续观测返回无效读数。");
            return result with { Sustained = sample, SustainedSeconds = elapsed, SustainedError = null };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return result with { SustainedSeconds = elapsed, SustainedError = ex.Message }; }
    }
}
