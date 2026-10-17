namespace ClickClean.Core;

public enum StartupPermissionAction { Continue, RelaunchLimited, Stop }

public static class StartupPermissionPolicy
{
    // 一次交接后仍然提权必须停止，避免兼容性标记或禁用UAC导致启动循环。
    public static StartupPermissionAction Evaluate(bool elevated, bool handedOff) => !elevated
        ? StartupPermissionAction.Continue : handedOff ? StartupPermissionAction.Stop : StartupPermissionAction.RelaunchLimited;
}
