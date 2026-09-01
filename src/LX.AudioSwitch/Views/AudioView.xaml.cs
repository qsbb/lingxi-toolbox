using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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

    /// <summary>
    /// 录制态按键转发给 VM（在按钮 PreviewKeyDown 阶段截获，避免滚轮/焦点副作用）。
    /// 未录制时 Enter/Space 触发按钮默认行为（进入录制）。
    /// </summary>
    private void HotkeyCapButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.AudioViewModel vm || !vm.IsRecordingHotkey)
        {
            return; // 未在录制：让按钮默认行为发生（Command 执行进入录制态）
        }
        e.Handled = true;
        vm.HandleRecordingKey(e.Key, Keyboard.Modifiers);
    }
}
