using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClickClean;

public interface IAutomationEnvironmentReader : IDisposable
{
    AutomationEnvironment Read();
}

public sealed class AutomationEnvironmentReader : IAutomationEnvironmentReader
{
    private readonly object gate = new();
    private SafeWaitHandle? lowMemory;
    private IntPtr query, counter;
    private bool firstDiskSample = true;
    private bool disposed;

    public AutomationEnvironment Read()
    {
        lock (gate)
        {
            if (disposed) return new();
            bool? low = null;
            lowMemory ??= Native.CreateMemoryResourceNotification(0);
            if (!lowMemory.IsInvalid && Native.QueryMemoryResourceNotification(lowMemory, out var signaled)) low = signaled;
            return new(low, ForegroundActivity.IsFullscreen(), ReadDiskBusy());
        }
    }
    private bool? ReadDiskBusy()
    {
        if (query == IntPtr.Zero)
        {
            if (Native.PdhOpenQueryW(null, 0, out query) != 0) { query = IntPtr.Zero; return null; }
            if (Native.PdhAddEnglishCounterW(query, @"\PhysicalDisk(_Total)\% Idle Time", 0, out counter) != 0)
            { Native.PdhCloseQuery(query); query = counter = IntPtr.Zero; return null; }
            firstDiskSample = true;
        }
        if (Native.PdhCollectQueryData(query) != 0) return null;
        if (firstDiskSample) { firstDiskSample = false; return null; }
        if (Native.PdhGetFormattedCounterValue(counter, 0x200, out _, out var value) != 0 || value.Status > 1 || !double.IsFinite(value.Value)) return null;
        // 全盘汇总计数是避让启发式，不能解释为某个具体磁盘的瞬时占用。
        return Math.Clamp(100 - value.Value, 0, 100) >= 80;
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; lowMemory?.Dispose();
            if (query != IntPtr.Zero) Native.PdhCloseQuery(query);
            query = counter = IntPtr.Zero;
        }
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Explicit, Size = 16)] public struct CounterValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
        [DllImport("kernel32.dll", SetLastError = true)] public static extern SafeWaitHandle CreateMemoryResourceNotification(int type);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool QueryMemoryResourceNotification(SafeWaitHandle handle, [MarshalAs(UnmanagedType.Bool)] out bool signaled);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQueryW(string? source, nuint data, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, nuint data, out IntPtr counter);
        [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
        [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(IntPtr query);
    }
}

public static class ForegroundActivity
{
    public static bool IsFullscreen()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || window == GetShellWindow()) return false;
        GetWindowThreadProcessId(window, out var process);
        GetWindowThreadProcessId(GetShellWindow(), out var shellProcess);
        if (process == 0 || process == shellProcess || process == Environment.ProcessId || !GetWindowRect(window, out var rect)) return false;
        return System.Windows.Forms.Screen.AllScreens.Any(screen => {
            var b = screen.Bounds;
            return rect.Left <= b.Left && rect.Top <= b.Top && rect.Right >= b.Right && rect.Bottom >= b.Bottom;
        });
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);
}
