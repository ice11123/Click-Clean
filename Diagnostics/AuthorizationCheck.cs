using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;
using ClickClean.Security;

namespace ClickClean.Diagnostics;

internal static class AuthorizationCheck
{
    public static void Policy(string output, bool inspectTask)
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var cases = new Dictionary<string, bool> {
            ["当前账户只读运行"] = AuthorizedCleanupTask.IsSafeSecurity("O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;" + sid + ")", sid),
            ["拒绝当前账户完全控制"] = !AuthorizedCleanupTask.IsSafeSecurity("O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;" + sid + ")", sid),
            ["拒绝普通账户拥有任务"] = !AuthorizedCleanupTask.IsSafeSecurity("O:" + sid + "G:BAD:P(A;;GRGX;;;" + sid + ")", sid),
            ["拒绝Everyone运行"] = !AuthorizedCleanupTask.IsSafeSecurity("O:BAG:BAD:P(A;;GRGX;;;WD)(A;;GRGX;;;" + sid + ")", sid),
            ["拒绝缺少用户运行权限"] = !AuthorizedCleanupTask.IsSafeSecurity("O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)", sid)
        };
        if (cases.Values.Any(value => !value)) throw new InvalidDataException("授权任务ACL回归失败。");
        bool? writeDenied = null;
        if (inspectTask) {
            AuthorizedCleanupTask.ValidateConfiguration();
            if (!WindowsMemoryApi.IsAdmin()) {
                dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!; service.Connect();
                dynamic folder = service.GetFolder("\\"); dynamic task = folder.GetTask(AuthorizedCleanupTask.Name);
                try {
                    var descriptor = (string)task.GetSecurityDescriptor(5);
                    try { task.SetSecurityDescriptor(descriptor, 0); }
                    catch (Exception ex) when (ex.HResult == unchecked((int)0x80070005)) { writeDenied = true; }
                    if (writeDenied != true) throw new InvalidDataException("普通令牌仍能修改高权限任务ACL。");
                }
                finally { Marshal.FinalReleaseComObject(task); Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = true, Checks = cases, TaskInspected = inspectTask, OrdinaryWriteDenied = writeDenied, CleanupCommandsSent = 0 }));
    }
    // 仅供隔离CI的session 0认证验收；不申请UAC，不发送整理命令，不是产品启动后备路径。
    public static void DirectComponent(string output)
    {
        if (!WindowsMemoryApi.IsAdmin()) throw new InvalidOperationException("隔离组件探针需要已获得的管理员令牌。");
        if (!ProtectedInstallation.IsProtected(out var reason)) throw new InvalidOperationException(reason);
        var nonce = Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent(); var access = new PipeSecurity();
        access.SetAccessRuleProtection(true, false); access.SetOwner(identity.User!);
        access.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(CleanupProtocol.Prefix + nonce, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 256, 256, access);
        using var process = Process.Start(new ProcessStartInfo(ProtectedInstallation.HelperPath) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = ProtectedInstallation.DirectoryPath,
            Arguments = "--pipe " + nonce + " --client " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }) ?? throw new InvalidOperationException("隔离组件未启动。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
        PeerIdentity.VerifyScheduledClient(pipe);
        if (CleanupProtocol.ReadInt(pipe, TimeSpan.FromSeconds(10)) != CleanupProtocol.Version) throw new InvalidDataException("组件协议不符。");
        CleanupProtocol.WriteInt(pipe, CleanupProtocol.Quit);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Authenticated = true, CommandsSent = 0, Mode = "isolated-already-elevated", SessionId = Process.GetCurrentProcess().SessionId }));
    }
}
