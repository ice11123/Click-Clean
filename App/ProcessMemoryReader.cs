using System.Diagnostics;

namespace ClickClean;

public sealed record ProcessSampleBatch(IReadOnlyList<ProcessMemorySample> Samples, int Unavailable);
public interface IProcessMemoryReader { ProcessSampleBatch Read(); }

public sealed class ProcessMemoryReader : IProcessMemoryReader
{
    public ProcessSampleBatch Read()
    {
        var samples = new List<ProcessMemorySample>(); var unavailable = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { samples.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks, process.ProcessName, process.PrivateMemorySize64, process.WorkingSet64)); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                { unavailable++; }
            }
        }
        return new(samples, unavailable);
    }
}
