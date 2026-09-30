using System.Runtime.InteropServices;

namespace LingXi.Audio.Mac;

/// <summary>
/// macOS CoreAudio 互操作声明（仅本文件负责 P/Invoke 与结构布局）。
///
/// 拆出来的理由：CoreAudio 的属性寻址（selector/scope/element）和
/// <c>AudioBufferList</c> 的内存布局极易出错，集中一处便于对照与复核。
/// </summary>
internal static class MacCoreAudioInterop
{
    internal const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    internal const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    internal const string ObjectiveC = "/usr/lib/libobjc.A.dylib";

    internal const uint AudioObjectSystemObject = 1;

    // 属性选择器（四字符码，'xxxx' 大端）。
    //
    // ⚠️ 这些值**必须用 Swift 的 kAudio* 常量核对**，不能凭记忆写：
    //    早先我把 BufferFrameSize 写成 'bufs'、Latency 写成 'latn'、
    //    SafetyOffset 写成 'safo'、VirtualFormat 写成 'vfmt'，四个全是错的，
    //    表现为读取返回 'who?'(2003332927) 且值为 0，被误判成"时序问题"查了很久。
    //    正确值：'fsiz' / 'ltnc' / 'saft' / 'sfmt'。
    internal const uint SelectorDevices = 0x64657623;       // 'dev#'
    internal const uint SelectorDeviceUID = 0x75696420;     // 'uid '
    internal const uint SelectorDeviceName = 0x6C6E616D;    // 'lnam'
    internal const uint SelectorStreamConfig = 0x736C6179;  // 'slay'
    internal const uint SelectorBufferFrameSize = 0x6673697A; // 'fsiz'（⚠️ 不是 'bufs'）
    internal const uint SelectorDeviceLatency = 0x6C746E63;   // 'ltnc'（⚠️ 不是 'latn'）
    internal const uint SelectorSafetyOffset = 0x73616674;    // 'saft'（⚠️ 不是 'safo'）
    internal const uint SelectorVirtualFormat = 0x73666D74;   // 'sfmt'（⚠️ 不是 'vfmt'）
    internal const uint SelectorTapFormat = 0x74666D74;      // 'tfmt'（tap 对象的格式）
    internal const uint SelectorNominalSampleRate = 0x6E737274; // 'nsrt'（设备标称采样率）

    internal const uint ScopeGlobal = 0x676C6F62;  // 'glob'
    internal const uint ScopeOutput = 0x6F757470;  // 'outp'
    internal const uint ScopeInput = 0x696E7074;   // 'inpt'

    internal const uint EncodingUtf8 = 0x08000100;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [DllImport(CoreAudio)]
    internal static extern int AudioObjectGetPropertyDataSize(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, out uint dataSize);

    [DllImport(CoreAudio)]
    internal static extern int AudioObjectGetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier,
        ref uint dataSize, IntPtr data);

    [DllImport(CoreAudio)]
    internal static extern int AudioObjectSetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier,
        uint dataSize, IntPtr data);

    [DllImport(CoreAudio)]
    internal static extern int AudioHardwareCreateProcessTap(IntPtr description, out uint tapId);

    [DllImport(CoreAudio)]
    internal static extern int AudioHardwareDestroyProcessTap(uint tapId);

    [DllImport(CoreAudio)]
    internal static extern int AudioHardwareCreateAggregateDevice(IntPtr description, out uint deviceId);

    [DllImport(CoreAudio)]
    internal static extern int AudioHardwareDestroyAggregateDevice(uint deviceId);

    // IOProc 回调：5 参数版本（C# 侧用委托封送）
    internal delegate int AudioDeviceIOProc(
        uint deviceId,
        IntPtr now,
        IntPtr inputData,
        IntPtr inputTime,
        IntPtr outputData,
        IntPtr outputTime,
        IntPtr clientData);

    [DllImport(CoreAudio)]
    internal static extern int AudioDeviceCreateIOProcID(
        uint deviceId, AudioDeviceIOProc proc, IntPtr clientData, out IntPtr procId);

    [DllImport(CoreAudio)]
    internal static extern int AudioDeviceDestroyIOProcID(uint deviceId, IntPtr procId);

    [DllImport(CoreAudio)]
    internal static extern int AudioDeviceStart(uint deviceId, IntPtr procId);

    [DllImport(CoreAudio)]
    internal static extern int AudioDeviceStop(uint deviceId, IntPtr procId);

    // Objective-C 运行时（用于构造 CATapDescription）
    [DllImport(ObjectiveC)]
    internal static extern IntPtr objc_getClass(string name);

    [DllImport(ObjectiveC)]
    internal static extern IntPtr sel_registerName(string name);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr IntPtr_objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    internal static extern void void_objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    internal static extern void void_objc_msgSend_IntPtr_IntPtr_IntPtr(
        IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr IntPtr_objc_msgSend_IntPtr_IntPtr_long(
        IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, long c);

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string value, uint encoding);

    [DllImport(CoreFoundation)]
    internal static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, long bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    internal static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFArrayCreate(IntPtr allocator, IntPtr[] values, long count, IntPtr callbacks);

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFDictionaryCreate(
        IntPtr allocator, IntPtr[] keys, IntPtr[] values, long count,
        IntPtr keyCallbacks, IntPtr valueCallbacks);

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFNumberCreate(IntPtr allocator, long theType, ref int value);

    [DllImport(CoreFoundation)]
    internal static extern IntPtr CFUUIDCreateString(IntPtr allocator, IntPtr uuid);

    /// <summary>CFNumber 的 kCFNumberSInt32Type。</summary>
    internal const long CFNumberSInt32Type = 3;

    /// <summary>
    /// 取 CoreFoundation 的 "type callbacks" 数据符号地址。
    ///
    /// ⚠️ 必须传真实回调表，不能传 IntPtr.Zero：传零会让 CFArray/CFDictionary 在
    /// retain/release 时走到空指针（实测表现为进程静默卡死，且没有崩溃日志）。
    /// </summary>
    internal static IntPtr TypeCallbacks(string symbol)
    {
        var handle = NativeLibrary.Load(CoreFoundation);
        return NativeLibrary.GetExport(handle, symbol);
    }

    private static readonly IntPtr ArrayCallbacks = TypeCallbacks("kCFTypeArrayCallBacks");
    private static readonly IntPtr DictKeyCallbacks = TypeCallbacks("kCFTypeDictionaryKeyCallBacks");
    private static readonly IntPtr DictValueCallbacks = TypeCallbacks("kCFTypeDictionaryValueCallBacks");

    internal static IntPtr CreateArray(IntPtr[] values) =>
        CFArrayCreate(IntPtr.Zero, values, values.LongLength, ArrayCallbacks);

    internal static IntPtr CreateDictionary(IntPtr[] keys, IntPtr[] values) =>
        CFDictionaryCreate(IntPtr.Zero, keys, values, keys.LongLength, DictKeyCallbacks, DictValueCallbacks);

    internal static IntPtr CreateString(string value) =>
        CFStringCreateWithCString(IntPtr.Zero, value, EncodingUtf8);

    internal static IntPtr CreateInt(int value)
    {
        var local = value;
        return CFNumberCreate(IntPtr.Zero, CFNumberSInt32Type, ref local);
    }
}
