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
    // 验收安装器预授权任务和双向认证，不发送整理命令，不申请逐次UAC。
    public static Task ValidateSystemComponentAsync() => Task.Run(() => { using var probe = Session.Start(); });
    // 每轮只启动受保护任务；授权由安装器保留，不让GUI持有管理员令牌。
    public IDisposable EnablePrivilege()
    {
        if (session is not null) throw new InvalidOperationException("整理授权会话已经建立。");
        return new Scope(this);
    }
    public int Execute(MemoryCommand command)
    {
        if ((int)command == 100) return NativeApi.Execute(command);
        if (!CleanupProtocol.IsCommand((int)command)) throw new ArgumentOutOfRangeException(nameof(command));
        if (BuildInfo.ExperimentalX86) throw new NotSupportedException("32位试验版不支持全系统三步，请使用x64正式版。");
        session ??= Session.Start();
        CleanupProtocol.WriteInt(session.Pipe, (int)command);
        return CleanupProtocol.ReadInt(session.Pipe);
    }
    private sealed class Scope(ElevatedMemoryApi owner) : IDisposable
    {
        public void Dispose() { owner.session?.Dispose(); owner.session = null; }
    }
    private sealed class Session(NamedPipeServerStream pipe) : IDisposable
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
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
            try
            {
                AuthorizedCleanupTask.Run(nonce, Environment.ProcessId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
                PeerIdentity.VerifyScheduledClient(pipe);
                if (CleanupProtocol.ReadInt(pipe, TimeSpan.FromSeconds(10)) != CleanupProtocol.Version)
                    throw new InvalidDataException("系统组件需要升级，请运行正式安装包。");
                return new Session(pipe);
            }
            catch { pipe.Dispose(); throw; }
        }
        public void Dispose()
        {
            try { if (Pipe.IsConnected) CleanupProtocol.WriteInt(Pipe, CleanupProtocol.Quit); }
            catch (IOException) { }
            finally { Pipe.Dispose(); }
            // 不杀死组件：系统调用结束或管道断开后组件自行退出。
        }
    }
}
