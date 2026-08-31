namespace LingXi.Audio;

/// <summary>
/// 音频端点服务（QuickAudioSwitch AudioDeviceService 的接口化，开发文档 8.3）。
/// 领域内核零第三方依赖，视图层只依赖本接口（公理 A3）。
/// </summary>
public interface IAudioEndpointService : IDisposable
{
    /// <summary>设备/默认端点变化（热插拔、切换、属性变更）。回调线程非 UI 线程。</summary>
    event EventHandler? DevicesChanged;

    /// <summary>枚举音频端点。</summary>
    IReadOnlyList<AudioEndpoint> GetDevices(DataFlow flow, bool activeOnly = false);

    /// <summary>当前默认端点 ID；失败返回 null。</summary>
    string? GetDefaultId(DataFlow flow);

    /// <summary>设置默认设备（Console/Multimedia/Communications 三角色同切）。</summary>
    void SetDefault(string deviceId);
}
