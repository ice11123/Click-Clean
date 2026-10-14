using System.Text.Json;

namespace ClickClean.Core;

public sealed class UserSettings
{
    public int SchemaVersion { get; set; } = 5;
    public string Language { get; set; } = "zh-CN";
    public bool LanguageChosen { get; set; }
    public bool AutomationNeedsConfirmation { get; set; }
    public bool Startup { get; set; }
    public string Theme { get; set; } = "light";
    public bool ReduceMotion { get; set; }
    public bool MiniIsland { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public bool AutoDownloadUpdates { get; set; }
    public bool AutoApplyHidden { get; set; }
    public AutomationSettings Automation { get; set; } = new();
    public MemoryCommand[] SelectedSteps { get; set; } = [MemoryCommand.WorkingSets, MemoryCommand.ModifiedPages, MemoryCommand.StandbyCache];
    public DateTimeOffset? LastCleanupUtc { get; set; }
    public void Validate()
    {
        // 首次升级启用新的底部入口；以后仍尊重用户关闭选择。
        if (SchemaVersion < 2) { MiniIsland = true; SchemaVersion = 2; }
        // 兼容读取旧字段，但新版只允许用户点击下载和重启应用。
        AutoDownloadUpdates = false; AutoApplyHidden = false;
        if (SchemaVersion < 4)
        {
            // 不把旧版已启用的温和自动规则无声升级为裁剪和刷盘。
            AutomationNeedsConfirmation |= Automation?.ThresholdEnabled == true || Automation?.TimerEnabled == true;
            SchemaVersion = 4;
        }
        if (SchemaVersion < 5)
        {
            // 旧全系统自动规则必须重新确认；不把用户的授权直接移交给新策略。
            AutomationNeedsConfirmation |= Automation?.ThresholdEnabled == true || Automation?.TimerEnabled == true;
            if (Automation is not null) Automation = Automation with { TriggerMode = AutomaticTriggerMode.SystemPressure, FullCleanup = false };
            SchemaVersion = 5;
        }
        Language = Language == "en-US" ? "en-US" : "zh-CN";
        Theme = Theme is "light" or "dark" ? Theme : "system";
        Automation = (Automation ?? new()).Validated();
        // 常规后台优化不是高级全系统操作，禁止通过设置文件把它混入自定义步骤。
        SelectedSteps = (SelectedSteps ?? []).Where(command => command is MemoryCommand.WorkingSets or MemoryCommand.ModifiedPages or MemoryCommand.StandbyCache).Distinct().Order().ToArray();
    }
}

public sealed class Store
{
    private readonly object gate = new();
    private static readonly JsonSerializerOptions json = new() { WriteIndented = true };
    public string Root { get; }
    public UserSettings Settings { get; }
    public List<CleanupResult> History { get; private set; }
    public bool MigratedLegacy { get; }
    public Store(string? root = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // 数据与 Velopack 安装目录分离，更新和卸载不会整体删除用户数据。
        Root = root ?? Path.Combine(local, "ClickCleanData");
        Directory.CreateDirectory(Root);
        if (root is null && !File.Exists(Path.Combine(Root, "settings.json")) && !File.Exists(Path.Combine(Root, "migration.json")))
        {
            var legacy = Path.Combine(local, "Qingqi");
            foreach (var file in new[] { "settings.json", "history.json" })
            {
                var source = Path.Combine(legacy, file);
                if (File.Exists(source) && new FileInfo(source).Length < 4_000_000)
                {
                    File.Copy(source, Path.Combine(Root, file), false);
                    MigratedLegacy = true;
                }
            }
            Save("migration.json", new { From = MigratedLegacy ? "Qingqi" : "none", Time = DateTimeOffset.UtcNow });
        }
        Settings = LoadSettings(); Settings.Validate();
        History = LoadHistory();
    }
    private UserSettings LoadSettings()
    {
        try
        {
            var path = Path.Combine(Root, "settings.json");
            if (!File.Exists(path)) return new();
            if (new FileInfo(path).Length > 1_000_000) throw new IOException("设置文件过大。");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var settings = document.RootElement.Deserialize<UserSettings>() ?? new();
            // 已有用户不重新询问语言，缺少语言字段的旧安装保持中文。
            if (!document.RootElement.TryGetProperty(nameof(UserSettings.LanguageChosen), out _)) settings.LanguageChosen = true;
            // 老文件没有 schema 时也必须走升级确认，不能误认成新安装。
            if (!document.RootElement.TryGetProperty(nameof(UserSettings.SchemaVersion), out _)) settings.SchemaVersion = 1;
            return settings;
        }
        catch (Exception ex) { Log("读取设置失败，使用默认值：" + ex.Message); return new(); }
    }
    private List<CleanupResult> LoadHistory()
    {
        var valid = new List<CleanupResult>();
        try
        {
            var path = Path.Combine(Root, "history.json");
            if (!File.Exists(path)) return valid;
            if (new FileInfo(path).Length > 4_000_000) throw new IOException("历史文件过大。");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("历史格式不是数组。");
            foreach (var row in document.RootElement.EnumerateArray())
            {
                try
                {
                    var result = row.Deserialize<CleanupResult>();
                    if (ValidResult(result)) valid.Add(result!);
                    else Log("跳过无效历史记录。");
                }
                catch (JsonException) { Log("跳过无法解析的历史记录。"); }
                if (valid.Count == 30) break;
            }
        }
        catch (Exception ex) { Log("读取历史失败：" + ex.Message); }
        return valid;
    }
    public static bool ValidResult(CleanupResult? result) => result is not null &&
        result.Before is not null && result.Steps is not null && result.Steps.Count <= 3 &&
        result.Steps.All(s => s is not null && Enum.IsDefined(s.Command) && double.IsFinite(s.Seconds) && s.Seconds >= 0) &&
        double.IsFinite(result.Seconds) && result.Seconds >= 0 &&
        (result.BackgroundCleanup is null || ValidBackgroundSummary(result.BackgroundCleanup)) &&
        result.Before.Total <= long.MaxValue && result.Before.Available <= result.Before.Total &&
        result.Before.Commit <= long.MaxValue &&
        (result.After is null || result.After.Total <= long.MaxValue && result.After.Available <= result.After.Total && result.After.Commit <= long.MaxValue) &&
        (result.FollowUp is null || result.FollowUp.Total <= long.MaxValue && result.FollowUp.Available <= result.FollowUp.Total && result.FollowUp.Commit <= long.MaxValue) &&
        (result.FollowUpSeconds is null || double.IsFinite(result.FollowUpSeconds.Value) && result.FollowUpSeconds >= 0) &&
        (result.FollowUp is null || result.FollowUpSeconds is not null) &&
        (result.Sustained is null || result.Sustained.Total > 0 && result.Sustained.Total <= long.MaxValue && result.Sustained.Available <= result.Sustained.Total && result.Sustained.Commit <= long.MaxValue) &&
        (result.SustainedSeconds is null || double.IsFinite(result.SustainedSeconds.Value) && result.SustainedSeconds >= 0) &&
        (result.Sustained is null || result.SustainedSeconds is not null);
    private static bool ValidBackgroundSummary(BackgroundCleanupSummary value) =>
        value.Examined is >= 0 and <= 256 && value.Eligible >= 0 && value.Eligible <= value.Examined &&
        value.Attempted is >= 0 and <= 3 && value.Completed >= 0 && value.Completed <= value.Attempted && value.Failed >= 0 && value.Failed <= value.Attempted &&
        value.Completed + value.Failed == value.Attempted && Enum.IsDefined(value.StopReason) &&
        value.Failures is not null && value.Failures.Count == value.Failed &&
        value.Failures.All(failure => failure is not null && failure.ProcessId > 0 && failure.Win32Error >= 0);
    private void Save(string name, object value)
    {
        lock (gate)
        {
            var file = Path.Combine(Root, name); var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, json));
            File.Move(temporary, file, true);
        }
    }
    public void SaveSettings() { Settings.Validate(); Save("settings.json", Settings); }
    public void Add(CleanupResult result)
    {
        if (!ValidResult(result)) throw new ArgumentException("整理结果无效。");
        History.Insert(0, result); History = History.Take(30).ToList();
        Save("history.json", History); Log(JsonSerializer.Serialize(result));
    }
    public bool ReplaceObservation(CleanupResult result)
    {
        if (!ValidResult(result)) throw new ArgumentException("观测记录无效。");
        var index = History.FindIndex(item => item.Id == result.Id);
        // 清空历史后迟到的观测不得重新插入记录。
        if (index < 0) return false;
        History[index] = result; Save("history.json", History); return true;
    }
    public void Clear() { History.Clear(); Save("history.json", History); }
    public void Log(string message)
    {
        try
        {
            lock (gate)
            {
                var path = Path.Combine(Root, "diagnostic.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { /* 日志失败不应阻止系统状态读取与错误反馈。 */ }
    }
    public void Export(string target, string version)
    {
        var text = $"Click-Clean {version}\n{Environment.OSVersion}\n" + JsonSerializer.Serialize(History, json) + "\n";
        var path = Path.Combine(Root, "diagnostic.log");
        if (File.Exists(path)) text += File.ReadAllText(path);
        File.WriteAllText(target, text);
    }
}
