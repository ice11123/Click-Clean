using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClickClean;

public partial class MainWindow : Window
{
    private readonly Store store;
    private readonly MemoryEngine engine;
    private readonly IMemoryApi memory;
    private readonly bool preview;
    private readonly DispatcherTimer timer;
    private readonly AutomationPolicy automation = new();
    private readonly UpdateService updates;
    private readonly Stopwatch uptime = Stopwatch.StartNew(), operationClock = new();
    private CancellationTokenSource? cancellation;
    private System.Windows.Forms.NotifyIcon? tray;
    private MiniIsland? mini;
    private bool exiting, loadingSettings, exitRequested, lastInputWasPointer, tickRunning, applying, activeAutomatic;
    private string currentStep = "", islandTitle = "即清 · 随时就绪", islandDetail = "";
    private bool islandExpanded;
    private double nextUpdateCheck = 15;
    private double lastThemeCheck = -30;
    private double resultUntil;
    public bool IsBusy => cancellation is not null || engine.IsRunning;
    public bool TrayVisible => tray?.Visible == true;
    public double SamplingSeconds => timer.Interval.TotalSeconds;
    public bool MotionEnabled => !store.Settings.ReduceMotion && SystemParameters.ClientAreaAnimation;
    public MiniIsland? DesktopIsland => mini;

    public MainWindow(Store store, bool trayMode = false, bool verification = false, IMemoryApi? memoryApi = null)
    {
        this.store = store; preview = verification;
        updates = new UpdateService(Path.Combine(store.Root, "update-state.json"));
        memory = memoryApi ?? (verification ? new PreviewMemoryApi() : new WindowsMemoryApi());
        engine = new MemoryEngine(memory);
        InitializeComponent(); ApplyTheme();
        VersionText.Text = $"Windows 11 · x64 · {BuildInfo.Version}";
        AboutVersion.Text = $"版本 {BuildInfo.Version} · {BuildInfo.Commit}";
        AdminText.Text = preview ? "交互预览 · 模拟数据" : "管理员已就绪";
        Title = preview ? "Click-Clean 即清 · 交互预览" : "Click-Clean 即清";
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(trayMode ? 10 : 1) };
        timer.Tick += async (_, _) => await Tick();
        IsVisibleChanged += (_, _) => SetSampling();
        PreviewMouseDown += AnimatePress; PreviewMouseUp += AnimateRelease;
        LostMouseCapture += (_, _) => AnimateRelease(this, null!);
        PreviewKeyDown += (_, e) => {
            lastInputWasPointer = false;
            if (e.Key == Key.Escape && Overlay.Visibility == Visibility.Visible) { CloseOverlay(); e.Handled = true; }
        };
        Closing += OnClosing;
        StateChanged += (_, _) => SetSampling();
        WorkingCheck.IsChecked = store.Settings.SelectedSteps.Contains(MemoryCommand.WorkingSets);
        ModifiedCheck.IsChecked = store.Settings.SelectedSteps.Contains(MemoryCommand.ModifiedPages);
        StandbyCheck.IsChecked = store.Settings.SelectedSteps.Contains(MemoryCommand.StandbyCache);
        var recent = store.Settings.LastCleanupUtc is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(store.Settings.Automation.CooldownMinutes);
        automation.Configure(store.Settings.Automation, recent);
        updates.Changed += UpdateChanged;
        if (!preview)
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开 Click-Clean 即清", null, (_, _) => Dispatcher.BeginInvoke(ShowMain));
            menu.Items.Add("即刻整理", null, (_, _) => Dispatcher.BeginInvoke(async () => await Cleanup(Enum.GetValues<MemoryCommand>())));
            menu.Items.Add("显示 / 收起桌面岛", null, (_, _) => Dispatcher.BeginInvoke(ToggleIsland));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(TryExit));
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application;
            tray = new() { Icon = icon, Text = "Click-Clean 即清", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMain);
            TryRefreshStartup();
        }
        if (store.History.FirstOrDefault() is { } recentResult) ShowResult(recentResult);
        RefreshMemory(); RefreshAutomationSummary();
        if (store.Settings.MiniIsland) ShowIsland();
        timer.Start();
    }

    private async Task Tick()
    {
        if (tickRunning || exiting || applying) return;
        tickRunning = true;
        try
        {
            if (uptime.Elapsed.TotalSeconds - lastThemeCheck >= 30) { ApplyTheme(); lastThemeCheck = uptime.Elapsed.TotalSeconds; }
            var snapshot = RefreshMemory();
            if (IsBusy && operationClock.Elapsed.TotalSeconds >= 60)
            {
                StatusText.Text = $"{currentStep}耗时较长，仍在等待系统返回；可取消后续步骤。";
                SetIsland("仍在等待系统返回", currentStep + " · 取消只影响后续步骤", true, false);
            }
            if (!preview)
            {
                var trigger = automation.Evaluate(snapshot?.Load, IsBusy || applying || updates.Busy);
                if (trigger is not null)
                    await Cleanup(store.Settings.Automation.FullCleanup ? Enum.GetValues<MemoryCommand>() : [MemoryCommand.StandbyCache], trigger, true);
                if (store.Settings.CheckUpdates && uptime.Elapsed.TotalSeconds >= nextUpdateCheck)
                {
                    nextUpdateCheck = uptime.Elapsed.TotalSeconds + 6 * 3600;
                    _ = CheckUpdates(store.Settings.AutoDownloadUpdates);
                }
                if (store.Settings.AutoApplyHidden && !IsVisible && mini?.IsRevealed != true && updates.Ready && !IsBusy)
                    ApplyUpdate();
            }
            RefreshAutomationSummary();
            if (!IsBusy && !updates.Ready && uptime.Elapsed.TotalSeconds >= resultUntil)
                SetIsland(snapshot is null ? "状态暂不可读" : $"即清 · 内存 {snapshot.Load:0}%", "", false, false);
        }
        finally { tickRunning = false; }
    }
    public void ShowMain() { Show(); WindowState = WindowState.Normal; Activate(); }
    private MemorySnapshot? RefreshMemory()
    {
        try
        {
            var snapshot = memory.Read();
            LoadText.Text = snapshot.Load.ToString("0"); MemoryBar.Value = snapshot.Load;
            UsedText.Text = Labels.Bytes(snapshot.Used); AvailableText.Text = Labels.Bytes(snapshot.Available);
            TotalText.Text = Labels.Bytes(snapshot.Total); CommitText.Text = $"{Labels.Bytes(snapshot.Commit)} / {Labels.Bytes(snapshot.CommitLimit)}";
            if (tray is not null) tray.Text = $"Click-Clean · {snapshot.Load:0}% · 可用 {Labels.Bytes(snapshot.Available)}";
            return snapshot;
        }
        catch (Exception ex) { StatusText.Text = "内存读取失败：" + ex.Message; store.Log(ex.ToString()); return null; }
    }
    public void ApplyTheme(string? overrideTheme = null)
    {
        var theme = overrideTheme ?? store.Settings.Theme; var dark = theme == "dark";
        if (theme == "system")
        {
            using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        if (SystemParameters.HighContrast)
        {
            SetBrush("Bg", SystemColors.WindowColor); SetBrush("Surface", SystemColors.WindowColor);
            SetBrush("Text", SystemColors.WindowTextColor); SetBrush("Muted", SystemColors.WindowTextColor);
            SetBrush("Line", SystemColors.WindowTextColor); SetBrush("Accent", SystemColors.HighlightColor);
            SetBrush("Tint", SystemColors.ControlColor);
            SetBrush("IslandBg", SystemColors.WindowColor); SetBrush("IslandText", SystemColors.WindowTextColor); SetBrush("IslandMuted", SystemColors.WindowTextColor); return;
        }
        SetBrush("Bg", dark ? "#121620" : "#F4F6FA"); SetBrush("Surface", dark ? "#1C2230" : "#FFFFFF");
        SetBrush("Text", dark ? "#F0F3FA" : "#1C2636"); SetBrush("Muted", dark ? "#A2AFC4" : "#647187");
        SetBrush("Line", dark ? "#30394B" : "#E4E9F1"); SetBrush("Accent", dark ? "#7D9DF0" : "#4D74D9");
        SetBrush("Tint", dark ? "#2A3550" : "#EBF0FD");
        SetBrush("IslandBg", "#141821"); SetBrush("IslandText", "#FFFFFF"); SetBrush("IslandMuted", "#BDC6D7");
    }
    private static void SetBrush(string key, string color) => SetBrush(key, (Color)ColorConverter.ConvertFromString(color));
    private static void SetBrush(string key, Color color)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush && brush.Color == color) return;
        Application.Current.Resources[key] = new SolidColorBrush(color);
    }
    private async void Clean_Click(object sender, RoutedEventArgs e) => await Cleanup(Enum.GetValues<MemoryCommand>());
    private MemoryCommand[] Selected() => new[] { (WorkingCheck, MemoryCommand.WorkingSets), (ModifiedCheck, MemoryCommand.ModifiedPages), (StandbyCheck, MemoryCommand.StandbyCache) }
        .Where(p => p.Item1.IsChecked == true).Select(p => p.Item2).ToArray();
    private void Selection_Click(object sender, RoutedEventArgs e)
    {
        if (Selected().Length == 0) { StatusText.Text = "请至少选择一项。"; return; }
        store.Settings.SelectedSteps = Selected(); SaveSettings();
    }
    private async void Custom_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Length == 0) { StatusText.Text = "请至少选择一个整理项目。"; return; }
        await Cleanup(selected);
    }
    private async Task Cleanup(IEnumerable<MemoryCommand> commands, string trigger = "手动", bool automatic = false)
    {
        if (IsBusy || applying) { StatusText.Text = "当前操作尚未结束，请稍等。"; return; }
        cancellation = new();
        activeAutomatic = automatic;
        CleanButton.IsEnabled = CustomButton.IsEnabled = Options.IsEnabled = false;
        if (mini is not null) mini.CleanButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible; CancelButton.IsEnabled = true;
        CleanButton.Content = "正在整理…"; StatusText.Text = preview ? "正在演示整理过程，不调用系统整理接口。" : "正在准备权限…";
        SetIsland(preview ? "演示整理" : "正在整理", trigger + " · 全系统", true, lastInputWasPointer);
        operationClock.Restart(); UpdateChanged();
        var success = false;
        try
        {
            var result = await engine.RunAsync(commands, command => Dispatcher.Invoke(() => {
                currentStep = Labels.Name(command); StatusText.Text = "正在" + currentStep + "…";
                SetIsland(currentStep, (preview ? "模拟 · " : "") + trigger + "整理", true, false);
            }), cancellation.Token);
            result = result with { Trigger = preview ? "预览（模拟）" : trigger };
            success = result.Success; ShowResult(result);
            StatusText.Text = result.Success ? "整理完成，缓存会随着使用重新建立。" : result.Error ?? "已取消尚未开始的步骤。";
            try { store.Add(result); } catch (Exception ex) { StatusText.Text += " 记录保存失败：" + ex.Message; }
        }
        catch (Exception ex)
        {
            StatusText.Text = "整理失败：" + ex.Message; store.Log(ex.ToString());
            SetIsland("整理失败", ex.Message, true, false); resultUntil = uptime.Elapsed.TotalSeconds + 10;
        }
        finally
        {
            automation.RecordCompletion(success, automatic);
            if (!preview) { store.Settings.LastCleanupUtc = DateTimeOffset.UtcNow; SaveSettings(); }
            operationClock.Stop(); cancellation.Dispose(); cancellation = null;
            activeAutomatic = false;
            CleanButton.IsEnabled = CustomButton.IsEnabled = Options.IsEnabled = true;
            if (mini is not null) mini.CleanButton.IsEnabled = true;
            mini?.SetState(islandTitle, islandDetail, false, false);
            CancelButton.Visibility = Visibility.Collapsed; CleanButton.Content = "即刻整理";
            RefreshMemory(); RefreshAutomationSummary(); UpdateChanged();
            if (exitRequested) TryExit();
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel(); CancelButton.IsEnabled = false;
        StatusText.Text = "已请求取消；等待当前系统调用返回后停止。";
        SetIsland("正在等待当前步骤", "已取消后续步骤", true, false);
    }
    public void ShowResult(CleanupResult result)
    {
        ResultCard.Visibility = Visibility.Visible;
        ResultTitle.Text = result.Success ? "整理完成" : result.Steps.Any(s => s.Success) ? "部分完成" : result.Cancelled ? "已取消" : "整理失败";
        ResultDelta.Text = "可用内存 " + Labels.Delta(result.AvailableChange);
        ResultMeta.Text = $"{result.Trigger} · {result.Seconds:0.0}s";
        ResultError.Text = result.Error ?? (result.Cancelled ? "已停止尚未开始的步骤。" : "系统观测变化，包含其他程序活动的影响。");
        ResultDetails.Text = $"{result.Time:yyyy-MM-dd HH:mm:ss}\n" + string.Join("\n", result.Steps.Select(s => $"{(s.Success ? "✓" : "!")} {Labels.Name(s.Command)} · {s.Seconds:0.00}s · {s.StatusHex}"));
        resultUntil = uptime.Elapsed.TotalSeconds + 10;
        SetIsland(ResultTitle.Text, "可用内存 " + Labels.Delta(result.AvailableChange), true, lastInputWasPointer);
    }
    public void SetIsland(string title, string detail, bool expanded, bool animate)
    {
        islandTitle = title; islandDetail = detail; islandExpanded = expanded;
        mini?.SetState(title, detail, IsBusy, animate && MotionEnabled);
    }
    private void OpenOverlay(string title, bool settings, bool animate)
    {
        OverlayTitle.Text = title; SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        Header.IsEnabled = MainContent.IsEnabled = Footer.IsEnabled = false; Overlay.Visibility = Visibility.Visible;
        if (animate && MotionEnabled)
        {
            Overlay.RenderTransformOrigin = new Point(1, 0); var transform = new ScaleTransform(0.97, 0.97); Overlay.RenderTransform = transform;
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
            Overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        else { Overlay.RenderTransform = Transform.Identity; Overlay.BeginAnimation(OpacityProperty, null); Overlay.Opacity = 1; }
        Overlay.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(lastInputWasPointer);
    public void OpenSettings(bool animate = false)
    {
        loadingSettings = true;
        try
        {
            StartupCheck.IsChecked = store.Settings.Startup; MotionCheck.IsChecked = store.Settings.ReduceMotion;
            ThemeCombo.SelectedIndex = store.Settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
            IslandCheck.IsChecked = store.Settings.MiniIsland;
            CloseToTrayCheck.IsChecked = store.Settings.CloseToTray;
            UpdateCheck.IsChecked = store.Settings.CheckUpdates; DownloadCheck.IsChecked = store.Settings.AutoDownloadUpdates;
            ApplyHiddenCheck.IsChecked = store.Settings.AutoApplyHidden;
            var a = store.Settings.Automation; ThresholdCheck.IsChecked = a.ThresholdEnabled; TimerCheck.IsChecked = a.TimerEnabled;
            ThresholdInput.Text = a.ThresholdPercent.ToString(); SustainInput.Text = a.SustainSeconds.ToString();
            CooldownInput.Text = a.CooldownMinutes.ToString(); IntervalInput.Text = a.IntervalMinutes.ToString(); FullAutoCheck.IsChecked = a.FullCleanup;
        }
        finally { loadingSettings = false; }
        ShowSettingsSection("appearance"); OpenOverlay("设置", true, animate);
    }
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => ShowSettingsSection((string)((Button)sender).Tag);
    public void ShowSettingsSection(string section)
    {
        AppearanceSection.Visibility = section == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        AutomationSection.Visibility = section == "automation" ? Visibility.Visible : Visibility.Collapsed;
        UpdatesSection.Visibility = section == "updates" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = section == "about" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void History_Click(object sender, RoutedEventArgs e) => OpenHistory(lastInputWasPointer);
    public void OpenHistory(bool animate = false)
    {
        HistoryItems.Children.Clear();
        if (store.History.Count == 0) HistoryItems.Children.Add(new TextBlock { Text = "还没有整理记录。", TextWrapping = TextWrapping.Wrap });
        foreach (var item in store.History)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"{item.Time:MM-dd HH:mm} · {item.Trigger} · {(item.Success ? "完成" : item.Cancelled ? "取消" : "失败 / 部分完成")}", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"可用内存 {Labels.Delta(item.AvailableChange)} · {item.Seconds:0.0}s", Margin = new Thickness(0, 6, 0, 4) });
            panel.Children.Add(new TextBlock { Text = string.Join("\n", item.Steps.Select(s => $"{Labels.Name(s.Command)} {s.StatusHex}")) + (item.Error is null ? "" : "\n" + item.Error), TextWrapping = TextWrapping.Wrap, FontSize = 11 });
            var border = new Border { Child = panel, CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10) };
            border.SetResourceReference(Border.BackgroundProperty, "Tint"); HistoryItems.Children.Add(border);
        }
        OpenOverlay("整理历史", false, animate);
    }
    private void CloseOverlay_Click(object sender, RoutedEventArgs e) => CloseOverlay();
    public void CloseOverlay()
    {
        Overlay.Visibility = Visibility.Collapsed; Header.IsEnabled = MainContent.IsEnabled = Footer.IsEnabled = true;
        CleanButton.Focus();
    }
    private void TryRefreshStartup()
    {
        try
        {
            var existed = StartupTask.Exists();
            if (existed || store.Settings.Startup)
            {
                StartupTask.Set(true); store.Settings.Startup = true;
                if (store.MigratedLegacy) StartupTask.RemoveLegacy();
                SaveSettings();
            }
        }
        catch (Exception ex) { store.Log("登录启动状态同步失败：" + ex.Message); }
    }
    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        if (preview) { StatusText.Text = "预览模式不创建登录任务。"; StartupCheck.IsChecked = false; return; }
        try { StartupTask.Set(StartupCheck.IsChecked == true); store.Settings.Startup = StartupCheck.IsChecked == true; SaveSettings(); }
        catch (Exception ex) { StartupCheck.IsChecked = store.Settings.Startup; MessageBox.Show(this, ex.Message, "无法修改登录启动"); store.Log(ex.ToString()); }
    }
    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loadingSettings || !IsInitialized || store is null) return;
        store.Settings.Theme = ThemeCombo.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" }; ApplyTheme(); SaveSettings();
    }
    private void Preferences_Click(object sender, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        store.Settings.ReduceMotion = MotionCheck.IsChecked == true; store.Settings.MiniIsland = IslandCheck.IsChecked == true;
        store.Settings.CloseToTray = CloseToTrayCheck.IsChecked == true;
        store.Settings.CheckUpdates = UpdateCheck.IsChecked == true; store.Settings.AutoDownloadUpdates = DownloadCheck.IsChecked == true;
        store.Settings.AutoApplyHidden = ApplyHiddenCheck.IsChecked == true;
        if (store.Settings.MiniIsland) ShowIsland(); else mini?.Hide();
        if (mini is not null) mini.MotionAllowed = MotionEnabled;
        if (!MotionEnabled) SetIsland(islandTitle, islandDetail, islandExpanded, false);
        SaveSettings(); SetSampling();
    }
    private void SaveAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadNumber(ThresholdInput, 60, 98, out var threshold) || !ReadNumber(SustainInput, 15, 600, out var sustain) ||
            !ReadNumber(IntervalInput, 15, 1440, out var interval) || !ReadNumber(CooldownInput, 5, 240, out var cooldown))
        { AutomationFeedback.Text = "请输入标注范围内的整数；设置尚未改变。"; return; }
        var previous = store.Settings.Automation;
        var next = new AutomationSettings { ThresholdEnabled = ThresholdCheck.IsChecked == true, TimerEnabled = TimerCheck.IsChecked == true,
            ThresholdPercent = threshold, SustainSeconds = sustain, IntervalMinutes = interval, CooldownMinutes = cooldown, FullCleanup = FullAutoCheck.IsChecked == true };
        if ((!previous.ThresholdEnabled && next.ThresholdEnabled || !previous.TimerEnabled && next.TimerEnabled || !previous.FullCleanup && next.FullCleanup) && !preview)
        {
            if (MessageBox.Show(this, next.FullCleanup ? "自动模式将整理全系统工作集、刷新修改页并清理待机缓存，可能短暂卡顿或产生磁盘忙碌。是否启用？" : "自动模式将按条件清理全系统待机缓存；不保证降低使用率或提高性能。是否启用？", "启用自动清理", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        store.Settings.Automation = next;
        automation.Configure(next);
        if (activeAutomatic && !next.TimerEnabled && !next.ThresholdEnabled) cancellation?.Cancel();
        SaveSettings(); RefreshAutomationSummary(); AutomationFeedback.Text = preview ? "已保存预览设置，不会自动执行系统整理。" : "已保存。软件运行期间生效；连续失败暂停后可再次保存恢复。";
    }
    private static bool ReadNumber(TextBox field, int min, int max, out int value)
    { var valid = int.TryParse(field.Text.Trim(), out value) && value >= min && value <= max; if (!valid) field.Focus(); return valid; }
    private void RefreshAutomationSummary()
    {
        var a = store.Settings.Automation;
        AutomationSummary.Text = automation.Paused ? "自动清理已暂停：连续失败 3 次，请在设置中恢复。" :
            $"阈值：{(a.ThresholdEnabled ? a.ThresholdPercent + "% / " + a.SustainSeconds + "秒" : "关闭")}    定时：{(a.TimerEnabled ? a.IntervalMinutes + "分钟" : "关闭")}";
        if (a.TimerEnabled && automation.TimerRemaining is { } remaining && !automation.Paused) AutomationSummary.Text += $"\n下一周期约 {Math.Ceiling(remaining.TotalMinutes):0} 分钟后；冷却 {a.CooldownMinutes} 分钟。";
    }
    private void Island_Click(object sender, RoutedEventArgs e) => ToggleIsland();
    private void ToggleIsland()
    {
        store.Settings.MiniIsland = mini?.IsVisible != true;
        if (store.Settings.MiniIsland) ShowIsland(); else mini?.Hide();
        IslandCheck.IsChecked = store.Settings.MiniIsland; SaveSettings(); SetSampling();
    }
    public void ShowIsland()
    {
        if (mini is null) {
            mini = new MiniIsland(async () => { lastInputWasPointer = true; await Cleanup(Enum.GetValues<MemoryCommand>(), "底部岛"); });
            mini.RevealChanged += SetSampling;
        }
        mini.MotionAllowed = MotionEnabled;
        mini.SetState(islandTitle, islandDetail, IsBusy, false); mini.CleanButton.IsEnabled = !IsBusy;
        mini.Show(); SetSampling();
    }
    private void SetSampling() => timer.Interval = TimeSpan.FromSeconds(IsVisible && WindowState != WindowState.Minimized ? 1 : mini?.IsVisible == true && mini.IsRevealed ? 3 : 10);
    private async Task CheckUpdates(bool download)
    {
        if (applying) return;
        if (preview) { UpdateFeedback.Text = "交互预览不会访问发布服务或替换程序。"; return; }
        await updates.CheckAsync(download);
        if (updates.Ready) SetIsland("更新已就绪", "短暂重启生效 · 无需安装向导", true, MotionEnabled);
    }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdates(store.Settings.AutoDownloadUpdates);
    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdates(true);
    private void UpdateChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateChanged); return; }
        UpdateFeedback.Text = updates.Status; CheckUpdateButton.IsEnabled = !updates.Busy;
        DownloadUpdateButton.Visibility = updates.Available && !updates.Ready ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled = !updates.Busy;
        ApplyUpdateButton.Visibility = updates.Ready ? Visibility.Visible : Visibility.Collapsed;
        ApplyUpdateButton.IsEnabled = !updates.Busy && !IsBusy && !applying;
    }
    private void ApplyUpdate_Click(object sender, RoutedEventArgs e) => ApplyUpdate();
    private async void ApplyUpdate()
    {
        if (preview || IsBusy || applying || updates.Busy || !updates.Ready) return;
        applying = true; timer.Stop(); SaveSettings();
        try { await updates.ApplyAsync(!IsVisible); }
        catch (Exception ex)
        {
            applying = false; UpdateFeedback.Text = "更新应用失败，现有版本保留：" + ex.Message;
            store.Log(ex.ToString()); timer.Start(); ShowMain();
        }
    }
    private void Repository_Click(object sender, RoutedEventArgs e)
    { if (BuildInfo.Repository.Length == 0) { MessageBox.Show(this, "此开发构建尚未连接正式仓库。"); return; } OpenExternal(BuildInfo.Repository); }
    private void License_Click(object sender, RoutedEventArgs e)
    { var file = Path.Combine(AppContext.BaseDirectory, "LICENSE"); if (File.Exists(file)) OpenExternal(file); else MessageBox.Show(this, "请在正式发行包的 LICENSE 中查看 GPL-3.0 完整条款。"); }
    private static void OpenExternal(string value)
    { try { Process.Start(new ProcessStartInfo(value) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, "无法打开"); } }
    private void SaveSettings()
    { try { store.SaveSettings(); } catch (Exception ex) { store.Log(ex.ToString()); StatusText.Text = "设置保存失败：" + ex.Message; } }
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "清空本地整理记录？", "清空记录", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { store.Clear(); ResultCard.Visibility = Visibility.Collapsed; OpenHistory(); } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出诊断日志", FileName = "Click-Clean-诊断.txt", Filter = "文本文件|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { store.Export(dialog.FileName, BuildInfo.Version); MessageBox.Show(this, "已导出。分享前请检查个人信息。", "即清"); } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }
    private void DeleteData_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || updates.Busy) { StatusText.Text = "请等当前操作结束后再清除数据。"; return; }
        if (MessageBox.Show(this, "删除即清自己的设置、历史和诊断日志并退出？不删除旧清栖数据。", "清除本地用户数据", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if (!preview) StartupTask.Set(false);
            foreach (var file in new[] { "settings.json", "settings.json.tmp", "history.json", "history.json.tmp", "diagnostic.log", "diagnostic.log.previous", "migration.json", "update-state.json", "update-state.json.tmp" })
                File.Delete(Path.Combine(store.Root, file));
            TryExit();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "清除失败"); }
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => TryExit();
    public void TryExit()
    {
        if (IsBusy) { exitRequested = true; ShowMain(); cancellation?.Cancel(); StatusText.Text = "等待当前步骤返回后退出，已取消后续步骤。"; return; }
        exiting = true; DisposeResources(); Application.Current.Shutdown();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        if (store.Settings.CloseToTray && !preview) Hide(); else TryExit();
    }
    public void DisposeResources()
    {
        timer.Stop(); updates.Dispose(); mini?.Close(); mini = null;
        if (tray is not null) { var icon = tray.Icon; tray.Visible = false; tray.Dispose(); icon?.Dispose(); tray = null; }
    }
    private Button? pressedButton;
    private void AnimatePress(object sender, MouseButtonEventArgs e)
    {
        lastInputWasPointer = true;
        if (e.ChangedButton != MouseButton.Left || !MotionEnabled) return;
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not Button)
            source = source is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(source);
        if (source is not Button button || !button.IsEnabled) return;
        pressedButton = button; AnimateButton(button, 0.97);
    }
    private void AnimateRelease(object sender, MouseButtonEventArgs e)
    { if (pressedButton is not null) { AnimateButton(pressedButton, 1); pressedButton = null; } }
    private static void AnimateButton(Button button, double value)
    {
        if (button.RenderTransform is not ScaleTransform) { button.RenderTransform = new ScaleTransform(1, 1); button.RenderTransformOrigin = new Point(0.5, 0.5); }
        var scale = (ScaleTransform)button.RenderTransform;
        var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation); scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
    public void RenderTo(string path, double factor = 1) => Capture(this, path, factor);
    public static void Capture(Window window, string path, double factor = 1)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * factor), (int)(window.ActualHeight * factor), 96 * factor, 96 * factor, PixelFormats.Pbgra32);
        if (window.Background is { } background && background != Brushes.Transparent)
        {
            var backing = new DrawingVisual();
            using (var drawing = backing.RenderOpen()) drawing.DrawRectangle(background, null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            bitmap.Render(backing);
        }
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}

internal sealed class PreviewMemoryApi : IMemoryApi
{
    private ulong available = 12UL << 30;
    public MemorySnapshot Read() => new(32UL << 30, available, 21UL << 30, 48UL << 30);
    public IDisposable EnablePrivilege() => new NoPrivilege();
    public int Execute(MemoryCommand command) { Thread.Sleep(350); available = Math.Min(18UL << 30, available + (256UL << 20)); return 0; }
    private sealed class NoPrivilege : IDisposable { public void Dispose() { } }
}
