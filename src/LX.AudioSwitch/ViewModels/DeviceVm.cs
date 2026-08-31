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

    /// <summary>方向分组（true=输出/Render，false=输入/Capture），由 Refresh 按端点实际归属刷新。</summary>
    [ObservableProperty]
    private bool _isOutput = true;

    /// <summary>端点类型副标题（如「输出 · 扬声器」「输入 · 麦克风」）。</summary>
    [ObservableProperty]
    private string _endpointKindText = "输出 · 播放设备";

    /// <summary>设备类型图标（Segoe Fluent Icons 字形，按设备名解析）。</summary>
    [ObservableProperty]
    private string _iconGlyph = "\uE767";

    public DeviceVm(string id, string displayName)
    {
        Id = id;
        _displayName = displayName;
    }
}
