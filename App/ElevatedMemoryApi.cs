using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using ClickClean.Security;

namespace ClickClean;

public sealed class ElevatedMemoryApi(WindowsMemoryApi nativeApi) : IMemoryApi, IMemoryPerformanceApi, IDisposable
{
    public WindowsMemoryApi NativeApi { get; } = nativeApi;
    private Session? session;
    public MemorySnapshot Read() => NativeApi.Read();
    public MemoryPressureSnapshot ReadPressure() => NativeApi.ReadPressure();
    public BackgroundScanSummary ObserveBackgroundProcesses() => NativeApi.ObserveBackgroundProcesses();
    public BackgroundCleanupSummary? LastBackgroundCleanup => NativeApi.LastBackgroundCleanup;
    public void Dispose() { session?.Dispose(); session = null; NativeApi.Dispose(); }
    // 仅用于验收UAC和双向认证，不发送整理命令、不修改系统内存。
    public static Task ValidateSystemComponentAsync() => Task.Run(() => { using var probe = Session.Start(); });
    // 会话延迟至高级命令才申请 UAC，正常监测和定向后台整理不提权。
    public IDisposable EnablePrivilege()
    {
        if (session is not null) throw new InvalidOperationException("整理授权会话已经建立。");
        return new Scope(this);
    }
    public int Execute(MemoryCommand command)
    {
        if ((int)command == 100) return NativeApi.Execute(command);
        if (!CleanupProtocol.IsCommand((int)command)) throw new ArgumentOutOfRangeException(nameof(command));
        session ??= Session.Start();
        CleanupProtocol.WriteInt(session.Pipe, (int)command);
        return CleanupProtocol.ReadInt(session.Pipe);
    }
    private sealed class Scope(ElevatedMemoryApi owner) : IDisposable
    {
        public void Dispose() { owner.session?.Dispose(); owner.session = null; }
    }
    private sealed class Session(NamedPipeServerStream pipe, Process process) : IDisposable
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        private readonly Process process = process;
        public static Session Start()
        {
            if (!ProtectedInstallation.IsProtected(out var reason))
                throw new InvalidOperationException(reason + "，请使用正式安装包安装系统组件；不会从便携目录提权。");
            var nonce = Guid.NewGuid().ToString("N");
            using var identity = WindowsIdentity.GetCurrent();
            var access = new PipeSecurity(); access.SetAccessRuleProtection(true, false); access.SetOwner(identity.User!);
            access.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
            // CurrentUserOnly也要求相同提权级别，不能用于普通界面与提权组件；这里用显式账户ACL，再双向验证PID与登录会话。
            var pipe = NamedPipeServerStreamAcl.Create(CleanupProtocol.Prefix + nonce, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 256, 256, access);
            Process? process = null;
            try
            {
                process = Process.Start(new ProcessStartInfo(ProtectedInstallation.HelperPath) {
                    UseShellExecute = true, Verb = "runas", WorkingDirectory = ProtectedInstallation.DirectoryPath,
                    Arguments = "--pipe " + nonce + " --client " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    WindowStyle = ProcessWindowStyle.Hidden
                }) ?? throw new InvalidOperationException("系统整理组件未启动。");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
                PeerIdentity.VerifyClient(pipe, process.Id);
                if (CleanupProtocol.ReadInt(pipe, TimeSpan.FromSeconds(10)) != CleanupProtocol.Version)
                    throw new InvalidDataException("系统组件需要升级，请运行正式安装包。");
                return new Session(pipe, process);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            { pipe.Dispose(); process?.Dispose(); throw new OperationCanceledException("已取消管理员授权，未执行高级整理。", ex); }
            catch { pipe.Dispose(); process?.Dispose(); throw; }
        }
        public void Dispose()
        {
            try { if (Pipe.IsConnected) CleanupProtocol.WriteInt(Pipe, CleanupProtocol.Quit); }
            catch (IOException) { }
            finally { Pipe.Dispose(); process.Dispose(); }
            // 不杀死组件：系统调用结束或管道断开后组件自行退出。
        }
    }
}
