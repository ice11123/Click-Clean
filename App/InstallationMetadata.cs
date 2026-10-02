using Microsoft.Win32;

namespace ClickClean;

internal static class InstallationMetadata
{
    public static void RefreshVersion(Store store)
    {
        try
        {
            using var installation = Registry.CurrentUser.OpenSubKey(@"Software\ClickClean");
            if (installation?.GetValue("InstallPath") is not string installedRoot) return;
            var current = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var expected = Path.Combine(Path.GetFullPath(installedRoot), "current").TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)) return;
            using var uninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1", writable: true);
            uninstall?.SetValue("DisplayVersion", BuildInfo.Version, RegistryValueKind.String);
        }
        catch (Exception ex) { store.Log("同步安装版本失败：" + ex.Message); }
    }
}
