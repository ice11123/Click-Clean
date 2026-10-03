using ClickClean;

namespace ClickClean.Diagnostics;

internal sealed class FakeApplicationUpdates : IApplicationUpdates
{
    public bool Busy { get; private set; }
    public bool Ready => Phase == UpdatePhase.Ready;
    public bool Available => Phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Ready;
    public string Status { get; private set; } = "预览：尚未检查";
    public UpdatePhase Phase { get; private set; }
    public int Checks { get; private set; }
    public int Downloads { get; private set; }
    public int Applies { get; private set; }
    public event Action? Changed;
    public void Set(UpdatePhase phase, string message)
    {
        Phase = phase; Status = message; Busy = phase is UpdatePhase.Checking or UpdatePhase.Downloading; Changed?.Invoke();
    }
    public Task CheckAsync() { Checks++; Set(UpdatePhase.Current, "预览：当前已是最新版本"); return Task.CompletedTask; }
    public async Task DownloadAsync()
    {
        Downloads++; Set(UpdatePhase.Downloading, "预览：模拟下载，不访问网络");
        await Task.Delay(30); Set(UpdatePhase.Ready, "预览：更新就绪，等待手动重启");
    }
    public Task ApplyAsync(bool tray) { Applies++; return Task.CompletedTask; }
    public void Dispose() { }
}

internal sealed class TrackingMemoryApi : IMemoryApi
{
    public ulong Available { get; set; } = 12UL << 30;
    public List<MemoryCommand> Calls { get; } = [];
    public int Privileges { get; private set; }
    public MemorySnapshot Read() => new(32UL << 30, Available, 21UL << 30, 48UL << 30);
    public IDisposable EnablePrivilege() { Privileges++; return new Scope(); }
    public int Execute(MemoryCommand command) { Thread.Sleep(350); Calls.Add(command); Available += 256UL << 20; return 0; }
    private sealed class Scope : IDisposable { public void Dispose() { } }
}
