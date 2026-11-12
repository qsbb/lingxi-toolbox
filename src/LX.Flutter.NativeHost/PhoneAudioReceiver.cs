using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LingXi.Flutter.NativeHost;

/// <summary>
/// Toolbox-owned LAN receiver for the Android lx-pcm-v1 protocol. Lifetime is
/// exactly the NativeHost lifetime; there is no child process or auto-start.
/// PCM is written only to the dedicated LingXi virtual-microphone render endpoint;
/// the paired capture endpoint exposes it to other Windows applications as a microphone.
/// </summary>
internal sealed class PhoneAudioReceiver : IDisposable
{
    private const int Port = 8790;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _singleClient = new(1, 1);
    private readonly string _token;
    private readonly int _configuredPort;
    private readonly IPAddress _bindAddress;
    private readonly bool _enforceWindows;
    private readonly Func<IReadOnlyList<WaveDevice>> _renderDevices;
    private readonly Func<IReadOnlyList<string>> _captureDeviceNames;
    private readonly Func<int, int, IPhoneAudioOutput> _outputFactory;
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _acceptLoop;
    private string _connection = "未连接";
    private bool _clientConnected;
    private string? _lastError;
    private long _frames;
    private int _virtualRenderDeviceId = -1;

    internal PhoneAudioReceiver()
        : this(LoadOrCreateToken(), Port, IPAddress.Any, true,
            VirtualMicRenderWriter.Devices, CaptureDeviceNames,
            (deviceId, sampleRate) => new VirtualMicRenderWriter(deviceId, sampleRate))
    {
    }

    internal PhoneAudioReceiver(
        string token,
        int port,
        IPAddress bindAddress,
        bool enforceWindows,
        Func<IReadOnlyList<WaveDevice>> renderDevices,
        Func<IReadOnlyList<string>> captureDeviceNames,
        Func<int, int, IPhoneAudioOutput> outputFactory)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("配对码不能为空", nameof(token));
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _token = token;
        _configuredPort = port;
        _bindAddress = bindAddress;
        _enforceWindows = enforceWindows;
        _renderDevices = renderDevices;
        _captureDeviceNames = captureDeviceNames;
        _outputFactory = outputFactory;
    }

    internal object State()
    {
        lock (_gate)
        {
            var addresses = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x))
                .Select(x => x.ToString()).Distinct().ToArray();
            var renderEndpoint = PhoneAudioRoute.ResolveRenderEndpoint(_renderDevices());
            var captureEndpoint = PhoneAudioRoute.ResolveCaptureEndpoint(_captureDeviceNames());
            var port = _listener?.LocalEndpoint is IPEndPoint active ? active.Port : _configuredPort;
            return new
            {
                supported = !_enforceWindows || OperatingSystem.IsWindows(),
                running = _listener is not null,
                connected = _clientConnected,
                connection = _connection,
                port,
                token = _token,
                endpoint = $"ws://{(addresses.FirstOrDefault() ?? "<本机IP>")}:{port}",
                frames = Interlocked.Read(ref _frames),
                lastError = _lastError,
                virtualMicRenderEndpoint = renderEndpoint?.Name,
                virtualMicCaptureEndpoint = captureEndpoint,
                virtualMicDriverPresent = renderEndpoint is not null && captureEndpoint is not null,
            };
        }
    }

    internal object Start()
    {
        if (_enforceWindows && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 接收端仅支持 Windows");
        var endpoint = PhoneAudioRoute.ResolveRenderEndpoint(_renderDevices());
        var captureEndpoint = PhoneAudioRoute.ResolveCaptureEndpoint(_captureDeviceNames());
        if (endpoint is null || captureEndpoint is null)
            throw new InvalidOperationException("未检测到完整的凌溪虚拟麦克风播放/录音端点；为避免音频误播到扬声器，接收端未启动");
        lock (_gate)
        {
            if (_listener is not null) return State();
            var listener = new TcpListener(_bindAddress, _configuredPort);
            listener.Start(8);
            _virtualRenderDeviceId = endpoint.Id;
            _lastError = null;
            _connection = "等待手机连接";
            _clientConnected = false;
            _stop = new CancellationTokenSource();
            _listener = listener;
            _acceptLoop = AcceptLoop(listener, _stop.Token);
            HostLog.Info($"phone receiver started port={((IPEndPoint)listener.LocalEndpoint).Port} virtualMicRenderDevice={endpoint.Id}");
            return State();
        }
    }

    internal object Stop()
    {
        lock (_gate)
        {
            _stop?.Cancel();
            _listener?.Stop();
            _listener = null;
            _connection = "未连接";
            _clientConnected = false;
            _virtualRenderDeviceId = -1;
            _stop?.Dispose();
            _stop = null;
            HostLog.Info("phone receiver stopped");
            return State();
        }
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClient(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_gate) _lastError = PublicError(ex);
            HostLog.Error("phone receiver listener failed", ex);
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            if (!_singleClient.Wait(0)) return;
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                using var handshakeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshakeDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                await Upgrade(stream, handshakeDeadline.Token).ConfigureAwait(false);
                using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = true,
                    KeepAliveInterval = TimeSpan.FromSeconds(20),
                });
                using var helloDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                helloDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                var helloBytes = await ReceiveMessage(socket, helloDeadline.Token).ConfigureAwait(false);
                using var helloDoc = JsonDocument.Parse(helloBytes);
                var hello = helloDoc.RootElement;
                var suppliedToken = hello.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
                var protocol = hello.TryGetProperty("protocol", out var p) ? p.GetString() : null;
                var sampleRate = hello.TryGetProperty("sample_rate", out var rate) && rate.TryGetInt32(out var parsedRate)
                    ? parsedRate : 0;
                if (protocol != "lx-pcm-v1" || !FixedEquals(_token, suppliedToken))
                {
                    await SendJson(socket, new { type = "error", error = "配对码或协议无效" }, cancellationToken).ConfigureAwait(false);
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                if (sampleRate is < 8000 or > 96000)
                {
                    await SendJson(socket, new { type = "error", error = "音频采样率无效" }, cancellationToken).ConfigureAwait(false);
                    await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "invalid sample rate", CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                var deviceName = hello.TryGetProperty("device_name", out var n) ? n.GetString() ?? "Android" : "Android";
                var deviceId = Volatile.Read(ref _virtualRenderDeviceId);
                using var player = _outputFactory(deviceId, PhonePcmEncoder.OutputRate);
                lock (_gate)
                {
                    _connection = $"{Clip(deviceName, 64)} · {sampleRate} → {PhonePcmEncoder.OutputRate} Hz";
                    _clientConnected = true;
                }
                await SendJson(socket, new { type = "ready", sample_rate = sampleRate, output_sample_rate = PhonePcmEncoder.OutputRate }, cancellationToken).ConfigureAwait(false);
                var buffer = new byte[1024 * 1024];
                while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(
                            result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            result.CloseStatusDescription,
                            CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    if (result.MessageType != WebSocketMessageType.Binary) continue;
                    using var message = new MemoryStream();
                    message.Write(buffer, 0, result.Count);
                    while (!result.EndOfMessage)
                    {
                        result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (message.Length + result.Count > 1024 * 1024) throw new InvalidDataException("音频帧过大");
                        message.Write(buffer, 0, result.Count);
                    }
                    var floats = PhonePcmEncoder.DecodeFrame(message.GetBuffer().AsSpan(0, checked((int)message.Length)), sampleRate);
                    player.Write(floats, sampleRate);
                    Interlocked.Increment(ref _frames);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex) { HostLog.Info($"phone receiver client disconnected: {ex.WebSocketErrorCode}"); }
            catch (Exception ex)
            {
                lock (_gate) _lastError = PublicError(ex);
                HostLog.Error("phone receiver client failed", ex);
            }
            finally
            {
                lock (_gate)
                {
                    _clientConnected = false;
                    _connection = _listener is null ? "未连接" : "等待手机连接";
                }
                _singleClient.Release();
            }
        }
    }

    /// <summary>Exposes the capture-endpoint enumeration used by the read-only probe.</summary>
    internal static IReadOnlyList<string> CaptureDeviceNamesForProbe() => CaptureDeviceNames();

    private static IReadOnlyList<string> CaptureDeviceNames()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        var count = waveInGetNumDevs();
        var names = new List<string>();
        for (uint i = 0; i < count; i++)
        {
            if (waveInGetDevCaps((IntPtr)i, out var caps, (uint)Marshal.SizeOf<WaveInCaps>()) == 0)
                names.Add(caps.Name.TrimEnd('\0'));
        }
        return names;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps { public ushort ManufacturerId, ProductId; public uint DriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int waveInGetDevCaps(IntPtr id, out WaveInCaps caps, uint size);

    private static async Task Upgrade(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(1024);
        var one = new byte[1];
        while (bytes.Count < 8192)
        {
            var count = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new IOException("连接在 WebSocket 握手前关闭");
            bytes.Add(one[0]);
            var n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
        }
        var request = Encoding.ASCII.GetString(bytes.ToArray());
        if (!request.StartsWith("GET / HTTP/1.1\r\n", StringComparison.OrdinalIgnoreCase) ||
            !request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase) ||
            !request.Contains("Connection: Upgrade", StringComparison.OrdinalIgnoreCase) ||
            !request.Contains("Sec-WebSocket-Version: 13", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WebSocket 握手无效");
        var keyLine = request.Split("\r\n").FirstOrDefault(x => x.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
        var key = keyLine?.Split(':', 2)[1].Trim() ?? throw new InvalidDataException("WebSocket key 缺失");
        byte[] decodedKey;
        try { decodedKey = Convert.FromBase64String(key); }
        catch (FormatException) { throw new InvalidDataException("WebSocket key 格式无效"); }
        if (decodedKey.Length != 16) throw new InvalidDataException("WebSocket key 长度无效");
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var reply = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), cancellationToken).ConfigureAwait(false);
        return;
    }

    private static async Task<byte[]> ReceiveMessage(WebSocket socket, CancellationToken cancellationToken)
    {
        var bytes = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("手机在握手时断开");
            if (ms.Length + result.Count > 4096) throw new InvalidDataException("握手内容过大");
            ms.Write(bytes, 0, result.Count);
        } while (!result.EndOfMessage);
        return ms.ToArray();
    }

    private static Task SendJson(WebSocket socket, object value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static bool FixedEquals(string expected, string actual)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string LoadOrCreateToken()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LingXi");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "phone-receiver-token");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length is >= 24 and <= 128) return existing;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        File.WriteAllText(path, token, new UTF8Encoding(false));
        return token;
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
    private static string PublicError(Exception ex) => ex switch
    {
        SocketException => "接收端口 8790 无法监听，请检查端口占用和防火墙",
        UnauthorizedAccessException => "无法访问所选音频设备，请检查设备状态",
        _ => "接收端发生错误，请检查设备和网络后重试",
    };

    public void Dispose()
    {
        Stop();
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}

internal interface IPhoneAudioOutput : IDisposable
{
    void Write(float[] input, int inputRate);
}

internal sealed class VirtualMicRenderWriter : IPhoneAudioOutput
{
    private IntPtr _handle;
    private bool _disposed;
    private const int TargetChannels = 2;
    private const int WhdrDone = 0x00000001;

    internal VirtualMicRenderWriter(int id, int rate)
    {
        var format = new WaveFormat { FormatTag = 1, Channels = (ushort)TargetChannels, SamplesPerSec = (uint)rate,
            AvgBytesPerSec = (uint)(rate * TargetChannels * 2), BlockAlign = (ushort)(TargetChannels * 2), BitsPerSample = 16 };
        var result = waveOutOpen(out _handle, (uint)id, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0) throw new IOException($"打开凌溪虚拟麦克风 render 端点失败 ({result})");
    }

    internal static IReadOnlyList<WaveDevice> Devices()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<WaveDevice>();
        var count = waveOutGetNumDevs();
        var list = new List<WaveDevice>();
        for (uint i = 0; i < count; i++)
        {
            if (waveOutGetDevCaps((IntPtr)i, out var caps, (uint)Marshal.SizeOf<WaveCaps>()) == 0)
                list.Add(new WaveDevice((int)i, caps.Name.TrimEnd('\0')));
        }
        return list;
    }

    public void Write(float[] input, int inputRate)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VirtualMicRenderWriter));
        var pcm = PhonePcmEncoder.Encode(input, inputRate);
        var data = Marshal.AllocHGlobal(pcm.Length);
        var headerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        try
        {
            Marshal.Copy(pcm, 0, data, pcm.Length);
            var header = new WaveHeader { Data = data, BufferLength = (uint)pcm.Length };
            Marshal.StructureToPtr(header, headerPtr, false);
            Check(waveOutPrepareHeader(_handle, headerPtr, (uint)Marshal.SizeOf<WaveHeader>()));
            Check(waveOutWrite(_handle, headerPtr, (uint)Marshal.SizeOf<WaveHeader>()));
            var deadline = Environment.TickCount64 + 3000;
            while (Environment.TickCount64 < deadline)
            {
                header = Marshal.PtrToStructure<WaveHeader>(headerPtr);
                if ((header.Flags & WhdrDone) != 0) return;
                Thread.Sleep(2);
            }
            throw new TimeoutException("音频设备未及时消费 PCM 帧");
        }
        finally
        {
            try { waveOutUnprepareHeader(_handle, headerPtr, (uint)Marshal.SizeOf<WaveHeader>()); } catch { }
            Marshal.FreeHGlobal(headerPtr);
            Marshal.FreeHGlobal(data);
        }
    }

    private static void Check(int result) { if (result != 0) throw new IOException($"Windows 音频输出失败 ({result})"); }
    public void Dispose() { if (_disposed) return; _disposed = true; waveOutReset(_handle); waveOutClose(_handle); }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveCaps { public ushort ManufacturerId, ProductId; public uint DriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; public uint Support; }
    [StructLayout(LayoutKind.Sequential)] private struct WaveFormat { public ushort FormatTag, Channels; public uint SamplesPerSec, AvgBytesPerSec; public ushort BlockAlign, BitsPerSample, ExtraSize; }
    [StructLayout(LayoutKind.Sequential)] private struct WaveHeader { public IntPtr Data; public uint BufferLength, BytesRecorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern int waveOutGetDevCaps(IntPtr id, out WaveCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr handle, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr handle);
}

