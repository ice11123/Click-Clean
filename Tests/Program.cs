using ClickClean.Core;

var passed = 0;
async Task Check(string name, Func<Task> test)
{
    await test(); passed++; Console.WriteLine("通过：" + name);
}
void Assert(bool condition) { if (!condition) throw new Exception("断言失败"); }
var all = Enum.GetValues<MemoryCommand>();
await Check("顺序固定、重复项目去重、权限恢复", async () => {
    var api = new FakeApi(); var result = await new MemoryEngine(api).RunAsync(all.Reverse().Concat(all));
    Assert(api.Calls.SequenceEqual(all) && result.Success && api.Restored);
});
await Check("全部七种自定义组合", async () => {
    for (var mask = 1; mask < 8; mask++) {
        var selected = all.Where((_, i) => (mask & (1 << i)) != 0).ToArray();
        var api = new FakeApi(); var result = await new MemoryEngine(api).RunAsync(selected.Reverse());
        Assert(result.Success && api.Calls.SequenceEqual(selected));
    }
});
await Check("每一步失败即停止，保留已执行步骤与错误码", async () => {
    foreach (var failed in all) {
        var api = new FakeApi { ExecuteFunc = c => c == failed ? unchecked((int)0xC0000061) : 0 };
        var result = await new MemoryEngine(api).RunAsync(all);
        Assert(!result.Success && result.Steps.Last().StatusHex == "0xC0000061" && api.Restored && api.Calls.Last() == failed);
    }
});
await Check("开始前取消不执行系统调用", async () => {
    var token = new CancellationToken(true); var api = new FakeApi();
    var result = await new MemoryEngine(api).RunAsync(all, cancellation: token);
    Assert(result.Cancelled && api.Calls.Count == 0 && api.Restored);
});
await Check("当前调用不中断、取消仅阻止后续步骤", async () => {
    using var token = new CancellationTokenSource();
    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
    var api = new FakeApi { ExecuteFunc = _ => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return 0; } };
    var engine = new MemoryEngine(api); var task = engine.RunAsync(all, cancellation: token.Token);
    Assert(entered.Wait(TimeSpan.FromSeconds(5))); token.Cancel();
    await Task.Delay(100); Assert(!task.IsCompleted && engine.IsRunning);
    release.Set(); var result = await task; Assert(result.Cancelled && result.Steps.Count == 1 && !engine.IsRunning);
});
await Check("重复点击拒绝并发调用", async () => {
    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
    var api = new FakeApi { ExecuteFunc = _ => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return 0; } };
    var engine = new MemoryEngine(api); var first = engine.RunAsync(new[] { all[0] });
    Assert(entered.Wait(TimeSpan.FromSeconds(5)));
    try { await engine.RunAsync(all); throw new Exception("未阻止并发"); } catch (InvalidOperationException) { }
    release.Set(); await first; Assert(api.Calls.Count == 1);
});
await Check("零变化及负变化不伪造释放量", async () => {
    foreach (var available in new ulong[] { 100, 90 }) {
        var api = new FakeApi { AfterAvailable = available };
        var result = await new MemoryEngine(api).RunAsync(all);
        Assert(result.Success && result.AvailableChange == (long)available - 100);
    }
});
await Check("异常路径恢复权限、解除运行锁", async () => {
    var api = new FakeApi { ExecuteFunc = _ => throw new IOException("模拟系统异常") };
    var engine = new MemoryEngine(api); var result = await engine.RunAsync(all);
    Assert(result.Error == "模拟系统异常" && api.Restored && !engine.IsRunning);
    api.ExecuteFunc = _ => 0; Assert((await engine.RunAsync(all)).Success);
});
await Check("前后快照读取异常保持准确反馈", async () => {
    var api = new FakeApi { ReadFailure = 1 }; var engine = new MemoryEngine(api);
    try { await engine.RunAsync(all); throw new Exception("未报告快照失败"); } catch (IOException) { }
    Assert(!engine.IsRunning && api.Calls.Count == 0);
    api = new FakeApi { ReadFailure = 2 }; var result = await new MemoryEngine(api).RunAsync(all);
    Assert(!result.Success && result.Error!.Contains("读取整理后内存失败") && result.Steps.Count == 3 && result.After is null && result.AvailableChange is null);
});
await Check("非法步骤及空选择被拒绝", async () => {
    foreach (var selected in new[] { Array.Empty<MemoryCommand>(), new[] { (MemoryCommand)99 } }) {
        try { await new MemoryEngine(new FakeApi()).RunAsync(selected); throw new Exception("未拒绝无效步骤"); } catch (ArgumentException) { }
    }
});
await Check("默认关闭：高占用及多个周期均不自动触发", () => {
    var clock = new FakeTime(); var policy = new AutomationPolicy(clock); policy.Configure(new());
    for (var i = 0; i < 180; i++) { Assert(policy.Evaluate(99, false) is null); clock.Advance(60); }
    return Task.CompletedTask;
});
await Check("阈值必须持续超限，瞬时波动重新计时", () => {
    var clock = new FakeTime(); var policy = new AutomationPolicy(clock); policy.Configure(new() { ThresholdEnabled = true });
    Assert(policy.Evaluate(90, false) is null); clock.Advance(45); Assert(policy.Evaluate(90, false) is null);
    Assert(policy.Evaluate(84, false) is null); clock.Advance(10); Assert(policy.Evaluate(90, false) is null);
    clock.Advance(59); Assert(policy.Evaluate(90, false) is null); clock.Advance(1); Assert(policy.Evaluate(90, false) == "阈值");
    return Task.CompletedTask;
});
await Check("冷却与迟滞：持续高占用不反复触发", () => {
    var clock = new FakeTime(); var policy = new AutomationPolicy(clock); policy.Configure(new() { ThresholdEnabled = true, CooldownMinutes = 5 });
    policy.Evaluate(90, false); clock.Advance(60); Assert(policy.Evaluate(90, false) == "阈值"); policy.RecordCompletion(true, true);
    for (var i = 0; i < 10; i++) { clock.Advance(60); Assert(policy.Evaluate(90, false) is null); }
    policy.Evaluate(80, false); policy.Evaluate(90, false); clock.Advance(60); Assert(policy.Evaluate(90, false) == "阈值");
    return Task.CompletedTask;
});
await Check("定时与阈值相撞合并，忙碌时跳过不积压", () => {
    var clock = new FakeTime(); var policy = new AutomationPolicy(clock); policy.Configure(new() { ThresholdEnabled = true, TimerEnabled = true, IntervalMinutes = 15 });
    for (var i = 0; i < 14; i++) { policy.Evaluate(60, false); clock.Advance(60); }
    policy.Evaluate(90, false); clock.Advance(60); Assert(policy.Evaluate(90, false) == "阈值 + 定时");
    Assert(policy.Evaluate(90, false) is null);
    clock = new(); policy = new(clock); policy.Configure(new() { TimerEnabled = true, IntervalMinutes = 15 });
    for (var i = 0; i < 16; i++) { Assert(policy.Evaluate(60, true) is null); clock.Advance(60); }
    Assert(policy.Evaluate(60, false) is null);
    return Task.CompletedTask;
});
await Check("休眠和墙上时钟调整不产生补跑", () => {
    var clock = new FakeTime(); var policy = new AutomationPolicy(clock); policy.Configure(new() { TimerEnabled = true, IntervalMinutes = 15, ThresholdEnabled = true });
    policy.Evaluate(90, false); clock.Advance(3600); Assert(policy.Evaluate(90, false) is null);
    clock.Utc = clock.Utc.AddDays(-10); clock.Advance(30); Assert(policy.Evaluate(90, false) is null);
    clock.Advance(30); Assert(policy.Evaluate(90, false) == "阈值");
    return Task.CompletedTask;
});
await Check("手动清理进入冷却，坏采样不自动触发", () => {
    var clock = new FakeTime(); var policy = new AutomationPolicy(clock); policy.Configure(new() { ThresholdEnabled = true, CooldownMinutes = 5 });
    policy.RecordCompletion(true, false); policy.Evaluate(90, false); clock.Advance(60); Assert(policy.Evaluate(90, false) is null);
    Assert(policy.Evaluate(double.NaN, false) is null); Assert(policy.Evaluate(null, false) is null);
    clock.Advance(60); Assert(policy.Evaluate(90, false) is null);
    return Task.CompletedTask;
});
await Check("连续自动失败暂停，设置恢复及重启冷却", () => {
    var clock = new FakeTime(); var policy = new AutomationPolicy(clock); policy.Configure(new() { ThresholdEnabled = true });
    policy.RecordCompletion(false, true); policy.RecordCompletion(false, true); policy.RecordCompletion(false, true); Assert(policy.Paused);
    policy.Configure(new() { ThresholdEnabled = true }, recentCleanup: true); Assert(!policy.Paused);
    policy.Evaluate(90, false); clock.Advance(60); Assert(policy.Evaluate(90, false) is null);
    return Task.CompletedTask;
});
await Check("设置与历史防御：空字段、越界与部分坏记录", () => {
    var root = Path.Combine(Path.GetTempPath(), "ClickCleanTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    try {
        File.WriteAllText(Path.Combine(root, "settings.json"), "{\"Theme\":null,\"Automation\":{\"ThresholdPercent\":-5,\"IntervalMinutes\":0},\"SelectedSteps\":null}");
        var good = new CleanupResult(DateTimeOffset.Now, new(1000, 100, 500, 2000), null, [new(MemoryCommand.StandbyCache, 0, 0)], false, "快照失败", 0);
        File.WriteAllText(Path.Combine(root, "history.json"), "[null,{\"Steps\":null},\"错误类型\"," + System.Text.Json.JsonSerializer.Serialize(good) + "]");
        var store = new Store(root); Assert(store.Settings.Theme == "system" && store.Settings.Automation.ThresholdPercent == 60 && store.Settings.Automation.IntervalMinutes == 15);
        Assert(store.Settings.SelectedSteps.Length == 3 && store.History.Count == 1 && store.History[0].AvailableChange is null);
        store.SaveSettings(); Assert(new Store(root).Settings.Theme == "system");
    } finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
});
await Check("历史上限及原子保存往返", () => {
    var root = Path.Combine(Path.GetTempPath(), "ClickCleanTests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    try {
        var store = new Store(root); var snapshot = new MemorySnapshot(1000, 100, 500, 2000);
        for (var i = 0; i < 35; i++) store.Add(new(DateTimeOffset.Now, snapshot, snapshot, [new(MemoryCommand.StandbyCache, 0, 0)], false, null, 0));
        Assert(store.History.Count == 30 && new Store(root).History.Count == 30); store.Clear(); Assert(new Store(root).History.Count == 0);
    } finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
});
await Check("底部岛靠近浮出、离开延迟收回和边界迟滞", () => {
    var policy = new DockRevealPolicy();
    Assert(!policy.Evaluate(false, false, false, 0));
    Assert(policy.Evaluate(true, false, false, 1));
    Assert(policy.Evaluate(false, true, false, 1.1));
    Assert(policy.Evaluate(false, false, false, 1.2));
    Assert(!policy.Evaluate(false, false, false, 1.4));
    Assert(!policy.Evaluate(false, true, false, 1.5));
    return Task.CompletedTask;
});
await Check("底部岛全屏抑制、复位及无效时钟防御", () => {
    var policy = new DockRevealPolicy();
    Assert(!policy.Evaluate(true, true, true, 0));
    Assert(policy.Evaluate(true, false, false, 1));
    Assert(!policy.Evaluate(false, false, false, 0.5));
    Assert(policy.Evaluate(true, false, false, 2));
    Assert(!policy.Evaluate(true, true, false, double.NaN));
    policy.Reset(); Assert(!policy.Revealed);
    return Task.CompletedTask;
});
await Check("旧岛设置升级、新版关闭选择持久化", () => {
    var legacy = new UserSettings { SchemaVersion = 1, MiniIsland = false, Startup = true };
    legacy.Validate(); Assert(legacy.SchemaVersion == 2 && legacy.MiniIsland && legacy.Startup);
    legacy.MiniIsland = false; legacy.Validate(); Assert(!legacy.MiniIsland);
    var restored = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(legacy))!;
    restored.Validate(); Assert(!restored.MiniIsland && restored.SchemaVersion == 2);
    return Task.CompletedTask;
});
await Check("底部岛三阶段、严格两秒与采样更新不重置期限", () => {
    var clock = new FakeTime(); var status = new DockStatus(clock);
    status.SetMemory(new(1000, 370, 500, 2000)); Assert(status.Text == "内存 63%");
    status.SetOperation("2/3 刷新修改页"); clock.Advance(10); Assert(status.Text == "2/3 刷新修改页");
    status.SetResult("完成 · +0.00 GiB"); Assert(status.ResultRemaining.TotalSeconds == 2);
    clock.Advance(1); status.SetMemory(new(1000, 500, 500, 2000)); Assert(status.Text == "完成 · +0.00 GiB");
    clock.Utc = clock.Utc.AddDays(-3); Assert(status.ResultRemaining.TotalSeconds == 1);
    clock.Advance(1); Assert(status.Text == "内存 50%" && status.ResultRemaining == TimeSpan.Zero);
    status.SetResult("部分完成 · −1.00 GiB"); Assert(status.Text.Contains("−1.00"));
    status.SetOperation("准备整理…"); Assert(status.ResultRemaining == TimeSpan.Zero);
    status.SetIdle(); Assert(status.Text == "内存 50%");
    return Task.CompletedTask;
});
await Check("灯色分档边界与未知、异常快照防御", () => {
    Assert(DockStatus.Band(0) == MemoryPressureBand.Low && DockStatus.Band(59.99) == MemoryPressureBand.Low);
    Assert(DockStatus.Band(60) == MemoryPressureBand.Moderate && DockStatus.Band(84.99) == MemoryPressureBand.Moderate);
    Assert(DockStatus.Band(85) == MemoryPressureBand.High && DockStatus.Band(100) == MemoryPressureBand.High);
    foreach (var load in new double?[] { null, double.NaN, double.PositiveInfinity, -1, 101 }) Assert(DockStatus.Band(load) == MemoryPressureBand.Unknown);
    var status = new DockStatus();
    foreach (var snapshot in new MemorySnapshot?[] { null, new(0, 0, 0, 0), new(100, 101, 0, 0) }) {
        status.SetMemory(snapshot); Assert(status.Text == "内存 —" && status.Pressure == MemoryPressureBand.Unknown);
    }
    return Task.CompletedTask;
});
Console.WriteLine($"全部通过：{passed}组测试（含7种组合及3种失败位置）。");

sealed class FakeApi : IMemoryApi
{
    public List<MemoryCommand> Calls { get; } = new();
    public Func<MemoryCommand, int> ExecuteFunc { get; set; } = _ => 0;
    public ulong AfterAvailable { get; init; } = 120;
    public int ReadFailure { get; init; }
    public bool Restored { get; private set; }
    private int reads;
    public MemorySnapshot Read() {
        reads++; if (reads == ReadFailure) throw new IOException("模拟快照异常");
        return new(1000, reads == 1 ? 100 : AfterAvailable, 500, 2000);
    }
    public IDisposable EnablePrivilege() => new Scope(() => Restored = true);
    public int Execute(MemoryCommand command) { Calls.Add(command); return ExecuteFunc(command); }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }
}

sealed class FakeTime : TimeProvider
{
    private long ticks;
    public DateTimeOffset Utc { get; set; } = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public override DateTimeOffset GetUtcNow() => Utc;
    public void Advance(int seconds) { ticks += seconds * TimeSpan.TicksPerSecond; Utc = Utc.AddSeconds(seconds); }
}
