namespace LingXi.Audio;

/// <summary>音频监听（设备级捕获 + 转发）的数据模型。
///
/// 与端点服务（<see cref="IAudioEndpointService"/>）分开：那个只管"枚举设备 / 切默认设备"，
/// 这里是长驻的音频流，生命周期与错误面完全不同，混在一个接口里会让两边都难用。</summary>

/// <summary>能力探测结果，供 UI 决定"监听"按钮是否可用。</summary>
public sealed record AudioTapCapabilities(
    bool Supported,
    string? Reason,
    int MaxTargets,
    bool CanCaptureDevice);

/// <summary>启动监听请求。</summary>
public sealed record AudioTapRequest(
    string SourceDeviceId,
    IReadOnlyList<string> TargetDeviceIds,
    double Gain = 1.0);

/// <summary>一条活动会话的静态信息（启动时返回）。</summary>
public sealed record AudioTapSession(
    string SessionId,
    string SourceDeviceId,
    IReadOnlyList<string> TargetDeviceIds,
    double SampleRate,
    int Channels,
    double LatencyMs,
    bool Resampling,
    /// <summary>延迟是否为估算值（属性读不到时的兜底）。UI 据此决定是否显示"约"。</summary>
    bool LatencyEstimated = false);

/// <summary>会话运行状态（轮询用）。</summary>
public sealed record AudioTapStatus(
    bool Active,
    string? SessionId,
    string? SourceDeviceId,
    IReadOnlyList<string> TargetDeviceIds,
    double PeakLeft,
    double PeakRight,
    long DroppedFrames,
    double UptimeSeconds);

/// <summary>
/// 设备级音频监听：捕获"送往某个输出设备"的全部系统音频，转发到一个或多个输出设备。
///
/// 平台实现：
/// - macOS：CoreAudio 进程 tap（<c>CATapDescription</c>，14.2+）+ 私有聚合设备 + IOProc 转发
/// - Windows：WASAPI 环回（<c>AUDCLNT_STREAMFLAGS_LOOPBACK</c>）+ 渲染端转发
/// </summary>
public interface IAudioLoopbackService : IDisposable
{
    /// <summary>当前平台是否支持，以及不支持的原因（如系统版本过低）。</summary>
    AudioTapCapabilities GetCapabilities();

    /// <summary>
    /// 开始监听。失败时抛 <see cref="AudioTapException"/>（带机器可读错误码，直接回给前端）。
    /// </summary>
    AudioTapSession Start(AudioTapRequest request);

    /// <summary>停止指定会话；<paramref name="sessionId"/> 为空时停止全部。</summary>
    void Stop(string? sessionId = null);

    /// <summary>当前状态；无活动会话返回 null。</summary>
    AudioTapStatus? GetStatus();
}

/// <summary>带错误码的监听异常（错误码会原样回给前端，前端据此做本地化提示）。</summary>
public sealed class AudioTapException : Exception
{
    public AudioTapException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}
