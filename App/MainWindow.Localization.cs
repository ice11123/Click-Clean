namespace ClickClean;

public partial class MainWindow
{
    private MemorySnapshot? latestMemory;
    private string latestMemoryError = "";
    private void ChooseFirstLanguage()
    {
        if (disposed || preview || store.Settings.LanguageChosen) return;
        var chooser = new LanguageWindow(store.Settings.Language);
        if (IsVisible) { chooser.Owner = this; chooser.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        if (chooser.ShowDialog() != true) return;
        store.Settings.Language = chooser.SelectedLanguage; store.Settings.LanguageChosen = true;
        Localization.Language = chooser.SelectedLanguage;
        SaveSettings(); LoadPreferences();
    }
    private void LanguageChanged()
    {
        if (disposed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(LanguageChanged); return; }
        Title = Localization.Text(preview ? "Click-Clean 即清 · 交互预览" : BuildInfo.ExperimentalX86 ? "Click-Clean 即清 · x86试验版" : "Click-Clean 即清");
        Presentation.RefreshLanguage();
        if (tray?.ContextMenuStrip is { } menu)
            foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
                if (item.Tag is string key) item.Text = Localization.Text(key);
        MemoryChanged(latestMemory, latestMemoryError);
        mini?.RefreshStatus();
    }
    private void PressureChanged()
    {
        if (disposed) return;
        var pressure = sampler.Pressure;
        Presentation.PressureLow = pressure?.LowMemory == true;
        Presentation.PressureKnown = pressure?.LowMemory is not null && pressure.HighMemory is not null && !(pressure.LowMemory == true && pressure.HighMemory == true);
        if (!Presentation.PressureKnown)
        {
            Presentation.PressureTitle = "状态暂不可用";
            Presentation.PressureHint = "系统压力信号暂不可用，仍可手动整理。";
        }
        else if (pressure!.LowMemory == true)
        {
            Presentation.PressureTitle = "内存压力";
            Presentation.PressureHint = "Windows已报告低内存；手动点击执行全系统三步。";
        }
        else if (pressure.HighMemory == true)
        {
            Presentation.PressureTitle = "内存充裕";
            Presentation.PressureHint = "Windows报告内存充裕；手动整理仍可执行。";
        }
        else
        {
            Presentation.PressureTitle = "暂无低内存";
            Presentation.PressureHint = "Windows未报告低内存；手动整理仍可执行。";
        }
    }
}
