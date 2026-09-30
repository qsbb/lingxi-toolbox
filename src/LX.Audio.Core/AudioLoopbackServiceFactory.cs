using LingXi.Audio.Mac;

namespace LingXi.Audio;

/// <summary>创建音频监听服务（按平台分派；测试可替换为 mock）。</summary>
public static class AudioLoopbackServiceFactory
{
    /// <summary>
    /// 创建监听服务。不支持的平台返回 null —— 调用方（宿主）据此让
    /// <c>audio.tapCapabilities</c> 返回 supported=false，而不是抛异常。
    /// </summary>
    public static IAudioLoopbackService? Create()
    {
        if (OperatingSystem.IsMacOS()) return new MacAudioLoopbackService();
        if (OperatingSystem.IsWindows()) return new Windows.WasapiLoopbackService();
        return null;
    }
}
