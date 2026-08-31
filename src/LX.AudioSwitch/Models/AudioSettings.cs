namespace LingXi.AudioSwitch.Models;

public sealed class SavedDevice
{
    public string Id { get; set; } = "";
    public string Alias { get; set; } = "";
}

/// <summary>lx.audioswitch 设置段（开发文档 8.5 / 11.2）。</summary>
public sealed class AudioSettings
{
    public List<SavedDevice> SavedDevices { get; set; } = [];
    public string CycleHotkey { get; set; } = "Ctrl+Alt+A";
    public bool ShowSwitchHud { get; set; } = true;
}
