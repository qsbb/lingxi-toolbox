using System.Windows;
using System.Windows.Controls;

namespace LingXi.Ui.Controls;

/// <summary>
/// L2 玻璃卡片：半透明填充 + 顶亮底暗渐变描边 + 软阴影。
/// 材质令牌见 Themes/Tokens.*.xaml；铁律：只用于控制层/卡片容器。
/// </summary>
public class LxGlassCard : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(LxGlassCard),
        new PropertyMetadata(new CornerRadius(16)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    static LxGlassCard() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(LxGlassCard),
            new FrameworkPropertyMetadata(typeof(LxGlassCard)));
}
