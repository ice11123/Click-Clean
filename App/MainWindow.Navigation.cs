using System.Windows.Input;
using System.Windows.Threading;

namespace ClickClean;

public partial class MainWindow
{
    private readonly Dictionary<string, (IInputElement? Focus, double Offset)> navigationState = new();
    public bool Navigate(string page)
    {
        if (disposed || page is not ("overview" or "history" or "diagnostics" or "settings") || !CheckDraft()) return false;
        ClosePopup();
        navigationState[Presentation.Page] = (Keyboard.FocusedElement, PageScroll(Presentation.Page)?.VerticalOffset ?? 0);
        Presentation.Page = page;
        processes.SetActive(page == "diagnostics", IsVisible && WindowState != WindowState.Minimized);
        if (page == "history") RefreshHistory();
        var target = page switch { "history" => (FrameworkElement)HistoryPane, "diagnostics" => DiagnosticsPane, "settings" => SettingsPane, _ => OverviewPane };
        Dispatcher.BeginInvoke(() => {
            if (disposed || Presentation.Page != page) return;
            if (navigationState.TryGetValue(page, out var saved)) {
                if (saved.Focus is UIElement element && element.IsVisible && element.IsEnabled) element.Focus();
                else target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                PageScroll(page)?.ScrollToVerticalOffset(saved.Offset);
            }
            else target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }, DispatcherPriority.Loaded);
        return true;
    }
    private ScrollViewer? PageScroll(string page) => page switch { "overview" => OverviewPane.MainContent, "history" => HistoryPane.RecordsScroll, "settings" => SettingsPane.SettingsScroll, _ => null };
    public void OpenSettings(bool animate = false)
    {
        if (!Navigate("settings")) return;
        settingsReturn = "index"; Presentation.Settings.Section = "index"; LoadPreferences(); SettingsPane.SettingsScroll.ScrollToTop();
    }
    public void OpenHistory(bool animate = false) => Navigate("history");
    public void ShowSettingsSection(string section) { if (section == "diagnostics") Navigate("diagnostics"); else OpenSettingsSection(section, "index"); }
    private void OpenSettingsSection(string section, string source)
    {
        if (section is not ("appearance" or "automation" or "updates" or "about")) return;
        if (Presentation.Settings.Section != section && !CheckDraft()) return;
        if (Presentation.Page != "settings" && !Navigate("settings")) return;
        settingsReturn = source; Presentation.Settings.Section = section;
        LoadPreferences(); SettingsPane.SettingsScroll.ScrollToTop();
        Dispatcher.BeginInvoke(() => { if (!disposed) SettingsPane.SettingsBack.Focus(); }, DispatcherPriority.Loaded);
    }
    private bool CheckDraft()
    {
        var draft = Presentation.Settings.Draft;
        if (!draft.Dirty) return true;
        var decision = DraftDecisionOverride?.Invoke() ?? (preview ? MessageBoxResult.Cancel :
            MessageBox.Show(this, "自动整理规则尚未保存。\n是：保存并继续；否：放弃修改；取消：继续编辑。", "未保存的规则", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel));
        if (decision == MessageBoxResult.Yes) return SaveAutomation();
        if (decision == MessageBoxResult.No) { draft.Load(store.Settings.Automation); return true; }
        return false;
    }
    private void OpenPopup(string kind, FrameworkElement? target)
    {
        ClosePopup();
        Presentation.PopupKind = kind; popupReturnFocus = Keyboard.FocusedElement; popupPage = Presentation.Page;
        DetailsPopup.PlacementTarget = target ?? (Presentation.Diagnostics ? DiagnosticsPane : OverviewPane);
        PopupFrame.Width = Math.Max(220, Math.Min(460, LayoutRoot.ActualWidth));
        PopupScroll.MaxHeight = Math.Max(80, Math.Min(380, SystemParameters.WorkArea.Height - 160));
        DetailsPopup.IsOpen = true;
    }
    private void ClosePopup() => DetailsPopup.IsOpen = false;
    public void CloseOverlay() { ClosePopup(); Navigate("overview"); }
}
