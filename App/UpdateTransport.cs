using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace ClickClean;

public interface IUpdateTransport
{
    VelopackAsset? Pending { get; }
    string PackagesDirectory { get; }
    Task<UpdateInfo?> CheckAsync();
    Task DownloadAsync(UpdateInfo update, Action<int> progress, CancellationToken cancellation);
    void Apply(VelopackAsset asset, bool tray);
}

public sealed class VelopackUpdateTransport(UpdateManager manager) : IUpdateTransport
{
    public VelopackUpdateTransport() : this(new UpdateManager(new GithubSource(BuildInfo.Repository, null, false))) { }
    public VelopackAsset? Pending => manager.UpdatePendingRestart;
    public string PackagesDirectory => VelopackLocator.Current.PackagesDir ?? throw new InvalidOperationException("更新缓存目录不可用。");
    public Task<UpdateInfo?> CheckAsync() => manager.CheckForUpdatesAsync();
    public Task DownloadAsync(UpdateInfo update, Action<int> progress, CancellationToken cancellation) => manager.DownloadUpdatesAsync(update, progress, cancellation);
    public void Apply(VelopackAsset asset, bool tray) => manager.ApplyUpdatesAndRestart(asset, restartArgs: tray ? new[] { "--tray" } : Array.Empty<string>());
}
