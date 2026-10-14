using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Markup;

namespace ClickClean;

/// <summary>界面语言独立于系统区域设置；切换不改变数字、路径或系统原始错误。</summary>
public static partial class Localization
{
    private static string language = "zh-CN";
    public static event Action? Changed;
    public static string Language
    {
        get => language;
        set
        {
            var normalized = value == "en-US" ? "en-US" : "zh-CN";
            if (language == normalized) return;
            language = normalized;
            Changed?.Invoke();
        }
    }
    public static bool IsEnglish => Language == "en-US";
    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Text(key), args);
    public static string Text(string? key)
    {
        if (string.IsNullOrEmpty(key) || !IsEnglish) return key ?? "";
        if (english.TryGetValue(key, out var translated)) return translated;
        if (!key.Any(character => character is >= '\u4e00' and <= '\u9fff')) return key;
        foreach (var template in templates)
        {
            var match = template.Pattern.Match(key);
            if (!match.Success) continue;
            var values = template.Indexes.Select(index => (object)Text(match.Groups["p" + index].Value)).ToArray();
            return string.Format(CultureInfo.CurrentCulture, template.English, values);
        }
        // 诊断中的系统错误保留原样，只翻译应用拥有的前缀。
        foreach (var prefix in errorPrefixes)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) return Text(prefix) + Text(key[prefix.Length..]);
        return key;
    }
    private sealed record Template(Regex Pattern, string English, int[] Indexes);
    private static Template CreateTemplate(string source, string target)
    {
        var indexes = Regex.Matches(source, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
        var pattern = Regex.Replace(Regex.Escape(source), @"\\\{(\d+)\}", m => "(?<p" + m.Groups[1].Value + ">.*?)");
        return new(new Regex("\\A" + pattern + "\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), target, indexes);
    }
    private static readonly string[] errorPrefixes = ["操作未完成：", "设置未保存：", "设置保存失败：", "持续观测：", "读取失败：", "检查失败：", "下载失败：", "更新失败：", "内存读数暂不可用：", "进程读数暂不可用：", "整理失败：", "更新未完成，现有版本不变：", "更新应用失败，现有版本保留：", "读取整理后内存失败：", "复测不可用：", "持续观测记录未保存："];
    private static readonly Template[] templates = CreateTemplates();
    private static readonly Dictionary<string, string> english = CreateEnglish();
    private static partial Dictionary<string, string> CreateEnglish();
    private static partial Template[] CreateTemplates();
}

internal sealed class LocaleSource : INotifyPropertyChanged
{
    public static LocaleSource Instance { get; } = new();
    private LocaleSource() => Localization.Changed += () => PropertyChanged?.Invoke(this, new("Item[]"));
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => Localization.Text(key);
}

/// <summary>使用动态绑定而不是静态资源，使弹出层和页面在语言切换时同步更新。</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var escaped = Key.Replace("^", "^^", StringComparison.Ordinal).Replace("]", "^]", StringComparison.Ordinal).Replace(",", "^,", StringComparison.Ordinal);
        return new Binding("[" + escaped + "]") { Source = LocaleSource.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
    }
}
