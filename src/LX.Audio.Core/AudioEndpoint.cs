namespace LingXi.Audio;

/// <summary>端点状态。</summary>
public enum AudioDeviceState
{
    Active,
    Unplugged,
    Disabled,
    NotPresent,
    Unknown,
}

/// <summary>音频端点（CoreAudio MMDevice）。</summary>
public sealed record AudioEndpoint(string Id, string Name, AudioDeviceState State);
