using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ClickClean.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsMemoryApi : IMemoryApi
{
    public MemorySnapshot Read()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var performance = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!GetPerformanceInfo(ref performance, performance.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(memory.TotalPhysical, memory.AvailablePhysical,
            checked((ulong)performance.CommitTotal * (ulong)performance.PageSize),
            checked((ulong)performance.CommitLimit * (ulong)performance.PageSize)) {
                SystemCache = checked((ulong)performance.SystemCache * (ulong)performance.PageSize),
                KernelPaged = checked((ulong)performance.KernelPaged * (ulong)performance.PageSize),
                KernelNonpaged = checked((ulong)performance.KernelNonpaged * (ulong)performance.PageSize)
            };
    }

    public static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool ProfilePrivilegeEnabled()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var wanted)) throw new Win32Exception(Marshal.GetLastWin32Error());
            GetTokenInformation(token, 3, IntPtr.Zero, 0, out var size);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                if (!GetTokenInformation(token, 3, buffer, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++)
                {
                    var offset = 4 + i * 12;
                    if (unchecked((uint)Marshal.ReadInt32(buffer, offset)) == wanted.Low && Marshal.ReadInt32(buffer, offset + 4) == wanted.High)
                        return (Marshal.ReadInt32(buffer, offset + 8) & 2) != 0;
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public IDisposable EnablePrivilege()
    {
        if (!IsAdmin()) throw new InvalidOperationException("需要管理员权限，请通过ClickClean.exe启动并同意Windows授权。");
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref state, (uint)Marshal.SizeOf<TokenPrivileges>(), out var previous, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var lastError = Marshal.GetLastWin32Error();
            if (lastError != 0) throw new Win32Exception(lastError);
            return new PrivilegeScope(token, previous);
        }
        catch { token.Dispose(); throw; }
    }

    public int Execute(MemoryCommand command)
    {
        if (!Enum.IsDefined(command)) throw new ArgumentOutOfRangeException(nameof(command));
        var value = (int)command;
        return NtSetSystemInformation(80, ref value, sizeof(int));
    }

    private sealed class PrivilegeScope(SafeAccessTokenHandle token, TokenPrivileges previous) : IDisposable
    {
        public void Dispose()
        {
            // 操作完成后恢复原权限，避免长驻程序一直保留临时特权。
            try
            {
                if (previous.Count == 0) return;
                // 不请求PreviousState输出时才可将缓冲区长度设为0。
                if (!RestoreTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var error = Marshal.GetLastWin32Error();
                if (error != 0) throw new Win32Exception(error);
            }
            finally { token.Dispose(); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
            SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint Handles, Processes, Threads;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool GetPerformanceInfo(ref PerformanceInformation info, uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges state, uint length, out TokenPrivileges previous, out uint returnLength);
    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)] private static extern bool RestoreTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr buffer, uint length, out uint required);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int informationClass, ref int command, int length);
}
