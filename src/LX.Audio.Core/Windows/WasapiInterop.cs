using System.Runtime.InteropServices;

namespace LingXi.Audio.Windows;

/// <summary>
/// WASAPI 互操作声明（环回捕获 + 渲染输出）。
///
/// 与 <c>AudioDeviceService</c> 的 MMDevice 声明互补：那边只做设备枚举与默认设备切换，
/// 这里需要 <c>IAudioClient</c> / <c>IAudioCaptureClient</c> / <c>IAudioRenderClient</c>
/// 做实际音频流。IMMDevice/IMMDeviceEnumerator 复用同一份声明。
/// </summary>
internal static class WasapiInterop
{
    internal const int AUDCLNT_SHAREMODE_SHARED = 0;

    /// <summary>环回捕获标志（捕获"送往该渲染端点"的全部音频）。</summary>
    internal const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;

    /// <summary>事件驱动回调（比轮询延迟更低）。</summary>
    internal const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;

    internal const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    internal const int CLSCTX_ALL = 0x17;

    /// <summary>共享模式缓冲时长：100ms（环回捕获建议不小于 100ms）。</summary>
    internal const long BufferDurationHns = 1_000_000; // 100 ms in 100-ns units

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, int flags);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WAVEFORMATEX
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort CbSize;
    }

    /// <summary>IEEE float 格式标志（共享模式混音格式通常是 float32）。</summary>
    internal const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;
    internal const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    [DllImport("ole32.dll")]
    internal static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsCtx, ref Guid iid, out IntPtr instance);

    [DllImport("ole32.dll")]
    internal static extern int CoTaskMemFree(IntPtr ptr);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateEvent(IntPtr attrs, bool manualReset, bool initialState, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
