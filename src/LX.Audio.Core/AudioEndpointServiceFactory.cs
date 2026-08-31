namespace LingXi.Audio;

/// <summary>创建端点服务（测试可替换为 mock，公理 A2）。</summary>
public static class AudioEndpointServiceFactory
{
    /// <summary>创建服务；系统音频服务不可用时抛异常。</summary>
    public static IAudioEndpointService Create() => new AudioDeviceService();
}
