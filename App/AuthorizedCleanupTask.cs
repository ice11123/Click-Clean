using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics;
using ClickClean.Security;

namespace ClickClean;

// 任务只能由安装器创建；普通界面只有读取和运行权限，不能修改高权限执行路径。
public static class AuthorizedCleanupTask
{
    public static string Name { get { using var identity = WindowsIdentity.GetCurrent(); return AuthorizedTaskPolicy.NamePrefix + identity.User!.Value; } }
    public static bool IsSafeSecurity(string descriptor, string sid)
    {
        var security = new RawSecurityDescriptor(descriptor);
        if (security.Owner?.Value is not ("S-1-5-18" or "S-1-5-32-544") || security.DiscretionaryAcl is null) return false;
        var currentCanRun = false;
        foreach (GenericAce entry in security.DiscretionaryAcl) {
            if (entry is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed) continue;
            if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
            var account = ace.SecurityIdentifier.Value;
            if (account is "S-1-5-18" or "S-1-5-32-544") continue;
            // 非管理员不得写、删除、改ACL或取得所有权；其他账户也不能运行此授权任务。
            if (ProtectedInstallation.UnsafeAllow(account, (System.Security.AccessControl.FileSystemRights)ace.AccessMask, PropagationFlags.None)) return false;
            var canRun = (ace.AccessMask & (0x20000000 | 0x20)) != 0;
            if (canRun && account != sid) return false;
            currentCanRun |= canRun && account == sid;
        }
        return currentCanRun;
    }
    public static void Run(string nonce, int processId)
    {
        if (!Guid.TryParseExact(nonce, "N", out _) || processId <= 0) throw new ArgumentException("整理会话参数无效。");
        UseTask(task => {
            using var process = Process.GetCurrentProcess();
            if (process.SessionId <= 0) throw new InvalidOperationException("整理授权需要当前账户的交互式登录会话。");
            var started = Stopwatch.GetTimestamp();
            while ((int)task.State is 2 or 4) {
                if (Stopwatch.GetElapsedTime(started).TotalSeconds >= 5) throw new InvalidOperationException("整理组件仍在工作或退出，请稍后重试。");
                Thread.Sleep(50);
            }
            // 只替换GUID和当前进程ID，并限定到本次交互式会话。
            dynamic running = task.RunEx(new string[] { nonce, processId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, 4, process.SessionId, null);
            Marshal.FinalReleaseComObject(running);
        });
    }
    public static void ValidateConfiguration() => UseTask(null);
    private static void UseTask(Action<dynamic>? start)
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!.Value;
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Windows计划任务服务不可用。");
        dynamic service = Activator.CreateInstance(type)!;
        dynamic? folder = null, task = null, definition = null;
        try {
            service.Connect(); folder = service.GetFolder("\\"); task = folder.GetTask(Name); definition = task.Definition;
            dynamic action = definition.Actions.Item(1);
            try {
                var account = (string)definition.Principal.UserId;
                var taskSid = account.StartsWith("S-1-", StringComparison.Ordinal) ? account : ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
                var snapshot = new AuthorizedTaskDefinition(taskSid, (int)definition.Principal.LogonType, (int)definition.Principal.RunLevel,
                    (bool)task.Enabled, (bool)definition.Settings.AllowDemandStart, (int)definition.Triggers.Count,
                    (int)definition.Actions.Count, (int)action.Type, (string)action.Path, (string)action.Arguments,
                    (string)action.WorkingDirectory, (string)definition.Settings.ExecutionTimeLimit, (int)definition.Settings.MultipleInstances);
                if (!AuthorizedTaskPolicy.IsValid(snapshot, sid, ProtectedInstallation.HelperPath, ProtectedInstallation.DirectoryPath) ||
                    !IsSafeSecurity((string)task.GetSecurityDescriptor(5), sid))
                    throw new InvalidDataException("系统授权任务配置或权限不安全，请运行正式安装包修复。");
            }
            finally { Marshal.FinalReleaseComObject(action); }
            start?.Invoke(task);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002))
        { throw new InvalidOperationException("尚未完成一次性整理授权，请运行4.0正式安装包；不会逐次弹出管理员授权。", ex); }
        finally {
            if (definition is not null) Marshal.FinalReleaseComObject(definition);
            if (task is not null) Marshal.FinalReleaseComObject(task);
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
    }
}
