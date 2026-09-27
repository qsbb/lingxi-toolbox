using System.Runtime.InteropServices;

namespace LingXi.Flutter.NativeHost.Mac;

/// <summary>
/// macOS 音频设备读写（CoreAudio HAL）。
///
/// 与 Windows 版对应：Windows 走 COM（IPolicyConfig 三角色切换）；
/// macOS 走 <c>AudioObjectGet/SetPropertyData</c>。
/// 设备 id 用 CoreAudio 的 UID（跨重启稳定，等价于 Windows 的 endpoint id）。
/// </summary>
internal sealed class MacAudioService : IDisposable
{
    private const string CoreAudio =
        "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint AudioObjectSystemObject = 1;

    // 属性选择器（四字符码）
    private const uint SelectorDevices = 0x64657623;            // 'dev#'
    private const uint SelectorDefaultOutput = 0x644F7574;      // 'dOut'
    private const uint SelectorDefaultInput = 0x64496E20;       // 'dIn '
    private const uint SelectorDeviceUID = 0x75696420;          // 'uid '
    private const uint SelectorDeviceName = 0x6C6E616D;         // 'lnam'
    private const uint SelectorStreamConfig = 0x736C6179;       // 'slay'

    private const uint ScopeGlobal = 0x676C6F62;                // 'glob'
    private const uint ScopeOutput = 0x6F757470;                // 'outp'
    private const uint ScopeInput = 0x696E7074;                 // 'inpt'

    private const uint EncodingUtf8 = 0x08000100;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyDataSize(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, out uint dataSize);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier,
        ref uint dataSize, IntPtr data);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectSetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier,
        uint dataSize, ref uint data);

    // C 的 Boolean 是 1 字节；不显式声明会被按 4 字节封送，返回值不可靠。
    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, long bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    public List<object> ListOutputs() => ListDevices(ScopeOutput);

    public List<object> ListInputs() => ListDevices(ScopeInput);

    public string DefaultOutputId() => DefaultId(SelectorDefaultOutput, ScopeOutput);

    public string DefaultInputId() => DefaultId(SelectorDefaultInput, ScopeInput);

    /// <summary>把 <paramref name="uid"/> 对应设备设为默认输出/输入。</summary>
    public void SetDefault(string uid, bool capture)
    {
        var deviceId = FindDeviceByUid(uid)
            ?? throw new InvalidOperationException($"未找到音频设备：{uid}");
        var address = new PropertyAddress
        {
            Selector = capture ? SelectorDefaultInput : SelectorDefaultOutput,
            Scope = ScopeGlobal,
            Element = 0,
        };
        var value = deviceId;
        var status = AudioObjectSetPropertyData(
            AudioObjectSystemObject, ref address, 0, IntPtr.Zero, sizeof(uint), ref value);
        if (status != 0)
            throw new InvalidOperationException($"设置默认音频设备失败（OSStatus {status}）");
    }

    private static List<object> ListDevices(uint scope)
    {
        var devices = GetDeviceIds();
        var result = new List<object>();
        foreach (var device in devices)
        {
            if (ChannelCount(device, scope) <= 0) continue;
            var uid = ReadStringProperty(device, SelectorDeviceUID, ScopeGlobal);
            var name = ReadStringProperty(device, SelectorDeviceName, ScopeGlobal);
            if (string.IsNullOrEmpty(uid)) continue;
            result.Add(new
            {
                id = uid,
                name = string.IsNullOrEmpty(name) ? uid : name,
                // macOS 没有 Windows 的 Active/Disabled/Unplugged 三态；
                // 能出现在设备列表里即是可用设备。
                state = "active",
            });
        }
        return result;
    }

    private static uint[] GetDeviceIds()
    {
        var address = new PropertyAddress { Selector = SelectorDevices, Scope = ScopeGlobal, Element = 0 };
        var status = AudioObjectGetPropertyDataSize(
            AudioObjectSystemObject, ref address, 0, IntPtr.Zero, out var size);
        if (status != 0 || size == 0) return [];
        var count = (int)(size / sizeof(uint));
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(
                    AudioObjectSystemObject, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                return [];
            var result = new uint[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = (uint)Marshal.ReadInt32(buffer, i * sizeof(uint));
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint? FindDeviceByUid(string uid)
    {
        foreach (var device in GetDeviceIds())
        {
            if (ReadStringProperty(device, SelectorDeviceUID, ScopeGlobal) == uid) return device;
        }
        return null;
    }

    private static string DefaultId(uint selector, uint scope)
    {
        var address = new PropertyAddress { Selector = selector, Scope = ScopeGlobal, Element = 0 };
        var size = (uint)sizeof(uint);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(
                    AudioObjectSystemObject, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                return string.Empty;
            var device = (uint)Marshal.ReadInt32(buffer);
            if (device == 0) return string.Empty;
            var uid = ReadStringProperty(device, SelectorDeviceUID, ScopeGlobal);
            if (!string.IsNullOrEmpty(uid)) return uid;
            // 兜底：拿不到 UID 时至少不返回空
            return ReadStringProperty(device, SelectorDeviceName, ScopeGlobal) is { Length: > 0 } name
                ? name
                : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>读取该设备在指定方向的通道数（0 表示该方向不可用）。</summary>
    private static int ChannelCount(uint deviceId, uint scope)
    {
        var address = new PropertyAddress { Selector = SelectorStreamConfig, Scope = scope, Element = 0 };
        if (AudioObjectGetPropertyDataSize(deviceId, ref address, 0, IntPtr.Zero, out var size) != 0 || size < 4)
            return 0;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                return 0;
            // AudioBufferList { UInt32 mNumberBuffers; AudioBuffer[] {...} }
            // AudioBuffer 内含指针（8 字节对齐）→ mBuffers 实际从偏移 8 开始，
            // 单个 AudioBuffer 占 16 字节（channels/dataByteSize/data 指针）。
            var buffers = Marshal.ReadInt32(buffer);
            var channels = 0;
            for (var i = 0; i < buffers; i++)
            {
                channels += Marshal.ReadInt32(buffer, 8 + i * 16);
            }
            return channels;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadStringProperty(uint deviceId, uint selector, uint scope)
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

    public void Dispose()
    {
        // CoreAudio HAL 无长驻句柄需要释放（属性按需查询）。
    }
}
