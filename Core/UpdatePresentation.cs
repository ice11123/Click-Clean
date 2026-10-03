namespace ClickClean.Core;

public enum UpdatePhase { Idle, Checking, Current, Available, Downloading, Ready, Failed }
public enum UpdateAction { Check, Download, Apply }

public sealed record UpdatePresentation(bool Visible, string ButtonText, bool Enabled, UpdateAction Action)
{
    public static UpdatePresentation Create(UpdatePhase phase, bool available, bool ready, bool busy, bool cleanupBusy)
    {
        var action = ready ? UpdateAction.Apply : available ? UpdateAction.Download : UpdateAction.Check;
        var label = busy ? phase == UpdatePhase.Downloading ? "下载中…" : "检查中…"
            : action switch { UpdateAction.Apply => "重启并更新", UpdateAction.Download => "下载更新", _ => "重试检查" };
        return new(available || ready || phase is UpdatePhase.Downloading or UpdatePhase.Failed,
            label, !busy && (action != UpdateAction.Apply || !cleanupBusy), action);
    }
}
