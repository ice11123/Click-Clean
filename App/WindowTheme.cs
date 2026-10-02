using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ClickClean;

internal static class WindowTheme
{
    // Windows 11 的公开 DWM 属性；保留原生标题栏、系统按钮和窗口行为。
    internal static bool Apply(Window window, bool dark, bool highContrast, Color background, Color foreground)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;
        uint mode = dark && !highContrast ? 1u : 0u;
        var caption = highContrast ? uint.MaxValue : ColorRef(background);
        var text = highContrast ? uint.MaxValue : ColorRef(foreground);
        var modeStatus = DwmSetWindowAttribute(handle, 20, ref mode, sizeof(uint));
        var captionStatus = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(uint));
        var textStatus = DwmSetWindowAttribute(handle, 36, ref text, sizeof(uint));
        return modeStatus >= 0 && captionStatus >= 0 && textStatus >= 0;
    }
    private static uint ColorRef(Color color) => (uint)(color.R | color.G << 8 | color.B << 16);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref uint value, int size);
}
