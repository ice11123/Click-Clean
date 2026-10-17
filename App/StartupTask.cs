using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace ClickClean;
public static class StartupTask
{
    private static string Sid { get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; } }
    public static string Name => "ClickClean-" + Sid;
    public static string HandoffName => "ClickClean-Handoff-" + Sid;
    private static string TaskSecurity => "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;" + Sid + ")";
    public static bool RepairExistingRunLevel()
    {
        if (BuildInfo.ExperimentalX86) return false;
        dynamic service = Connect(); dynamic folder = service.GetFolder("\\");
        try {
            dynamic existing;
            try { existing = folder.GetTask(Name); }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return false; }
            try {
                dynamic definition = existing.Definition;
                try {
                    var account = (string)definition.Principal.UserId;
                    var owner = account.StartsWith("S-1-", StringComparison.Ordinal) ? account
                        : ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
                    if (owner != Sid) throw new InvalidDataException("登录任务不属于当前账户，未修改。");
                    if ((int)definition.Principal.RunLevel == 0) return false;
                    definition.Principal.RunLevel = 0;
                    // 保留原触发器、路径、启用状态；只降低权限并允许当前账户日后管理自己的普通任务。
                    dynamic repaired = folder.RegisterTaskDefinition(Name, definition, 6, Sid, null, 3, TaskSecurity);
                    Marshal.FinalReleaseComObject(repaired); return true;
                }
                finally { Marshal.FinalReleaseComObject(definition); }
            }
            finally { Marshal.FinalReleaseComObject(existing); }
        }
        finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
    // 只借助计划任务的当前账户交互令牌降权，不复制高权限令牌，不运行脚本或shell。
    public static void LaunchUnelevated(string arguments)
    {
        if (BuildInfo.ExperimentalX86) throw new NotSupportedException("32位试验版不提供登录启动迁移。");
        dynamic service = Connect(); dynamic folder = service.GetFolder("\\"); dynamic task = service.NewTask(0);
        try {
            task.RegistrationInfo.Description = "Click-Clean：一次性普通权限启动交接，无登录触发器。";
            task.Principal.UserId = Sid; task.Principal.LogonType = 3; task.Principal.RunLevel = 0;
            task.Settings.Enabled = true; task.Settings.Hidden = true;
            task.Settings.DisallowStartIfOnBatteries = false; task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.ExecutionTimeLimit = "PT0S"; task.Settings.MultipleInstances = 2;
            dynamic action = task.Actions.Create(0);
            try { action.Path = Environment.ProcessPath!; action.Arguments = arguments; action.WorkingDirectory = AppContext.BaseDirectory; }
            finally { Marshal.FinalReleaseComObject(action); }
            dynamic registered = folder.RegisterTaskDefinition(HandoffName, task, 6, Sid, null, 3, TaskSecurity);
            try { dynamic running = registered.Run(null); Marshal.FinalReleaseComObject(running); }
            finally { Marshal.FinalReleaseComObject(registered); }
        }
        finally { Marshal.FinalReleaseComObject(task); Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
    public static void RemoveHandoff()
    {
        dynamic service = Connect(); dynamic folder = service.GetFolder("\\");
        try { try { folder.DeleteTask(HandoffName, 0); } catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { } }
        finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
    public static void RemoveLegacy()
    {
        if (BuildInfo.ExperimentalX86) return;
        dynamic service = Connect(); dynamic folder = service.GetFolder("\\");
        try { try { folder.DeleteTask("Qingqi-" + Sid, 0); } catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { } }
        finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Windows计划任务服务不可用。");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }
    public static bool Exists()
    {
        if (BuildInfo.ExperimentalX86) return false;
        using var migration = Registry.CurrentUser.OpenSubKey(@"Software\ClickClean");
        if (migration?.GetValue("StartupMigrationEnabled") is int enabled && enabled == 1) return true;
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder("\\");
            try { dynamic task = folder.GetTask(Name); try { return task.Enabled; } finally { Marshal.FinalReleaseComObject(task); } }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return false; }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
    public static string InspectXml(bool run = false)
    {
        dynamic service = Connect(); dynamic folder = service.GetFolder("\\");
        try
        {
            dynamic task = folder.GetTask(Name);
            try
            {
                string xml = task.Xml;
                if (run) { dynamic instance = task.Run(null); Marshal.FinalReleaseComObject(instance); }
                return xml;
            }
            finally { Marshal.FinalReleaseComObject(task); }
        }
        finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
    public static void Set(bool enabled)
    {
        if (BuildInfo.ExperimentalX86) {
            if (enabled) throw new NotSupportedException("32位试验包不提供登录启动。");
            return;
        }
        dynamic service = Connect();
        dynamic folder = service.GetFolder("\\");
        try
        {
            if (!enabled)
            {
                try { folder.DeleteTask(Name, 0); }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                using var migration = Registry.CurrentUser.OpenSubKey(@"Software\ClickClean", true);
                migration?.DeleteValue("StartupMigrationEnabled", false);
                return;
            }
            dynamic task = service.NewTask(0);
            try
            {
                task.RegistrationInfo.Description = "Click-Clean：当前用户登录后以普通权限启动到托盘。";
                task.Principal.UserId = Sid;
                task.Principal.LogonType = 3;
                task.Principal.RunLevel = 0;
                task.Settings.Enabled = true;
                task.Settings.DisallowStartIfOnBatteries = false;
                task.Settings.StopIfGoingOnBatteries = false;
                task.Settings.ExecutionTimeLimit = "PT0S";
                task.Settings.MultipleInstances = 2;
                dynamic trigger = task.Triggers.Create(9);
                trigger.UserId = Sid;
                trigger.Delay = "PT5S";
                Marshal.FinalReleaseComObject(trigger);
                dynamic action = task.Actions.Create(0);
                action.Path = Environment.ProcessPath!;
                action.Arguments = "--tray";
                action.WorkingDirectory = AppContext.BaseDirectory;
                Marshal.FinalReleaseComObject(action);
                dynamic registered = folder.RegisterTaskDefinition(Name, task, 6, Sid, null, 3, TaskSecurity);
                Marshal.FinalReleaseComObject(registered);
                using var migration = Registry.CurrentUser.OpenSubKey(@"Software\ClickClean", true);
                migration?.DeleteValue("StartupMigrationEnabled", false);
            }
            finally { Marshal.FinalReleaseComObject(task); }
        }
        finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
}
