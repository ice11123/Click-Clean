using ClickClean.Core;

var passed = 0;
async Task Check(string name, Func<Task> test)
{
    await test(); passed++; Console.WriteLine("通过：" + name);
}
void Assert(bool condition) { if (!condition) throw new Exception("断言失败"); }
var all = Enum.GetValues<MemoryCommand>();
await Check("手动快捷入口低占用也恢复原版三步", () => {
    Assert(RoutineCleanupPolicy.Evaluate(new(1000, 900, 200, 2000)).Commands.SequenceEqual(all));
    return Task.CompletedTask;
});
await Check("默认自动阈值55且可合法保存，自动开关仍关闭", () => {
    var defaults = new AutomationSettings();
    Assert(defaults.ThresholdPercent == 55 && defaults.Validated().ThresholdPercent == 55 && !defaults.ThresholdEnabled && !defaults.TimerEnabled);
    return Task.CompletedTask;
});
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
    Assert(result.Success && result.ObservationError!.Contains("读取整理后内存失败") && result.Steps.Count == 3 && result.After is null && result.AvailableChange is null);
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
    var clock = new FakeTime(); var policy = new AutomationPolicy(clock); policy.Configure(new() { ThresholdEnabled = true, ThresholdPercent = 85 });
    Assert(policy.Evaluate(90, false) is null); clock.Advance(45); Assert(policy.Evaluate(90, false) is null);
    Assert(policy.Evaluate(84, false) is null); clock.Advance(10); Assert(policy.Evaluate(90, false) is null);
    clock.Advance(59); Assert(policy.Evaluate(90, false) is null); clock.Advance(1); Assert(policy.Evaluate(90, false) == "阈值");
    return Task.CompletedTask;
});
await Check("冷却与迟滞：持续高占用不反复触发", () => {
    var clock = new FakeTime(); var policy = new AutomationPolicy(clock); policy.Configure(new() { ThresholdEnabled = true, ThresholdPercent = 85, CooldownMinutes = 5 });
    policy.Evaluate(90, false); clock.Advance(60); Assert(policy.Evaluate(90, false) == "阈值"); policy.RecordCompletion(true, true);
    for (var i = 0; i < 10; i++) { clock.Advance(60); Assert(policy.Evaluate(90, false) is null); }
    policy.Evaluate(80, false); policy.Evaluate(90, false); clock.Advance(60); Assert(policy.Evaluate(90, false) == "阈值");
    return Task.CompletedTask;
});
await Check("定时与阈值相撞合并，忙碌时跳过不积压", () => {
    var clock = new FakeTime(); var policy = new AutomationPolicy(clock); policy.Configure(new() { ThresholdEnabled = true, ThresholdPercent = 85, TimerEnabled = true, IntervalMinutes = 15 });
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
        var store = new Store(root); Assert(store.Settings.Theme == "system" && store.Settings.Automation.ThresholdPercent == 55 && store.Settings.Automation.IntervalMinutes == 15);
        Assert(store.Settings.SelectedSteps.Length == 0 && store.History.Count == 1 && store.History[0].AvailableChange is null);
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
    legacy.Validate(); Assert(legacy.SchemaVersion == 4 && legacy.MiniIsland && legacy.Startup);
    legacy.MiniIsland = false; legacy.Validate(); Assert(!legacy.MiniIsland);
    var restored = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(System.Text.Json.JsonSerializer.Serialize(legacy))!;
    restored.Validate(); Assert(!restored.MiniIsland && restored.SchemaVersion == 4);
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
await Check("作者链接准确分流，不接受未知键或不安全协议", () => {
    const string repository = "https://github.com/ice11123/Click-Clean";
    Assert(ProjectLinks.Resolve("repository", repository)?.AbsoluteUri == repository);
    Assert(ProjectLinks.Resolve("blog", repository)?.AbsoluteUri == "https://ice11123.github.io/blog_test2/");
    Assert(ProjectLinks.Resolve("author", repository)?.AbsoluteUri == "https://github.com/ice11123");
    Assert(ProjectLinks.Author == "离子怪" && ProjectLinks.Resolve("unknown", repository) is null);
    foreach (var value in new[] { "", "not-a-url", "file:///C:/Windows", "http://github.com/ice11123/Click-Clean", "https://user:password@example.com" })
        Assert(ProjectLinks.Resolve("repository", value) is null);
    return Task.CompletedTask;
});
await Check("默认与旧设置都只手动下载应用更新，空高级选择不回填三步", () => {
    var settings = new UserSettings { SchemaVersion = 2, AutoDownloadUpdates = true, AutoApplyHidden = true, MiniIsland = false, SelectedSteps = [] };
    settings.Validate(); Assert(settings.SchemaVersion == 4 && !settings.AutoDownloadUpdates && !settings.AutoApplyHidden && settings.CheckUpdates && !settings.MiniIsland && settings.SelectedSteps.Length == 0);
    settings.AutoDownloadUpdates = settings.AutoApplyHidden = true; settings.Validate(); Assert(!settings.AutoDownloadUpdates && !settings.AutoApplyHidden);
    var old = new UserSettings { SchemaVersion = 2, SelectedSteps = all }; old.Validate(); Assert(old.SelectedSteps.SequenceEqual(all));
    return Task.CompletedTask;
});
await Check("更新提示覆盖可用、下载、就绪、失败，整理期间不能重启", () => {
    Assert(!UpdatePresentation.Create(UpdatePhase.Current, false, false, false, false).Visible);
    var found = UpdatePresentation.Create(UpdatePhase.Available, true, false, false, false); Assert(found.Visible && found.Enabled && found.Action == UpdateAction.Download);
    var downloading = UpdatePresentation.Create(UpdatePhase.Downloading, true, false, true, false); Assert(downloading.Visible && !downloading.Enabled);
    var ready = UpdatePresentation.Create(UpdatePhase.Ready, true, true, false, false); Assert(ready.Action == UpdateAction.Apply && ready.Enabled);
    Assert(!UpdatePresentation.Create(UpdatePhase.Ready, true, true, false, true).Enabled);
    var failed = UpdatePresentation.Create(UpdatePhase.Failed, false, false, false, false); Assert(failed.Visible && failed.Action == UpdateAction.Check);
    return Task.CompletedTask;
});
await Check("自动55边界选择原版三步，未知读数仍安全跳过", () => {
    foreach (var snapshot in new MemorySnapshot?[] { null, new(0, 0, 0, 0), new(1000, 1001, 0, 0) })
        Assert(RoutineCleanupPolicy.Evaluate(snapshot).Skipped);
    Assert(RoutineCleanupPolicy.Evaluate(new(1000, 451, 200, 2000), automatic: true).Skipped);
    Assert(RoutineCleanupPolicy.Evaluate(new(1000, 450, 200, 2000), automatic: true).Commands.SequenceEqual(all));
    Assert(RoutineCleanupPolicy.Evaluate(new(1000, 150, 200, 2000), automatic: true).Commands.SequenceEqual(all));
    Assert(RoutineCleanupPolicy.Evaluate(new(1000, 900, 200, 2000), automatic: false).Commands.SequenceEqual(all));
    foreach (var invalid in new[] { 54, 99 }) {
        try { RoutineCleanupPolicy.Evaluate(new(1000, 450, 200, 2000), invalid, true); throw new Exception("非法门槛未拒绝"); }
        catch (ArgumentOutOfRangeException) { }
    }
    return Task.CompletedTask;
});
await Check("55阈值持续60秒且需回落50才能重新武装", () => {
    var clock = new FakeTime(); var policy = new AutomationPolicy(clock); policy.Configure(new() { ThresholdEnabled = true, CooldownMinutes = 5 });
    Assert(policy.Evaluate(54.99, false) is null); Assert(policy.Evaluate(55, false) is null);
    clock.Advance(59); Assert(policy.Evaluate(55, false) is null); clock.Advance(1); Assert(policy.Evaluate(55, false) == "阈值");
    policy.RecordCompletion(true, true);
    for (var i = 0; i < 6; i++) { clock.Advance(60); Assert(policy.Evaluate(55, false) is null); }
    Assert(policy.Evaluate(51, false) is null); Assert(policy.Evaluate(55, false) is null); clock.Advance(60); Assert(policy.Evaluate(55, false) is null);
    Assert(policy.Evaluate(50, false) is null); Assert(policy.Evaluate(55, false) is null); clock.Advance(60); Assert(policy.Evaluate(55, false) == "阈值");
    return Task.CompletedTask;
});
await Check("旧自动开关保留但需确认，旧阈值和手动选择不覆盖", () => {
    foreach (var originalFull in new[] { false, true }) {
        var old = new UserSettings { SchemaVersion = 3, SelectedSteps = [], Automation = new() { ThresholdEnabled = true, ThresholdPercent = 85, FullCleanup = originalFull } };
        old.Validate(); Assert(old.SchemaVersion == 4 && old.AutomationNeedsConfirmation && old.Automation.ThresholdEnabled && old.Automation.ThresholdPercent == 85 && old.SelectedSteps.Length == 0 && old.Automation.FullCleanup);
        old.AutomationNeedsConfirmation = false; old.Validate(); Assert(!old.AutomationNeedsConfirmation);
    }
    var defaults = new UserSettings(); defaults.Validate(); Assert(!defaults.AutomationNeedsConfirmation && defaults.SelectedSteps.SequenceEqual(all));
    var disabled = new UserSettings { SchemaVersion = 3 }; disabled.Validate(); Assert(!disabled.AutomationNeedsConfirmation);
    return Task.CompletedTask;
});
await Check("无schema的旧自动设置也必须确认，确认状态持久保存", () => {
    var root = Path.Combine(Path.GetTempPath(), "ClickClean-schema4-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    try {
        File.WriteAllText(Path.Combine(root, "settings.json"), "{\"Automation\":{\"TimerEnabled\":true,\"ThresholdPercent\":72},\"SelectedSteps\":[]}");
        var migrated = new Store(root); Assert(migrated.Settings.AutomationNeedsConfirmation && migrated.Settings.Automation.TimerEnabled && migrated.Settings.Automation.ThresholdPercent == 72);
        migrated.SaveSettings(); Assert(new Store(root).Settings.AutomationNeedsConfirmation);
        migrated.Settings.AutomationNeedsConfirmation = false; migrated.SaveSettings(); Assert(!new Store(root).Settings.AutomationNeedsConfirmation);
    } finally { Directory.Delete(root, true); }
    return Task.CompletedTask;
});
await Check("真实复测保留即时、负值和实际间隔，权限在观测前恢复", async () => {
    var api = new FakeApi { ReadValue = read => read switch { 1 => 100, 2 => 120, _ => 90 } };
    var result = await new MemoryEngine(api).RunAsync(all, followUpDelay: TimeSpan.FromMilliseconds(20), observing: () => Assert(api.Restored));
    Assert(result.Success && result.AvailableChange == 20 && result.FollowUpChange == -10 && result.FollowUpSeconds >= 0.015 && result.CommitChange == 0);
    Assert(result.ObservationError is null);
});
await Check("复测失败不改写已成功调用，即时值不可用时仍可复测", async () => {
    var failed = await new MemoryEngine(new FakeApi { ReadFailure = 3 }).RunAsync(all, followUpDelay: TimeSpan.Zero);
    Assert(failed.Success && failed.AvailableChange == 20 && failed.FollowUp is null && failed.FollowUpSeconds is null && failed.ObservationError!.Contains("复测不可用"));
    var instantFailed = await new MemoryEngine(new FakeApi { ReadFailure = 2 }).RunAsync(all, followUpDelay: TimeSpan.Zero);
    Assert(instantFailed.Success && instantFailed.AvailableChange is null && instantFailed.FollowUpChange == 20 && instantFailed.ObservationError!.Contains("读取整理后"));
});
await Check("取消复测不撤销成功调用，观测期间保持并发保护", async () => {
    using var cancel = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var api = new FakeApi(); var engine = new MemoryEngine(api);
    var task = engine.RunAsync(all, cancellation: cancel.Token, followUpDelay: TimeSpan.FromSeconds(5), observing: () => entered.SetResult());
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert(engine.IsRunning && api.Restored);
    try { await engine.RunAsync(all); throw new Exception("观察期间允许并发"); } catch (InvalidOperationException) { }
    cancel.Cancel(); var result = await task;
    Assert(result.Success && !result.Cancelled && result.FollowUp is null && result.ObservationError!.Contains("已取消复测") && !engine.IsRunning);
});
await Check("跳过自动周期不计失败、不标记实际调用，旧全量配置仍可识别", () => {
    var clock = new FakeTime(); var policy = new AutomationPolicy(clock); policy.Configure(new() { TimerEnabled = true, IntervalMinutes = 15 });
    policy.RecordCompletion(false, true); policy.RecordSkipped(); Assert(policy.ConsecutiveFailures == 1);
    clock.Advance(60); Assert(policy.Evaluate(90, false) is null);
    var previous = new UserSettings { Automation = new() { FullCleanup = true, ThresholdEnabled = true } }; previous.Validate(); Assert(previous.Automation.FullCleanup);
    return Task.CompletedTask;
});
await Check("新观测历史往返兼容旧记录，拒绝坏复测间隔", () => {
    var before = new MemorySnapshot(1000, 100, 500, 2000);
    var result = new CleanupResult(DateTimeOffset.Now, before, before with { Available = 120 }, [new(MemoryCommand.StandbyCache, 0, 0)], false, null, 0) {
        FollowUp = before with { Available = 90 }, FollowUpSeconds = 3.1, ObservationError = "观测提示"
    };
    var restored = System.Text.Json.JsonSerializer.Deserialize<CleanupResult>(System.Text.Json.JsonSerializer.Serialize(result))!;
    Assert(Store.ValidResult(restored) && restored.FollowUpChange == -10 && restored.AvailableChange == 20 && restored.FollowUpSeconds == 3.1);
    Assert(!Store.ValidResult(result with { FollowUpSeconds = double.NaN }) && !Store.ValidResult(result with { FollowUpSeconds = null }));
    Assert(Store.ValidResult(result with { FollowUp = null, FollowUpSeconds = null }));
    return Task.CompletedTask;
});
Console.WriteLine($"全部通过：{passed}组测试（含7种组合及3种失败位置）。");

sealed class FakeApi : IMemoryApi
{
    public List<MemoryCommand> Calls { get; } = new();
    public Func<MemoryCommand, int> ExecuteFunc { get; set; } = _ => 0;
    public ulong AfterAvailable { get; init; } = 120;
    public Func<int, ulong>? ReadValue { get; init; }
    public int ReadFailure { get; init; }
    public bool Restored { get; private set; }
    private int reads;
    public MemorySnapshot Read() {
        reads++; if (reads == ReadFailure) throw new IOException("模拟快照异常");
        return new(1000, ReadValue?.Invoke(reads) ?? (reads == 1 ? 100 : AfterAvailable), 500, 2000);
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
