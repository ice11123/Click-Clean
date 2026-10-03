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
    private readonly IApplicationUpdates updates;
    private readonly bool simulatedUpdates;
    private readonly Action<Uri> projectLinkOpener;
    private readonly Stopwatch uptime = Stopwatch.StartNew(), operationClock = new();
    private CancellationTokenSource? cancellation;
    private System.Windows.Forms.NotifyIcon? tray;
    private MiniIsland? mini;
    private bool exiting, loadingSettings, exitRequested, lastInputWasPointer, tickRunning, applying, activeAutomatic, observingMemory;
    private IInputElement? overlayReturnFocus;
    private double overlayReturnOffset;
    private string currentStep = "";
    private readonly DockStatus dockStatus = new();
    private double nextUpdateCheck = 6 * 3600;
    private double lastThemeCheck = -30;
    public bool IsBusy => cancellation is not null || engine.IsRunning;
    public bool TrayVisible => tray?.Visible == true;
    public double SamplingSeconds => timer.Interval.TotalSeconds;
    public bool MotionEnabled => !store.Settings.ReduceMotion && SystemParameters.ClientAreaAnimation;
    public MiniIsland? DesktopIsland => mini;
    public bool NativeThemeApplied { get; private set; }

    public MainWindow(Store store, bool trayMode = false, bool verification = false, IMemoryApi? memoryApi = null, Action<Uri>? projectLinkOpener = null, IApplicationUpdates? updateOverride = null)
    {
        this.store = store; preview = verification;
        this.projectLinkOpener = projectLinkOpener ?? (uri => OpenExternal(uri.AbsoluteUri));
        updates = updateOverride ?? new UpdateService(Path.Combine(store.Root, "update-state.json"));
        simulatedUpdates = verification && updateOverride is not null;
        memory = memoryApi ?? (verification ? new PreviewMemoryApi() : new WindowsMemoryApi());
        engine = new MemoryEngine(memory);
        InitializeComponent(); ApplyTheme();
        SourceInitialized += (_, _) => ApplyTheme();
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
        Loaded += async (_, _) => { if (store.Settings.CheckUpdates && (!preview || simulatedUpdates)) await CheckUpdates(); };
        if (!preview)
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开 Click-Clean 即清", null, (_, _) => Dispatcher.BeginInvoke(ShowMain));
            menu.Items.Add("一键整理", null, (_, _) => Dispatcher.BeginInvoke(async () => await RoutineCleanup()));
            menu.Items.Add("显示 / 收起桌面岛", null, (_, _) => Dispatcher.BeginInvoke(ToggleIsland));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(TryExit));
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application;
            tray = new() { Icon = icon, Text = "Click-Clean 即清", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMain);
            TryRefreshStartup();
        }
        if (store.History.FirstOrDefault() is { } recentResult) ShowResult(recentResult, false);
        RefreshMemory(); RefreshAutomationSummary();
        if (store.Settings.MiniIsland) ShowIsland();
        timer.Start();
    }

    private Task Tick()
    {
        if (tickRunning || exiting || applying) return Task.CompletedTask;
        tickRunning = true;
        try
        {
            if (uptime.Elapsed.TotalSeconds - lastThemeCheck >= 30) { ApplyTheme(); lastThemeCheck = uptime.Elapsed.TotalSeconds; }
            var snapshot = RefreshMemory();
            if (IsBusy && operationClock.Elapsed.TotalSeconds >= 60)
            {
                StatusText.Text = $"{currentStep}耗时较长，仍在等待系统返回；可取消后续步骤。";
                SetDockOperation("等待 · " + currentStep);
            }
            if (!preview)
            {
                var trigger = automation.Evaluate(snapshot?.Load, IsBusy || applying || updates.Busy || store.Settings.AutomationNeedsConfirmation);
                if (trigger is not null)
                    _ = RoutineCleanup(trigger, true);
                if (store.Settings.CheckUpdates && uptime.Elapsed.TotalSeconds >= nextUpdateCheck)
                {
                    nextUpdateCheck = uptime.Elapsed.TotalSeconds + 6 * 3600;
                    _ = CheckUpdates();
                }
            }
            RefreshAutomationSummary();
        }
        finally { tickRunning = false; }
        return Task.CompletedTask;
    }
    public void ShowMain() { Show(); WindowState = WindowState.Normal; Activate(); }
    private MemorySnapshot? RefreshMemory()
    {
        try
        {
            var snapshot = memory.Read();
            dockStatus.SetMemory(snapshot); mini?.RefreshStatus();
            LoadText.Text = snapshot.Load.ToString("0"); MemoryBar.Value = snapshot.Load;
            UsedText.Text = Labels.Bytes(snapshot.Used); AvailableText.Text = Labels.Bytes(snapshot.Available);
            TotalText.Text = Labels.Bytes(snapshot.Total); CommitText.Text = $"{Labels.Bytes(snapshot.Commit)} / {Labels.Bytes(snapshot.CommitLimit)}";
            if (tray is not null) tray.Text = $"Click-Clean · {snapshot.Load:0}% · 可用 {Labels.Bytes(snapshot.Available)}";
            return snapshot;
        }
        catch (Exception ex) { dockStatus.SetMemory(null); mini?.RefreshStatus(); StatusText.Text = "内存读取失败：" + ex.Message; store.Log(ex.ToString()); return null; }
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
            SetBrush("Line", SystemColors.WindowTextColor); SetBrush("Accent", SystemColors.HighlightColor); SetBrush("AccentText", SystemColors.HighlightTextColor);
            SetBrush("Tint", SystemColors.ControlColor); SetBrush("DetailAccent", SystemColors.WindowTextColor);
            SetBrush("IslandBg", SystemColors.WindowColor); SetBrush("IslandText", SystemColors.WindowTextColor); SetBrush("IslandMuted", SystemColors.WindowTextColor);
            SetBrush("IslandBorder", SystemColors.WindowTextColor); SetBrush("IslandHoverBorder", SystemColors.HighlightColor); SetBrush("IslandGlow", SystemColors.HighlightColor);
            if (mini is not null) mini.MotionAllowed = MotionEnabled;
            NativeThemeApplied = WindowTheme.Apply(this, false, true, SystemColors.WindowColor, SystemColors.WindowTextColor); return;
        }
        SetBrush("Bg", dark ? "#000000" : "#F5F2EC"); SetBrush("Surface", dark ? "#0D0D0D" : "#FFFFFF");
        SetBrush("Text", dark ? "#FFFFFF" : "#16243C"); SetBrush("Muted", dark ? "#B8B8B8" : "#53647A");
        SetBrush("Line", dark ? "#343030" : "#E0DAD2"); SetBrush("Accent", dark ? "#B74454" : "#A62E3A");
        SetBrush("AccentText", "#FFFFFF"); SetBrush("Tint", dark ? "#2A1117" : "#F6E8EA");
        SetBrush("DetailAccent", dark ? "#FFFFFF" : "#96641F");
        SetBrush("IslandBg", dark ? "#000000" : "#FEF8F3"); SetBrush("IslandText", dark ? "#FFFFFF" : "#16243C"); SetBrush("IslandMuted", dark ? "#B8B8B8" : "#53647A");
        SetBrush("IslandBorder", dark ? "#55202A" : "#D8B784"); SetBrush("IslandHoverBorder", dark ? "#B74454" : "#A62E3A"); SetBrush("IslandGlow", dark ? "#74212F" : "#E9BE6C");
        if (mini is not null) mini.MotionAllowed = MotionEnabled;
        NativeThemeApplied = WindowTheme.Apply(this, dark, false, ((SolidColorBrush)Application.Current.Resources["Bg"]).Color, ((SolidColorBrush)Application.Current.Resources["Text"]).Color);
    }
    private static void SetBrush(string key, string color) => SetBrush(key, (Color)ColorConverter.ConvertFromString(color));
    private static void SetBrush(string key, Color color)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush && brush.Color == color) return;
        Application.Current.Resources[key] = new SolidColorBrush(color);
    }
    private async void Clean_Click(object sender, RoutedEventArgs e) => await RoutineCleanup();
    private async Task RoutineCleanup(string trigger = "手动", bool automatic = false)
    {
        if (IsBusy || applying) return;
        if (automatic && store.Settings.AutomationNeedsConfirmation) return;
        MemorySnapshot? snapshot = null;
        try { snapshot = memory.Read(); } catch (Exception ex) { store.Log("压力检查失败：" + ex.Message); }
        var plan = RoutineCleanupPolicy.Evaluate(snapshot, store.Settings.Automation.ThresholdPercent, automatic);
        StatusText.Text = plan.Message;
        if (plan.Skipped)
        {
            dockStatus.SetResult(snapshot is null ? "状态未知 · 未整理" : "无需整理"); mini?.RefreshStatus();
            if (automatic) automation.RecordSkipped();
            return;
        }
        await Cleanup(plan.Commands, trigger, automatic);
    }
    private MemoryCommand[] Selected() => new[] { (WorkingCheck, MemoryCommand.WorkingSets), (ModifiedCheck, MemoryCommand.ModifiedPages), (StandbyCheck, MemoryCommand.StandbyCache) }
        .Where(p => p.Item1.IsChecked == true).Select(p => p.Item2).ToArray();
    private void Selection_Click(object sender, RoutedEventArgs e)
    {
        store.Settings.SelectedSteps = Selected(); SaveSettings();
        if (Selected().Length == 0) StatusText.Text = "高级手动项目未选择；不会自动补选三步。";
    }
    private async void Custom_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected();
        if (selected.Length == 0) { StatusText.Text = "请至少选择一个整理项目。"; return; }
        if (!preview && selected.Any(command => command is MemoryCommand.WorkingSets or MemoryCommand.ModifiedPages) &&
            MessageBox.Show(this, "所选步骤会干预全系统驻留页或写回修改页，可能导致重新加载、短暂卡顿和磁盘忙碌。它不释放应用的全部分配，也不保证提速。仍要执行？", "高级手动整理", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Cleanup(selected);
    }
    private async Task Cleanup(IEnumerable<MemoryCommand> commands, string trigger = "手动", bool automatic = false)
    {
        if (IsBusy || applying) { StatusText.Text = "当前操作尚未结束，请稍等。"; return; }
        var selected = commands.Distinct().OrderBy(c => (int)c).ToArray();
        cancellation = new();
        activeAutomatic = automatic;
        CleanButton.IsEnabled = CustomButton.IsEnabled = Options.IsEnabled = false;
        if (mini is not null) { mini.CleanButton.IsEnabled = false; mini.IsWorking = true; }
        CancelButton.Visibility = Visibility.Visible; CancelButton.IsEnabled = true;
        CleanButton.Content = "正在整理…"; StatusText.Text = preview ? "正在演示整理过程，不调用系统整理接口。" : "正在准备权限…";
        SetDockOperation("准备整理…");
        operationClock.Restart(); UpdateChanged();
        var success = false;
        var operationExecuted = false;
        try
        {
            var result = await engine.RunAsync(selected, command => Dispatcher.Invoke(() => {
                operationExecuted = true;
                currentStep = Labels.Name(command); StatusText.Text = "正在" + currentStep + "…";
                SetDockOperation($"{Array.IndexOf(selected, command) + 1}/{selected.Length} {currentStep}");
            }), cancellation.Token, preview ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromSeconds(3), () => Dispatcher.Invoke(() => {
                observingMemory = true; CancelButton.Content = "结束复测";
                currentStep = "复测内存状态"; StatusText.Text = "系统调用已返回，正在复测；观测变化不等于提速。";
                SetDockOperation("复测内存…");
            }));
            result = result with { Trigger = preview ? "预览（模拟）" : trigger };
            success = result.Success; ShowResult(result);
            StatusText.Text = result.Success ? result.ObservationError ?? "步骤已完成；前后变化含系统其他活动，不证明性能提升。" : result.Error ?? "已取消尚未开始的步骤。";
            try { store.Add(result); } catch (Exception ex) { StatusText.Text += " 记录保存失败：" + ex.Message; }
        }
        catch (Exception ex)
        {
            StatusText.Text = "整理失败：" + ex.Message; store.Log(ex.ToString());
            dockStatus.SetResult("整理失败"); mini?.RefreshStatus();
        }
        finally
        {
            automation.RecordCompletion(success, automatic);
            if (!preview && operationExecuted) { store.Settings.LastCleanupUtc = DateTimeOffset.UtcNow; SaveSettings(); }
            operationClock.Stop(); cancellation.Dispose(); cancellation = null;
            activeAutomatic = false;
            observingMemory = false; CancelButton.Content = "取消后续";
            CleanButton.IsEnabled = CustomButton.IsEnabled = Options.IsEnabled = true;
            if (mini is not null) { mini.CleanButton.IsEnabled = true; mini.IsWorking = false; }
            CancelButton.Visibility = Visibility.Collapsed; CleanButton.Content = "一键整理";
            RefreshMemory(); RefreshAutomationSummary(); UpdateChanged();
            if (exitRequested) TryExit();
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        cancellation?.Cancel(); CancelButton.IsEnabled = false;
        StatusText.Text = observingMemory ? "已请求结束复测，保留已经执行的步骤与即时观测。" : "已请求取消；等待当前系统调用返回后停止。";
        SetDockOperation(observingMemory ? "结束复测…" : "取消后续 · 等待返回");
    }
    public void ShowResult(CleanupResult result, bool showDock = true)
    {
        ResultCard.Visibility = Visibility.Visible;
        ResultTitle.Text = result.Success ? "步骤完成" : result.Steps.Any(s => s.Success) ? "部分完成" : result.Cancelled ? "已取消" : "整理失败";
        if (!showDock) ResultTitle.Text = "上次 · " + ResultTitle.Text;
        ResultDelta.Text = "可用内存 " + Labels.Delta(result.AvailableChange);
        ResultMeta.Text = $"{result.Time:MM-dd HH:mm} · {result.Seconds:0.0}s";
        ResultError.Text = result.Error ?? result.ObservationError ?? (result.Cancelled ? "已停止尚未开始的步骤。" : "观测变化不代表性能提升。");
        ResultDetails.Text = $"{result.Time:yyyy-MM-dd HH:mm:ss} · {result.Trigger}\n" + string.Join("\n", result.Steps.Select(s => $"{(s.Success ? "✓" : "!")} {Labels.Name(s.Command)} · {s.Seconds:0.00}s · {s.StatusHex}")) +
            $"\n即时可用变化：{Labels.Delta(result.AvailableChange)}\n即时提交变化：{Labels.Delta(result.CommitChange)}\n" +
            (result.FollowUp is null ? "复测：未完成 / 无记录" : $"即时采样后 {result.FollowUpSeconds:0.00}s 复测变化：{Labels.Delta(result.FollowUpChange)}") +
            $"\n整理前提交：{Labels.Bytes(result.Before.Commit)} / {Labels.Bytes(result.Before.CommitLimit)}" +
            (result.Before.SystemCache is { } cache ? $"\n整理前系统缓存（含系统工作集，不是纯待机）：{Labels.Bytes(cache)}" : "") +
            (result.ObservationError is null ? "" : "\n" + result.ObservationError);
        if (showDock) {
            var title = result.Success ? "完成" : result.Cancelled ? "已取消" : result.Steps.Any(s => s.Success) ? "部分完成" : "失败";
            dockStatus.SetResult(title + " · " + (result.AvailableChange is null ? "变化未知" : Labels.Delta(result.AvailableChange)));
            mini?.RefreshStatus();
        }
    }
    public void SetDockOperation(string text)
    {
        dockStatus.SetOperation(text); mini?.RefreshStatus();
    }
    public void PreviewDockIdle(MemorySnapshot snapshot) { dockStatus.SetIdle(); dockStatus.SetMemory(snapshot); mini?.RefreshStatus(); }
    private void OpenOverlay(string title, bool settings, bool animate)
    {
        if (Overlay.Visibility != Visibility.Visible) { overlayReturnFocus = Keyboard.FocusedElement; overlayReturnOffset = MainContent.VerticalOffset; }
        OverlayTitle.Text = title; SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        Header.IsEnabled = MainContent.IsEnabled = Footer.IsEnabled = false; Overlay.Visibility = Visibility.Visible;
        if (animate && MotionEnabled)
        {
            // 设置和历史已经是平面工具页，不再从标题按钮缩放成一张大弹卡。
            Overlay.RenderTransform = Transform.Identity;
            Overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(120)));
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
            UpdateCheck.IsChecked = store.Settings.CheckUpdates; DownloadCheck.IsChecked = false;
            ApplyHiddenCheck.IsChecked = false;
            var a = store.Settings.Automation; ThresholdCheck.IsChecked = a.ThresholdEnabled; TimerCheck.IsChecked = a.TimerEnabled;
            ThresholdInput.Text = a.ThresholdPercent.ToString(); SustainInput.Text = a.SustainSeconds.ToString();
            CooldownInput.Text = a.CooldownMinutes.ToString(); IntervalInput.Text = a.IntervalMinutes.ToString(); FullAutoCheck.IsChecked = a.FullCleanup;
            FullAutoCheck.IsEnabled = false;
            AutomationFeedback.Text = store.Settings.AutomationNeedsConfirmation ? "本版自动整理改为原版三步，旧自动配置已暂停。已有阈值保留，默认值为55%；请核对后确认保存，或关闭两个开关后保存。" : "";
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
            HistoryItems.Children.Add(CreateHistoryCard(item));
        }
        OpenOverlay("整理历史", false, animate);
    }
    private static Border CreateHistoryCard(CleanupResult item)
    {
        var panel = new StackPanel();
        var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = $"{item.Time:MM-dd HH:mm}", FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var outcome = new TextBlock { Text = item.Success ? "完成" : item.Cancelled ? "取消" : item.Steps.Any(step => step.Success) ? "部分完成" : "失败", FontSize = 11 };
        var badge = new Border { Child = outcome, CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 4, 8, 4) };
        badge.SetResourceReference(Border.BackgroundProperty, "Tint"); Grid.SetColumn(badge, 1); header.Children.Add(badge); panel.Children.Add(header);
        panel.Children.Add(new TextBlock { Text = "可用内存 " + Labels.Delta(item.AvailableChange), FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 5), TextWrapping = TextWrapping.Wrap });
        var meta = new TextBlock { Text = $"{item.Trigger} · {item.Seconds:0.0}s · {item.Steps.Count} 个已执行步骤", FontSize = 11, TextWrapping = TextWrapping.Wrap };
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); panel.Children.Add(meta);
        var detail = new TextBlock { Text = string.Join("\n", item.Steps.Select(step => $"{Labels.Name(step.Command)} · {step.Seconds:0.00}s · {step.StatusHex}")) +
            $"\n即时提交变化：{Labels.Delta(item.CommitChange)}\n" +
            (item.FollowUp is null ? "复测：无记录 / 未完成" : $"即时采样后 {item.FollowUpSeconds:0.00}s 复测可用变化：{Labels.Delta(item.FollowUpChange)}") +
            (item.Error is null ? "" : "\n" + item.Error) + (item.ObservationError is null ? "" : "\n" + item.ObservationError), TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var expander = new Expander { Header = "查看步骤与返回码", Content = detail, Margin = new Thickness(0, 10, 0, 0) };
        expander.SetResourceReference(Control.ForegroundProperty, "Muted"); panel.Children.Add(expander);
        // 历史是证据列表，不应给每条记录重复套一张视觉主卡。
        var card = new Border { Child = panel, Padding = new Thickness(0, 14, 0, 14), BorderThickness = new Thickness(0, 0, 0, 1) };
        card.SetResourceReference(Border.BorderBrushProperty, "Line"); return card;
    }
    private void CloseOverlay_Click(object sender, RoutedEventArgs e) => CloseOverlay();
    public void CloseOverlay()
    {
        if (Overlay.Visibility != Visibility.Visible) return;
        Overlay.Visibility = Visibility.Collapsed; Header.IsEnabled = MainContent.IsEnabled = Footer.IsEnabled = true;
        if (overlayReturnFocus is UIElement target && target.IsVisible && target.IsEnabled) target.Focus(); else SettingsButton.Focus();
        // 焦点恢复不应把状态主视觉突然滚走。
        var offset = overlayReturnOffset;
        _ = Dispatcher.InvokeAsync(() => MainContent.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
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
        store.Settings.CheckUpdates = UpdateCheck.IsChecked == true; store.Settings.AutoDownloadUpdates = false;
        store.Settings.AutoApplyHidden = false;
        if (store.Settings.MiniIsland) ShowIsland(); else mini?.Hide();
        if (mini is not null) mini.MotionAllowed = MotionEnabled;
        SaveSettings(); SetSampling();
    }
    private void SaveAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadNumber(ThresholdInput, 55, 98, out var threshold) || !ReadNumber(SustainInput, 15, 600, out var sustain) ||
            !ReadNumber(IntervalInput, 15, 1440, out var interval) || !ReadNumber(CooldownInput, 5, 240, out var cooldown))
        { AutomationFeedback.Text = "请输入标注范围内的整数；设置尚未改变。"; return; }
        var next = new AutomationSettings { ThresholdEnabled = ThresholdCheck.IsChecked == true, TimerEnabled = TimerCheck.IsChecked == true,
            ThresholdPercent = threshold, SustainSeconds = sustain, IntervalMinutes = interval, CooldownMinutes = cooldown, FullCleanup = true };
        if ((next.ThresholdEnabled || next.TimerEnabled) && !preview)
        {
            if (MessageBox.Show(this, $"自动模式在达到 {threshold}% 占用门槛时执行原版三步：裁剪全系统工作集、刷新修改页、清理待机缓存。\n\n这可能造成缺页、短暂卡顿和磁盘忙碌，不保证提速。55% 在正常使用中也可能经常达到；冷却 {cooldown} 分钟。确认保存并启用所选自动规则？", "确认自动三步整理", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        store.Settings.Automation = next;
        store.Settings.AutomationNeedsConfirmation = false;
        automation.Configure(next);
        if (activeAutomatic && !next.TimerEnabled && !next.ThresholdEnabled) cancellation?.Cancel();
        SaveSettings(); RefreshAutomationSummary(); AutomationFeedback.Text = preview ? "已保存预览设置，不会自动执行系统整理。" : "已保存。软件运行期间生效；连续失败暂停后可再次保存恢复。";
    }
    private static bool ReadNumber(TextBox field, int min, int max, out int value)
    { var valid = int.TryParse(field.Text.Trim(), out value) && value >= min && value <= max; if (!valid) field.Focus(); return valid; }
    private void RefreshAutomationSummary()
    {
        var a = store.Settings.Automation;
        if (store.Settings.AutomationNeedsConfirmation && (a.ThresholdEnabled || a.TimerEnabled) && !IsBusy)
            StatusText.Text = "旧自动配置已暂停：本版恢复原版三步，请在设置中核对阈值并确认保存。";
        AutomationSummary.Text = store.Settings.AutomationNeedsConfirmation ? "自动规则变更待确认；已有阈值和开关保留，尚未恢复执行。" : automation.Paused ? "自动清理已暂停：连续失败 3 次，请在设置中恢复。" :
            $"阈值：{(a.ThresholdEnabled ? a.ThresholdPercent + "% / " + a.SustainSeconds + "秒" : "关闭")}    定时：{(a.TimerEnabled ? a.IntervalMinutes + "分钟" : "关闭")}";
        if (a.TimerEnabled && automation.TimerRemaining is { } remaining && !automation.Paused && !store.Settings.AutomationNeedsConfirmation) AutomationSummary.Text += $"\n下一周期约 {Math.Ceiling(remaining.TotalMinutes):0} 分钟后；冷却 {a.CooldownMinutes} 分钟。";
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
            mini = new MiniIsland(async () => { lastInputWasPointer = true; await RoutineCleanup("底部岛"); }, dockStatus);
            mini.RevealChanged += () => { SetSampling(); if (mini.IsRevealed) RefreshMemory(); };
        }
        mini.MotionAllowed = MotionEnabled; mini.IsWorking = IsBusy;
        mini.RefreshStatus(); mini.CleanButton.IsEnabled = !IsBusy;
        mini.Show(); SetSampling();
    }
    private void SetSampling() => timer.Interval = TimeSpan.FromSeconds(IsVisible && WindowState != WindowState.Minimized || mini?.IsVisible == true && mini.IsRevealed ? 1 : 10);
    private async Task CheckUpdates()
    {
        if (applying) return;
        if (preview && !simulatedUpdates) { UpdateFeedback.Text = "交互预览不会访问发布服务或替换程序。"; return; }
        await updates.CheckAsync();
    }
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckUpdates();
    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (applying || preview && !simulatedUpdates) return;
        await updates.DownloadAsync();
    }
    private async void HomeUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (applying || updates.Busy || preview && !simulatedUpdates) return;
        if (updates.Ready) ApplyUpdate();
        else if (updates.Available) await updates.DownloadAsync();
        else await CheckUpdates();
    }
    private void UpdateChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateChanged); return; }
        UpdateFeedback.Text = updates.Status; CheckUpdateButton.IsEnabled = !updates.Busy;
        DownloadUpdateButton.Visibility = updates.Available && !updates.Ready ? Visibility.Visible : Visibility.Collapsed;
        DownloadUpdateButton.IsEnabled = !updates.Busy;
        ApplyUpdateButton.Visibility = updates.Ready ? Visibility.Visible : Visibility.Collapsed;
        ApplyUpdateButton.IsEnabled = !updates.Busy && !IsBusy && !applying;
        var presentation = UpdatePresentation.Create(updates.Phase, updates.Available, updates.Ready, updates.Busy, IsBusy || applying);
        HomeUpdateBanner.Visibility = presentation.Visible ? Visibility.Visible : Visibility.Collapsed;
        HomeUpdateText.Text = updates.Status; HomeUpdateButton.Content = presentation.ButtonText;
        HomeUpdateButton.IsEnabled = presentation.Enabled;
    }
    private void ApplyUpdate_Click(object sender, RoutedEventArgs e) => ApplyUpdate();
    private async void ApplyUpdate()
    {
        if (preview && !simulatedUpdates || IsBusy || applying || updates.Busy || !updates.Ready) return;
        applying = true; timer.Stop(); SaveSettings();
        try {
            await updates.ApplyAsync(!IsVisible);
            // 模拟更新不会退出进程；保证诊断不会留下停掉的采样计时器。
            if (simulatedUpdates) { applying = false; timer.Start(); UpdateChanged(); }
        }
        catch (Exception ex)
        {
            applying = false; UpdateFeedback.Text = HomeUpdateText.Text = "更新应用失败，现有版本保留：" + ex.Message;
            HomeUpdateBanner.Visibility = Visibility.Visible;
            store.Log(ex.ToString()); timer.Start(); ShowMain();
        }
    }
    private void Repository_Click(object sender, RoutedEventArgs e)
    { if (BuildInfo.Repository.Length == 0) { MessageBox.Show(this, "此开发构建尚未连接正式仓库。"); return; } OpenExternal(BuildInfo.Repository); }
    private void ProjectLink_Click(object sender, RoutedEventArgs e)
    {
        var uri = ProjectLinks.Resolve((sender as Button)?.Tag as string, BuildInfo.Repository);
        if (uri is null) { StatusText.Text = "链接配置无效，未打开外部页面。"; return; }
        projectLinkOpener(uri);
    }
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
