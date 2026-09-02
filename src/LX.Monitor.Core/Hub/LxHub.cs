using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LingXi.Monitor.Core;

/// <summary>
/// LX Hub 本地收数端（开发文档 9.3）。
/// 严格实现 servermonitor 上报协议：POST /servermonitor/report（别名
/// /server-monitor/report），头 X-SM-Token，体为快照 JSON。
/// </summary>
public sealed class LxHub : IDisposable
{
    private const int MaxBodyBytes = 1_000_000;
    private const int MaxConcurrentHandlers = 32;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly HubOptions _options;
    private readonly SnapshotStore _store;
    private readonly Forwarder? _forwarder;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _handlerSlots = new(MaxConcurrentHandlers, MaxConcurrentHandlers);
    private readonly List<Task> _handlers = [];
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;
    private bool _started;

    /// <summary>实际监听端口（启动后可能因占用顺延）。</summary>
    public int Port { get; private set; }

    /// <summary>收到合法快照（已入库）。</summary>
    public event Action<SnapshotEnvelope>? SnapshotReceived;

    /// <summary>运行日志。</summary>
    public event Action<string>? Log;

    public LxHub(HubOptions options, SnapshotStore store)
    {
        _options = options;
        _store = store;
        Port = Math.Clamp(options.Port, 1024, 65535);
        if (options.EnableForward)
        {
            _forwarder = new Forwarder(options.ForwardUrl!, options.ForwardToken);
        }
    }

    /// <summary>本机回环地址，供同机 agent 使用。</summary>
    public string ReportUrl => $"http://127.0.0.1:{Port}/servermonitor/report";

    /// <summary>局域网地址；未成功绑定全网卡时回退为回环地址。</summary>
    public string LanReportUrl =>
        $"http://{(IsLanBound ? GetLanIpv4() : "127.0.0.1")}:{Port}/servermonitor/report";

    /// <summary>实际是否以全网卡前缀监听。</summary>
    public bool IsLanBound { get; private set; }

    /// <summary>
    /// 启动监听；端口被占用自动顺延（最多尝试 4 个端口）。重复调用是幂等的。
    /// BindLan 的 URLACL 被拒时回退为仅本机监听。
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;

            var lan = _options.BindLan;
            IsLanBound = false;
            HttpListener? listener = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                listener = new HttpListener();
                listener.Prefixes.Add(lan ? $"http://+:{Port}/" : $"http://127.0.0.1:{Port}/");
                try
                {
                    listener.Start();
                    IsLanBound = lan;
                    break;
                }
                catch (HttpListenerException ex) when (lan && ex.ErrorCode == 5)
                {
                    listener.Close();
                    listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                    try
                    {
                        listener.Start();
                        IsLanBound = false;
                        lan = false;
                        Log?.Invoke("全网卡绑定被拒，已回退为仅本机监听");
                        break;
                    }
                    catch
                    {
                        listener.Close();
                        listener = null;
                        throw;
                    }
                }
                catch (HttpListenerException) when (attempt < 3)
                {
                    listener.Close();
                    listener = null;
                    Port++;
                    Log?.Invoke($"端口 {Port - 1} 被占用，尝试 {Port}");
                }
            }

            if (listener is null || !listener.IsListening)
                throw new HttpListenerException((int)HttpStatusCode.ServiceUnavailable, "Hub listener could not start");

            _listener = listener;
            _cts = new CancellationTokenSource();
            _started = true;
            _acceptTask = AcceptLoopAsync(listener, _cts.Token);
            Log?.Invoke(lan
                ? $"LX Hub 已启动：http://<本机IP>:{Port}/servermonitor/report（局域网可上报）"
                : $"LX Hub 已启动：{ReportUrl}");
        }
    }

    private static string GetLanIpv4()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and
                                not NetworkInterfaceType.Tunnel and not NetworkInterfaceType.Ppp)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Select(address => address.ToString())
                .ToList();
            return candidates.FirstOrDefault(ip => ip.StartsWith("192.168.", StringComparison.Ordinal))
                ?? candidates.FirstOrDefault(ip => ip.StartsWith("10.", StringComparison.Ordinal))
                ?? candidates.FirstOrDefault()
                ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (!await _handlerSlots.WaitAsync(TimeSpan.Zero, token).ConfigureAwait(false))
            {
                await WriteAsync(context, 503, "{\"error\":\"busy\"}", CancellationToken.None);
                continue;
            }

            var task = HandleWithSlotAsync(context, token);
            lock (_gate) _handlers.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_gate) _handlers.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleWithSlotAsync(HttpListenerContext context, CancellationToken serverToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            timeout.CancelAfter(RequestTimeout);
            await HandleAsync(context, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { context.Response.Close(); } catch { }
        }
        finally
        {
            _handlerSlots.Release();
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (context.Request.HttpMethod == "GET" && path is "/health" or "/")
            {
                var healthToken = context.Request.Headers["X-SM-Token"];
                if (IsLanBound &&
                    (string.IsNullOrWhiteSpace(healthToken) ||
                     _options.Tokens.Count == 0 ||
                     !_options.Tokens.Contains(healthToken)))
                {
                    await WriteAsync(context, 401, "{\"error\":\"invalid token\"}", token);
                    return;
                }
                await WriteAsync(context, 200, "{\"ok\":true}", token);
                return;
            }

            var isReport = path is "/servermonitor/report" or "/server-monitor/report";
            if (context.Request.HttpMethod != "POST" || !isReport)
            {
                await WriteAsync(context, 404, "{\"error\":\"not found\"}", token);
                return;
            }

            var tokenHeader = context.Request.Headers["X-SM-Token"];
            if (_options.Tokens.Count > 0 &&
                (string.IsNullOrWhiteSpace(tokenHeader) || !_options.Tokens.Contains(tokenHeader)))
            {
                await WriteAsync(context, 401, "{\"error\":\"invalid token\"}", token);
                return;
            }

            if (context.Request.ContentLength64 > MaxBodyBytes)
            {
                await WriteAsync(context, 413, "{\"error\":\"payload too large\"}", token);
                return;
            }

            var body = await ReadBodyLimitedAsync(context.Request.InputStream, MaxBodyBytes, token);
            if (body is null)
            {
                await WriteAsync(context, 413, "{\"error\":\"payload too large\"}", token);
                return;
            }

            var snapshot = SnapshotJson.Parse(body);
            if (!SnapshotValidator.TryValidate(snapshot, DateTimeOffset.UtcNow, out var validationError))
            {
                await WriteAsync(context, 422, JsonSerializer.Serialize(new { error = validationError }), token);
                return;
            }

            var envelope = new SnapshotEnvelope(snapshot!, tokenHeader, DateTimeOffset.UtcNow);
            if (!_store.TryUpsert(envelope, out var stale))
            {
                await WriteAsync(context, stale ? 409 : 422, stale ? "{\"error\":\"stale snapshot\"}" : "{\"error\":\"invalid snapshot\"}", token);
                return;
            }

            foreach (var handler in SnapshotReceived?.GetInvocationList()
                         .OfType<Action<SnapshotEnvelope>>() ?? [])
            {
                try
                {
                    handler(envelope);
                }
                catch (Exception ex)
                {
                    Log?.Invoke("快照订阅处理失败：" + ex.GetType().Name);
                }
            }
            if (_forwarder is not null)
            {
                _ = _forwarder.ForwardAsync(snapshot!, CancellationToken.None);
            }

            await WriteAsync(context, 200, "{\"ok\":true}", token);
        }
        catch (OperationCanceledException)
        {
            try { context.Response.Close(); } catch { }
        }
        catch (Exception ex)
        {
            Log?.Invoke("处理上报失败：" + ex.GetType().Name);
            try { await WriteAsync(context, 500, "{\"error\":\"internal\"}", CancellationToken.None); } catch { }
        }
    }

    private static async Task<string?> ReadBodyLimitedAsync(Stream input, int maxBytes, CancellationToken token)
    {
        using var buffer = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(chunk.AsMemory(), token);
            if (count == 0) return Encoding.UTF8.GetString(buffer.ToArray());
            if (buffer.Length + count > maxBytes) return null;
            buffer.Write(chunk, 0, count);
        }
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, string json, CancellationToken token)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, token);
        context.Response.Close();
    }

    public void Dispose()
    {
        Task? accept;
        Task[] handlers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
            accept = _acceptTask;
            handlers = _handlers.ToArray();
            _listener = null;
            _cts = null;
            _acceptTask = null;
        }

        try { accept?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { Task.WaitAll(handlers, TimeSpan.FromSeconds(2)); } catch { }
        // Handler tasks have their own 15-second cancellation window and release
        // this semaphore in finally. Keep it alive after the synchronous dispose
        // timeout so a late task cannot throw ObjectDisposedException.
        _forwarder?.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal static class SnapshotValidator
{
    private const int MaxNameLength = 128;
    private const int MaxListLength = 128;
    private const int MaxTextLength = 512;

    public static bool TryValidate(Snapshot? snapshot, DateTimeOffset now, out string error)
    {
        if (snapshot is null) { error = "bad snapshot"; return false; }
        if (snapshot.AgentTs is long agentTs)
        {
            var currentMs = now.ToUnixTimeMilliseconds();
            if (agentTs < 0 || agentTs > currentMs + TimeSpan.FromMinutes(5).TotalMilliseconds)
            {
                error = "invalid agent timestamp";
                return false;
            }
        }
        if (snapshot.Version != 1) { error = "unsupported version"; return false; }
        if (string.IsNullOrWhiteSpace(snapshot.Name) && string.IsNullOrWhiteSpace(snapshot.Os?.Hostname))
        {
            error = "missing name";
            return false;
        }
        if (snapshot.Name.Length > MaxNameLength || snapshot.Os?.Hostname?.Length > MaxNameLength)
        {
            error = "name too long";
            return false;
        }
        if (snapshot.Gpus?.Count > MaxListLength ||
            snapshot.Disks?.Count > MaxListLength ||
            snapshot.Load?.Count > 16)
        {
            error = "too many items";
            return false;
        }
        if (!TextFieldsWithinLimit(snapshot)) { error = "text field too long"; return false; }
        if (!NumbersWithinRange(snapshot)) { error = "metric out of range"; return false; }
        error = string.Empty;
        return true;
    }

    private static bool TextFieldsWithinLimit(Snapshot snapshot) =>
        new[]
        {
            snapshot.Os?.Platform, snapshot.Os?.Distro, snapshot.Os?.Release, snapshot.Os?.Arch,
            snapshot.Os?.Hostname, snapshot.Cpu?.Model, snapshot.Net?.Iface,
        }.All(value => value is null || value.Length <= MaxTextLength) &&
        (snapshot.Gpus ?? []).All(gpu => gpu.Model is null || gpu.Model.Length <= MaxTextLength) &&
        (snapshot.Disks ?? []).All(disk => disk.Mount is null || disk.Mount.Length <= MaxTextLength);

    private static bool NumbersWithinRange(Snapshot snapshot)
    {
        static bool Percent(double? value) => value is null || (double.IsFinite(value.Value) && value.Value is >= 0 and <= 100);
        static bool NonNegative(double? value) => value is null || (double.IsFinite(value.Value) && value.Value >= 0);
        return Percent(snapshot.Cpu?.Usage) && Percent(snapshot.Cpu?.Temp) && Percent(snapshot.Cpu?.Power) &&
               (snapshot.Cpu?.Cores is null || snapshot.Cpu.Cores is > 0 and <= 4096) &&
               (snapshot.Gpus ?? []).All(gpu => Percent(gpu.Usage) && NonNegative(gpu.Temp) && NonNegative(gpu.Power) && NonNegative(gpu.MemUsed) && NonNegative(gpu.MemTotal)) &&
               NonNegative(snapshot.Mem?.Used) && NonNegative(snapshot.Mem?.Total) && NonNegative(snapshot.Mem?.SwapUsed) && NonNegative(snapshot.Mem?.SwapTotal) &&
               NonNegative(snapshot.Net?.RxSec) && NonNegative(snapshot.Net?.TxSec) && NonNegative(snapshot.Net?.RxTotal) && NonNegative(snapshot.Net?.TxTotal) &&
               (snapshot.Load ?? []).All(value => double.IsFinite(value) && value >= 0) &&
               (snapshot.Disks ?? []).All(disk => NonNegative(disk.Used) && NonNegative(disk.Total)) &&
               NonNegative(snapshot.Os?.Uptime);
    }
}
