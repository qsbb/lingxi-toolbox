using System.Windows;
using System.Windows.Input;
using LingXi.Audio;

namespace LingXi.AudioSwitch.Views;

/// <summary>
/// 添加音频设备悬浮窗（页头 "＋ 添加设备"）：监控 AddMachineWindow 同款自绘玻璃 Sheet。
/// Owner=主窗口中心、不进任务栏；Esc / 关闭钮 / 点窗外空白（Deactivated）均可关闭。
/// 诞生保护期防打开动画期间焦点抖动误关。
/// </summary>
public partial class AddDeviceWindow : Window
{
    /// <summary>确认后选择的设备（取消为 null）。</summary>
    public AudioEndpoint? Selected { get; private set; }

    private DateTime _bornAt;

    public AddDeviceWindow(IReadOnlyList<AudioEndpoint> candidates)
    {
        InitializeComponent();
        DeviceList.ItemsSource = candidates;
        if (candidates.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            AddButton.IsEnabled = false;
        }
        Loaded += (_, _) => _bornAt = DateTime.UtcNow;
    }

    private void DeviceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        AddButton.IsEnabled = DeviceList.SelectedItem is AudioEndpoint;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Selected = DeviceList.SelectedItem as AudioEndpoint;
        DialogResult = Selected is not null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Esc 关闭。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    /// <summary>点窗外空白 = 关闭（300ms 诞生保护期内忽略焦点抖动）。</summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if ((DateTime.UtcNow - _bornAt).TotalMilliseconds > 300)
        {
            DialogResult ??= false;
        }
    }
}
