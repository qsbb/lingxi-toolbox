using CommunityToolkit.Mvvm.ComponentModel;
using LingXi.Ui.Controls;

namespace LingXi.AudioSwitch.ViewModels;

/// <summary>设备行 VM（对应 QAS DeviceItem，状态语义色改走 LxStatusLevel）。</summary>
public partial class DeviceVm : ObservableObject
{
    public string Id { get; }

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private bool _isAvailable;

    [ObservableProperty]
    private string _statusText = "检测中";

    [ObservableProperty]
    private LxStatusLevel _level = LxStatusLevel.Info;

    [ObservableProperty]
    private bool _isCurrent;

    public DeviceVm(string id, string displayName)
    {
        Id = id;
        _displayName = displayName;
    }
}
