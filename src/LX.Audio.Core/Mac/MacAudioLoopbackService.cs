using System.Diagnostics;
using System.Runtime.InteropServices;
using static LingXi.Audio.Mac.MacCoreAudioInterop;

namespace LingXi.Audio.Mac;

/// <summary>
/// macOS 设备级音频监听：捕获"送往某输出设备"的全部系统音频，转发到一个或多个输出设备。
///
/// 实现路径（2026-09-30 实机验证过）：
///   1. <c>CATapDescription</c> 设备级 tap（<c>initExcludingProcesses:andDeviceUID:withStream:</c>）
///      —— 只抓送往 <c>sourceDeviceId</c> 的音频，不是全局、也不是单 App；
///   2. 建私有聚合设备，把 tap 挂到 <c>taps</c>、把目标设备挂到 <c>subdevices</c>
///      —— 一条流即可同时输出到多个设备；
///   3. <c>AudioDeviceCreateIOProcID</c> 回调里：读输入 AudioBufferList → 写输出 AudioBufferList。
///
/// ⚠️ 两个实机踩过的点：
///   - <c>AudioBufferList.mBuffers</c> 的偏移是 **8**（不是 4）：结构含指针，8 字节对齐；
///     按 4 读会把通道数读成 0，表现为"枚举不到设备"。
///   - 必须打包成带 bundle id 的 .app 并声明 <c>NSAudioCaptureUsageDescription</c>，
///     否则 <c>AudioDeviceStart</c> 会静默失败（返回错误码但没有任何系统提示）。
/// </summary>
public sealed class MacAudioLoopbackService : IAudioLoopbackService
{
    /// <summary>进程 tap 需要 macOS 14.2+。</summary>
    private static readonly Version MinMacOs = new(14, 2);

    private readonly object _gate = new();
    private Session? _session;
    private bool _disposed;

    public AudioTapCapabilities GetCapabilities()
    {
        if (!OperatingSystem.IsMacOS())
            return new AudioTapCapabilities(false, "非 macOS 平台", 0, false);

        var version = Environment.OSVersion.Version;
        if (version < MinMacOs)
        {
            return new AudioTapCapabilities(false,
                $"系统音频捕获需要 macOS {MinMacOs} 及以上（当前 {version.Major}.{version.Minor}）", 0, false);
        }

        // 进程 tap 是 14.2 才有的符号；这里再确认一次动态库能解析到它。
        if (!CanResolveTapSymbol())
            return new AudioTapCapabilities(false, "CoreAudio 未提供进程 tap 接口", 0, false);

        return new AudioTapCapabilities(true, null, MaxTargets: 8, CanCaptureDevice: true);
    }

    /// <summary>枚举可作为捕获源/播放目标的输出设备（UID + 名称 + 通道数）。</summary>
    public IReadOnlyList<(string Uid, string Name, int Channels)> ListOutputDevices()
    {
        var result = new List<(string, string, int)>();
        foreach (var device in AllDeviceIds())
        {
            var channels = OutputChannelCount(device);
            if (channels <= 0) continue;
            var uid = ReadString(device, SelectorDeviceUID, ScopeGlobal);
            if (string.IsNullOrEmpty(uid)) continue;
            var name = ReadString(device, SelectorDeviceName, ScopeGlobal);
            result.Add((uid, string.IsNullOrEmpty(name) ? uid : name, channels));
        }
        return result;
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

            // 源 == 目标会形成回环啸叫，直接拦掉（实测过的风险）
            if (request.TargetDeviceIds.Contains(request.SourceDeviceId, StringComparer.Ordinal))
                throw new AudioTapException("tap_same_device", "捕获源与播放目标不能是同一个设备");

            StopLocked();

            var sourceUid = request.SourceDeviceId;
            var targetUids = request.TargetDeviceIds.ToArray();

            // 目标设备必须存在
            foreach (var uid in targetUids)
            {
                if (FindDeviceByUid(uid) is null)
                    throw new AudioTapException("tap_target_not_found", $"找不到目标设备：{uid}");
            }

            var session = new Session(sourceUid, targetUids, Math.Clamp(request.Gain, 0.0, 4.0));
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
            return new AudioTapSession(
                session.Id, sourceUid, targetUids,
                session.SampleRate, session.Channels, session.LatencyMs, Resampling: false,
                LatencyEstimated: session.LatencyEstimated);
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
            if (_session is null) return null;
            return _session.Snapshot();
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

    // ------------------------------------------------------------------ 内部

    private static bool CanResolveTapSymbol()
    {
        try
        {
            var handle = NativeLibrary.Load(CoreAudio);
            return NativeLibrary.TryGetExport(handle, "AudioHardwareCreateProcessTap", out _);
        }
        catch
        {
            return false;
        }
    }

    private static uint? FindDeviceByUid(string uid)
    {
        foreach (var device in AllDeviceIds())
        {
            if (ReadString(device, SelectorDeviceUID, ScopeGlobal) == uid) return device;
        }
        return null;
    }

    internal static uint[] AllDeviceIds()
    {
        var address = new PropertyAddress { Selector = SelectorDevices, Scope = ScopeGlobal, Element = 0 };
        if (AudioObjectGetPropertyDataSize(AudioObjectSystemObject, ref address, 0, IntPtr.Zero, out var size) != 0
            || size == 0)
            return [];

        var count = (int)(size / sizeof(uint));
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(AudioObjectSystemObject, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                return [];
            var result = new uint[count];
            for (var i = 0; i < count; i++) result[i] = (uint)Marshal.ReadInt32(buffer, i * sizeof(uint));
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string ReadString(uint deviceId, uint selector, uint scope)
    {
        var address = new PropertyAddress { Selector = selector, Scope = scope, Element = 0 };
        var size = (uint)IntPtr.Size;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                return string.Empty;
            var cfString = Marshal.ReadIntPtr(buffer);
            if (cfString == IntPtr.Zero) return string.Empty;
            try
            {
                var bytes = new byte[1024];
                return CFStringGetCString(cfString, bytes, bytes.Length, EncodingUtf8)
                    ? System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0')
                    : string.Empty;
            }
            finally
            {
                CFRelease(cfString);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static int OutputChannelCount(uint deviceId)
    {
        var address = new PropertyAddress { Selector = SelectorStreamConfig, Scope = ScopeOutput, Element = 0 };
        if (AudioObjectGetPropertyDataSize(deviceId, ref address, 0, IntPtr.Zero, out var size) != 0 || size < 4)
            return 0;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0) return 0;
            // AudioBufferList { UInt32 mNumberBuffers; AudioBuffer mBuffers[] } —— 指针成员导致 8 字节对齐
            var buffers = Marshal.ReadInt32(buffer);
            var channels = 0;
            for (var i = 0; i < buffers; i++) channels += Marshal.ReadInt32(buffer, 8 + i * 16);
            return channels;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static uint ReadUInt(uint deviceId, uint selector, uint scope)
    {
        var address = new PropertyAddress { Selector = selector, Scope = scope, Element = 0 };
        var size = (uint)sizeof(uint);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var st = AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, buffer);
            if (st != 0) return 0;
            return (uint)Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static double ReadDouble(uint deviceId, uint selector, uint scope)
    {
        var address = new PropertyAddress { Selector = selector, Scope = scope, Element = 0 };
        var size = (uint)sizeof(double);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0) return 0;
            return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static AudioStreamBasicDescription ReadFormat(uint deviceId, uint scope,
        uint selector = SelectorVirtualFormat)
    {
        var address = new PropertyAddress { Selector = selector, Scope = scope, Element = 0 };
        var size = (uint)Marshal.SizeOf<AudioStreamBasicDescription>();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var asbd = new AudioStreamBasicDescription();
            if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, buffer) == 0)
                asbd = Marshal.PtrToStructure<AudioStreamBasicDescription>(buffer);
            return asbd;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>一次监听会话：持有 tap、聚合设备与 IOProc，负责全部释放。</summary>
    private sealed class Session : IDisposable
    {
        private readonly string _sourceUid;
        private readonly string[] _targetUids;
        private readonly double _gain;

        private IntPtr _tapDescription;
        private IntPtr _aggregateDescription;
        private uint _tapId;
        private uint _aggregateId;
        private IntPtr _procId;
        private AudioDeviceIOProc? _procDelegate; // 必须保活，否则 GC 回收后回调崩溃
        private bool _running;

        private readonly Stopwatch _clock = new();
        private long _peakLeftBits;
        private long _peakRightBits;
        private long _droppedFrames;

        internal Session(string sourceUid, string[] targetUids, double gain)
        {
            _sourceUid = sourceUid;
            _targetUids = targetUids;
            _gain = gain;
            Id = Guid.NewGuid().ToString("N");
        }

        internal string Id { get; }
        internal double SampleRate { get; private set; }
        internal int Channels { get; private set; }
        internal double LatencyMs { get; private set; }

        /// <summary>延迟是否为估算值（属性读不到时）。UI 应据此标注"约"。</summary>
        internal bool LatencyEstimated { get; private set; }

        internal void Open()
        {
            // 1) 设备级 tap：只捕获送往 _sourceUid 的音频
            var cls = objc_getClass("CATapDescription");
            if (cls == IntPtr.Zero) throw new AudioTapException("tap_unsupported", "系统未提供 CATapDescription");

            var selInit = sel_registerName("initExcludingProcesses:andDeviceUID:withStream:");
            var selName = sel_registerName("setName:");
            var selPrivate = sel_registerName("setPrivate:");
            var selUuid = sel_registerName("UUID");

            var alloc = IntPtr_objc_msgSend(cls, sel_registerName("alloc"));
            var emptyArray = CreateArray([]);
            var deviceUid = CreateString(_sourceUid);
            var instance = IntPtr_objc_msgSend_IntPtr_IntPtr_long(alloc, selInit, emptyArray, deviceUid, 0);
            if (instance == IntPtr.Zero)
                throw new AudioTapException("tap_start_failed", "无法创建设备级 tap（设备 UID 无效？）");

            var name = CreateString("凌溪工具箱监听");
            void_objc_msgSend_IntPtr(instance, selName, name);
            void_objc_msgSend_IntPtr(instance, selPrivate, (IntPtr)1);
            _tapDescription = instance;

            var status = AudioHardwareCreateProcessTap(instance, out _tapId);
            if (status != 0)
            {
                throw new AudioTapException("tap_permission_denied",
                    $"创建音频捕获失败（OSStatus {status}）；请在「系统设置 → 隐私与安全性 → 音频录制」中允许本应用");
            }

            // 2) 私有聚合设备：tap 作为输入 + 目标设备作为输出子设备
            // ⚠️ CATapDescription.UUID 返回的是 NSUUID*（ObjC 对象），**不是** CFUUIDRef。
            // 早先把它直接传给 CFUUIDCreateString，类型不匹配导致进程静默挂死（实测）。
            // 正确路径：NSUUID → UUIDString(NSString) → UTF8String(const char*)。
            var nsUuid = IntPtr_objc_msgSend(instance, selUuid);
            if (nsUuid == IntPtr.Zero)
                throw new AudioTapException("tap_start_failed", "无法读取 tap 的 UUID");
            var nsUuidString = IntPtr_objc_msgSend(nsUuid, sel_registerName("UUIDString"));
            var utf8Ptr = IntPtr_objc_msgSend(nsUuidString, sel_registerName("UTF8String"));
            var tapUid = Marshal.PtrToStringUTF8(utf8Ptr)
                ?? throw new AudioTapException("tap_start_failed", "无法转换 tap UUID");

            var subDevices = _targetUids.Select(uid => new Dictionary<string, IntPtr>
            {
                ["uid"] = CreateString(uid),
            }).ToArray();

            var aggregate = BuildAggregateDescription(tapUid, _targetUids);
            status = AudioHardwareCreateAggregateDevice(aggregate, out _aggregateId);
            if (status != 0)
            {
                CFRelease(aggregate);
                throw new AudioTapException("tap_start_failed", $"创建聚合设备失败（OSStatus {status}）");
            }
            // ⚠️ 描述字典必须保活到会话结束：实测若创建后立刻 CFRelease，
            //    聚合设备会被一并回收，后续所有属性查询都返回 'who?'（对象不存在）。
            _aggregateDescription = aggregate;

            // 3) 读格式与延迟，供 UI 展示。
            //    ⚠️ 格式必须从 **tap 对象**读（kAudioTapPropertyFormat='tfmt'）；
            //    在聚合设备上读 vfmt 会拿到 0（实测 sampleRate=0/ch=0），
            //    因为聚合设备的流格式要到流启动后才填充。
            var tapFormat = ReadFormat(_tapId, ScopeGlobal, SelectorTapFormat);
            var sampleRate = tapFormat.SampleRate;
            if (sampleRate <= 0)
            {
                // 兜底：用主目标设备的标称采样率
                var master = FindDeviceByUid(_targetUids[0]);
                sampleRate = master is null ? 48000 : ReadDouble(master.Value, SelectorNominalSampleRate, ScopeGlobal);
                if (sampleRate <= 0) sampleRate = 48000;
            }
            SampleRate = sampleRate;

            // A4：目标设备采样率与源不一致时**明确报错**，而不是让它变调。
            // 本工程没有内置重采样器，静默转发的后果是音调不对（比报错更难排查）。
            foreach (var targetUid in _targetUids)
            {
                var target = FindDeviceByUid(targetUid);
                if (target is null) continue;
                var targetRate = ReadDouble(target.Value, SelectorNominalSampleRate, ScopeGlobal);
                if (targetRate > 0 && Math.Abs(targetRate - SampleRate) > 0.5)
                {
                    throw new AudioTapException("tap_format_mismatch",
                        $"采样率不一致：源 {SampleRate:F0}Hz / 目标 {targetRate:F0}Hz，暂不支持该组合");
                }
            }
            Channels = (int)(tapFormat.ChannelsPerFrame > 0 ? tapFormat.ChannelsPerFrame : 2);
            // 延迟属性（bufs/latn/safo）在聚合设备**启动后**才稳定可读：
            // 刚创建时读会拿到 'who?'（实测）。因此这里先记默认值，
            // 等 AudioDeviceStart 成功后再刷新一次（见 RefreshLatency）。
            // 实测基线：512(缓冲) + 88(设备) + 576(安全偏移) @48kHz ≈ 24.5ms。
            LatencyMs = 24.5;
            LatencyEstimated = true;

            // 4) IOProc：读 tap 输入 → 写目标输出
            _procDelegate = OnAudio;
            status = AudioDeviceCreateIOProcID(_aggregateId, _procDelegate, IntPtr.Zero, out _procId);
            if (status != 0)
                throw new AudioTapException("tap_start_failed", $"创建音频回调失败（OSStatus {status}）");

            status = AudioDeviceStart(_aggregateId, _procId);
            if (status != 0)
            {
                throw new AudioTapException("tap_start_failed",
                    $"启动音频流失败（OSStatus {status}）；请确认已授予「音频录制」权限");
            }

            // 流已启动 → 此时延迟属性可读，刷新为实测值
            RefreshLatency();

            _running = true;
            _clock.Start();
        }

        /// <summary>流启动后刷新延迟（启动前读不到，会返回 'who?'）。</summary>
        private void RefreshLatency()
        {
            try
            {
                var bufferFrames = ReadUInt(_aggregateId, SelectorBufferFrameSize, ScopeGlobal);
                var latency = ReadUInt(_aggregateId, SelectorDeviceLatency, ScopeOutput);
                var safety = ReadUInt(_aggregateId, SelectorSafetyOffset, ScopeOutput);
                if (bufferFrames + latency + safety > 0 && SampleRate > 0)
                {
                    LatencyMs = (bufferFrames + latency + safety) / SampleRate * 1000.0;
                    LatencyEstimated = false;
                }
            }
            catch
            {
                // 读不到就保留估算值（LatencyEstimated 仍为 true）
            }
        }

        /// <summary>把 tap 与目标设备组装成聚合设备描述（用 CFDictionary）。</summary>
        private IntPtr BuildAggregateDescription(string tapUid, string[] targetUids)
        {
            var cf = new List<IntPtr>();
            void Add(string key, IntPtr value)
            {
                cf.Add(CreateString(key));
                cf.Add(value);
            }

            Add("name", CreateString("凌溪监听"));
            Add("uid", CreateString("dev.lingxi.tap." + Id));
            var isPrivate = 1;
            Add("private", CreateInt(isPrivate));

            // taps: [ { uid: <tapUid>, drift: 1 } ]
            var tapKeys = new[]
            {
                CreateString("uid"),
                CreateString("drift"),
            };
            var drift = 1;
            var tapValues = new[]
            {
                CreateString(tapUid),
                CreateInt(drift),
            };
            var tapDict = CreateDictionary(tapKeys, tapValues);
            var tapArray = CreateArray([tapDict]);
            Add("taps", tapArray);

            // subdevices: [ { uid: <target> }, ... ]
            var subDicts = new List<IntPtr>();
            foreach (var uid in targetUids)
            {
                var k = new[] { CreateString("uid") };
                var v = new[] { CreateString(uid) };
                subDicts.Add(CreateDictionary(k, v));
            }
            var subArray = CreateArray(subDicts.ToArray());
            Add("subdevices", subArray);
            Add("master", CFStringCreateWithCString(IntPtr.Zero, targetUids[0], EncodingUtf8));

            var keys = new List<IntPtr>();
            var values = new List<IntPtr>();
            for (var i = 0; i < cf.Count; i += 2) { keys.Add(cf[i]); values.Add(cf[i + 1]); }
            return CreateDictionary(keys.ToArray(), values.ToArray());
        }


        /// <summary>音频回调：把捕获到的输入直接写进输出缓冲（零分配）。</summary>
        private int OnAudio(uint deviceId, IntPtr now, IntPtr inputData, IntPtr inputTime,
            IntPtr outputData, IntPtr outputTime, IntPtr clientData)
        {
            try
            {
                var inList = new AudioBufferListReader(inputData);
                var outList = new AudioBufferListReader(outputData);

                var frames = 0;
                for (var i = 0; i < outList.Count; i++)
                {
                    var outBuffer = outList.Get(i);
                    if (outBuffer.Data == IntPtr.Zero || outBuffer.Size == 0) continue;

                    var outSamples = (int)(outBuffer.Size / sizeof(float));
                    var inBuffer = i < inList.Count ? inList.Get(i) : default;
                    if (inBuffer.Data != IntPtr.Zero && inBuffer.Size > 0)
                    {
                        var inSamples = (int)(inBuffer.Size / sizeof(float));
                        var n = Math.Min(outSamples, inSamples);
                        CopyAndTrack(inBuffer.Data, outBuffer.Data, n);
                        for (var k = n; k < outSamples; k++)
                            Marshal.WriteInt32(outBuffer.Data, k * sizeof(float), 0);
                        frames = Math.Max(frames, n / Math.Max(Channels, 1));
                    }
                    else
                    {
                        // 没有输入数据时写静音，避免输出设备播放上次残留
                        for (var k = 0; k < outSamples; k++)
                            Marshal.WriteInt32(outBuffer.Data, k * sizeof(float), 0);
                    }
                }

                if (frames == 0) Interlocked.Increment(ref _droppedFrames);
            }
            catch
            {
                // 回调内绝不抛出：一旦抛异常整个音频流会被 CoreAudio 停掉
                Interlocked.Increment(ref _droppedFrames);
            }
            return 0;
        }

        private void CopyAndTrack(IntPtr src, IntPtr dst, int samples)
        {
            var leftPeak = 0.0;
            var rightPeak = 0.0;
            for (var i = 0; i < samples; i++)
            {
                var raw = Marshal.ReadInt32(src, i * sizeof(float));
                var value = BitConverter.Int32BitsToSingle(raw);
                var scaled = (float)(value * _gain);
                if (scaled > 1f) scaled = 1f;
                else if (scaled < -1f) scaled = -1f;
                Marshal.WriteInt32(dst, i * sizeof(float), BitConverter.SingleToInt32Bits(scaled));

                var magnitude = Math.Abs(scaled);
                if ((i & 1) == 0) { if (magnitude > leftPeak) leftPeak = magnitude; }
                else if (magnitude > rightPeak) rightPeak = magnitude;
            }
            Interlocked.Exchange(ref _peakLeftBits, BitConverter.DoubleToInt64Bits(leftPeak));
            Interlocked.Exchange(ref _peakRightBits, BitConverter.DoubleToInt64Bits(rightPeak));
        }

        internal AudioTapStatus Snapshot() => new(
            _running,
            Id,
            _sourceUid,
            _targetUids,
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _peakLeftBits)),
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _peakRightBits)),
            Interlocked.Read(ref _droppedFrames),
            _clock.Elapsed.TotalSeconds);

        public void Dispose()
        {
            if (_running && _procId != IntPtr.Zero)
            {
                AudioDeviceStop(_aggregateId, _procId);
                _running = false;
            }
            if (_procId != IntPtr.Zero)
            {
                AudioDeviceDestroyIOProcID(_aggregateId, _procId);
                _procId = IntPtr.Zero;
            }
            if (_aggregateId != 0)
            {
                AudioHardwareDestroyAggregateDevice(_aggregateId);
                _aggregateId = 0;
            }
            if (_tapId != 0)
            {
                AudioHardwareDestroyProcessTap(_tapId);
                _tapId = 0;
            }
            if (_aggregateDescription != IntPtr.Zero)
            {
                CFRelease(_aggregateDescription);
                _aggregateDescription = IntPtr.Zero;
            }
            if (_tapDescription != IntPtr.Zero)
            {
                CFRelease(_tapDescription);
                _tapDescription = IntPtr.Zero;
            }
            _procDelegate = null;
            _clock.Stop();
        }
    }

    /// <summary>AudioBufferList 的只读视图（避免 unsafe，按 8 字节对齐读取）。</summary>
    private readonly struct AudioBufferListReader
    {
        private readonly IntPtr _list;

        internal AudioBufferListReader(IntPtr list) => _list = list;

        internal int Count => _list == IntPtr.Zero ? 0 : Marshal.ReadInt32(_list);

        internal (IntPtr Data, uint Size) Get(int index)
        {
            if (_list == IntPtr.Zero) return (IntPtr.Zero, 0);
            // AudioBuffer { UInt32 mNumberChannels; UInt32 mDataByteSize; void* mData }
            var offset = 8 + index * 16;
            var size = (uint)Marshal.ReadInt32(_list, offset + 4);
            var data = Marshal.ReadIntPtr(_list, offset + 8);
            return (data, size);
        }
    }
}
