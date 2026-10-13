using Microsoft.Win32;

namespace ClickClean;

internal static class ThemePalette
{
    public static bool Apply(Window window, string theme, bool reduceMotion)
    {
        var dark = theme == "dark";
        if (theme == "system") { using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"); dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0; }
        var contrast = SystemParameters.HighContrast;
        void Brush(string key, Color color) {
            if (Application.Current.Resources[key] is SolidColorBrush existing && existing.Color == color) return;
            Application.Current.Resources[key] = new SolidColorBrush(color);
        }
        var palette = new Dictionary<string, string> {
            ["Bg"] = dark ? "#000000" : "#F7F8FA", ["Surface"] = dark ? "#111114" : "#FFFFFF", ["Text"] = dark ? "#F4F5F7" : "#17233B", ["Muted"] = dark ? "#A3A9B3" : "#586579",
            ["Line"] = dark ? "#303039" : "#DCE1E8", ["Accent"] = dark ? "#7F2333" : "#A42A3A", ["AccentHover"] = dark ? "#922A3E" : "#912334", ["AccentText"] = "#FFFFFF", ["Tint"] = dark ? "#231116" : "#F7E9EC",
            ["DetailAccent"] = dark ? "#F4F5F7" : "#8B661E", ["IslandBg"] = dark ? "#000000" : "#FFFFFF", ["IslandText"] = dark ? "#F4F5F7" : "#17233B", ["IslandMuted"] = dark ? "#A3A9B3" : "#586579",
            ["IslandBorder"] = dark ? "#303039" : "#DCE1E8", ["IslandHoverBorder"] = dark ? "#922A3E" : "#A42A3A", ["IslandGlow"] = dark ? "#74212F" : "#E9BE6C",
            ["AcrylicTint"] = dark ? "#D51E2027" : "#CCEBEFF5", ["AcrylicHover"] = dark ? "#EC2B2830" : "#E6E4E9F2",
            ["AcrylicPressed"] = dark ? "#EE211D25" : "#EED9DFEA", ["AcrylicEdge"] = dark ? "#654E4F5D" : "#B5CDD5E1"
        };
        foreach (var pair in palette) Brush(pair.Key, (Color)ColorConverter.ConvertFromString(pair.Value));
        Application.Current.Resources["AcrylicGrain"] = contrast ? Brushes.Transparent : dark ? FrostedMaterial.DarkGrain : FrostedMaterial.LightGrain;
        Application.Current.Resources["AcrylicHighlight"] = contrast ? Brushes.Transparent : new LinearGradientBrush(
            dark ? Color.FromArgb(12, 255, 255, 255) : Color.FromArgb(145, 255, 255, 255), Colors.Transparent, 90);
        if (contrast) {
            foreach (var key in new[] { "Bg", "Surface", "IslandBg", "Tint" }) Brush(key, SystemColors.WindowColor);
            foreach (var key in new[] { "Text", "Muted", "Line", "DetailAccent", "IslandText", "IslandMuted", "IslandBorder" }) Brush(key, SystemColors.WindowTextColor);
            foreach (var key in new[] { "Accent", "AccentHover", "IslandHoverBorder", "IslandGlow" }) Brush(key, SystemColors.HighlightColor);
            Brush("AccentText", SystemColors.HighlightTextColor);
            foreach (var key in new[] { "AcrylicTint", "AcrylicHover", "AcrylicPressed" }) Brush(key, SystemColors.WindowColor);
            Brush("AcrylicEdge", SystemColors.WindowTextColor);
        }
        Application.Current.Resources["AnimationsAllowed"] = !reduceMotion && SystemParameters.ClientAreaAnimation && !contrast;
        return WindowTheme.Apply(window, dark, contrast, ((SolidColorBrush)Application.Current.Resources["Bg"]).Color, ((SolidColorBrush)Application.Current.Resources["Text"]).Color);
    }
}
