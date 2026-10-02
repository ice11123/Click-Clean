using System.Security.Principal;

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
        if (Environment.OSVersion.Version.Build < 22000 || !Environment.Is64BitProcess ||
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
        { MessageBox.Show("Click-Clean 仅支持 Windows 11 x64。", "平台不支持"); Shutdown(2); return; }
        if (e.Args.SequenceEqual(new[] { "--remove-startup" }))
        { try { StartupTask.Set(false); Shutdown(); } catch { Shutdown(1); } return; }
        if (e.Args.Any(a => a != "--tray") && !e.Args.SequenceEqual(new[] { "--exit" }))
        { MessageBox.Show("不支持的启动参数。", "Click-Clean"); Shutdown(2); return; }
        if (!WindowsMemoryApi.IsAdmin())
        { MessageBox.Show("请双击 ClickClean.exe 并同意管理员授权。", "Click-Clean"); Shutdown(1); return; }
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
            var store = new Store();
            InstallationMetadata.RefreshVersion(store);
            DispatcherUnhandledException += (_, args) => {
                store.Log(args.Exception.ToString());
                MessageBox.Show(args.Exception.Message, "Click-Clean 运行错误"); args.Handled = true;
            };
            var window = new MainWindow(store, e.Args.Contains("--tray")); MainWindow = window;
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
