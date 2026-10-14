using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ClickClean.Diagnostics;

/// <summary>只整理本次创建的测试子进程；不扫描或干预用户的其他应用。</summary>
internal static class ControlledMemoryExperiment
{
    private sealed record Measurement(double Milliseconds, uint PageFaults, ulong ReadBytes, ulong WriteBytes, long WorkingSet);
    private sealed record Trial(int Pair, string Arm, bool Trimmed, int Error, long BeforeWorkingSet, long AfterWorkingSet, Measurement Access);

    public static void Child()
    {
        var pages = new byte[128 * 1024 * 1024];
        byte sum = 0;
        void Touch() { for (var offset = 0; offset < pages.Length; offset += 4096) sum ^= pages[offset]; }
        for (var offset = 0; offset < pages.Length; offset += 4096) pages[offset] = (byte)(offset / 4096);
        for (var i = 0; i < 5; i++) Touch();
        using var process = Process.GetCurrentProcess();
        using var input = new StreamReader(Console.OpenStandardInput());
        using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        output.WriteLine("ready");
        while (input.ReadLine() is { } command)
        {
            if (command == "exit") break;
            if (command == "warm") { Touch(); output.WriteLine("warm"); continue; }
            if (command != "measure") break;
            var before = ReadCounters(process.Handle);
            if (!Native.GetProcessIoCounters(process.Handle, out var ioBefore)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var started = Stopwatch.GetTimestamp(); Touch();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var after = ReadCounters(process.Handle);
            if (!Native.GetProcessIoCounters(process.Handle, out var ioAfter)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            process.Refresh();
            output.WriteLine(JsonSerializer.Serialize(new Measurement(elapsed, unchecked(after.PageFaults - before.PageFaults),
                ioAfter.ReadBytes - ioBefore.ReadBytes, ioAfter.WriteBytes - ioBefore.WriteBytes, process.WorkingSet64)));
        }
        GC.KeepAlive(pages); GC.KeepAlive(sum);
    }

    public static async Task Run(string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--memory-fixture") {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException("无法创建受控测试进程。");
        var errors = child.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (await child.StandardOutput.ReadLineAsync(timeout.Token) != "ready") throw new InvalidOperationException("受控进程未就绪。");
            using var handle = Native.OpenProcess(0x1000 | 0x0400 | 0x0100, false, child.Id);
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var trials = new List<Trial>(); var random = new Random(3000);
            for (var pair = 1; pair <= 10; pair++)
            {
                // 每臂先恢复相同的暖页初态；次序随机，计时包含裁剪后的首次访问代价。
                var arms = random.Next(2) == 0 ? new[] { false, true } : new[] { true, false };
                foreach (var trim in arms)
                {
                    await child.StandardInput.WriteLineAsync("warm");
                    if (await child.StandardOutput.ReadLineAsync(timeout.Token) != "warm") throw new InvalidOperationException("预热应答无效。");
                    child.Refresh(); var before = child.WorkingSet64;
                    var success = !trim || Native.K32EmptyWorkingSet(handle);
                    var error = success ? 0 : Marshal.GetLastWin32Error();
                    child.Refresh(); var after = child.WorkingSet64;
                    await child.StandardInput.WriteLineAsync("measure");
                    var raw = await child.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("受控进程提前退出。");
                    var measurement = JsonSerializer.Deserialize<Measurement>(raw) ?? throw new InvalidDataException("受控测量无效。");
                    trials.Add(new(pair, trim ? "trim-owned-fixture" : "no-operation", trim && success, error, before, after, measurement));
                    if (!success) throw new System.ComponentModel.Win32Exception(error);
                }
            }
            using var native = new WindowsMemoryApi();
            var pressure = native.ReadPressure();
            var routine = RoutineCleanupPolicy.Evaluate(pressure.Memory, lowMemory: pressure.LowMemory, highMemory: pressure.HighMemory);
            await child.StandardInput.WriteLineAsync("exit");
            await child.WaitForExitAsync(timeout.Token);
            File.WriteAllText(output, JsonSerializer.Serialize(new {
                Version = BuildInfo.Version, FixtureMiB = 128, Pairs = 10, TrimmedOnlyOwnedFixture = true,
                SystemPressure = pressure, RoutineCommands = routine.Commands, RoutineMessage = routine.Message,
                Trials = trials, ChildError = await errors,
                Limitation = "只验证API和暖页再次访问代价，不是应用、游戏或全系统提速证明；PageFaults含软缺页，不能当硬缺页。"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            // 只清理本次持有的子进程，不按名称终止其他实例。
            if (!child.HasExited) { try { child.Kill(); await child.WaitForExitAsync(); } catch (InvalidOperationException) { } }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Counters
    {
        public uint Size, PageFaults;
        public nuint PeakWorkingSet, WorkingSet, PeakPaged, Paged, PeakNonpaged, Nonpaged, Pagefile, PeakPagefile;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    {
        public ulong Reads, Writes, Other, ReadBytes, WriteBytes, OtherBytes;
    }
    private static Counters ReadCounters(IntPtr process)
    {
        var counters = new Counters { Size = (uint)Marshal.SizeOf<Counters>() };
        if (!Native.GetProcessMemoryInfo(process, ref counters, counters.Size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return counters;
    }
    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] public static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool K32EmptyWorkingSet(SafeProcessHandle process);
        [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    }
}
