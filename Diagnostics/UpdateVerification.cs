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
            File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new { Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); throw; }
    }

    public static async Task ApplyFixture(string root, string feedPath, string resultPath)
    {
        if (!WindowsMemoryApi.IsAdmin()) throw new InvalidOperationException("更新重启实测需要管理员授权。");
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, ".click-clean-update-fixture"))) throw new InvalidOperationException("只允许更新带专用标记的测试目录。");
        var process = new FixtureProcess(Path.Combine(root, "current", "ClickClean.exe"));
        var locator = new WindowsVelopackLocator(process, null);
        var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(feedPath)), null, locator);
        var update = await manager.CheckForUpdatesAsync() ?? throw new InvalidOperationException("测试目录未发现新版本。");
        await manager.DownloadUpdatesAsync(update);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new { From = manager.CurrentVersion?.ToString(), To = update.TargetFullRelease.Version.ToString(), Downloaded = true, Applying = true }));
        manager.ApplyUpdatesAndRestart(update.TargetFullRelease, ["--tray"]);
    }

    private sealed class FixtureProcess(string executable) : IProcessImpl
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
