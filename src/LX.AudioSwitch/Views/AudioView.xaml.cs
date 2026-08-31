using System.Windows;
using System.Windows.Controls;

namespace LingXi.AudioSwitch.Views;

public partial class AudioView : UserControl
{
    public AudioView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdateFallback();
        UpdateFallback();
    }

    /// <summary>DataContext 为空 = 音频服务初始化失败 → 显示兜底文案。</summary>
    private void UpdateFallback()
    {
        var unavailable = DataContext is null;
        FallbackText.Visibility = unavailable ? Visibility.Visible : Visibility.Collapsed;
        MainScroll.Visibility = unavailable ? Visibility.Collapsed : Visibility.Visible;
    }
}
