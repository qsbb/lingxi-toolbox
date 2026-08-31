using System.Windows;
using System.Windows.Media;

namespace LingXi.Ui.Theme;

/// <summary>令牌字典热切换（Light/Dark），与 WPF-UI 的 ApplicationThemeManager 配合使用。</summary>
public static class LxThemeManager
{
    public static void Apply(bool dark)
    {
        var resources = Application.Current.Resources;
        var dict = resources.MergedDictionaries;

        for (var i = dict.Count - 1; i >= 0; i--)
        {
            var source = dict[i].Source?.ToString() ?? string.Empty;
            if (source.Contains("Tokens.Light.xaml", StringComparison.Ordinal) ||
                source.Contains("Tokens.Dark.xaml", StringComparison.Ordinal))
            {
                dict.RemoveAt(i);
            }
        }

        var uri = new Uri($"pack://application:,,,/LX.Ui;component/Themes/Tokens.{(dark ? "Dark" : "Light")}.xaml");
        dict.Insert(0, new ResourceDictionary { Source = uri });

        // 关键：WPF-UI 的 FluentWindow 模板 Border 直接绑定它自己的 ApplicationBackgroundBrush
        //（浅色≈纯白），优先级高于窗口本体的 Background。这里用索引器写入覆盖，
        // 令模板底色与我们的 LxPageBgBrush 对齐（索引器条目优先于合并字典，DynamicResource 会即时刷新）。
        var bg = dark ? Color.FromRgb(0x1E, 0x1E, 0x22) : Color.FromRgb(0xF2, 0xF2, 0xF7);
        resources["ApplicationBackgroundBrush"] = new SolidColorBrush(bg);
        resources["ApplicationBackgroundColor"] = bg;
    }
}
