namespace ClickClean.Core;

// 鼠标靠近仅改变显示状态；清理必须由独立点击事件触发。
public sealed class DockRevealPolicy
{
    public bool Revealed { get; private set; }
    private double lastNear;
    public bool Evaluate(bool nearEdge, bool insideDock, bool fullscreen, double seconds)
    {
        if (fullscreen || !double.IsFinite(seconds)) { Reset(); return false; }
        if (nearEdge || Revealed && insideDock) { lastNear = seconds; Revealed = true; }
        else if (seconds - lastNear >= 0.22 || seconds < lastNear) Revealed = false;
        return Revealed;
    }
    public void Reset() { Revealed = false; lastNear = 0; }
}
