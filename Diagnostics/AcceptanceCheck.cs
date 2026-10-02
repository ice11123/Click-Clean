using System.Diagnostics;
using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace ClickClean.Diagnostics;

internal static class AcceptanceCheck
{
    private sealed record Configuration(string OutputDirectory, string OldPortable, string ReleaseDirectory, string Installer, bool Resume = false);
    public static async Task Run(string configurationPath)
    {
        if (!WindowsMemoryApi.IsAdmin()) throw new InvalidOperationException("验收需要管理员授权。");
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(configurationPath)) ?? throw new InvalidDataException("验收配置无效。");
        var output = Path.GetFullPath(config.OutputDirectory);
        if (!config.Resume && Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new InvalidOperationException("验收目录必须尚不存在或为空。");
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\ClickClean"))
            if (key?.GetValue("InstallPath") is string) throw new InvalidOperationException("已存在正式即清安装，拒绝覆盖其登记。");
        if (!config.Resume && Process.GetProcessesByName("ClickClean").Length > 0) throw new InvalidOperationException("请先从托盘正常退出即清。");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); checks.Add(message); Save(); }
        void Save() => File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new { Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        if (config.Resume)
        {
            using var previous = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "verification.json")));
            checks.AddRange(previous.RootElement.GetProperty("Checks").EnumerateArray().Select(item => item.GetString()!));
            var previousExe = Path.Combine(output, "update-fixture", "current", "ClickClean.exe");
            if (OwnProcess(previousExe) is { } prior) await Exit(previousExe, prior);
            if (Process.GetProcessesByName("ClickClean").Length > 0) throw new InvalidOperationException("其他即清实例仍在运行，拒绝开始安装验收。");
        }
        else
        {
        Program.SystemCheck(Path.Combine(output, "system-memory.json"));
        Check(Environment.ExitCode == 0, "真实三步整理返回成功，特权恢复");
        await CheckStartup(output);
        Check(true, "专用最高权限计划任务成功启动诊断并已删除");

        var fixture = Path.Combine(output, "update-fixture");
        ZipFile.ExtractToDirectory(config.OldPortable, fixture);
        File.WriteAllText(Path.Combine(fixture, ".click-clean-update-fixture"), "仅用于即清更新验收");
        var applied = Path.Combine(output, "update-dispatched.json");
        var child = Start(Environment.ProcessPath!, ["--apply-update-fixture", fixture, config.ReleaseDirectory, applied]);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        Check(child.ExitCode == 0, "实际更新已下载并交给原生替换器");
        var fixtureExe = Path.Combine(fixture, "current", "ClickClean.exe");
        var expectedVersion = FileVersionInfo.GetVersionInfo(Path.Combine(Path.GetDirectoryName(config.Installer)!, "publish", "ClickClean.dll")).FileVersion;
        Process? restarted = null;
        for (var i = 0; i < 60; i++)
        {
            restarted = OwnProcess(fixtureExe);
            if (restarted is not null && FileVersionInfo.GetVersionInfo(Path.Combine(fixture, "current", "ClickClean.dll")).FileVersion == expectedVersion) break;
            await Task.Delay(500);
        }
        Check(restarted is not null && FileVersionInfo.GetVersionInfo(Path.Combine(fixture, "current", "ClickClean.dll")).FileVersion == expectedVersion, "1.9.0 实际替换为当前版本并重启到托盘");
        await Exit(fixtureExe, restarted!);
        Check(true, "更新后的程序可通过正常退出通道结束");
        }

        var installation = Path.Combine(output, "installed");
        var setup = Start(config.Installer, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DIR=" + installation, "/LOG=" + Path.Combine(output, "install.log")]);
        await setup.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        Check(setup.ExitCode == 0 && File.Exists(Path.Combine(installation, "current", "ClickClean.exe")), "中文安装包成功安装到专用验收目录");
        var installedExe = Path.Combine(installation, "current", "ClickClean.exe");
        var main = Start(installedExe, ["--tray"]);
        await Task.Delay(2500);
        Check(!main.HasExited, "安装版管理员启动并常驻托盘");
        var duplicate = Start(installedExe, []);
        await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Check(duplicate.ExitCode == 0 && !main.HasExited, "重复启动唤回现有实例");
        var overwritten = Start(config.Installer, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DIR=" + installation, "/LOG=" + Path.Combine(output, "busy-install.log")]);
        await overwritten.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Check(overwritten.ExitCode != 0 && !main.HasExited, "运行中拒绝覆盖安装，不强杀应用");
        await Task.Delay(10000);
        var cpu = main.TotalProcessorTime; var clock = Stopwatch.StartNew(); await Task.Delay(20000); main.Refresh();
        File.WriteAllText(Path.Combine(output, "idle-performance.json"), JsonSerializer.Serialize(new {
            WorkingSetMiB = main.WorkingSet64 / 1048576d, PrivateMiB = main.PrivateMemorySize64 / 1048576d,
            CpuMachinePercent = (main.TotalProcessorTime - cpu).TotalSeconds / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100, SampleSeconds = clock.Elapsed.TotalSeconds
        }, new JsonSerializerOptions { WriteIndented = true }));
        await Exit(installedExe, main);
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickCleanData", "settings.json");
        var settingsBefore = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        var reinstall = Start(config.Installer, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DIR=" + installation, "/LOG=" + Path.Combine(output, "reinstall.log")]);
        await reinstall.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        Check(reinstall.ExitCode == 0 && (settingsBefore is null || settingsBefore.SequenceEqual(File.ReadAllBytes(settingsPath))), "退出后覆盖安装保留设置");
        var uninstall = Start(Path.Combine(installation, "unins000.exe"), ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + Path.Combine(output, "uninstall.log")]);
        await uninstall.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        using var afterKey = Registry.CurrentUser.OpenSubKey(@"Software\ClickClean");
        Check(uninstall.ExitCode == 0 && !File.Exists(installedExe) && afterKey is null, "卸载移除程序与安装登记");
        Check(settingsBefore is null || settingsBefore.SequenceEqual(File.ReadAllBytes(settingsPath)), "默认卸载保留用户设置");
    }
    private static Process Start(string executable, string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var value in args) info.ArgumentList.Add(value);
        return Process.Start(info) ?? throw new InvalidOperationException("无法启动验收进程。");
    }
    private static Process? OwnProcess(string executable)
    {
        foreach (var process in Process.GetProcessesByName("ClickClean"))
        {
            try { if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return process; }
            catch (Win32Exception) { }
            process.Dispose();
        }
        return null;
    }
    private static async Task Exit(string executable, Process instance)
    {
        // 更新器会启动短命的钩子进程；等待真正的主实例出现再发送正常退出请求。
        await Task.Delay(2000);
        for (var i = 0; i < 30; i++)
        {
            var request = Start(executable, ["--exit"]);
            await request.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(500);
            var remaining = OwnProcess(executable);
            if (remaining is null) { await Task.Delay(500); if (OwnProcess(executable) is null) return; }
            remaining?.Dispose();
        }
        throw new TimeoutException("主程序尚未正常退出，不强制终止。");
    }
    private static async Task CheckStartup(string output)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value; var name = "ClickClean-Verification-" + sid + "-" + Guid.NewGuid().ToString("N");
        var probe = Path.Combine(output, "startup-probe.json");
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("计划任务服务不可用。");
        dynamic service = Activator.CreateInstance(type)!; service.Connect(); dynamic folder = service.GetFolder("\\");
        try
        {
            dynamic task = service.NewTask(0);
            try
            {
                task.Principal.UserId = sid; task.Principal.LogonType = 3; task.Principal.RunLevel = 1;
                task.Settings.DisallowStartIfOnBatteries = false; task.Settings.StopIfGoingOnBatteries = false;
                dynamic trigger = task.Triggers.Create(9); trigger.UserId = sid; Marshal.FinalReleaseComObject(trigger);
                dynamic action = task.Actions.Create(0); action.Path = Environment.ProcessPath!;
                action.Arguments = "--startup-probe \"" + probe + "\""; Marshal.FinalReleaseComObject(action);
                dynamic registered = folder.RegisterTaskDefinition(name, task, 6, sid, null, 3, null);
                try { dynamic instance = registered.Run(null); Marshal.FinalReleaseComObject(instance); }
                finally { Marshal.FinalReleaseComObject(registered); }
                for (var i = 0; i < 40 && !File.Exists(probe); i++) await Task.Delay(500);
                using var result = JsonDocument.Parse(File.ReadAllText(probe));
                if (!result.RootElement.GetProperty("Admin").GetBoolean()) throw new InvalidOperationException("测试登录任务未取得管理员权限。");
            }
            finally { Marshal.FinalReleaseComObject(task); }
        }
        finally { try { folder.DeleteTask(name, 0); } finally { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); } }
    }
}
