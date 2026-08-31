using System.Windows;
using System.Windows.Controls;

namespace LingXi.Ui.Controls;

/// <summary>状态等级 → 语义色（令牌 LxStatus*Brush / LxPill*BgBrush）。</summary>
public enum LxStatusLevel
{
    Info,
    Ok,
    Warn,
    Bad,
}

/// <summary>状态胶囊：小圆点 + 文本（在线/已拔出/离线/信息通用）。</summary>
public class LxStatusPill : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LxStatusPill), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(LxStatusLevel), typeof(LxStatusPill), new PropertyMetadata(LxStatusLevel.Info));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public LxStatusLevel Level
    {
        get => (LxStatusLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    static LxStatusPill() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(LxStatusPill),
            new FrameworkPropertyMetadata(typeof(LxStatusPill)));
}
