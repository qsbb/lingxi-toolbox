using System.Windows;

namespace LingXi.Ui.Theme;

/// <summary>令牌字典热切换（Light/Dark），与 WPF-UI 的 ApplicationThemeManager 配合使用。</summary>
public static class LxThemeManager
{
    public static void Apply(bool dark)
    {
        var dict = Application.Current.Resources.MergedDictionaries;

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
    }
}
