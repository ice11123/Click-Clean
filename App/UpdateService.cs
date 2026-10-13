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
    public static bool ExperimentalX86 => typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "BuildFlavor" && a.Value == "experimental-x86");
    public static string PlatformLabel => ExperimentalX86 ? "Windows 11 · x86试验版" : "Windows 11 x64";
    public static bool PlatformSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64 &&
        (Environment.Is64BitProcess || ExperimentalX86);
}

public interface IApplicationUpdates : IDisposable
{
    bool Busy { get; }
    bool Ready { get; }
    bool Available { get; }
    string Status { get; }
    UpdatePhase Phase { get; }
    int? Progress { get; }
    event Action? Changed;
    Task CheckAsync();
    Task DownloadAsync();
    Task ApplyAsync(bool tray);
    void Cancel();
}

public sealed class UpdateService : IApplicationUpdates
{
    private readonly string statePath;
    private readonly Func<IUpdateTransport> transportFactory;
    private readonly TimeSpan checkTimeout;
    public UpdateService(string statePath, Func<IUpdateTransport>? transportFactory = null, TimeSpan? checkTimeout = null)
    {
        this.statePath = statePath; this.transportFactory = transportFactory ?? (() => new VelopackUpdateTransport());
        this.checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(30);
        if (this.checkTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(checkTimeout));
    }
    private sealed record PreparedUpdate(string FileName, string SHA256, long Size, string Version);
    private IUpdateTransport? transport;
    private UpdateInfo? available;
    private VelopackAsset? readyAsset;
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? operation;
    private int gate;
    public bool Busy => Volatile.Read(ref gate) != 0;
    public bool Ready { get; private set; }
    public bool Available => available is not null;
    public string Status { get; private set; } = "尚未检查更新";
    public UpdatePhase Phase { get; private set; }
    public int? Progress { get; private set; }
    public event Action? Changed;
    private void Report(string value, UpdatePhase phase) { Status = value; Phase = phase; Changed?.Invoke(); }
    // 自动检查没有下载参数，无法因旧设置而进入下载或应用路径。
    public Task CheckAsync() => RunAsync(false);
    public Task DownloadAsync() => RunAsync(true);
    private async Task RunAsync(bool download)
    {
        // 试验包与正式更新清单隔离，禁止32位程序下载64位包后覆盖自身。
        if (BuildInfo.ExperimentalX86) { Report("32位试验版请从发行页手动下载新版；不使用正式版更新通道。", UpdatePhase.Current); return; }
        if (Interlocked.CompareExchange(ref gate, 1, 0) != 0) return;
        using var active = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        operation = active; Progress = null;
        try
        {
            if (!Uri.TryCreate(BuildInfo.Repository, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com")
            { Report("此开发构建尚未连接已验证的发布仓库。", UpdatePhase.Failed); return; }
            transport ??= transportFactory();
            if (readyAsset is null && transport.Pending is { } pending && File.Exists(statePath))
            {
                try
                {
                    var state = JsonSerializer.Deserialize<PreparedUpdate>(await File.ReadAllTextAsync(statePath));
                    if (state is not null && state.FileName == pending.FileName && state.Version == pending.Version.ToString())
                    { await VerifyAsync(pending, state.SHA256, state.Size, active.Token); readyAsset = pending; }
                }
                catch { /* 待更新记录损坏时保持旧版运行，可重新检查下载。 */ }
            }
            Ready = readyAsset is not null;
            Report("正在检查正式版本…", UpdatePhase.Checking);
            available = await transport.CheckAsync().WaitAsync(checkTimeout, active.Token);
            if (available is null) { Report(Ready ? "已下载更新，点击重启后生效。" : "当前已是最新正式版本。", Ready ? UpdatePhase.Ready : UpdatePhase.Current); return; }
            Report($"发现 {available.TargetFullRelease.Version} · 由你决定是否下载更新。", Ready ? UpdatePhase.Ready : UpdatePhase.Available);
            if (download)
            {
                Report("正在下载并校验更新，不会自动重启…", UpdatePhase.Downloading);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                Progress = 0;
                await transport.DownloadAsync(available, value => {
                    if (!timeout.IsCancellationRequested && ReferenceEquals(operation, active)) {
                        Progress = Math.Clamp(value, 0, 100);
                        Report($"下载 {Progress}% · 可取消，完成后由你重启更新", UpdatePhase.Downloading);
                    }
                }, timeout.Token);
                // 对已有缓存也重新核验，不能仅因为文件存在便将其视为已验证。
                await VerifyAsync(available.TargetFullRelease, available.TargetFullRelease.SHA256, available.TargetFullRelease.Size, timeout.Token);
                var record = new PreparedUpdate(available.TargetFullRelease.FileName, available.TargetFullRelease.SHA256,
                    available.TargetFullRelease.Size, available.TargetFullRelease.Version.ToString());
                await File.WriteAllTextAsync(statePath + ".tmp", JsonSerializer.Serialize(record), timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                File.Move(statePath + ".tmp", statePath, true);
                readyAsset = available.TargetFullRelease;
                Ready = true;
                Progress = 100;
                Report("更新已就绪 · 点击重启并更新，无需安装向导。", UpdatePhase.Ready);
            }
        }
        catch (TimeoutException) { Report("检查更新超时，可重试；当前版本继续运行。", UpdatePhase.Failed); }
        catch (OperationCanceledException) {
            Ready = readyAsset is not null;
            Report(shutdown.IsCancellationRequested ? "更新操作已结束。" : active.IsCancellationRequested ? "已取消更新操作，可重新检查或下载。" : "下载超时，可重新下载。", Ready ? UpdatePhase.Ready : UpdatePhase.Failed);
        }
        catch (Exception ex)
        {
            Ready = readyAsset is not null;
            Report(ex is Velopack.Exceptions.NotInstalledException
                ? "这是未打包的开发目录；应用内更新需使用正式安装版或 Velopack 便携版。"
                : "更新未完成，现有版本不变：" + ex.Message, UpdatePhase.Failed);
        }
        finally { operation = null; Volatile.Write(ref gate, 0); Changed?.Invoke(); }
    }
    public async Task ApplyAsync(bool tray)
    {
        if (!Ready || Busy || readyAsset is null || transport is null) return;
        try {
            var state = JsonSerializer.Deserialize<PreparedUpdate>(await File.ReadAllTextAsync(statePath))
                ?? throw new InvalidDataException("缺少已校验的更新记录，请重新下载更新。");
            if (state.FileName != readyAsset.FileName || state.Version != readyAsset.Version.ToString()) throw new InvalidDataException("更新记录与缓存版本不一致。");
            await VerifyAsync(readyAsset, state.SHA256, state.Size);
        }
        catch {
            readyAsset = null; Ready = false;
            Report("更新缓存校验失败，请重新下载后再重启。", UpdatePhase.Failed);
            throw;
        }
        // 仅由界面的统一协调入口在无整理调用时执行；不使用会限时杀进程的等待 API。
        transport.Apply(readyAsset, tray);
    }
    private async Task VerifyAsync(VelopackAsset asset, string expectedHash, long expectedSize, CancellationToken cancellation = default)
    {
        if (Path.GetFileName(asset.FileName) != asset.FileName || string.IsNullOrWhiteSpace(asset.FileName))
            throw new InvalidDataException("更新文件名无效。");
        var path = Path.Combine(transport?.PackagesDirectory ?? throw new InvalidOperationException("更新缓存目录不可用。"), asset.FileName);
        if (expectedHash.Length != 64 || new FileInfo(path).Length != expectedSize)
            throw new InvalidDataException("更新包尺寸或校验信息无效，请重新下载。");
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation));
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包已损坏，请重新下载；当前版本未被替换。");
    }
    public void Cancel() { try { operation?.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose() => shutdown.Cancel();
}
