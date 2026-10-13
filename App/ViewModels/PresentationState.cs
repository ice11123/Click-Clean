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
    public string Outcome => result.Success ? "步骤完成" : result.Cancelled ? "已取消" : result.Steps.Any(s => s.Success) ? "部分完成" : "整理失败";
    public string Delta => (result.FollowUp is null ? "即时可用 " : "复测可用 ") + Labels.Delta(result.PreferredChange);
    public string Meta => $"{result.Time:MM-dd HH:mm} · {result.Trigger} · {result.Seconds:0.0} 秒";
    public string HomeOutcome => "上次整理 · " + (result.Success ? "已完成" : Outcome);
    public string HomeDelta => "可用内存 " + Labels.Delta(result.PreferredChange);
    public string Observation => result.Sustained is not null ? $"{result.SustainedSeconds:0} 秒观测 {Labels.Delta(result.SustainedChange)}" :
        result.SustainedError is not null ? "持续观测：" + result.SustainedError : pendingObservation ? "30 秒持续观测中，可继续使用" : "持续观测：无记录";
    public void SetPending(bool value) { if (pendingObservation == value) return; pendingObservation = value; Notify(nameof(Observation)); }
    public string Details => $"{Outcome}\n{result.Time:yyyy-MM-dd HH:mm:ss} · {result.Trigger}\n\n" +
        string.Join("\n", result.Steps.Select(s => $"{(s.Success ? "✓" : "!")} {Labels.Name(s.Command)} · {s.Seconds:0.00} 秒 · {s.StatusHex}")) +
        $"\n\n即时可用变化：{Labels.Delta(result.AvailableChange)}\n即时提交变化：{Labels.Delta(result.CommitChange)}\n" +
        (result.FollowUp is null ? "复测：未完成 / 无记录" : $"即时采样后 {result.FollowUpSeconds:0.00} 秒复测：{Labels.Delta(result.FollowUpChange)}") +
        "\n" + Observation + $"\n整理前提交量 / 上限：{Labels.Bytes(result.Before.Commit)} / {Labels.Bytes(result.Before.CommitLimit)}" +
        (result.Before.SystemCache is { } cache ? $"\n系统缓存（含系统工作集，不是纯待机）：{Labels.Bytes(cache)}" : "") +
        (result.Error is null ? "" : "\n\n" + result.Error) + (result.ObservationError is null ? "" : "\n" + result.ObservationError) +
        "\n\n前后变化包含系统其他活动，不代表性能提升。";
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
    public string Growth { get => growth; private set => Set(ref growth, value); }
    public string Description => $"{Name} · PID {Id} · 私有内存 {Size} · 本次增长 {Growth}";
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
    public string SaveLabel => Dirty ? "保存规则" : "确认并保存规则";
    public string Feedback { get => feedback; set { if (Set(ref feedback, value)) Notify(nameof(HasFeedback)); } }
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
    public string Summary => !AnyEnabled ? "自动整理已关闭。保存后生效。" :
        (ThresholdEnabled ? $"{(Mode == 1 ? "Windows 报告低内存" : "占用 ≥" + Threshold + "%")} 持续 {Sustain} 秒" : "") +
        (ThresholdEnabled && TimerEnabled ? "；" : "") + (TimerEnabled ? $"每 {Interval} 分钟检查，且满足{(Mode == 1 ? "系统低内存条件" : "占用 ≥" + Threshold + "%")}" : "") +
        $"。执行原版三步，冷却 {Cooldown} 分钟。";
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
            ThresholdPercent = p, SustainSeconds = s, IntervalMinutes = i, CooldownMinutes = c, PauseFullscreen = PauseFullscreen, PauseDiskBusy = PauseDisk, FullCleanup = true };
        return true;
    }
}

public sealed class SettingsState : BindableState
{
    private int theme;
    private bool reduceMotion, island, closeToTray, startup, checkUpdates;
    private string feedback = "", section = "index";
    public AutomationDraft Draft { get; } = new();
    public int Theme { get => theme; set { if (Set(ref theme, value)) Notify(nameof(AppearanceSummary)); } }
    public bool ReduceMotion { get => reduceMotion; set => Set(ref reduceMotion, value); }
    public bool Island { get => island; set { if (Set(ref island, value)) Notify(nameof(AppearanceSummary)); } }
    public string AppearanceSummary => (Theme == 1 ? "深色" : Theme == 2 ? "跟随系统" : "浅色") + " · 桌面岛" + (Island ? "开启" : "关闭") + " · 托盘与启动";
    public bool CloseToTray { get => closeToTray; set => Set(ref closeToTray, value); }
    public bool Startup { get => startup; set => Set(ref startup, value); }
    public bool CheckUpdates { get => checkUpdates; set => Set(ref checkUpdates, value); }
    public string Feedback { get => feedback; set { if (Set(ref feedback, value)) Notify(nameof(HasFeedback)); } }
    public bool HasFeedback => Feedback.Length > 0;
    public string Section { get => section; set { if (Set(ref section, value)) foreach (var n in new[] { nameof(Index), nameof(Appearance), nameof(Automation), nameof(Updates), nameof(About), nameof(Heading), nameof(IsChild) }) Notify(n); } }
    public bool Index => Section == "index";
    public bool Appearance => Section == "appearance";
    public bool Automation => Section == "automation";
    public bool Updates => Section == "updates";
    public bool About => Section == "about";
    public bool IsChild => !Index;
    public string Heading => Section switch { "appearance" => "常规与外观", "automation" => "自动整理", "updates" => "版本更新", "about" => "关于与本地数据", _ => "设置" };
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
    public string AboutVersion => $"版本 {BuildInfo.Version} · {BuildInfo.PlatformLabel}\n提交 {BuildInfo.Commit}";
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
    public string MemoryError { get => memoryError; set { if (Set(ref memoryError, value)) Notify(nameof(MemoryFailed)); } }
    public bool MemoryFailed => MemoryError.Length > 0;
    public double Percentage { get => percentage; set => Set(ref percentage, value); }
    public bool Busy { get => busy; set { if (Set(ref busy, value)) { Notify(nameof(CanClean)); Notify(nameof(CleanLabel)); Notify(nameof(OperationVisible)); Notify(nameof(IdleHintVisible)); } } }
    private bool applying;
    public bool Applying { get => applying; set { if (Set(ref applying, value)) Notify(nameof(CanClean)); } }
    public bool CanClean => !Busy && !Applying;
    public string CleanLabel => Busy ? "正在整理…" : "一键整理";
    public string Operation { get => operation; set { if (Set(ref operation, value)) { Notify(nameof(OperationVisible)); Notify(nameof(IdleHintVisible)); } } }
    public bool OperationVisible => Busy || Operation is not ("" or "就绪" or "步骤已完成");
    public bool IdleHintVisible => !OperationVisible;
    public bool CanCancel { get => canCancel; set => Set(ref canCancel, value); }
    public string CancelText { get => cancelText; set => Set(ref cancelText, value); }
    public string Automatic { get => automatic; set => Set(ref automatic, value); }
    private string automaticBrief = "已关闭  ›";
    public string AutomaticBrief { get => automaticBrief; set => Set(ref automaticBrief, value); }
    public ResultRow? Recent { get => recent; set { if (Set(ref recent, value)) { Notify(nameof(HasRecent)); Notify(nameof(NoRecent)); } } }
    public bool HasRecent => Recent is not null;
    public bool NoRecent => !HasRecent;
    public ResultRow? Detail { get => detail; set => Set(ref detail, value); }
    public string ProcessFeedback { get => processFeedback; set => Set(ref processFeedback, value); }
    public string UpdateText { get => updateText; set => Set(ref updateText, value); }
    public string UpdateButton { get => updateButton; set => Set(ref updateButton, value); }
    public bool UpdateVisible { get => updateVisible; set { if (Set(ref updateVisible, value)) Notify(nameof(HomeBannerVisible)); } }
    public bool HomeBannerVisible => Overview && UpdateVisible;
    public bool UpdateBusy { get => updateBusy; set { if (Set(ref updateBusy, value)) Notify(nameof(NotUpdateBusy)); } }
    public bool NotUpdateBusy => !UpdateBusy;
    public bool UpdateReady { get => updateReady; set => Set(ref updateReady, value); }
    public bool UpdateAvailable { get => updateAvailable; set => Set(ref updateAvailable, value); }
    public bool UpdateActionEnabled { get => updateActionEnabled; set => Set(ref updateActionEnabled, value); }
    public bool RecoveryVisible { get => recoveryVisible; set => Set(ref recoveryVisible, value); }
    public double DownloadProgress { get => downloadProgress; set => Set(ref downloadProgress, value); }
    public bool DownloadIndeterminate { get => downloadIndeterminate; set => Set(ref downloadIndeterminate, value); }
    private bool downloading;
    public bool Downloading { get => downloading; set => Set(ref downloading, value); }
    private string processDetail = "";
    public string ProcessDetail { get => processDetail; set => Set(ref processDetail, value); }
    public string PopupKind { get => popupKind; set { if (Set(ref popupKind, value)) foreach (var n in new[] { nameof(ResultPopup), nameof(MemoryPopup), nameof(CustomPopup), nameof(ProcessPopup) }) Notify(n); } }
    public bool ResultPopup => PopupKind == "result";
    public bool MemoryPopup => PopupKind == "memory";
    public bool CustomPopup => PopupKind == "custom";
    public bool ProcessPopup => PopupKind == "process";
}
