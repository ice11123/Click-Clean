using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ClickClean.ViewModels;

public abstract class BindableState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(name); return true;
    }
    protected void Notify([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new(name));
    public virtual void RefreshLanguage() => Notify("");
}

public sealed class UiCommand(Func<object?, Task> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public async void Execute(object? parameter) => await execute(parameter);
}

public sealed class ResultRow : BindableState
{
    private CleanupResult result;
    private bool pendingObservation;
    public ResultRow(CleanupResult value) => result = value;
    public Guid Id => result.Id;
    public CleanupResult Result => result;
    private bool IsBackground => result.BackgroundCleanup is not null || result.Steps.Any(s => s.Command == MemoryCommand.BackgroundWorkingSets);
    public string Outcome => Localization.Text(result.Cancelled ? "已取消" : result.Error is not null ? (result.BackgroundCleanup?.Completed > 0 || result.Steps.Any(s => s.Success)) ? "部分完成" : "整理失败" : IsBackground ?
        result.BackgroundCleanup is not { } background ? "状态暂不可用" : background.Completed > 0 ? background.Failed > 0 ? "部分完成" : "已完成" : background.Failed > 0 ? "整理失败" : "无需整理" :
        result.Success ? "步骤完成" : result.Steps.Any(s => s.Success) ? "部分完成" : "整理失败");
    public string Delta => IsBackground ? HomeDelta : Localization.Text(result.FollowUp is null ? "即时可用 " : "复测可用 ") + Localization.Text(Labels.Delta(result.PreferredChange));
    public string Meta => $"{result.Time:MM-dd HH:mm} · {Localization.Text(result.Trigger)} · {result.Seconds:0.0} {Localization.Text("秒")}";
    public string HomeOutcome => Localization.Format("上次整理 · {0}", Outcome);
    public string HomeDelta => IsBackground ? result.BackgroundCleanup is { Completed: > 0 } background ? Localization.Format("已整理 {0} 个后台", background.Completed) : result.Cancelled || result.Error is not null ? Outcome : BackgroundReason :
        result.Steps.Count == 0 && (result.Cancelled || result.Error is not null) ? Outcome :
        Localization.Text("可用内存 ") + Localization.Text(Labels.Delta(result.PreferredChange));
    private string BackgroundReason => Localization.Text(result.BackgroundCleanup?.StopReason switch {
        BackgroundStopReason.NoPressure => "当前无需整理", BackgroundStopReason.NoIdleCandidates => "未找到符合条件的闲置后台",
        BackgroundStopReason.ActivityChanged => "后台活动已变化，停止整理",
        BackgroundStopReason.PressureRelieved => "低内存压力已缓解", BackgroundStopReason.PressureUnavailable or BackgroundStopReason.SamplingUnavailable => "状态暂不可用", _ => "未执行整理"
    });
    public string Observation => result.Sustained is not null ? Localization.Format("{0} 秒观测 {1}", $"{result.SustainedSeconds:0}", Localization.Text(Labels.Delta(result.SustainedChange))) :
        result.SustainedError is not null ? Localization.Text("持续观测：") + Localization.Text(result.SustainedError) : Localization.Text(pendingObservation ? "30 秒持续观测中，可继续使用" : "持续观测：无记录");
    public void SetPending(bool value) { if (pendingObservation == value) return; pendingObservation = value; Notify(nameof(Observation)); }
    public string Details => $"{Outcome}\n{result.Time:yyyy-MM-dd HH:mm:ss} · {Localization.Text(result.Trigger)}\n\n" +
        string.Join("\n", result.Steps.Select(s => $"{(s.Success ? "✓" : "!")} {Localization.Text(Labels.Name(s.Command))} · {s.Seconds:0.00} {Localization.Text("秒")} · {s.StatusHex}")) +
        BackgroundDetails + "\n\n" + Localization.Format("即时可用变化：{0}", Localization.Text(Labels.Delta(result.AvailableChange))) + "\n" + Localization.Format("即时提交变化：{0}", Localization.Text(Labels.Delta(result.CommitChange))) + "\n" +
        (result.FollowUp is null ? Localization.Text("复测：未完成 / 无记录") : Localization.Format("即时采样后 {0} 秒复测：{1}", $"{result.FollowUpSeconds:0.00}", Localization.Text(Labels.Delta(result.FollowUpChange)))) +
        "\n" + Observation + "\n" + Localization.Format("整理前提交量 / 上限：{0} / {1}", Labels.Bytes(result.Before.Commit), Labels.Bytes(result.Before.CommitLimit)) +
        (result.Before.SystemCache is { } cache ? "\n" + Localization.Format("系统缓存（含系统工作集，不是纯待机）：{0}", Labels.Bytes(cache)) : "") +
        (result.Error is null ? "" : "\n\n" + Localization.Text(result.Error)) + (result.ObservationError is null ? "" : "\n" + Localization.Text(result.ObservationError)) +
        "\n\n" + Localization.Text("前后变化包含系统其他活动，不代表性能提升。");
    private string BackgroundDetails => result.BackgroundCleanup is not { } background ? "" : "\n" +
        Localization.Format("后台：检查 {0} · 尝试 {1} · 完成 {2} · 失败 {3}", background.Examined, background.Attempted, background.Completed, background.Failed) + "\n" + BackgroundReason +
        "\n" + Localization.Format("工作集变化：{0}", Localization.Text(Labels.Delta(background.WorkingSetChange))) +
        string.Join("", background.Failures.Select(f => $"\nPID {f.ProcessId} · Win32 {f.Win32Error}"));
    public void Update(CleanupResult value)
    {
        result = value;
        foreach (var name in new[] { nameof(Result), nameof(Outcome), nameof(Delta), nameof(Meta), nameof(Observation), nameof(Details), nameof(HomeOutcome), nameof(HomeDelta) }) Notify(name);
    }
}

public sealed class ProcessRow : BindableState
{
    public int Id { get; }
    public long Started { get; }
    public string Name { get; }
    private string size = "", growth = "";
    public string Size { get => size; private set => Set(ref size, value); }
    public string Growth { get => Localization.Text(growth); private set => Set(ref growth, value); }
    public string Description => Localization.Format("{0} · PID {1} · 私有内存 {2} · 本次增长 {3}", Name, Id, Size, Growth);
    public ProcessRow(ProcessGrowthRow value) { Id = value.Current.Id; Started = value.Current.StartedUtcTicks; Name = value.Current.Name; Update(value); }
    public void Update(ProcessGrowthRow value)
    {
        Size = $"{value.Current.PrivateBytes / 1048576d:0.0} MiB";
        Growth = value.PrivateGrowth is null ? "建立基线" : $"{(value.PrivateGrowth >= 0 ? "+" : "−")}{Math.Abs((double)value.PrivateGrowth) / 1048576d:0.0} MiB";
        Notify(nameof(Description));
    }
}

public sealed class AutomationDraft : BindableState
{
    private bool loading, dirty, thresholdEnabled, timerEnabled, fullscreen, disk;
    private int mode;
    private string threshold = "55", sustain = "60", interval = "60", cooldown = "30", feedback = "";
    private AutomationSettings baseline = new();
    public bool Dirty { get => dirty; private set { if (Set(ref dirty, value)) Notify(nameof(SaveLabel)); } }
    public string SaveLabel => Localization.Text(Dirty ? "保存规则" : "确认并保存规则");
    public string Feedback { get => Localization.Text(feedback); set { if (Set(ref feedback, value)) Notify(nameof(HasFeedback)); } }
    public bool HasFeedback => Feedback.Length > 0;
    public bool ThresholdEnabled { get => thresholdEnabled; set { if (Set(ref thresholdEnabled, value)) Changed(); } }
    public bool TimerEnabled { get => timerEnabled; set { if (Set(ref timerEnabled, value)) Changed(); } }
    public int Mode { get => mode; set { if (Set(ref mode, value)) Changed(); } }
    public string Threshold { get => threshold; set { if (Set(ref threshold, value)) Changed(); } }
    public string Sustain { get => sustain; set { if (Set(ref sustain, value)) Changed(); } }
    public string Interval { get => interval; set { if (Set(ref interval, value)) Changed(); } }
    public string Cooldown { get => cooldown; set { if (Set(ref cooldown, value)) Changed(); } }
    public bool PauseFullscreen { get => fullscreen; set { if (Set(ref fullscreen, value)) Changed(); } }
    public bool PauseDisk { get => disk; set { if (Set(ref disk, value)) Changed(); } }
    public bool AnyEnabled => ThresholdEnabled || TimerEnabled;
    public bool PercentageEnabled => AnyEnabled && Mode == 0;
    public bool SystemPressureEnabled => AnyEnabled && Mode == 1;
    public string Summary => !AnyEnabled ? Localization.Text("自动整理已关闭。保存后生效。") :
        (ThresholdEnabled ? Localization.Format("{0} 持续 {1} 秒", Mode == 1 ? Localization.Text("Windows 低内存通知") : Localization.Format("占用 ≥{0}%", Threshold), Sustain) : "") +
        (ThresholdEnabled && TimerEnabled ? " · " : "") + (TimerEnabled ? Localization.Format("每{0}分钟检查", Interval) : "") +
        "\n" + Localization.Format("后台优化 · 冷却 {0} 分钟", Cooldown);
    private void Changed()
    {
        if (!loading) {
            Feedback = "";
            Dirty = ThresholdEnabled != baseline.ThresholdEnabled || TimerEnabled != baseline.TimerEnabled || Mode != (baseline.TriggerMode == AutomaticTriggerMode.SystemPressure ? 1 : 0) ||
                Threshold != baseline.ThresholdPercent.ToString() || Sustain != baseline.SustainSeconds.ToString() || Interval != baseline.IntervalMinutes.ToString() || Cooldown != baseline.CooldownMinutes.ToString() ||
                PauseFullscreen != baseline.PauseFullscreen || PauseDisk != baseline.PauseDiskBusy;
        }
        foreach (var name in new[] { nameof(AnyEnabled), nameof(PercentageEnabled), nameof(SystemPressureEnabled), nameof(Summary) }) Notify(name);
    }
    public void Load(AutomationSettings value)
    {
        loading = true; baseline = value;
        ThresholdEnabled = value.ThresholdEnabled; TimerEnabled = value.TimerEnabled; Mode = value.TriggerMode == AutomaticTriggerMode.SystemPressure ? 1 : 0;
        Threshold = value.ThresholdPercent.ToString(); Sustain = value.SustainSeconds.ToString(); Interval = value.IntervalMinutes.ToString(); Cooldown = value.CooldownMinutes.ToString();
        PauseFullscreen = value.PauseFullscreen; PauseDisk = value.PauseDiskBusy; loading = false; Dirty = false; Feedback = "";
    }
    public bool TryBuild(out AutomationSettings value, out string field)
    {
        value = baseline; field = "";
        bool Read(string input, int min, int max, string name, out int number) {
            if (int.TryParse(input, out number) && number >= min && number <= max) return true;
            Feedback = $"{name}请输入 {min}～{max} 范围内的整数，规则尚未保存。"; return false;
        }
        var p = baseline.ThresholdPercent; var s = baseline.SustainSeconds; var i = baseline.IntervalMinutes; var c = baseline.CooldownMinutes;
        if (PercentageEnabled && !Read(Threshold, 55, 98, "占用门槛", out p)) { field = "ThresholdInput"; return false; }
        if (ThresholdEnabled && !Read(Sustain, 15, 600, "持续时间（秒）", out s)) { field = "SustainInput"; return false; }
        if (TimerEnabled && !Read(Interval, 15, 1440, "检查间隔（分钟）", out i)) { field = "IntervalInput"; return false; }
        if (AnyEnabled && !Read(Cooldown, 5, 240, "冷却时间（分钟）", out c)) { field = "CooldownInput"; return false; }
        value = baseline with { ThresholdEnabled = ThresholdEnabled, TimerEnabled = TimerEnabled, TriggerMode = Mode == 1 ? AutomaticTriggerMode.SystemPressure : AutomaticTriggerMode.Percentage,
            ThresholdPercent = p, SustainSeconds = s, IntervalMinutes = i, CooldownMinutes = c, PauseFullscreen = PauseFullscreen, PauseDiskBusy = PauseDisk, FullCleanup = false };
        return true;
    }
}

public sealed class SettingsState : BindableState
{
    private int theme, language;
    private bool reduceMotion, island, closeToTray, startup, checkUpdates;
    private string feedback = "", section = "index";
    public AutomationDraft Draft { get; } = new();
    public int Theme { get => theme; set { if (Set(ref theme, value)) Notify(nameof(AppearanceSummary)); } }
    public int Language { get => language; set => Set(ref language, value); }
    public bool ReduceMotion { get => reduceMotion; set => Set(ref reduceMotion, value); }
    public bool Island { get => island; set { if (Set(ref island, value)) Notify(nameof(AppearanceSummary)); } }
    public string AppearanceSummary => Localization.Text(Theme == 1 ? "深色" : Theme == 2 ? "跟随系统" : "浅色") + " · " + Localization.Text("桌面岛") + " " + Localization.Text(Island ? "开启" : "关闭");
    public bool CloseToTray { get => closeToTray; set => Set(ref closeToTray, value); }
    public bool Startup { get => startup; set => Set(ref startup, value); }
    public bool CheckUpdates { get => checkUpdates; set => Set(ref checkUpdates, value); }
    public string Feedback { get => Localization.Text(feedback); set { if (Set(ref feedback, value)) Notify(nameof(HasFeedback)); } }
    public bool HasFeedback => Feedback.Length > 0;
    public string Section { get => section; set { if (Set(ref section, value)) foreach (var n in new[] { nameof(Index), nameof(Appearance), nameof(Automation), nameof(Updates), nameof(About), nameof(Heading), nameof(IsChild) }) Notify(n); } }
    public bool Index => Section == "index";
    public bool Appearance => Section == "appearance";
    public bool Automation => Section == "automation";
    public bool Updates => Section == "updates";
    public bool About => Section == "about";
    public bool IsChild => !Index;
    public string Heading => Localization.Text(Section switch { "appearance" => "常规与外观", "automation" => "自动整理", "updates" => "版本更新", "about" => "关于与本地数据", _ => "设置" });
    public override void RefreshLanguage() { base.RefreshLanguage(); Draft.RefreshLanguage(); }
}

public sealed class ShellState(ICommand command) : BindableState
{
    public ICommand Command { get; } = command;
    public bool Preview { get; init; }
    public bool StableFeatures => !BuildInfo.ExperimentalX86;
    private bool compact;
    public bool Compact { get => compact; set => Set(ref compact, value); }
    public SettingsState Settings { get; } = new();
    public ObservableCollection<ResultRow> History { get; } = [];
    public ObservableCollection<ProcessRow> Processes { get; } = [];
    private string page = "overview", load = "—", available = "—", used = "—", total = "—", commit = "—", cache = "—", memoryError = "", operation = "就绪", cancelText = "取消后续", automatic = "自动整理：关闭", processFeedback = "正在建立观察基线…", updateText = "尚未检查更新", updateButton = "检查更新", popupKind = "result";
    private bool busy, canCancel, updateVisible, updateBusy, updateReady, updateAvailable, updateActionEnabled, recoveryVisible;
    private double percentage, downloadProgress;
    private bool downloadIndeterminate;
    private ResultRow? recent, detail;
    public string Version => "v" + BuildInfo.Version;
    public string AboutVersion => Localization.Format("版本 {0} · {1}\n提交 {2}", BuildInfo.Version, BuildInfo.PlatformLabel, BuildInfo.Commit);
    public string Page { get => page; set { if (Set(ref page, value)) foreach (var n in new[] { nameof(Overview), nameof(Records), nameof(Diagnostics), nameof(SettingsPage), nameof(HomeBannerVisible) }) Notify(n); } }
    public bool Overview => Page == "overview";
    public bool Records => Page == "history";
    public bool Diagnostics => Page == "diagnostics";
    public bool SettingsPage => Page == "settings";
    public string Load { get => load; set => Set(ref load, value); }
    public string Available { get => available; set => Set(ref available, value); }
    public string Used { get => used; set => Set(ref used, value); }
    public string Total { get => total; set => Set(ref total, value); }
    public string Commit { get => commit; set => Set(ref commit, value); }
    public string Cache { get => cache; set => Set(ref cache, value); }
    public string MemoryError { get => Localization.Text(memoryError); set { if (Set(ref memoryError, value)) Notify(nameof(MemoryFailed)); } }
    public bool MemoryFailed => MemoryError.Length > 0;
    public double Percentage { get => percentage; set => Set(ref percentage, value); }
    public bool Busy { get => busy; set { if (Set(ref busy, value)) { Notify(nameof(CanClean)); Notify(nameof(AdvancedAvailable)); Notify(nameof(CleanLabel)); Notify(nameof(OperationVisible)); Notify(nameof(IdleHintVisible)); } } }
    private bool applying;
    public bool Applying { get => applying; set { if (Set(ref applying, value)) { Notify(nameof(CanClean)); Notify(nameof(AdvancedAvailable)); } } }
    public bool CanClean => !Busy && !Applying;
    public string CleanLabel => Localization.Text(Busy ? "正在整理…" : "优化内存");
    public string Operation { get => Localization.Text(operation); set { if (Set(ref operation, value)) { Notify(nameof(OperationVisible)); Notify(nameof(IdleHintVisible)); } } }
    public bool OperationVisible => Busy || operation is not ("" or "就绪" or "步骤已完成");
    public bool IdleHintVisible => !OperationVisible;
    public bool CanCancel { get => canCancel; set => Set(ref canCancel, value); }
    public string CancelText { get => Localization.Text(cancelText); set => Set(ref cancelText, value); }
    public string Automatic { get => Localization.Text(automatic); set => Set(ref automatic, value); }
    private string automaticBrief = "已关闭  ›";
    public string AutomaticBrief { get => Localization.Text(automaticBrief); set => Set(ref automaticBrief, value); }
    public ResultRow? Recent { get => recent; set { if (Set(ref recent, value)) { Notify(nameof(HasRecent)); Notify(nameof(NoRecent)); } } }
    public bool HasRecent => Recent is not null;
    public bool NoRecent => !HasRecent;
    public ResultRow? Detail { get => detail; set => Set(ref detail, value); }
    public string ProcessFeedback { get => Localization.Text(processFeedback); set => Set(ref processFeedback, value); }
    public string UpdateText { get => Localization.Text(updateText); set => Set(ref updateText, value); }
    public string UpdateButton { get => Localization.Text(updateButton); set => Set(ref updateButton, value); }
    public bool UpdateVisible { get => updateVisible; set { if (Set(ref updateVisible, value)) Notify(nameof(HomeBannerVisible)); } }
    public bool HomeBannerVisible => Overview && UpdateVisible;
    public bool UpdateBusy { get => updateBusy; set { if (Set(ref updateBusy, value)) Notify(nameof(NotUpdateBusy)); } }
    public bool NotUpdateBusy => !UpdateBusy;
    public bool UpdateReady { get => updateReady; set { if (Set(ref updateReady, value)) Notify(nameof(UpdateApplyVisible)); } }
    public bool UpdateAvailable { get => updateAvailable; set { if (Set(ref updateAvailable, value)) Notify(nameof(UpdateDownloadVisible)); } }
    private bool systemComponentMissing, requiresInstaller;
    public bool SystemComponentMissing { get => systemComponentMissing; set { if (Set(ref systemComponentMissing, value)) Notify(nameof(AdvancedAvailable)); } }
    public bool RequiresInstaller { get => requiresInstaller; set { if (Set(ref requiresInstaller, value)) { Notify(nameof(UpdateDownloadVisible)); Notify(nameof(UpdateApplyVisible)); } } }
    public bool AdvancedAvailable => CanClean && !SystemComponentMissing;
    public bool UpdateDownloadVisible => UpdateAvailable && !RequiresInstaller;
    public bool UpdateApplyVisible => UpdateReady && !RequiresInstaller;
    public bool UpdateActionEnabled { get => updateActionEnabled; set => Set(ref updateActionEnabled, value); }
    public bool RecoveryVisible { get => recoveryVisible; set => Set(ref recoveryVisible, value); }
    public double DownloadProgress { get => downloadProgress; set => Set(ref downloadProgress, value); }
    public bool DownloadIndeterminate { get => downloadIndeterminate; set => Set(ref downloadIndeterminate, value); }
    private bool downloading;
    public bool Downloading { get => downloading; set => Set(ref downloading, value); }
    private string processDetail = "";
    public ProcessRow? ProcessDetailRow { get; set; }
    public string ProcessDetail { get => ProcessDetailRow?.Description ?? Localization.Text(processDetail); set { ProcessDetailRow = null; Set(ref processDetail, value); } }
    private string pressureTitle = "正在监测", pressureHint = "正在读取系统压力信号";
    private bool pressureLow, pressureKnown;
    public string PressureTitle { get => Localization.Text(pressureTitle); set => Set(ref pressureTitle, value); }
    public string PressureHint { get => Localization.Text(pressureHint); set => Set(ref pressureHint, value); }
    public bool PressureLow { get => pressureLow; set => Set(ref pressureLow, value); }
    public bool PressureKnown { get => pressureKnown; set => Set(ref pressureKnown, value); }
    public string PopupKind { get => popupKind; set { if (Set(ref popupKind, value)) foreach (var n in new[] { nameof(ResultPopup), nameof(MemoryPopup), nameof(CustomPopup), nameof(ProcessPopup) }) Notify(n); } }
    public bool ResultPopup => PopupKind == "result";
    public bool MemoryPopup => PopupKind == "memory";
    public bool CustomPopup => PopupKind == "custom";
    public bool ProcessPopup => PopupKind == "process";
    public override void RefreshLanguage()
    {
        base.RefreshLanguage(); Settings.RefreshLanguage();
        foreach (var row in History) row.RefreshLanguage();
        foreach (var row in Processes) row.RefreshLanguage();
        Recent?.RefreshLanguage(); Detail?.RefreshLanguage();
    }
}
