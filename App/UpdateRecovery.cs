using System.Text.Json;

namespace ClickClean;

/// <summary>记录更新启动是否完成，并保留本地配置快照供人工恢复。</summary>
public sealed class UpdateRecovery(string root)
{
    private string Marker => Path.Combine(root, "update-launch.json");
    private sealed record LaunchState(string PreviousVersion, DateTimeOffset StartedUtc, int LaunchAttempts = 0);
    public bool HasIncompleteUpdate {
        get {
            try { return File.Exists(Marker) && Read()?.LaunchAttempts > 1; }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return true; }
        }
    }
    private LaunchState? Read() {
        if (new FileInfo(Marker).Length > 16384) throw new IOException("启动恢复记录过大。");
        return JsonSerializer.Deserialize<LaunchState>(File.ReadAllText(Marker));
    }
    public void Prepare(string previousVersion)
    {
        var backup = Path.Combine(root, "before-update"); Directory.CreateDirectory(backup);
        foreach (var name in new[] { "settings.json", "history.json" }) {
            var source = Path.Combine(root, name);
            if (File.Exists(source)) File.Copy(source, Path.Combine(backup, name), true);
        }
        File.WriteAllText(Marker, JsonSerializer.Serialize(new LaunchState(previousVersion, DateTimeOffset.UtcNow)));
    }
    public void BeginLaunch()
    {
        try { if (File.Exists(Marker) && Read() is { } state) File.WriteAllText(Marker, JsonSerializer.Serialize(state with { LaunchAttempts = state.LaunchAttempts + 1 })); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public void MarkHealthy(string version)
    {
        try {
            if (!File.Exists(Marker)) return;
            File.WriteAllText(Path.Combine(root, "last-healthy-version.json"), JsonSerializer.Serialize(new { Version = version, TimeUtc = DateTimeOffset.UtcNow }));
            File.Delete(Marker);
        }
        catch (IOException) { /* 保留标记，供下次启动提示恢复入口。 */ }
        catch (UnauthorizedAccessException) { }
    }
    public void CancelAttempt() { try { File.Delete(Marker); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
