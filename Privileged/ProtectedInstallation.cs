using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClickClean.Security;

public static class ProtectedInstallation
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Click-Clean", "Privileged");
    public static string HelperPath => Path.Combine(DirectoryPath, "ClickClean.Privileged.exe");
    private static readonly HashSet<string> trusted = ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];
    private const FileSystemRights writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | (FileSystemRights)0x40000000 | (FileSystemRights)0x10000000;
    public static bool UnsafeAllow(string sid, FileSystemRights rights, PropagationFlags propagation) =>
        (propagation & PropagationFlags.InheritOnly) == 0 && (rights & writes) != 0 && !trusted.Contains(sid);
    public static bool TrustedOwner(string sid) => trusted.Contains(sid);
    public static int SelfTest() =>
        !UnsafeAllow("S-1-5-32-545", (FileSystemRights)0x40000000, PropagationFlags.None) ||
        !UnsafeAllow("S-1-5-32-545", (FileSystemRights)0x10000000, PropagationFlags.None) ||
        !UnsafeAllow("S-1-5-32-545", FileSystemRights.TakeOwnership, PropagationFlags.None) ||
        UnsafeAllow("S-1-3-0", (FileSystemRights)0x10000000, PropagationFlags.InheritOnly) ||
        UnsafeAllow("S-1-5-18", FileSystemRights.FullControl, PropagationFlags.None) ||
        UnsafeAllow("S-1-5-32-545", FileSystemRights.ReadAndExecute, PropagationFlags.None) ||
        !TrustedOwner("S-1-5-32-544") || TrustedOwner("S-1-5-32-545") || TrustedOwner("S-1-5-21-1-2-3-1001") ? 1 : 0;
    public static bool IsProtected(out string reason)
    {
        reason = "";
        try
        {
            if (!Environment.Is64BitProcess || !File.Exists(HelperPath)) { reason = "系统组件未安装"; return false; }
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            // 拒绝重解析跳转；文件、父目录均不能由普通用户替换或修改权限。
            foreach (var path in new[] { root, Path.Combine(root, "Click-Clean"), DirectoryPath, HelperPath })
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { reason = "系统组件路径不安全"; return false; }
                FileSystemSecurity security = System.IO.Directory.Exists(path)
                    ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                    : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !TrustedOwner(owner.Value))
                { reason = "系统组件所有者不安全"; return false; }
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (rule.AccessControlType == AccessControlType.Allow && UnsafeAllow(rule.IdentityReference.Value, rule.FileSystemRights, rule.PropagationFlags))
                    { reason = "系统组件允许普通用户写入"; return false; }
            }
            return true;
        }
        catch { reason = "系统组件权限无法验证"; return false; }
    }
}
