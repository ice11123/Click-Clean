using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ClickClean.Security;
using Microsoft.Win32.SafeHandles;

namespace ClickClean.Privileged;

internal static class Program
{
    public static int Main(string[] args)
    {
        // 自测不启用特权、不连接界面、不执行任何整理。
        if (args.SequenceEqual(new[] { "--self-test" })) return CleanupProtocol.SelfTest() | ProtectedInstallation.SelfTest();
        if (!CleanupProtocol.TryArguments(args, out var pipeName, out var client)) return 2;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
                !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || !ProtectedInstallation.IsProtected(out _) ||
                !string.Equals(Environment.ProcessPath, ProtectedInstallation.HelperPath, StringComparison.OrdinalIgnoreCase)) return 3;
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(30000);
            PeerIdentity.VerifyServer(pipe, client);
            CleanupProtocol.WriteInt(pipe, CleanupProtocol.Version);
            using var privilege = EnablePrivilege();
            var previous = 1;
            while (true)
            {
                var command = CleanupProtocol.ReadInt(pipe, TimeSpan.FromSeconds(30));
                if (command == CleanupProtocol.Quit) return 0;
                // 每次授权最多按固定顺序执行三个命令，不允许无限循环或重复刷盘。
                if (!CleanupProtocol.IsCommand(command) || command <= previous) return 4;
                previous = command;
                var status = NtSetSystemInformation(80, ref command, sizeof(int));
                CleanupProtocol.WriteInt(pipe, status);
                if (status < 0) return 5;
            }
        }
        catch { return 6; }
    }
    private static IDisposable EnablePrivilege()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        if (!OpenProcessToken(process.Handle, 0x28, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var requested = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref requested, (uint)Marshal.SizeOf<TokenPrivileges>(), out var previous, out _) || Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new PrivilegeScope(token, previous);
        }
        catch { token.Dispose(); throw; }
    }
    private sealed class PrivilegeScope(SafeAccessTokenHandle token, TokenPrivileges previous) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (previous.Count != 0 && !RestoreTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { token.Dispose(); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges state, uint length, out TokenPrivileges previous, out uint returnLength);
    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)] private static extern bool RestoreTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int informationClass, ref int command, int length);
}
