using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using ClickClean;

namespace ClickClean.Diagnostics;
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--architecture-check") {
            RunUpdateCheck(async () => {
                var expectedX86 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X86;
                if (BuildInfo.ExperimentalX86 != expectedX86 || !BuildInfo.PlatformSupported) throw new InvalidOperationException("构建标识或平台门禁不匹配。");
                var memory = new WindowsMemoryApi().Read();
                if (memory.Total == 0 || memory.Available > memory.Total) throw new InvalidDataException("本机只读内存样本无效。");
                if (expectedX86) {
                    var transportCreated = false;
                    using var updates = new UpdateService(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "unused-update-state.json"), () => { transportCreated = true; throw new InvalidOperationException("不能进入x64更新通道"); });
                    await updates.CheckAsync(); await updates.DownloadAsync();
                    if (transportCreated || updates.Ready || !updates.Status.Contains("32位试验版")) throw new InvalidOperationException("32位更新隔离失效。");
                }
                var method = typeof(MiniIsland).GetNestedType("Native", System.Reflection.BindingFlags.NonPublic)!.GetMethod("PrimaryFullscreen", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                method.Invoke(null, new object[] { IntPtr.Zero });
                var native = typeof(MiniIsland).GetNestedType("Native", System.Reflection.BindingFlags.NonPublic)!;
                var foreground = (IntPtr)native.GetMethod("GetForegroundWindow", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null)!;
                native.GetMethod(expectedX86 ? "GetWindowLong" : "GetWindowLongPtr", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { foreground, -16 });
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), OsArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), BuildInfo.ExperimentalX86, BuildInfo.PlatformSupported, ReadOnly = true, NativeEntryResolved = true, StyleReadFromWindow = foreground != IntPtr.Zero, Memory = memory }, new JsonSerializerOptions { WriteIndented = true }));
            }, Path.ChangeExtension(args[1], ".failure.txt")); return;
        }
        if (args.Length == 2 && args[0] == "--profile-runtime") { RuntimeProfile.Run(args[1]); return; }
        if (args.Length == 2 && args[0] == "--profile-pointer") { RuntimeProfile.Run(args[1], true); return; }
        if (args.Length == 2 && args[0] == "--profile-ui") { RuntimeProfile.Run(args[1], true, true); return; }
        if (args.Length == 2 && args[0] == "--read-environment") {
            RunUpdateCheck(async () => {
                using var reader = new AutomationEnvironmentReader(); var rows = new List<AutomationEnvironment>();
                for (var i = 0; i < 3; i++) { rows.Add(reader.Read()); await Task.Delay(1000); }
                var processes = new ProcessMemoryReader().Read();
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { Environment = rows, ProcessCount = processes.Samples.Count, Unavailable = processes.Unavailable, ReadOnly = true }, new JsonSerializerOptions { WriteIndented = true }));
                if (rows.All(row => row.LowMemory is null) || rows.All(row => row.DiskBusy is null) || processes.Samples.Count == 0) throw new InvalidOperationException("真实只读探针未获得有效样本。");
            }, Path.ChangeExtension(args[1], ".failure.txt")); return;
        }
        if (args.Length == 2 && args[0] == "--system-check") { SystemCheck(args[1]); return; }
        if (args.Length == 2 && args[0] == "--memory-read") {
            RunUpdateCheck(() => {
                var api = new WindowsMemoryApi(); var samples = Enumerable.Range(0, 10).Select(_ => api.Read()).ToArray();
                if (samples.Any(value => value.Total == 0 || value.Available > value.Total || value.SystemCache is null || value.KernelPaged is null || value.KernelNonpaged is null))
                    throw new InvalidDataException("只读系统内存样本无效。");
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { ReadOnly = true, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
                return Task.CompletedTask;
            }, Path.ChangeExtension(args[1], ".failure.txt")); return;
        }
        if (args.Length == 2 && args[0] == "--acceptance") { RunUpdateCheck(() => AcceptanceCheck.Run(args[1]), Path.ChangeExtension(args[1], ".failure.txt")); return; }
        if (args.Length == 2 && args[0] == "--startup-probe") { File.WriteAllText(args[1], JsonSerializer.Serialize(new { Admin = WindowsMemoryApi.IsAdmin(), ProcessId = Environment.ProcessId })); return; }
        if (args.Length == 3 && args[0] == "--verify-updates") { RunUpdateCheck(() => UpdateVerification.Check(args[1], args[2]), Path.Combine(args[2], "failure.txt")); return; }
        if (args.Length == 3 && args[0] == "--update-installed") { RunUpdateCheck(() => UpdateVerification.ApplyInstalled(args[1], args[2]), Path.ChangeExtension(args[2], ".failure.txt")); return; }
        if (args.Length == 4 && args[0] == "--apply-update-fixture") { RunUpdateCheck(() => UpdateVerification.ApplyFixture(args[1], args[2], args[3]), args[3]); return; }
        var output = args.Length == 2 && args[0] == "--capture" ? Path.GetFullPath(args[1]) : null;
        var root = output is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickCleanPreview") : Path.Combine(output, "preview-data");
        Directory.CreateDirectory(root);
        var store = new Store(root); var app = new App(preview: true); app.InitializeComponent();
        var openedLinks = new List<Uri>();
        var fakeUpdates = output is null ? null : new FakeApplicationUpdates();
        var fakeMemory = output is null ? null : new TrackingMemoryApi();
        var environment = output is null ? null : new FakeEnvironment(); var processes = output is null ? null : new FakeProcesses();
        var window = new MainWindow(store, verification: true, memoryApi: fakeMemory, projectLinkOpener: output is null ? null : openedLinks.Add, updateOverride: fakeUpdates, environmentOverride: environment, processOverride: processes); app.MainWindow = window;
        window.Loaded += async (_, _) => {
            window.ShowIsland();
            if (output is null) return;
            try { await Capture(window, store, output, openedLinks, fakeUpdates!, fakeMemory!, environment!, processes!); app.Shutdown(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); app.Shutdown(1); }
        };
        app.Run(window);
    }
    private static void RunUpdateCheck(Func<Task> action, string failurePath)
    {
        try { action().GetAwaiter().GetResult(); }
        catch (Exception ex) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(failurePath))!); File.WriteAllText(failurePath, ex.ToString()); Environment.ExitCode = 1; }
    }
    private static Task Capture(MainWindow window, Store store, string output, List<Uri> openedLinks, FakeApplicationUpdates update, TrackingMemoryApi memory, FakeEnvironment environment, FakeProcesses processes)
        => RebuiltUiCheck.Run(window, store, output, openedLinks, update, memory, environment, processes);
    internal static void SystemCheck(string output)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(output))!; Directory.CreateDirectory(directory);
        try
        {
            if (!WindowsMemoryApi.IsAdmin()) throw new InvalidOperationException("实测必须通过 UAC 以管理员运行诊断程序。");
            var initial = WindowsMemoryApi.ProfilePrivilegeEnabled();
            var result = new MemoryEngine(new WindowsMemoryApi()).RunAsync(Enum.GetValues<MemoryCommand>()).GetAwaiter().GetResult();
            var restored = WindowsMemoryApi.ProfilePrivilegeEnabled() == initial;
            File.WriteAllText(output, JsonSerializer.Serialize(new { Result = result, PrivilegeRestored = restored }, new JsonSerializerOptions { WriteIndented = true }));
            if (!result.Success || !restored) Environment.ExitCode = 1;
        }
        catch (Exception ex) { File.WriteAllText(output, ex.ToString()); Environment.ExitCode = 1; }
    }
}
