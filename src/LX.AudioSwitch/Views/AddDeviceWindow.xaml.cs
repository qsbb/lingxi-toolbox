using System.Windows;
using LingXi.Audio;

namespace LingXi.AudioSwitch.Views;

public partial class AddDeviceWindow : Window
{
    /// <summary>确认后选择的设备（取消为 null）。</summary>
    public AudioEndpoint? Selected { get; private set; }

    public AddDeviceWindow(IReadOnlyList<AudioEndpoint> candidates)
    {
        InitializeComponent();
        DeviceList.ItemsSource = candidates;
    }

    private void DeviceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        AddButton.IsEnabled = DeviceList.SelectedItem is AudioEndpoint;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Selected = DeviceList.SelectedItem as AudioEndpoint;
        DialogResult = Selected is not null;
    }
}
