using System.ComponentModel;
using Microsoft.Win32;

namespace ClickClean;

public partial class MainWindow
{
    private void LoadPreferences()
    {
        loadingSettings = true;
        try {
            var value = store.Settings; var state = Presentation.Settings;
            state.Theme = value.Theme switch { "dark" => 1, "system" => 2, _ => 0 };
            state.Language = value.Language == "en-US" ? 1 : 0;
            state.ReduceMotion = value.ReduceMotion; state.Island = value.MiniIsland; state.CloseToTray = value.CloseToTray; state.Startup = value.Startup; state.CheckUpdates = value.CheckUpdates;
            if (!state.Draft.Dirty) { state.Draft.Load(value.Automation); if (value.AutomationNeedsConfirmation) state.Draft.Feedback = "旧自动配置已暂停，请核对执行方式并确认保存。"; }
        }
        finally { loadingSettings = false; }
    }
    private void PreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (loadingSettings || disposed || e.PropertyName is not ("Theme" or "Language" or "ReduceMotion" or "Island" or "CloseToTray" or "Startup" or "CheckUpdates")) return;
        var state = Presentation.Settings; var value = store.Settings;
        var old = (value.Theme, value.Language, value.LanguageChosen, value.ReduceMotion, value.MiniIsland, value.CloseToTray, value.Startup, value.CheckUpdates);
        try {
            if (e.PropertyName == "Startup") {
                if (BuildInfo.ExperimentalX86) throw new NotSupportedException("32位试验包暂不提供登录启动，请使用推荐的64位正式版。");
                if (preview) { state.Feedback = "交互预览不创建登录任务。"; LoadPreferences(); return; }
                StartupTask.Set(state.Startup);
            }
            value.Theme = state.Theme switch { 1 => "dark", 2 => "system", _ => "light" };
            value.Language = state.Language == 1 ? "en-US" : "zh-CN";
            if (e.PropertyName == "Language") value.LanguageChosen = true;
            value.ReduceMotion = state.ReduceMotion; value.MiniIsland = state.Island; value.CloseToTray = state.CloseToTray; value.Startup = state.Startup; value.CheckUpdates = state.CheckUpdates;
            value.AutoDownloadUpdates = value.AutoApplyHidden = false;
            store.SaveSettings(); state.Feedback = ""; Localization.Language = value.Language; ApplyTheme();
            if (value.MiniIsland) ShowIsland(); else mini?.Hide();
            SetSampling();
        }
        catch (Exception ex) {
            value.Theme = old.Theme; value.ReduceMotion = old.ReduceMotion; value.MiniIsland = old.MiniIsland; value.CloseToTray = old.CloseToTray; value.Startup = old.Startup; value.CheckUpdates = old.CheckUpdates;
            value.Language = old.Language; value.LanguageChosen = old.LanguageChosen; Localization.Language = value.Language;
            if (e.PropertyName == "Startup" && !preview && !BuildInfo.ExperimentalX86) {
                try { StartupTask.Set(old.Startup); } catch (Exception rollback) { store.Log("登录任务恢复失败：" + rollback); value.Startup = StartupTask.Exists(); }
            }
            LoadPreferences(); state.Feedback = "设置未保存：" + ex.Message; store.Log(ex.ToString());
        }
    }
    private bool SaveSettings()
    {
        try { store.SaveSettings(); return true; }
        catch (Exception ex) { Presentation.Settings.Feedback = "设置保存失败：" + ex.Message; store.Log(ex.ToString()); return false; }
    }
    private bool SaveAutomation()
    {
        var draft = Presentation.Settings.Draft;
        if (!draft.TryBuild(out var value, out var field)) {
            if (SettingsPane.FindName(field) is UIElement target) { target.Focus(); if (target is FrameworkElement element) element.BringIntoView(); }
            return false;
        }
        if ((value.ThresholdEnabled || value.TimerEnabled) && !preview) {
            var warning = value.TriggerMode == AutomaticTriggerMode.Percentage
                ? "达到占用门槛后执行全系统三步，使用安装时授权，可能增加缺页和磁盘活动。不保证提速。继续？"
                : "保存后，仅在 Windows 确认低内存时整理闲置后台。不会执行全系统三步。继续？";
            if (MessageBox.Show(this, Localization.Text(warning), Localization.Text("确认自动整理规则"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return false;
        }
        var previous = store.Settings.Automation; var confirmation = store.Settings.AutomationNeedsConfirmation;
        store.Settings.Automation = value; store.Settings.AutomationNeedsConfirmation = false;
        if (!SaveSettings()) { store.Settings.Automation = previous; store.Settings.AutomationNeedsConfirmation = confirmation; draft.Feedback = Presentation.Settings.Feedback; return false; }
        automation.Configure(value); cleanup.CancelAutomaticIfDisabled(); draft.Load(value);
        draft.Feedback = preview ? "已保存模拟配置，不执行系统自动整理。" : "已保存，软件运行期间生效。"; RefreshAutomationSummary(); return true;
    }
    private void RefreshAutomationSummary()
    {
        var value = store.Settings.Automation;
        Presentation.AutomaticBrief = store.Settings.AutomationNeedsConfirmation ? "待确认  ›" : automation.Paused ? "已暂停  ›" :
            !value.ThresholdEnabled && !value.TimerEnabled ? "已关闭  ›" : automation.DeferredReason is not null ? "暂缓执行  ›" : "已开启  ›";
        Presentation.Automatic = store.Settings.AutomationNeedsConfirmation ? "自动规则待确认" : automation.Paused ? "自动整理已暂停 · 连续失败" :
            automation.DeferredReason is { } reason ? "自动整理暂缓 · " + reason :
            !value.ThresholdEnabled && !value.TimerEnabled ? "自动整理：关闭" :
            "自动整理：" + (value.ThresholdEnabled ? value.TriggerMode == AutomaticTriggerMode.SystemPressure ? "系统压力" : $"{value.ThresholdPercent}% / {value.SustainSeconds}秒" : "") +
            (value.ThresholdEnabled && value.TimerEnabled ? " · " : "") + (value.TimerEnabled ? $"每{value.IntervalMinutes}分钟检查" : "");
    }
    private void ClearHistory()
    {
        if (MessageBox.Show(this, Localization.Text("清空本地整理记录？"), Localization.Text("清空记录"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        cleanup.StopObservation(); store.Clear(); Presentation.Recent = null; Presentation.Detail = null; RefreshHistory(); ClosePopup();
    }
    private void ExportLog()
    {
        var dialog = new SaveFileDialog { Title = Localization.Text("导出诊断日志"), FileName = "Click-Clean-diagnostic.txt", Filter = Localization.Text("文本文件|*.txt") };
        if (dialog.ShowDialog(this) != true) return;
        store.Export(dialog.FileName, BuildInfo.Version); Presentation.ProcessFeedback = "已导出。分享前请检查个人信息。";
    }
    private void DeleteData()
    {
        if (IsBusy || updates.Busy || applying) { Presentation.Settings.Feedback = "请等当前操作结束后再清除数据。"; return; }
        if (MessageBox.Show(this, Localization.Text("删除即清自己的设置、历史和诊断日志并退出？不删除旧清栖数据。"), Localization.Text("清除本地数据"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        cleanup.StopObservation(); if (!preview) StartupTask.Set(false);
        foreach (var file in new[] { "settings.json", "settings.json.tmp", "history.json", "history.json.tmp", "diagnostic.log", "diagnostic.log.previous", "migration.json", "update-state.json", "update-state.json.tmp" }) File.Delete(Path.Combine(store.Root, file));
        DisposeResources(); Application.Current.Shutdown();
    }
}
