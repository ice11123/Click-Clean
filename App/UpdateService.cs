using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace ClickClean;

public static class BuildInfo
{
    public static string Version => typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "开发版";
    public static string Repository => typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value ?? "";
    public static string Commit => typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "开发构建";
}

public interface IApplicationUpdates : IDisposable
{
    bool Busy { get; }
    bool Ready { get; }
    bool Available { get; }
    string Status { get; }
    UpdatePhase Phase { get; }
    event Action? Changed;
    Task CheckAsync();
    Task DownloadAsync();
    Task ApplyAsync(bool tray);
}

public sealed class UpdateService : IApplicationUpdates
{
    private readonly string statePath;
    public UpdateService(string statePath) => this.statePath = statePath;
    private sealed record PreparedUpdate(string FileName, string SHA256, long Size, string Version);
    private UpdateManager? manager;
    private UpdateInfo? available;
    private VelopackAsset? readyAsset;
    private readonly CancellationTokenSource shutdown = new();
    private int gate;
    public bool Busy => Volatile.Read(ref gate) != 0;
    public bool Ready { get; private set; }
    public bool Available => available is not null;
    public string Status { get; private set; } = "尚未检查更新";
    public UpdatePhase Phase { get; private set; }
    public event Action? Changed;
    private void Report(string value, UpdatePhase phase) { Status = value; Phase = phase; Changed?.Invoke(); }
    // 自动检查没有下载参数，无法因旧设置而进入下载或应用路径。
    public Task CheckAsync() => RunAsync(false);
    public Task DownloadAsync() => RunAsync(true);
    private async Task RunAsync(bool download)
    {
        if (Interlocked.CompareExchange(ref gate, 1, 0) != 0) return;
        try
        {
            if (!Uri.TryCreate(BuildInfo.Repository, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com")
            { Report("此开发构建尚未连接已验证的发布仓库。", UpdatePhase.Failed); return; }
            manager ??= new UpdateManager(new GithubSource(BuildInfo.Repository, null, false));
            if (readyAsset is null && manager.UpdatePendingRestart is { } pending && File.Exists(statePath))
            {
                try
                {
                    var state = JsonSerializer.Deserialize<PreparedUpdate>(await File.ReadAllTextAsync(statePath));
                    if (state is not null && state.FileName == pending.FileName && state.Version == pending.Version.ToString())
                    { await VerifyAsync(pending, state.SHA256, state.Size); readyAsset = pending; }
                }
                catch { /* 待更新记录损坏时保持旧版运行，可重新检查下载。 */ }
            }
            Ready = readyAsset is not null;
            Report("正在检查正式版本…", UpdatePhase.Checking);
            available = await manager.CheckForUpdatesAsync();
            if (available is null) { Report(Ready ? "已下载更新，点击重启后生效。" : "当前已是最新正式版本。", Ready ? UpdatePhase.Ready : UpdatePhase.Current); return; }
            Report($"发现 {available.TargetFullRelease.Version} · 由你决定是否下载更新。", Ready ? UpdatePhase.Ready : UpdatePhase.Available);
            if (download)
            {
                Report("正在下载并校验更新，不会自动重启…", UpdatePhase.Downloading);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                await manager.DownloadUpdatesAsync(available, null, timeout.Token);
                // 对已有缓存也重新核验，不能仅因为文件存在便将其视为已验证。
                await VerifyAsync(available.TargetFullRelease, available.TargetFullRelease.SHA256, available.TargetFullRelease.Size);
                var record = new PreparedUpdate(available.TargetFullRelease.FileName, available.TargetFullRelease.SHA256,
                    available.TargetFullRelease.Size, available.TargetFullRelease.Version.ToString());
                await File.WriteAllTextAsync(statePath + ".tmp", JsonSerializer.Serialize(record));
                File.Move(statePath + ".tmp", statePath, true);
                readyAsset = available.TargetFullRelease;
                Ready = true;
                Report("更新已就绪 · 点击重启并更新，无需安装向导。", UpdatePhase.Ready);
            }
        }
        catch (Exception ex)
        {
            Ready = readyAsset is not null;
            Report(ex is Velopack.Exceptions.NotInstalledException
                ? "这是未打包的开发目录；应用内更新需使用正式安装版或 Velopack 便携版。"
                : "更新未完成，现有版本不变：" + ex.Message, UpdatePhase.Failed);
        }
        finally { Volatile.Write(ref gate, 0); Changed?.Invoke(); }
    }
    public async Task ApplyAsync(bool tray)
    {
        if (!Ready || Busy || readyAsset is null || manager is null) return;
        var state = JsonSerializer.Deserialize<PreparedUpdate>(await File.ReadAllTextAsync(statePath))
            ?? throw new InvalidDataException("缺少已校验的更新记录，请重新下载更新。");
        await VerifyAsync(readyAsset, state.SHA256, state.Size);
        // 仅由界面的统一协调入口在无整理调用时执行；不使用会限时杀进程的等待 API。
        manager.ApplyUpdatesAndRestart(readyAsset, restartArgs: tray ? new[] { "--tray" } : Array.Empty<string>());
    }
    private static async Task VerifyAsync(VelopackAsset asset, string expectedHash, long expectedSize)
    {
        if (Path.GetFileName(asset.FileName) != asset.FileName || string.IsNullOrWhiteSpace(asset.FileName))
            throw new InvalidDataException("更新文件名无效。");
        var path = Path.Combine(VelopackLocator.Current.PackagesDir ?? throw new InvalidOperationException("更新缓存目录不可用。"), asset.FileName);
        if (expectedHash.Length != 64 || new FileInfo(path).Length != expectedSize)
            throw new InvalidDataException("更新包尺寸或校验信息无效，请重新下载。");
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包已损坏，请重新下载；当前版本未被替换。");
    }
    public void Dispose() => shutdown.Cancel();
}
