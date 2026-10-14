using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace ClickClean.Diagnostics;

internal static class UpdateVerification
{
    public static async Task Check(string feedPath, string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        UpdateManager Manager(string version, string area, string source)
        {
            var root = Path.Combine(output, area); var packages = Path.Combine(root, "packages"); Directory.CreateDirectory(packages);
            var locator = new TestVelopackLocator("ClickClean", version, packages, root, root, Path.Combine(root, "Update.exe"), "win");
            return new UpdateManager(new SimpleFileSource(new DirectoryInfo(source)), null, locator);
        }
        try
        {
            using var feed = JsonDocument.Parse(File.ReadAllText(Path.Combine(feedPath, "releases.win.json")));
            var targetVersion = feed.RootElement.GetProperty("Assets").EnumerateArray()
                .Where(row => row.GetProperty("Type").GetString() == "Full")
                .Select(row => row.GetProperty("Version").GetString()!)
                .OrderByDescending(value => Version.Parse(value)).First();
            var manager = Manager("0.0.1", "download", feedPath);
            var update = await manager.CheckForUpdatesAsync();
            Assert(update?.TargetFullRelease.Version.ToString() == targetVersion, "旧版本发现 " + targetVersion);
            await manager.DownloadUpdatesAsync(update!);
            var asset = update!.TargetFullRelease;
            var package = Path.Combine(output, "download", "packages", asset.FileName);
            Assert(File.Exists(package), "更新包已下载");
            using (var stream = File.OpenRead(package)) Assert(Convert.ToHexString(SHA256.HashData(stream)).Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase), "更新包 SHA-256 与发布源一致");
            Assert(manager.UpdatePendingRestart?.Version.ToString() == targetVersion, "下载后识别待重启版本");
            Assert(await Manager(targetVersion, "same", feedPath).CheckForUpdatesAsync() is null, "同版本不重复更新");
            Assert(await Manager("9999.0.0", "newer", feedPath).CheckForUpdatesAsync() is null, "默认拒绝降级");
            var brokenFeed = Path.Combine(output, "corrupt-feed"); Directory.CreateDirectory(brokenFeed);
            File.Copy(Path.Combine(feedPath, "releases.win.json"), Path.Combine(brokenFeed, "releases.win.json"), true);
            File.Copy(Path.Combine(feedPath, asset.FileName), Path.Combine(brokenFeed, asset.FileName), true);
            using (var stream = new FileStream(Path.Combine(brokenFeed, asset.FileName), FileMode.Open, FileAccess.ReadWrite)) { stream.Position = stream.Length / 2; var value = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(value ^ 0x01)); }
            var corruptManager = Manager("0.0.1", "corrupt-download", brokenFeed);
            var corruptUpdate = await corruptManager.CheckForUpdatesAsync(); var rejected = false;
            try { await corruptManager.DownloadUpdatesAsync(corruptUpdate!); }
            catch (Velopack.Exceptions.ChecksumFailedException) { rejected = true; }
            Assert(rejected, "损坏更新包被校验拒绝");
            Assert(corruptManager.UpdatePendingRestart is null, "损坏更新包不标记为就绪");
            await ServiceChecks(update!, feedPath, output, (value, message) => Assert(value, message));
            File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new { Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); throw; }
    }

    private static async Task ServiceChecks(UpdateInfo update, string feed, string output, Action<bool, string> assert)
    {
        var area = Path.Combine(output, "service-contracts"); Directory.CreateDirectory(area);
        var transport = new FaultTransport(update, area);
        using var service = new UpdateService(Path.Combine(area, "prepared.json"), () => transport, TimeSpan.FromMilliseconds(100));
        transport.Check = () => Task.FromException<UpdateInfo?>(new System.Net.Http.HttpRequestException("模拟旧代理拒绝连接", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));
        await service.CheckAsync(); assert(!service.Busy && service.Phase == UpdatePhase.Failed && !service.Ready && service.Status.Contains("托盘退出"), "旧代理连接拒绝提示完整退出重开，不修改系统配置或绕过代理");
        transport.Check = () => Task.FromException<UpdateInfo?>(new IOException("模拟离线"));
        await service.CheckAsync(); assert(!service.Busy && service.Phase == UpdatePhase.Failed && !service.Ready, "实际更新服务离线失败可恢复，不标记就绪");
        transport.Check = () => new TaskCompletionSource<UpdateInfo?>().Task;
        await service.CheckAsync(); assert(!service.Busy && service.Status.Contains("超时"), "实际更新服务检查超时解除忙碌状态");
        var checking = service.CheckAsync(); service.Cancel(); await checking;
        assert(!service.Busy && service.Status.Contains("取消"), "实际检查可取消，迟到检查不会更改UI状态");
        transport.Check = () => Task.FromResult<UpdateInfo?>(update);
        await service.CheckAsync(); assert(service.Available && !service.Ready && transport.Downloads == 0, "自动检查不进入下载或应用路径");
        transport.Download = async (progress, token) => { progress(45); await Task.Delay(Timeout.Infinite, token); };
        var download = service.DownloadAsync();
        await Task.Delay(10); assert(service.Busy && service.Progress == 45, "真实下载接口进度传递到应用服务");
        service.Cancel(); await download;
        assert(!service.Busy && !service.Ready && !File.Exists(Path.Combine(area, "prepared.json")), "取消下载不保存就绪标记");
        transport.Download = (_, _) => Task.FromException(new IOException("模拟磁盘空间不足"));
        await service.DownloadAsync(); assert(!service.Ready && !service.Busy && service.Status.Contains("空间不足"), "模拟磁盘不足保留旧版本并显示可重试错误");
        transport.Download = (_, _) => { File.WriteAllBytes(Path.Combine(area, update.TargetFullRelease.FileName), [1, 2, 3]); return Task.CompletedTask; };
        await service.DownloadAsync(); assert(!service.Ready && service.Phase == UpdatePhase.Failed, "实际服务拒绝损坏包而非只信传输成功");
        transport.Download = (progress, _) => { File.Copy(Path.Combine(feed, update.TargetFullRelease.FileName), Path.Combine(area, update.TargetFullRelease.FileName), true); progress(100); return Task.CompletedTask; };
        await service.DownloadAsync(); assert(service.Ready && !service.Busy && transport.Applies == 0, "失败后重新下载成功，校验就绪仍不自动应用");
        var downloads = transport.Downloads; await service.CheckAsync();
        assert(service.Ready && transport.Downloads == downloads && transport.Applies == 0, "重复检查保持已经校验的就绪状态");
        await service.ApplyAsync(false); assert(transport.Applies == 1, "仅显式应用更新调用重启接口（模拟重启）");
        File.WriteAllBytes(Path.Combine(area, update.TargetFullRelease.FileName), [1]);
        var rejected = false;
        try { await service.ApplyAsync(false); } catch (InvalidDataException) { rejected = true; }
        assert(rejected && transport.Applies == 1 && !service.Ready && service.Available, "重启前再次校验缓存，篡改不得应用且恢复重新下载入口");
        var recovery = new UpdateRecovery(area); File.WriteAllText(Path.Combine(area, "settings.json"), "{}");
        recovery.Prepare("2.3.1");
        recovery.BeginLaunch(); assert(!recovery.HasIncompleteUpdate, "首次正常启动不误报更新失败"); recovery.BeginLaunch();
        assert(new UpdateRecovery(area).HasIncompleteUpdate && File.Exists(Path.Combine(area, "before-update", "settings.json")), "首次启动未确认保留恢复标记和配置备份");
        recovery.MarkHealthy("2.4.0"); assert(!recovery.HasIncompleteUpdate && File.Exists(Path.Combine(area, "last-healthy-version.json")), "界面健康启动清除恢复标记");

        var nextMajor = new VelopackAsset {
            PackageId = update.TargetFullRelease.PackageId,
            Version = SemanticVersion.Parse((Version.Parse(BuildInfo.Version).Major + 1) + ".0.0"),
            Type = update.TargetFullRelease.Type, FileName = update.TargetFullRelease.FileName,
            SHA1 = update.TargetFullRelease.SHA1, SHA256 = update.TargetFullRelease.SHA256, Size = update.TargetFullRelease.Size
        };
        var migration = new FaultTransport(new UpdateInfo(nextMajor, false, null!, []), area);
        using var majorService = new UpdateService(Path.Combine(area, "major.json"), () => migration, componentAvailable: () => true);
        await majorService.CheckAsync(); await majorService.DownloadAsync(); await majorService.ApplyAsync(false);
        assert(majorService.RequiresInstaller && majorService.Available && !majorService.Ready && migration.Downloads == 0 && migration.Applies == 0,
            "跨主版本系统组件迁移只提示正式安装包，绝不下载后提权替换");
        var missingTransport = new FaultTransport(update, area) { Check = () => Task.FromResult<UpdateInfo?>(null) };
        using var missing = new UpdateService(Path.Combine(area, "missing.json"), () => missingTransport, componentAvailable: () => false);
        await missing.CheckAsync();
        assert(missing.SystemComponentMissing && !missing.RequiresInstaller && missing.Phase == UpdatePhase.Current && missing.Status.Contains("日常优化可用"),
            "缺少高级组件不阻断日常优化，明确提示安装迁移");
    }

    private sealed class FaultTransport(UpdateInfo update, string packages) : IUpdateTransport
    {
        public Func<Task<UpdateInfo?>> Check { get; set; } = () => Task.FromResult<UpdateInfo?>(update);
        public Func<Action<int>, CancellationToken, Task> Download { get; set; } = (_, _) => Task.CompletedTask;
        public VelopackAsset? Pending => null;
        public string PackagesDirectory => packages;
        public int Downloads { get; private set; }
        public int Applies { get; private set; }
        public Task<UpdateInfo?> CheckAsync() => Check();
        public Task DownloadAsync(UpdateInfo value, Action<int> progress, CancellationToken cancellation) { Downloads++; return Download(progress, cancellation); }
        public void Apply(VelopackAsset value, bool tray) => Applies++;
    }

    public static async Task ApplyFixture(string root, string feedPath, string resultPath)
    {
        if (!WindowsMemoryApi.IsAdmin()) throw new InvalidOperationException("更新重启实测需要管理员授权。");
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, ".click-clean-update-fixture"))) throw new InvalidOperationException("只允许更新带专用标记的测试目录。");
        var process = new TargetProcess(Path.Combine(root, "current", "ClickClean.exe"));
        var locator = new WindowsVelopackLocator(process, null);
        var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(feedPath)), null, locator);
        var update = await manager.CheckForUpdatesAsync() ?? throw new InvalidOperationException("测试目录未发现新版本。");
        await manager.DownloadUpdatesAsync(update);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new { From = manager.CurrentVersion?.ToString(), To = update.TargetFullRelease.Version.ToString(), Downloaded = true, Applying = true }));
        manager.ApplyUpdatesAndRestart(update.TargetFullRelease, ["--tray"]);
    }

    // 本机交付通道：仅更新当前用户已注册的默认安装，不执行安装向导或真实整理。
    public static async Task ApplyInstalled(string feedPath, string resultPath)
    {
        if (!WindowsMemoryApi.IsAdmin()) throw new InvalidOperationException("本机更新需要管理员授权。");
        using var registration = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ClickClean_is1");
        var root = Path.GetFullPath(registration?.GetValue("InstallLocation") as string ?? throw new InvalidOperationException("没有已注册的 Click-Clean 安装。"));
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ClickClean");
        if (!root.TrimEnd(Path.DirectorySeparatorChar).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("只允许更新已核验的当前用户默认安装。");
        var executable = Path.Combine(root, "current", "ClickClean.exe");
        if (!File.Exists(executable) || !File.Exists(Path.Combine(root, "Update.exe"))) throw new InvalidOperationException("安装布局不完整，不替换文件。");
        var locator = new WindowsVelopackLocator(new TargetProcess(executable), null);
        var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(Path.GetFullPath(feedPath))), null, locator);
        var update = await manager.CheckForUpdatesAsync() ?? throw new InvalidOperationException("本地发布源没有比已安装版更新的正式包。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await manager.DownloadUpdatesAsync(update, null, timeout.Token);
        var asset = update.TargetFullRelease;
        if (Path.GetFileName(asset.FileName) != asset.FileName) throw new InvalidDataException("更新文件名无效。");
        var package = Path.Combine(locator.PackagesDir!, asset.FileName);
        await using (var stream = File.OpenRead(package)) {
            if (stream.Length != asset.Size || !Convert.ToHexString(await SHA256.HashDataAsync(stream, timeout.Token)).Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包校验失败，不替换文件。");
        }
        var before = manager.CurrentVersion?.ToString();
        var exit = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true }; exit.ArgumentList.Add("--exit");
        using (var command = Process.Start(exit) ?? throw new InvalidOperationException("无法请求旧实例正常退出。")) await command.WaitForExitAsync(timeout.Token);
        while (InstalledRunning(executable)) await Task.Delay(250, timeout.Token);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new { From = before, To = asset.Version.ToString(), SHA256 = asset.SHA256, Downloaded = true, GracefulExit = true, Applying = true }));
        manager.ApplyUpdatesAndRestart(asset, ["--tray"]);
    }
    private static bool InstalledRunning(string executable)
    {
        foreach (var process in Process.GetProcessesByName("ClickClean")) {
            using (process) {
                try { if (!process.HasExited && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return true; }
                catch (InvalidOperationException) { /* 检查期间已经退出。 */ }
            }
        }
        return false;
    }
    private sealed class TargetProcess(string executable) : IProcessImpl
    {
        public string GetCurrentProcessPath() => executable;
        public uint GetCurrentProcessId() => (uint)Environment.ProcessId;
        public void Exit(int exitCode) => Environment.Exit(exitCode);
        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
        {
            var info = new ProcessStartInfo(exePath) { WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = !showWindow };
            foreach (var argument in args) info.ArgumentList.Add(argument);
            Process.Start(info);
        }
    }
}
