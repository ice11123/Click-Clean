using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClickClean.Core;

public sealed partial class WindowsMemoryApi
{
    private readonly object backgroundGate = new();
    private readonly object notificationGate = new();
    private readonly BackgroundProcessTracker backgroundTracker = new();
    private SafeWaitHandle? lowNotification, highNotification;
    private BackgroundCleanupSummary? backgroundSummary;
    private volatile bool disposed;
    public BackgroundCleanupSummary? LastBackgroundCleanup { get { lock (backgroundGate) return backgroundSummary; } }
    public MemoryPressureSnapshot ReadPressure()
    {
        lock (notificationGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lowNotification ??= CreateMemoryResourceNotification(0);
            highNotification ??= CreateMemoryResourceNotification(1);
            bool? Query(SafeWaitHandle handle) => !handle.IsInvalid && QueryMemoryResourceNotification(handle, out var signaled) ? signaled : null;
            return new(Read(), Query(lowNotification), Query(highNotification));
        }
    }
    public BackgroundScanSummary ObserveBackgroundProcesses()
    {
        lock (backgroundGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return ObserveProcesses(out _);
        }
    }
    private BackgroundScanSummary ObserveProcesses(out WindowState? windows)
    {
        windows = CaptureWindows();
        if (windows is null) return backgroundTracker.Observe([]);
        var ids = new uint[1024];
        if (!EnumProcesses(ids, checked((uint)(ids.Length * sizeof(uint))), out var bytes)) return backgroundTracker.Observe([]);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (sid is null || !ProcessIdToSessionId((uint)Environment.ProcessId, out var session)) return backgroundTracker.Observe([]);
        var samples = new List<BackgroundProcessSample>();
        var examined = 0;
        var currentWindows = windows;
        // 一次最多扫描256个句柄，超过上限的进程不会成为本轮候选。
        foreach (var id in ids.Take((int)(bytes / sizeof(uint)))
            .Where(id => id > 4 && id != Environment.ProcessId && !currentWindows.Interactive.Contains(id) &&
                ProcessIdToSessionId(id, out var candidateSession) && candidateSession == session).Take(256))
        {
            examined++;
            using var process = OpenProcess(0x1000, false, id);
            if (process.IsInvalid) continue;
            var sample = ReadProcess(process, id, sid, session, windows, filterWorkingSet: !backgroundTracker.RequiresObservation((int)id));
            if (sample is not null) samples.Add(sample);
        }
        return backgroundTracker.Observe(samples, windows.ForegroundPath) with { Examined = examined };
    }
    private int ExecuteBackgroundCleanup()
    {
        lock (backgroundGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            backgroundSummary = new(0, 0, 0, 0, 0, null, BackgroundStopReason.PressureUnavailable);
            var pressure = ReadPressure();
            var reason = PressureReason(pressure);
            if (reason is not null) { backgroundSummary = backgroundSummary with { StopReason = reason.Value }; return 0; }
            var scan = ObserveProcesses(out var windows);
            if (windows is null) { backgroundSummary = backgroundSummary with { StopReason = BackgroundStopReason.SamplingUnavailable }; return 0; }
            var candidates = backgroundTracker.Candidates();
            var attempted = 0; var completed = 0; var failed = 0; long? workingSetChange = 0; ulong actualBudget = 0;
            var failures = new List<BackgroundProcessFailure>();
            var stopped = candidates.Count == 0 ? BackgroundStopReason.NoIdleCandidates : BackgroundStopReason.Completed;
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (sid is null || !ProcessIdToSessionId((uint)Environment.ProcessId, out var session))
            { backgroundSummary = new(scan.Examined, scan.Eligible, 0, 0, 0, null, BackgroundStopReason.SamplingUnavailable); return 0; }
            try {
                foreach (var expected in candidates)
                {
                    // 压力解除不继续处理其它程序，避免把一次调用变成无条件全系统裁剪。
                    reason = PressureReason(ReadPressure());
                    if (reason is not null) { stopped = reason == BackgroundStopReason.NoPressure ? BackgroundStopReason.PressureRelieved : reason.Value; break; }
                    windows = CaptureWindows();
                    if (windows is null) { stopped = BackgroundStopReason.SamplingUnavailable; break; }
                    using var process = OpenProcess(0x1000 | 0x100, false, (uint)expected.Id);
                    if (process.IsInvalid) continue;
                    var actual = ReadProcess(process, (uint)expected.Id, sid, session, windows);
                    // 筛选和执行使用同一进程句柄，验证创建时间避免PID重用；有活动竞态即跳过。
                    if (actual is null || !backgroundTracker.Revalidate(expected, actual) ||
                        actualBudget + actual.WorkingSet > BackgroundProcessTracker.MaximumRoundWorkingSet) continue;
                    actualBudget += actual.WorkingSet;
                    attempted++;
                    if (!EmptyWorkingSet(process)) {
                        failures.Add(new(actual.Id, Marshal.GetLastWin32Error()));
                        failed++; backgroundTracker.RecordTrim(actual, null); continue;
                    }
                    completed++;
                    var after = ReadProcess(process, (uint)expected.Id, sid, session, windows, filterWorkingSet: false);
                    backgroundTracker.RecordTrim(actual, after);
                    // 驻留变化可为负值，读取失败必须保持未知；不是私有提交释放量或性能收益。
                    if (after is null) workingSetChange = null;
                    else if (workingSetChange is { } total) workingSetChange = checked(total + (long)after.WorkingSet - (long)actual.WorkingSet);
                }
            }
            catch {
                // 后续读数异常不能抹去已执行的事实；引擎接收错误，详情仍保留实际完成数量。
                stopped = BackgroundStopReason.SamplingUnavailable; workingSetChange = null;
                throw;
            }
            finally {
                if (candidates.Count > 0 && attempted == 0 && stopped == BackgroundStopReason.Completed) stopped = BackgroundStopReason.ActivityChanged;
                backgroundSummary = new(scan.Examined, scan.Eligible, attempted, completed, failed, workingSetChange, stopped) { Failures = failures };
            }
            // 部分完成也不能报完全成功；详细错误保留原始Win32码，历史仍保留完成的数量。
            return failed > 0 ? failures[0].Win32Error > 0
                ? unchecked((int)(0x80070000u | (uint)failures[0].Win32Error & 0xFFFFu)) : unchecked((int)0x80004005) : 0;
        }
    }
    private static BackgroundStopReason? PressureReason(MemoryPressureSnapshot pressure)
    {
        var memory = pressure.Memory;
        if (memory.Total == 0 || memory.Total > long.MaxValue || memory.Available > memory.Total ||
            pressure.LowMemory is null || pressure.HighMemory is null || pressure.LowMemory == true && pressure.HighMemory == true)
            return BackgroundStopReason.PressureUnavailable;
        return pressure.LowMemory == true ? null : BackgroundStopReason.NoPressure;
    }
    private sealed record WindowState(uint ForegroundId, string? ForegroundPath, HashSet<uint> Interactive);
    private static WindowState? CaptureWindows()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || GetWindowThreadProcessId(foreground, out var foregroundId) == 0) return null;
        var interactive = new HashSet<uint>();
        if (!EnumWindows((window, _) => {
            if (IsWindowVisible(window) && !IsIconic(window) && GetWindowThreadProcessId(window, out var id) != 0) interactive.Add(id);
            return true;
        }, IntPtr.Zero)) return null;
        using var process = OpenProcess(0x1000, false, foregroundId);
        var path = process.IsInvalid ? null : ReadImage(process);
        if (path is null) return null;
        return new(foregroundId, path, interactive);
    }
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase) {
        "ClickClean", "ClickClean.Privileged", "explorer", "dwm", "audiodg", "conhost", "RuntimeBroker", "ApplicationFrameHost",
        "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "sihost", "TextInputHost", "dllhost", "taskhostw",
        "SecurityHealthSystray", "MsMpEng", "MpDefenderCoreService", "MBAMService", "360tray", "HipsTray", "HipsDaemon"
    };
    private static BackgroundProcessSample? ReadProcess(SafeProcessHandle process, uint id, string sid, uint session, WindowState windows, bool filterWorkingSet = true)
    {
        try
        {
            if (!GetExitCodeProcess(process, out var exitCode) || exitCode != 259 || !IsProcessCritical(process, out var critical) || critical ||
                !ProcessIdToSessionId(id, out var actualSession) || actualSession != session) return null;
            if (id == windows.ForegroundId || windows.Interactive.Contains(id)) return null;
            var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            if (!GetProcessMemoryInfo(process, ref counters, counters.Size) || filterWorkingSet &&
                ((ulong)counters.WorkingSet < BackgroundProcessTracker.MinimumWorkingSet || (ulong)counters.WorkingSet > BackgroundProcessTracker.MaximumWorkingSet)) return null;
            var image = ReadImage(process);
            if (image is null || ExcludedNames.Contains(Path.GetFileNameWithoutExtension(image))) return null;
            var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (image.StartsWith(system, StringComparison.OrdinalIgnoreCase)) return null;
            if (!OpenProcessToken(process.DangerousGetHandle(), 8, out var token)) return null;
            using (token)
            using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
                if (!string.Equals(owner.User?.Value, sid, StringComparison.Ordinal)) return null;
            if (!GetProcessTimes(process, out var creation, out _, out var kernel, out var user) ||
                !GetProcessIoCounters(process, out var io)) return null;
            var foreground = id == windows.ForegroundId || string.Equals(image, windows.ForegroundPath, StringComparison.OrdinalIgnoreCase);
            return new(checked((int)id), creation, image, (ulong)counters.WorkingSet, checked((ulong)kernel + (ulong)user),
                checked(io.ReadBytes + io.WriteBytes + io.OtherBytes), counters.PageFaults, true, false, windows.Interactive.Contains(id), foreground);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or ArgumentException or OverflowException or System.Security.SecurityException)
        { return null; }
    }
    private static string? ReadImage(SafeProcessHandle process)
    {
        var buffer = new StringBuilder(1024); var length = (uint)buffer.Capacity;
        if (QueryFullProcessImageName(process, 0, buffer, ref length)) return length > 0 ? buffer.ToString() : null;
        if (Marshal.GetLastWin32Error() != 122) return null;
        buffer = new StringBuilder(32768); length = (uint)buffer.Capacity;
        return QueryFullProcessImageName(process, 0, buffer, ref length) && length > 0 ? buffer.ToString() : null;
    }
    public void Dispose()
    {
        lock (backgroundGate)
        {
            if (disposed) return;
            lock (notificationGate) { disposed = true; lowNotification?.Dispose(); highNotification?.Dispose(); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessMemoryCounters {
        public uint Size, PageFaults;
        public nuint PeakWorkingSet, WorkingSet, QuotaPeakPaged, QuotaPaged, QuotaPeakNonpaged, QuotaNonpaged, Pagefile, PeakPagefile, PrivateUsage;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeWaitHandle CreateMemoryResourceNotification(int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryMemoryResourceNotification(SafeWaitHandle handle, out bool signaled);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool EnumProcesses([Out] uint[] ids, uint size, out uint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint process, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessCritical(SafeProcessHandle process, out bool critical);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint length);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessMemoryCounters counters, uint size);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool EmptyWorkingSet(SafeProcessHandle process);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
}
