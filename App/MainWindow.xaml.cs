using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ClickClean.ViewModels;

namespace ClickClean;

public partial class MainWindow : Window
{
    private readonly Store store;
    private readonly bool preview, simulatedUpdates;
    private readonly IMemoryApi memory;
    private readonly IApplicationUpdates updates;
    private readonly Action<Uri> projectLinkOpener;
    private readonly AutomationPolicy automation = new();
    private readonly CleanupCoordinator cleanup;
    private readonly SamplingCoordinator sampler;
    private readonly ProcessObservation processes;
    private readonly UpdateRecovery recovery;
    private readonly DockStatus dockStatus = new();
    private System.Windows.Forms.NotifyIcon? tray;
    private MiniIsland? mini;
    private bool applying, disposed, exitRequested, loadingSettings, pointerInput;
    private string settingsReturn = "index", popupPage = "";
    private IInputElement? popupReturnFocus;
    public ShellState Presentation { get; }
    public bool IsBusy => cleanup.Busy;
    public bool TrayVisible => tray?.Visible == true;
    public double SamplingSeconds => sampler.Seconds;
    public bool MotionEnabled => !store.Settings.ReduceMotion && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
    public MiniIsland? DesktopIsland => mini;
    public bool NativeThemeApplied { get; private set; }
    public Func<MessageBoxResult>? DraftDecisionOverride { get; set; }

    public MainWindow(Store store, bool trayMode = false, bool verification = false, IMemoryApi? memoryApi = null,
        Action<Uri>? projectLinkOpener = null, IApplicationUpdates? updateOverride = null,
        IAutomationEnvironmentReader? environmentOverride = null, IProcessMemoryReader? processOverride = null)
    {
        this.store = store; preview = verification;
        this.projectLinkOpener = projectLinkOpener ?? (uri => OpenExternal(uri.AbsoluteUri));
        updates = updateOverride ?? new UpdateService(Path.Combine(store.Root, "update-state.json"));
        simulatedUpdates = preview && updateOverride is not null;
        memory = memoryApi ?? (preview ? new PreviewMemoryApi() : new WindowsMemoryApi());
        recovery = new UpdateRecovery(store.Root);
        cleanup = new(store, memory, automation, Dispatcher, preview);
        sampler = new(store, memory, cleanup, automation, updates, environmentOverride ?? (preview ? null : new AutomationEnvironmentReader()), preview, () => applying);
        processes = new(processOverride ?? new ProcessMemoryReader()); processes.Initialize();
        Presentation = new(new UiCommand(DispatchAction)) { Preview = preview };
        InitializeComponent(); DataContext = Presentation;
        Title = preview ? "Click-Clean 即清 · 交互预览" : "Click-Clean 即清";
        SourceInitialized += (_, _) => ApplyTheme();
        PreviewMouseDown += (_, _) => pointerInput = true;
        PreviewKeyDown += (_, e) => { pointerInput = false; if (e.Key == Key.Escape && DetailsPopup.IsOpen) { ClosePopup(); e.Handled = true; } };
        PopupFrame.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { ClosePopup(); e.Handled = true; } };
        DetailsPopup.Closed += (_, _) => {
            if (Presentation.Page == popupPage && popupReturnFocus is UIElement target && target.IsVisible && target.IsEnabled) target.Focus();
        };
        DetailsPopup.Opened += (_, _) => {
            PopupFrame.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            var animate = pointerInput && MotionEnabled;
            PopupShift.Y = animate ? 6 : 0;
            IslandMotion.To(PopupShift, TranslateTransform.YProperty, 0, 180, animate);
            PopupFrame.Opacity = animate ? 0.8 : 1;
            IslandMotion.Opacity(PopupFrame, 1, 180, animate);
        };
        Closing += OnClosing;
        IsVisibleChanged += (_, _) => SetSampling();
        StateChanged += (_, _) => SetSampling();
        SizeChanged += (_, _) => UpdateLayoutMode();
        SystemParameters.StaticPropertyChanged += SystemPreferenceChanged;
        WorkingCheck.IsChecked = store.Settings.SelectedSteps.Contains(MemoryCommand.WorkingSets);
        ModifiedCheck.IsChecked = store.Settings.SelectedSteps.Contains(MemoryCommand.ModifiedPages);
        StandbyCheck.IsChecked = store.Settings.SelectedSteps.Contains(MemoryCommand.StandbyCache);
        LoadPreferences();
        Presentation.Settings.PropertyChanged += PreferenceChanged;
        var recent = store.Settings.LastCleanupUtc is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(store.Settings.Automation.CooldownMinutes);
        automation.Configure(store.Settings.Automation, recent);
        cleanup.Changed += CleanupChanged;
        cleanup.DockOperation += SetDockOperation;
        cleanup.DockResult += text => { dockStatus.SetResult(text); mini?.RefreshStatus(); };
        cleanup.ResultAvailable += ShowResult;
        sampler.MemoryChanged += MemoryChanged;
        sampler.ThemePoll += () => ApplyTheme();
        sampler.AutomaticChanged += RefreshAutomationSummary;
        processes.Changed += ProcessesChanged;
        processes.Failed += text => Presentation.ProcessFeedback = text;
        updates.Changed += UpdateChanged;
        Loaded += async (_, _) => {
            FitWorkArea(); ApplyTheme();
            if (!preview) { Presentation.RecoveryVisible = recovery.HasIncompleteUpdate; _ = Dispatcher.BeginInvoke(() => recovery.MarkHealthy(BuildInfo.Version), DispatcherPriority.ApplicationIdle); }
            if (store.Settings.CheckUpdates && (!preview || simulatedUpdates)) await CheckUpdates();
        };
        if (!preview) CreateTray();
        RefreshHistory();
        if (store.History.FirstOrDefault() is { } result) ShowResult(result, false);
        sampler.Read(); RefreshAutomationSummary(); ApplyTheme();
        if (store.Settings.MiniIsland) ShowIsland();
        sampler.SetVisible(!trayMode, false); sampler.Start(); UpdateChanged();
    }

    private async Task DispatchAction(object? action)
    {
        if (disposed) return;
        try {
            if (action is ResultRow row) { Presentation.Detail = row; OpenPopup("result", Keyboard.FocusedElement as FrameworkElement); return; }
            if (action is ProcessRow process) { Presentation.ProcessDetail = process.Description; OpenPopup("process", Keyboard.FocusedElement as FrameworkElement); return; }
            switch (action as string) {
                case "overview": Navigate("overview"); break;
                case "history": OpenHistory(); break;
                case "diagnostics": Navigate("diagnostics"); break;
                case "settings": OpenSettings(); break;
                case "settings-appearance": OpenSettingsSection("appearance", "index"); break;
                case "settings-automation": OpenSettingsSection("automation", "index"); break;
                case "settings-updates": OpenSettingsSection("updates", "index"); break;
                case "settings-about": OpenSettingsSection("about", "index"); break;
                case "automation-shortcut": OpenSettingsSection("automation", "overview"); break;
                case "settings-back": if (settingsReturn == "overview") Navigate("overview"); else OpenSettings(); break;
                case "clean": await RoutineCleanup(); break;
                case "cancel-clean": cleanup.Cancel(); break;
                case "memory-details": OpenPopup("memory", OverviewPane.MemoryDetailsButton); break;
                case "custom": OpenPopup("custom", OverviewPane.CustomButton); break;
                case "selection": store.Settings.SelectedSteps = Selected(); SaveSettings(); break;
                case "custom-run": await CustomCleanup(); break;
                case "recent-details": if (Presentation.Recent is { } value) { Presentation.Detail = value; OpenPopup("result", OverviewPane.RecentDetailsButton); } break;
                case "close-popup": ClosePopup(); break;
                case "copy-result": if (Presentation.Detail is { } detail) Clipboard.SetText(detail.Details); break;
                case "refresh-processes": await processes.Read(); break;
                case "save-automation": SaveAutomation(); break;
                case "check-update": await CheckUpdates(); break;
                case "download-update": if (!applying && (!preview || simulatedUpdates)) await updates.DownloadAsync(); break;
                case "cancel-update": updates.Cancel(); break;
                case "apply-update": await ApplyUpdate(); break;
                case "home-update":
                    if (updates.Ready) await ApplyUpdate();
                    else if (updates.Available && (!preview || simulatedUpdates)) await updates.DownloadAsync();
                    else await CheckUpdates();
                    break;
                case "repository": case "blog": case "author":
                    var uri = ProjectLinks.Resolve(action as string, BuildInfo.Repository);
                    if (uri is null) Presentation.Settings.Feedback = "链接配置无效。"; else projectLinkOpener(uri);
                    break;
                case "releases": OpenExternal(BuildInfo.Repository.TrimEnd('/') + "/releases"); break;
                case "license":
                    var file = Path.Combine(AppContext.BaseDirectory, "LICENSE");
                    if (File.Exists(file)) OpenExternal(file); else Presentation.Settings.Feedback = "完整许可证见正式包的 LICENSE 文件。";
                    break;
                case "clear-history": ClearHistory(); break;
                case "export": ExportLog(); break;
                case "delete-data": DeleteData(); break;
                case "exit": TryExit(); break;
            }
        }
        catch (Exception ex) { store.Log(ex.ToString()); Presentation.Settings.Feedback = "操作未完成：" + ex.Message; if (Presentation.Overview) Presentation.Operation = "操作未完成：" + ex.Message; }
    }

    private async Task RoutineCleanup(string trigger = "手动", bool automatic = false)
    {
        if (IsBusy || applying || disposed) return;
        var environment = sampler.Environment;
        if (automatic) { if (preview) environment = await sampler.ReadEnvironment(); else environment = environment with { Fullscreen = ForegroundActivity.IsFullscreen() }; }
        if (!IsBusy && !applying && !disposed) await cleanup.RunRoutineAsync(trigger, automatic, environment);
    }
    private MemoryCommand[] Selected() => new[] { (WorkingCheck, MemoryCommand.WorkingSets), (ModifiedCheck, MemoryCommand.ModifiedPages), (StandbyCheck, MemoryCommand.StandbyCache) }.Where(x => x.Item1.IsChecked == true).Select(x => x.Item2).ToArray();
    private async Task CustomCleanup()
    {
        if (IsBusy || applying) return;
        var selected = Selected();
        if (selected.Length == 0) { Presentation.Operation = "请至少选择一个整理项目。"; return; }
        if (!preview && selected.Any(c => c is MemoryCommand.WorkingSets or MemoryCommand.ModifiedPages) &&
            MessageBox.Show(this, "所选步骤作用于全系统，可能导致重新加载、短暂卡顿和磁盘忙碌。仍要执行？", "自定义整理", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ClosePopup(); await cleanup.RunAsync(selected);
    }
    private void CleanupChanged()
    {
        if (disposed) return;
        Presentation.Busy = IsBusy; Presentation.Operation = cleanup.Status;
        Presentation.CanCancel = cleanup.CanCancel; Presentation.CancelText = cleanup.CancelLabel;
        if (Presentation.Recent is { } recent) recent.SetPending(cleanup.PendingResult == recent.Id);
        if (mini is not null) { mini.CleanButton.IsEnabled = !IsBusy && !applying; mini.IsWorking = IsBusy; }
        sampler.Read(); RefreshAutomationSummary(); UpdateChanged();
        if (!IsBusy && exitRequested) Dispatcher.BeginInvoke(TryExit);
    }
    private void MemoryChanged(MemorySnapshot? snapshot, string error)
    {
        Presentation.Load = snapshot?.Load.ToString("0") ?? "—"; Presentation.Percentage = snapshot?.Load ?? 0;
        Presentation.Available = snapshot is null ? "—" : Labels.Bytes(snapshot.Available);
        Presentation.Used = snapshot is null ? "—" : Labels.Bytes(snapshot.Used);
        Presentation.Total = snapshot is null ? "—" : Labels.Bytes(snapshot.Total);
        Presentation.Commit = snapshot is null ? "—" : Labels.Bytes(snapshot.Commit) + " / " + Labels.Bytes(snapshot.CommitLimit);
        Presentation.Cache = snapshot?.SystemCache is { } cache ? Labels.Bytes(cache) : "—";
        Presentation.MemoryError = error; dockStatus.SetMemory(snapshot); mini?.RefreshStatus();
        if (tray is not null) tray.Text = snapshot is null ? "Click-Clean · 内存读数暂不可用" : $"Click-Clean · {snapshot.Load:0}% · 可用 {Labels.Bytes(snapshot.Available)}";
    }
    public void ShowResult(CleanupResult result, bool showDock = true)
    {
        if (disposed) return;
        if (Presentation.Recent?.Id == result.Id) Presentation.Recent.Update(result); else Presentation.Recent = new(result);
        if (Presentation.Detail?.Id == result.Id) Presentation.Detail.Update(result);
        RefreshHistory();
        if (showDock) { dockStatus.SetResult((result.Success ? "完成" : result.Cancelled ? "已取消" : result.Steps.Any(s => s.Success) ? "部分完成" : "失败") + " · " + (result.PreferredChange is null ? "变化未知" : (result.FollowUp is null ? "即时 " : "复测 ") + Labels.Delta(result.PreferredChange))); mini?.RefreshStatus(); }
    }
    private void RefreshHistory()
    {
        var ids = store.History.Select(r => r.Id).ToHashSet();
        for (var i = Presentation.History.Count - 1; i >= 0; i--) if (!ids.Contains(Presentation.History[i].Id)) Presentation.History.RemoveAt(i);
        for (var index = 0; index < store.History.Count; index++) {
            var value = store.History[index]; var row = Presentation.History.FirstOrDefault(r => r.Id == value.Id);
            if (row is null) Presentation.History.Insert(index, new(value));
            else { row.Update(value); var previous = Presentation.History.IndexOf(row); if (previous != index) Presentation.History.Move(previous, index); }
        }
        HistoryPane.EmptyHistory.Visibility = Presentation.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ProcessesChanged(IReadOnlyList<ProcessGrowthRow> rows, string feedback)
    {
        if (disposed) return;
        var held = DiagnosticsPane.GrowthItems.IsMouseOver || DiagnosticsPane.GrowthItems.IsKeyboardFocusWithin || DetailsPopup.IsOpen;
        foreach (var row in Presentation.Processes) { var value = rows.FirstOrDefault(r => r.Current.Id == row.Id && r.Current.StartedUtcTicks == row.Started); if (value is not null) row.Update(value); }
        if (!held) {
            var keys = rows.Select(r => (r.Current.Id, r.Current.StartedUtcTicks)).ToHashSet();
            for (var i = Presentation.Processes.Count - 1; i >= 0; i--) if (!keys.Contains((Presentation.Processes[i].Id, Presentation.Processes[i].Started))) Presentation.Processes.RemoveAt(i);
            for (var i = 0; i < rows.Count; i++) {
                var value = rows[i]; var row = Presentation.Processes.FirstOrDefault(r => r.Id == value.Current.Id && r.Started == value.Current.StartedUtcTicks);
                if (row is null) Presentation.Processes.Insert(i, new(value));
                else { var index = Presentation.Processes.IndexOf(row); if (index != i) Presentation.Processes.Move(index, i); }
            }
        }
        Presentation.ProcessFeedback = feedback + (held ? " · 操作中保持行位置" : "");
    }
    public void ApplyTheme(string? overrideTheme = null) { NativeThemeApplied = ThemePalette.Apply(this, overrideTheme ?? store.Settings.Theme, store.Settings.ReduceMotion); if (mini is not null) { mini.MotionAllowed = MotionEnabled; mini.RefreshStatus(); } }
    private void SystemPreferenceChanged(object? sender, PropertyChangedEventArgs e) { if (!disposed) Dispatcher.BeginInvoke(() => { if (!disposed) { FitWorkArea(); ApplyTheme(); } }); }
    private void FitWorkArea() { var area = SystemParameters.WorkArea; MinWidth = Math.Min(440, area.Width); MinHeight = Math.Min(540, area.Height); Width = Math.Min(Width, area.Width); Height = Math.Min(Height, area.Height); UpdateLayoutMode(); }
    private void UpdateLayoutMode()
    {
        Presentation.Compact = ActualWidth < 640;
        var margin = Presentation.Compact ? 16 : 28;
        ContentFrame.Margin = new(margin, 24, margin, 0);
        Footer.Margin = new(margin, 12, margin, 14);
    }
    public void ShowMain() { Show(); WindowState = WindowState.Normal; Activate(); }
    public void SetDockOperation(string text) { dockStatus.SetOperation(text); mini?.RefreshStatus(); }
    public void PreviewDockIdle(MemorySnapshot snapshot) { dockStatus.SetIdle(); dockStatus.SetMemory(snapshot); mini?.RefreshStatus(); }
    private void SetSampling() { if (disposed) return; var visible = IsVisible && WindowState != WindowState.Minimized; sampler.SetVisible(visible, mini?.IsVisible == true && mini.IsRevealed); processes.SetVisible(visible); }
    public void ShowIsland()
    {
        if (disposed) return;
        if (mini is null) {
            mini = new MiniIsland(async () => { pointerInput = true; await RoutineCleanup("底部岛"); }, dockStatus);
            mini.RevealChanged += () => { SetSampling(); if (mini?.IsRevealed == true) sampler.Read(); };
        }
        mini.MotionAllowed = MotionEnabled; mini.IsWorking = IsBusy; mini.CleanButton.IsEnabled = !IsBusy && !applying;
        mini.Show(); mini.RefreshStatus(); SetSampling();
    }
    private void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开 Click-Clean 即清", null, (_, _) => Dispatcher.BeginInvoke(ShowMain));
        menu.Items.Add("一键整理", null, (_, _) => Dispatcher.BeginInvoke(async () => await RoutineCleanup()));
        menu.Items.Add("显示 / 收起桌面岛", null, (_, _) => Dispatcher.BeginInvoke(() => { Presentation.Settings.Island = mini?.IsVisible != true; }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator()); menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(TryExit));
        var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application;
        tray = new() { Icon = icon, Text = "Click-Clean 即清", ContextMenuStrip = menu, Visible = true }; tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMain);
        try { if (StartupTask.Exists() || store.Settings.Startup) { StartupTask.Set(true); store.Settings.Startup = true; if (store.MigratedLegacy) StartupTask.RemoveLegacy(); SaveSettings(); LoadPreferences(); } } catch (Exception ex) { store.Log(ex.ToString()); }
    }
    public void TryExit()
    {
        if (!CheckDraft()) return;
        if (IsBusy) { exitRequested = true; ShowMain(); cleanup.Cancel(); return; }
        DisposeResources(); Application.Current.Shutdown();
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (disposed) return; e.Cancel = true; if (store.Settings.CloseToTray && !preview) { ClosePopup(); Hide(); } else TryExit(); }
    public void DisposeResources()
    {
        if (disposed) return; disposed = true;
        ClosePopup(); SystemParameters.StaticPropertyChanged -= SystemPreferenceChanged; updates.Changed -= UpdateChanged;
        Presentation.Settings.PropertyChanged -= PreferenceChanged;
        processes.Dispose(); sampler.Dispose(); cleanup.Dispose(); updates.Dispose(); mini?.Close(); mini = null;
        if (tray is not null) { var icon = tray.Icon; tray.Visible = false; tray.Dispose(); icon?.Dispose(); tray = null; }
    }
    private static void OpenExternal(string value) => Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });
    public object? FindUiElement(string name) => FindName(name) ?? OverviewPane.FindName(name) ?? HistoryPane.FindName(name) ?? DiagnosticsPane.FindName(name) ?? SettingsPane.FindName(name);
    public void RenderTo(string path, double factor = 1) => Capture(this, path, factor);
    public static void Capture(Window window, string path, double factor = 1)
    {
        window.UpdateLayout();
        // WPF 离屏渲染不包含原生标题栏；按客户区取图，避免把非客户区高度填成底部白边。
        var visual = window.Content as FrameworkElement ?? window;
        var width = visual.ActualWidth; var height = visual.ActualHeight;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * factor), (int)Math.Ceiling(height * factor), 96 * factor, 96 * factor, PixelFormats.Pbgra32);
        if (window.Background is { } background && background != Brushes.Transparent) {
            var backing = new DrawingVisual(); using (var drawing = backing.RenderOpen()) drawing.DrawRectangle(background, null, new Rect(0, 0, width, height)); bitmap.Render(backing);
        }
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
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
