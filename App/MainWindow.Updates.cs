namespace ClickClean;

public partial class MainWindow
{
    private async Task CheckUpdates()
    {
        if (applying || disposed) return;
        if (preview && !simulatedUpdates) { Presentation.UpdateText = "交互预览不会访问发布服务或替换程序。"; return; }
        await updates.CheckAsync();
    }
    private void UpdateChanged()
    {
        if (disposed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateChanged); return; }
        Presentation.UpdateText = updates.Status; Presentation.UpdateBusy = updates.Busy;
        Presentation.UpdateReady = updates.Ready; Presentation.UpdateAvailable = updates.Available && !updates.Ready;
        Presentation.Downloading = updates.Busy && updates.Phase == UpdatePhase.Downloading;
        Presentation.DownloadIndeterminate = updates.Progress is null; Presentation.DownloadProgress = updates.Progress ?? 0;
        var value = UpdatePresentation.Create(updates.Phase, updates.Available, updates.Ready, updates.Busy, IsBusy || applying);
        Presentation.UpdateVisible = value.Visible; Presentation.UpdateButton = value.ButtonText; Presentation.UpdateActionEnabled = value.Enabled;
    }
    private async Task ApplyUpdate()
    {
        if (preview && !simulatedUpdates || IsBusy || applying || updates.Busy || !updates.Ready || !CheckDraft()) return;
        applying = true; Presentation.Applying = true; sampler.Stop(); SaveSettings(); UpdateChanged();
        try {
            cleanup.StopObservation("准备重启更新，持续观测停止。");
            if (!preview) recovery.Prepare(BuildInfo.Version);
            await updates.ApplyAsync(!IsVisible);
            if (simulatedUpdates) { applying = false; Presentation.Applying = false; sampler.Start(); UpdateChanged(); }
        }
        catch (Exception ex) {
            if (!preview) recovery.CancelAttempt();
            applying = false; Presentation.Applying = false; Presentation.UpdateText = "更新应用失败，现有版本保留：" + ex.Message; Presentation.UpdateVisible = true;
            store.Log(ex.ToString()); sampler.Start(); ShowMain();
        }
    }
}
