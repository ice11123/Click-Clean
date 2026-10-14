using System.Security.Principal;
using Microsoft.Win32;

namespace ClickClean;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => StartupTask.Set(false)).Run();
        var app = new App(); app.InitializeComponent(); app.Run();
    }
}

public partial class App : Application
{
    private readonly bool preview;
    public App(bool preview = false) => this.preview = preview;
    private Mutex? singleton;
    private Mutex? installerGuard;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? waiter;
    private EventWaitHandle? exitSignal;
    private RegisteredWaitHandle? exitWaiter;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (preview) return;
        if (!BuildInfo.PlatformSupported)
        { MessageBox.Show("Click-Clean 仅支持 Windows 11 x64。", "平台不支持"); Shutdown(2); return; }
        if (e.Args.SequenceEqual(new[] { "--remove-startup" }))
        { if (BuildInfo.ExperimentalX86) { Shutdown(); return; } try { StartupTask.Set(false); Shutdown(); } catch { Shutdown(1); } return; }
        if (e.Args.Any(a => a is not ("--tray" or "--language=zh-CN" or "--language=en-US")) && !e.Args.SequenceEqual(new[] { "--exit" }))
        { MessageBox.Show("不支持的启动参数。", "Click-Clean"); Shutdown(2); return; }
        if (!e.Args.SequenceEqual(new[] { "--exit" }) && WindowsMemoryApi.IsAdmin())
        {
            var english = e.Args.Contains("--language=en-US");
            MessageBox.Show(english ? "Click-Clean now runs without administrator rights. Close the old instance, then launch normally from the Start menu."
                : "新版 Click-Clean 界面不再需要管理员权限。请关闭旧实例，通过开始菜单普通启动。", "Click-Clean");
            Shutdown(1); return;
        }
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var suffix = identity.User!.Value;
            if (e.Args.SequenceEqual(new[] { "--exit" }))
            {
                try { using var signal = EventWaitHandle.OpenExisting(@"Local\ClickClean.Exit." + suffix); signal.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
                Shutdown(); return;
            }
            singleton = new Mutex(true, @"Local\ClickClean.Singleton." + suffix, out var first);
            activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClickClean.Activate." + suffix);
            if (!first) { activation.Set(); Shutdown(); return; }
            // 安装与卸载要求用户先正常退出，不能强制中断正在刷盘的系统调用。
            installerGuard = new Mutex(false, @"Local\ClickClean.InstallationActive");
            var store = BuildInfo.ExperimentalX86
                ? new Store(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickCleanData-x86"))
                : new Store();
            if (!store.Settings.LanguageChosen)
            {
                using var installation = Registry.CurrentUser.OpenSubKey(@"Software\ClickClean");
                var language = e.Args.FirstOrDefault(a => a.StartsWith("--language=", StringComparison.Ordinal))?[11..]
                    ?? installation?.GetValue("InitialLanguage") as string;
                if (language is "zh-CN" or "en-US")
                { store.Settings.Language = language; store.Settings.LanguageChosen = true; store.SaveSettings(); }
            }
            if (BuildInfo.ExperimentalX86) { store.Settings.Startup = false; store.Settings.CheckUpdates = false; }
            var recovery = new UpdateRecovery(store.Root); recovery.BeginLaunch();
            InstallationMetadata.RefreshVersion(store);
            DispatcherUnhandledException += (_, args) => {
                store.Log(args.Exception.ToString());
                MessageBox.Show(args.Exception.Message, "Click-Clean 运行错误"); args.Handled = true;
            };
            var window = new MainWindow(store, e.Args.Contains("--tray")); MainWindow = window;
            Dispatcher.BeginInvoke(() => recovery.MarkHealthy(BuildInfo.Version), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            waiter = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(window.ShowMain), null, Timeout.Infinite, false);
            exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClickClean.Exit." + suffix);
            exitWaiter = ThreadPool.RegisterWaitForSingleObject(exitSignal, (_, _) => Dispatcher.BeginInvoke(window.TryExit), null, Timeout.Infinite, false);
            if (!e.Args.Contains("--tray")) window.Show();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Click-Clean 启动失败"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e)
    { exitWaiter?.Unregister(null); exitSignal?.Dispose(); waiter?.Unregister(null); activation?.Dispose(); installerGuard?.Dispose(); singleton?.Dispose(); base.OnExit(e); }
}
