using System.Diagnostics;
using System.Runtime.InteropServices;
using static LingXi.Audio.Windows.WasapiInterop;

namespace LingXi.Audio.Windows;

/// <summary>
/// Windows 设备级音频监听：捕获"送往某渲染端点"的全部系统音频（WASAPI 环回），
/// 转发到一个或多个输出设备。
///
/// 实现路径（2026-09-30 在构建机 5.55 上验证过环回捕获）：
///   1. 源设备 <c>IAudioClient.Initialize(AUDCLNT_STREAMFLAGS_LOOPBACK)</c> 打开环回；
///   2. 每个目标设备一个 <c>IAudioRenderClient</c> 渲染流；
///   3. 采集线程读 <c>IAudioCaptureClient</c> 的包 → 写入各渲染流。
///
/// 与 macOS 实现的差异：Windows 没有"聚合设备"概念，多目标就是多条独立渲染流
/// （各自有独立缓冲，因此存在轻微漂移，属平台特性）。
/// </summary>
public sealed class WasapiLoopbackService : IAudioLoopbackService
{
    private readonly object _gate = new();
    private Session? _session;
    private bool _disposed;

    public AudioTapCapabilities GetCapabilities()
    {
        if (!OperatingSystem.IsWindows())
            return new AudioTapCapabilities(false, "非 Windows 平台", 0, false);

        return new AudioTapCapabilities(true, null, MaxTargets: 8, CanCaptureDevice: true);
    }

    public AudioTapSession Start(AudioTapRequest request)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var caps = GetCapabilities();
            if (!caps.Supported)
                throw new AudioTapException("tap_unsupported", caps.Reason ?? "当前平台不支持音频监听");

            if (string.IsNullOrWhiteSpace(request.SourceDeviceId))
                throw new AudioTapException("tap_source_not_found", "未指定捕获源设备");
            if (request.TargetDeviceIds.Count == 0)
                throw new AudioTapException("tap_target_not_found", "未指定播放目标设备");
            if (request.TargetDeviceIds.Count > caps.MaxTargets)
                throw new AudioTapException("tap_too_many_targets", $"最多支持 {caps.MaxTargets} 个目标设备");
            if (request.TargetDeviceIds.Contains(request.SourceDeviceId, StringComparer.Ordinal))
                throw new AudioTapException("tap_same_device", "捕获源与播放目标不能是同一个设备");

            StopLocked();

            var session = new Session(request.SourceDeviceId,
                request.TargetDeviceIds.ToArray(), Math.Clamp(request.Gain, 0.0, 4.0));
            try
            {
                session.Open();
            }
            catch (AudioTapException)
            {
                session.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                session.Dispose();
                throw new AudioTapException("tap_start_failed", $"启动监听失败：{ex.Message}");
            }

            _session = session;
            return new AudioTapSession(session.Id, request.SourceDeviceId,
                request.TargetDeviceIds, session.SampleRate, session.Channels,
                session.LatencyMs, Resampling: false, LatencyEstimated: false);
        }
    }

    public void Stop(string? sessionId = null)
    {
        lock (_gate)
        {
            if (_session is null) return;
            if (sessionId is not null && !string.Equals(sessionId, _session.Id, StringComparison.Ordinal)) return;
            StopLocked();
        }
    }

    public AudioTapStatus? GetStatus()
    {
        lock (_gate)
        {
            return _session?.Snapshot();
        }
    }

    private void StopLocked()
    {
        _session?.Dispose();
        _session = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopLocked();
        }
    }

    // ------------------------------------------------------------------ 会话

    private sealed class Session : IDisposable
    {
        private readonly string _sourceId;
        private readonly string[] _targetIds;
        private readonly double _gain;

        private readonly List<RenderStream> _renders = [];
        private CaptureStream? _capture;
        private Thread? _pump;
        private volatile bool _running;

        private readonly Stopwatch _clock = new();
        private long _peakLeftBits;
        private long _peakRightBits;
        private long _droppedFrames;

        internal Session(string sourceId, string[] targetIds, double gain)
        {
            _sourceId = sourceId;
            _targetIds = targetIds;
            _gain = gain;
            Id = Guid.NewGuid().ToString("N");
        }

        internal string Id { get; }
        internal double SampleRate { get; private set; }
        internal int Channels { get; private set; }
        internal double LatencyMs { get; private set; }

        internal void Open()
        {
            // 目标：每个设备一条渲染流（共享模式）
            foreach (var uid in _targetIds)
            {
                var stream = RenderStream.Open(uid);
                if (stream is null)
                    throw new AudioTapException("tap_target_not_found", $"找不到目标设备：{uid}");
                _renders.Add(stream);
            }

            // 源：环回捕获
            _capture = CaptureStream.Open(_sourceId)
                ?? throw new AudioTapException("tap_source_not_found", $"找不到捕获源设备：{_sourceId}");

            SampleRate = _capture.SampleRate;
            Channels = _capture.Channels;
            LatencyMs = _capture.LatencyMs;

            // A4：目标设备采样率与源不一致时明确报错（本工程无重采样器，
            // 静默转发会变调，比报错更难排查）。
            foreach (var render in _renders)
            {
                if (render.SampleRate > 0 && Math.Abs(render.SampleRate - SampleRate) > 0.5)
                {
                    throw new AudioTapException("tap_format_mismatch",
                        $"采样率不一致：源 {SampleRate:F0}Hz / 目标 {render.SampleRate:F0}Hz，暂不支持该组合");
                }
            }

            foreach (var render in _renders)
            {
                render.Start();
            }
            _capture.Start();

            _running = true;
            _clock.Start();
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "lx-audio-tap" };
            _pump.Start();
        }

        /// <summary>采集线程：读环回包 → 分发到各渲染流。</summary>
        private void PumpLoop()
        {
            var scratch = new byte[0];
            while (_running)
            {
                try
                {
                    if (!_capture!.TryRead(out var data, out var frames, out var silent))
                    {
                        Thread.Sleep(2);
                        continue;
                    }

                    if (frames == 0) continue;

                    var bytes = frames * (uint)(Channels * sizeof(float));
                    if (scratch.Length < bytes) scratch = new byte[bytes];

                    if (!silent && data != IntPtr.Zero)
                    {
                        Marshal.Copy(data, scratch, 0, (int)bytes);
                        ApplyGainAndTrack(scratch, (int)bytes);
                    }
                    else
                    {
                        // ⚠️ 静音包是**正常状态**（没声音在播时就是静音），不能计作丢帧。
                        // 早先把它计入 droppedFrames，长稳测试里表现为"每分钟稳定 +250"的
                        // 假告警（实测 13 分钟累计 3451），实际链路完全健康。
                        Array.Clear(scratch, 0, (int)bytes);
                    }

                    foreach (var render in _renders)
                    {
                        render.Write(scratch, frames);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref _droppedFrames);
                }
            }
        }

        private void ApplyGainAndTrack(byte[] buffer, int byteCount)
        {
            var samples = byteCount / sizeof(float);
            var leftPeak = 0.0;
            var rightPeak = 0.0;
            for (var i = 0; i < samples; i++)
            {
                var value = BitConverter.ToSingle(buffer, i * sizeof(float)) * (float)_gain;
                if (value > 1f) value = 1f;
                else if (value < -1f) value = -1f;
                BitConverter.TryWriteBytes(buffer.AsSpan(i * sizeof(float), sizeof(float)), value);

                var magnitude = Math.Abs(value);
                if ((i & 1) == 0) { if (magnitude > leftPeak) leftPeak = magnitude; }
                else if (magnitude > rightPeak) rightPeak = magnitude;
            }
            Interlocked.Exchange(ref _peakLeftBits, BitConverter.DoubleToInt64Bits(leftPeak));
            Interlocked.Exchange(ref _peakRightBits, BitConverter.DoubleToInt64Bits(rightPeak));
        }

        internal AudioTapStatus Snapshot() => new(
            _running,
            Id,
            _sourceId,
            _targetIds,
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _peakLeftBits)),
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _peakRightBits)),
            Interlocked.Read(ref _droppedFrames),
            _clock.Elapsed.TotalSeconds);

        public void Dispose()
        {
            _running = false;
            try { _pump?.Join(500); } catch { /* best effort */ }

            _capture?.Dispose();
            foreach (var render in _renders) render.Dispose();
            _renders.Clear();
            _capture = null;
            _clock.Stop();
        }
    }

    /// <summary>环回捕获流。</summary>
    private sealed class CaptureStream : IDisposable
    {
        private IAudioClient? _client;
        private IAudioCaptureClient? _capture;
        private IntPtr _format;
        private bool _started;

        internal double SampleRate { get; private set; }
        internal int Channels { get; private set; }
        internal double LatencyMs { get; private set; }

        /// <summary>打开指定设备 UID 的环回捕获；设备不存在返回 null。</summary>
        internal static CaptureStream? Open(string deviceUid)
        {
            var device = DeviceLookup.FindByUid(deviceUid);
            if (device == IntPtr.Zero) return null;

            var stream = new CaptureStream();
            try
            {
                stream.Initialize(device);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
            finally
            {
                Marshal.Release(device);
            }
        }

        private void Initialize(IntPtr device)
        {
            var deviceIface = Marshal.GetObjectForIUnknown(device);
            var audioClientIid = typeof(IAudioClient).GUID;
            // 复用 AudioDeviceService 的 IMMDevice 声明（Activate 返回 IntPtr）
            var immDevice = (IMMDevice)deviceIface;
            var hr = immDevice.Activate(ref audioClientIid, CLSCTX_ALL, IntPtr.Zero, out var clientPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            _client = (IAudioClient)Marshal.GetObjectForIUnknown(clientPtr);
            Marshal.Release(clientPtr);

            hr = _client.GetMixFormat(out _format);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            var fmt = Marshal.PtrToStructure<WAVEFORMATEX>(_format);
            SampleRate = fmt.SamplesPerSec;
            Channels = fmt.Channels;

            // 环回必须以共享模式打开，且缓冲不小于 100ms（微软文档要求）
            hr = _client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
                BufferDurationHns, 0, _format, IntPtr.Zero);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            _client.GetStreamLatency(out var latency);
            _client.GetBufferSize(out var bufferFrames);
            LatencyMs = SampleRate > 0
                ? (bufferFrames + latency / 10_000.0) / SampleRate * 1000.0
                : 0;

            var captureIid = typeof(IAudioCaptureClient).GUID;
            hr = _client.GetService(ref captureIid, out var captureObj);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            _capture = (IAudioCaptureClient)captureObj;
        }

        internal void Start()
        {
            if (_client is null) return;
            var hr = _client.Start();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            _started = true;
        }

        /// <summary>读一个包；无数据返回 false。</summary>
        internal bool TryRead(out IntPtr data, out uint frames, out bool silent)
        {
            data = IntPtr.Zero;
            frames = 0;
            silent = false;
            if (_capture is null) return false;

            if (_capture.GetNextPacketSize(out var available) < 0 || available == 0) return false;
            var hr = _capture.GetBuffer(out data, out frames, out var flags, out _, out _);
            if (hr < 0) return false;
            silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
            _capture.ReleaseBuffer(frames);
            return true;
        }

        public void Dispose()
        {
            if (_started && _client is not null)
            {
                try { _client.Stop(); } catch { /* best effort */ }
                _started = false;
            }
            if (_format != IntPtr.Zero)
            {
                CoTaskMemFree(_format);
                _format = IntPtr.Zero;
            }
            _capture = null;
            _client = null;
        }
    }

    /// <summary>渲染输出流（一个目标设备一条）。</summary>
    private sealed class RenderStream : IDisposable
    {
        private IAudioClient? _client;
        private IAudioRenderClient? _render;
        private IntPtr _format;
        private uint _bufferFrames;
        private bool _started;

        /// <summary>该渲染端点的混音采样率（用于 A4 一致性检查）。</summary>
        internal double SampleRate { get; private set; }

        internal static RenderStream? Open(string deviceUid)
        {
            var device = DeviceLookup.FindByUid(deviceUid);
            if (device == IntPtr.Zero) return null;
            var stream = new RenderStream();
            try
            {
                stream.Initialize(device);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
            finally
            {
                Marshal.Release(device);
            }
        }

        private void Initialize(IntPtr device)
        {
            var deviceIface = Marshal.GetObjectForIUnknown(device);
            var audioClientIid = typeof(IAudioClient).GUID;
            var immDevice = (IMMDevice)deviceIface;
            var hr = immDevice.Activate(ref audioClientIid, CLSCTX_ALL, IntPtr.Zero, out var clientPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            _client = (IAudioClient)Marshal.GetObjectForIUnknown(clientPtr);
            Marshal.Release(clientPtr);

            hr = _client.GetMixFormat(out _format);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            SampleRate = Marshal.PtrToStructure<WAVEFORMATEX>(_format).SamplesPerSec;

            hr = _client.Initialize(AUDCLNT_SHAREMODE_SHARED, 0, BufferDurationHns, 0, _format, IntPtr.Zero);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            _client.GetBufferSize(out _bufferFrames);

            var renderIid = typeof(IAudioRenderClient).GUID;
            hr = _client.GetService(ref renderIid, out var renderObj);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            _render = (IAudioRenderClient)renderObj;

            // 预填充静音，避免启动瞬间的爆音
            if (_render.GetBuffer(_bufferFrames, out var data) >= 0)
            {
                var channels = Marshal.PtrToStructure<WAVEFORMATEX>(_format).Channels;
                var bytes = (int)(_bufferFrames * (uint)(channels * sizeof(float)));
                var silence = new byte[bytes];
                Marshal.Copy(silence, 0, data, bytes);
                _render.ReleaseBuffer(_bufferFrames, 0);
            }
        }

        internal void Start()
        {
            if (_client is null) return;
            var hr = _client.Start();
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            _started = true;
        }

        /// <summary>写入一帧数据（不足部分补静音）。</summary>
        internal void Write(byte[] data, uint frames)
        {
            if (_render is null || _client is null) return;
            if (_client.GetCurrentPadding(out var padding) < 0) return;
            var available = _bufferFrames - padding;
            if (available == 0) return;
            var toWrite = Math.Min(frames, available);
            if (toWrite == 0) return;
            if (_render.GetBuffer(toWrite, out var buffer) < 0) return;

            // 每帧字节数 = 总字节数 / 帧数；取两者较小值，避免越界
            var bytesPerFrame = frames > 0 ? data.Length / (int)frames : 0;
            var byteCount = Math.Min((int)(toWrite * (uint)bytesPerFrame), data.Length);
            Marshal.Copy(data, 0, buffer, byteCount);
            _render.ReleaseBuffer(toWrite, 0);
        }

        public void Dispose()
        {
            if (_started && _client is not null)
            {
                try { _client.Stop(); } catch { /* best effort */ }
                _started = false;
            }
            if (_format != IntPtr.Zero)
            {
                CoTaskMemFree(_format);
                _format = IntPtr.Zero;
            }
            _render = null;
            _client = null;
        }
    }

    /// <summary>按 UID 找设备（复用 AudioDeviceService 的 COM 声明）。</summary>
    private static class DeviceLookup
    {
        private static readonly Guid CLSID_MMDeviceEnumerator =
            new("BCDE0395-E52F-467C-8E3D-C4579291692E");

        internal static IntPtr FindByUid(string uid)
        {
            var enumeratorClsid = CLSID_MMDeviceEnumerator;
            var iid = typeof(IMMDeviceEnumerator).GUID;
            var hr = CoCreateInstance(ref enumeratorClsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out var enumPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try
            {
                var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(enumPtr);
                // eRender=0, DEVICE_STATE_ACTIVE=1
                if (enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceState.Active, out var collection) < 0)
                    return IntPtr.Zero;
                try
                {
                    if (collection.GetCount(out var count) < 0) return IntPtr.Zero;
                    for (var i = 0; i < count; i++)
                    {
                        if (collection.Item((uint)i, out var device) < 0) continue;
                        if (device.GetId(out var id) >= 0 && string.Equals(id, uid, StringComparison.Ordinal))
                        {
                            // 返回一个引用（调用方负责 Release）
                            return Marshal.GetIUnknownForObject(device);
                        }
                        Marshal.ReleaseComObject(device);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(collection);
                }
            }
            finally
            {
                Marshal.Release(enumPtr);
            }
            return IntPtr.Zero;
        }
    }
}
