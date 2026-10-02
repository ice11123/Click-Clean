using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ClickClean;
public static class StartupTask
{
    private static string Sid { get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; } }
    public static string Name => "ClickClean-" + Sid;
    public static void RemoveLegacy()
    {
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
        dynamic service = Connect();
        dynamic folder = service.GetFolder("\\");
        try
        {
            if (!enabled)
            {
                try { folder.DeleteTask(Name, 0); }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                return;
            }
            dynamic task = service.NewTask(0);
            try
            {
                task.RegistrationInfo.Description = "即清：当前用户登录后以管理员权限启动到托盘。";
                task.Principal.UserId = Sid;
                task.Principal.LogonType = 3;
                task.Principal.RunLevel = 1;
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
                dynamic registered = folder.RegisterTaskDefinition(Name, task, 6, Sid, null, 3, null);
                Marshal.FinalReleaseComObject(registered);
            }
            finally { Marshal.FinalReleaseComObject(task); }
        }
        finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
}
