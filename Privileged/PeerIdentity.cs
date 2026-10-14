using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ClickClean.Security;

public static class PeerIdentity
{
    public static void VerifyServer(NamedPipeClientStream pipe, int expectedProcess)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var process) || process != expectedProcess)
            throw new InvalidDataException("整理会话来源无效。");
        VerifyAccountAndSession(expectedProcess, false);
    }
    public static void VerifyClient(NamedPipeServerStream pipe, int expectedProcess)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var process) || process != expectedProcess)
            throw new InvalidDataException("整理组件身份无效。");
        VerifyAccountAndSession(expectedProcess, true);
    }
    private static void VerifyAccountAndSession(int processId, bool elevated)
    {
        using var process = Process.GetProcessById(processId);
        using var currentProcess = Process.GetCurrentProcess();
        if (process.SessionId != currentProcess.SessionId) throw new InvalidDataException("整理会话不属于当前登录会话。");
        using var handle = OpenProcess(0x1000, false, processId);
        if (handle.IsInvalid || !OpenProcessToken(handle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            using var current = WindowsIdentity.GetCurrent();
            if (identity.User != current.User) throw new InvalidDataException("整理会话不属于当前账户；不支持切换管理员账户授权。");
            if (elevated) {
                // IsInRole会复制主令牌，需要TOKEN_DUPLICATE。这里只读对端TokenElevation，
                // 不为身份验证申请复制高权限令牌；受保护组件连接前已自检管理员组。
                if (!GetTokenInformation(token, 20, out var elevation, sizeof(int), out var length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (length != sizeof(int) || elevation == 0) throw new InvalidDataException("整理组件未获得管理员授权。");
            }
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int process);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out int information, int length, out int returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int process);
}
