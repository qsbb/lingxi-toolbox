using System.Runtime.InteropServices;

namespace LingXi.Audio;

/// <summary>
/// CoreAudio 纯 COM 互操作实现。
/// 移植自 QuickAudioSwitch/AudioDeviceService.cs（255 行原样保留，仅接口化 + DataFlow 公开）：
/// - 构造用 Type.GetTypeFromCLSID 激活 MMDeviceEnumerator（绕开 .NET 8 CoClass 激活的坑）；
/// - SetDefault 走未公开接口 IPolicyConfig 的三角色循环（Windows 编程切默认设备的唯一事实手段）；
/// - IMMNotificationClient 回调 → DevicesChanged 事件（热插拔感知）。
/// </summary>
public sealed class AudioDeviceService : IAudioEndpointService
{
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly NotificationClient _notificationClient;
    private bool _disposed;

    public event EventHandler? DevicesChanged;

    public AudioDeviceService()
    {
        // Create the native MMDeviceEnumerator through its CLSID. This avoids a
        // fragile CoClass cast that can fail on some .NET 8 COM activation paths.
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var nativeEnumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)
            ?? throw new InvalidOperationException("无法创建 Windows 音频设备枚举器。");
        _enumerator = (IMMDeviceEnumerator)nativeEnumerator;
        _notificationClient = new NotificationClient(() => DevicesChanged?.Invoke(this, EventArgs.Empty));
        var hr = _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public IReadOnlyList<AudioEndpoint> GetDevices(DataFlow flow, bool activeOnly = false)
    {
        var mask = activeOnly ? DeviceState.Active : DeviceState.All;
        var endpoints = new List<AudioEndpoint>();
        IMMDeviceCollection? collection = null;
        try
        {
            var hr = _enumerator.EnumAudioEndpoints(flow, mask, out collection);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            hr = collection.GetCount(out var count);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            for (uint i = 0; i < count; i++)
            {
                hr = collection.Item(i, out var device);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                try
                {
                    hr = device.GetId(out var id);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                    hr = device.GetState(out var state);
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                    endpoints.Add(new AudioEndpoint(id, ReadFriendlyName(device) ?? "未命名设备", ToState(state)));
                }
                finally { Marshal.ReleaseComObject(device); }
            }
        }
        finally { if (collection is not null) Marshal.ReleaseComObject(collection); }
        return endpoints;
    }

    public string? GetDefaultId(DataFlow flow)
    {
        IMMDevice? device = null;
        try
        {
            var hr = _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia, out device);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            hr = device.GetId(out var id);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return id;
        }
        catch (COMException) { return null; }
        finally { if (device is not null) Marshal.ReleaseComObject(device); }
    }

    public void SetDefault(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
            {
                var hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            device.OpenPropertyStore(StorageMode.Read, out store);
            var key = new PropertyKey(DevicePropertyKeys.DeviceFriendlyName, 14);
            store.GetValue(ref key, out var value);
            try { return value.GetString(); }
            finally { value.Clear(); }
        }
        catch (COMException) { return null; }
        finally { if (store is not null) Marshal.ReleaseComObject(store); }
    }

    private static AudioDeviceState ToState(DeviceState state) => state switch
    {
        DeviceState.Active => AudioDeviceState.Active,
        DeviceState.Unplugged => AudioDeviceState.Unplugged,
        DeviceState.Disabled => AudioDeviceState.Disabled,
        DeviceState.NotPresent => AudioDeviceState.NotPresent,
        _ => AudioDeviceState.Unknown
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
        Marshal.ReleaseComObject(_enumerator);
        GC.SuppressFinalize(this);
    }

    private sealed class NotificationClient(Action changed) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed();
        public void OnDeviceAdded(string deviceId) => changed();
        public void OnDeviceRemoved(string deviceId) => changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId) => changed();
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }

    private static class DevicePropertyKeys
    {
        public static readonly Guid DeviceFriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    }
}

internal static class PropVariantExtensions
{
    public static string? GetString(this PropVariant value)
    {
        if (value.VariantType == 31 || value.VariantType == 30)
            return Marshal.PtrToStringUni(value.PointerValue);
        return null;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid formatId, uint propertyId)
{
    public Guid FormatId = formatId;
    public uint PropertyId = propertyId;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort VariantType;
    [FieldOffset(8)] public IntPtr PointerValue;

    public void Clear() => PropVariantClear(ref this);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant propVariant);
}

[Flags]
internal enum DeviceState : uint
{
    Active = 0x1,
    Disabled = 0x2,
    NotPresent = 0x4,
    Unplugged = 0x8,
    All = Active | Disabled | NotPresent | Unplugged
}

internal enum Role { Console = 0, Multimedia = 1, Communications = 2 }
internal enum StorageMode { Read = 0 }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(DataFlow dataFlow, DeviceState stateMask, [MarshalAs(UnmanagedType.Interface)] out IMMDeviceCollection devices);
    int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice endpoint);
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
    int RegisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
    int UnregisterEndpointNotificationCallback([MarshalAs(UnmanagedType.Interface)] IMMNotificationClient client);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator { }

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    int GetCount(out uint count);
    int Item(uint index, [MarshalAs(UnmanagedType.Interface)] out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr interfacePointer);
    int OpenPropertyStore(StorageMode access, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore properties);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetState(out DeviceState state);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    int GetCount(out uint count);
    int GetAt(uint index, out PropertyKey key);
    int GetValue(ref PropertyKey key, out PropVariant value);
    int SetValue(ref PropertyKey key, ref PropVariant value);
    int Commit();
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceState newState);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnDefaultDeviceChanged(DataFlow flow, Role role, [MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void OnPropertyValueChanged(string deviceId, PropertyKey key);
}

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class PolicyConfigClient { }

// 未公开 COM 接口：vtable 布局与 SoundSwitch 同源（风险与对策见开发文档 15 章）
[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    int GetMixFormat(IntPtr deviceId, IntPtr format);
    int GetDeviceFormat(IntPtr deviceId, int defaultFormat, IntPtr format);
    int ResetDeviceFormat(IntPtr deviceId);
    int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    int GetProcessingPeriod(IntPtr deviceId, int defaultPeriod, IntPtr period);
    int SetProcessingPeriod(IntPtr deviceId, IntPtr period);
    int GetShareMode(IntPtr deviceId, IntPtr mode);
    int SetShareMode(IntPtr deviceId, IntPtr mode);
    int GetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
    int SetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
    int SetEndpointVisibility(IntPtr deviceId, int visible);
}
