using System.Net;
using System.Text;

namespace LingXi.Monitor.Core;

/// <summary>
/// LX Hub 本地收数端（开发文档 9.3）。
/// 严格实现 servermonitor 上报协议：
///   POST /servermonitor/report（别名 /server-monitor/report），头 X-SM-Token，体为快照 JSON。
/// 兼容承诺：任何官方 agent 版本上报都不得报错（附录 D）。
/// </summary>
public sealed class LxHub : IDisposable
{
    private readonly HubOptions _options;
    private readonly SnapshotStore _store;
    private readonly Forwarder? _forwarder;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

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
        Port = options.Port;
        if (options.EnableForward)
        {
            _forwarder = new Forwarder(options.ForwardUrl!, options.ForwardToken);
        }
    }

    public string ReportUrl => $"http://127.0.0.1:{Port}/servermonitor/report";

    /// <summary>启动监听；端口被占用自动顺延（最多尝试 4 个端口）。
    /// BindLan 时优先绑定全网卡（局域网 agent 可直连）；无 URLACL 权限自动回退仅本机。</summary>
    public void Start()
    {
        var lan = _options.BindLan;
        for (var attempt = 0; ; attempt++)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(lan ? $"http://+:{Port}/" : $"http://127.0.0.1:{Port}/");
            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException ex) when (lan && ex.ErrorCode == 5)
            {
                // 通配符前缀需要 URLACL：管理员执行一次
                // netsh http add urlacl url=http://+:{Port}/ user=Everyone
                Log?.Invoke($"全网卡绑定被拒（管理员执行 netsh http add urlacl url=http://+:{Port}/ user=Everyone 可解锁局域网上报），回退仅本机监听");
                lan = false;
            }
            catch (HttpListenerException) when (attempt < 3)
            {
                Log?.Invoke($"端口 {Port} 被占用，尝试 {Port + 1}");
                Port++;
            }
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log?.Invoke(lan
            ? $"LX Hub 已启动：http://<本机IP>:{Port}/servermonitor/report（局域网可上报）"
            : $"LX Hub 已启动：{ReportUrl}");
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener!;
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (token.IsCancellationRequested)
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
            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;

            // 健康检查（测试/探活用）
            if (context.Request.HttpMethod == "GET" && path is "/health" or "/")
            {
                await WriteAsync(context, 200, "{\"ok\":true}");
                return;
            }

            var isReport = path is "/servermonitor/report" or "/server-monitor/report";
            if (context.Request.HttpMethod != "POST" || !isReport)
            {
                await WriteAsync(context, 404, "{\"error\":\"not found\"}");
                return;
            }

            var token = context.Request.Headers["X-SM-Token"];
            if (_options.Tokens.Count > 0 &&
                (string.IsNullOrWhiteSpace(token) || !_options.Tokens.Contains(token)))
            {
                await WriteAsync(context, 401, "{\"error\":\"invalid token\"}");
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            if (body.Length > 1_000_000)
            {
                await WriteAsync(context, 413, "{\"error\":\"payload too large\"}");
                return;
            }

            var snapshot = SnapshotJson.Parse(body);
            if (snapshot is null)
            {
                await WriteAsync(context, 400, "{\"error\":\"bad snapshot\"}");
                return;
            }

            var envelope = new SnapshotEnvelope(snapshot, token, DateTimeOffset.Now);
            _store.Upsert(envelope);
            SnapshotReceived?.Invoke(envelope);

            if (_forwarder is not null)
            {
                _ = _forwarder.ForwardAsync(snapshot, token);
            }

            await WriteAsync(context, 200, "{\"ok\":true}");
        }
        catch (Exception ex)
        {
            Log?.Invoke("处理上报失败：" + ex.Message);
            try
            {
                await WriteAsync(context, 500, "{\"error\":\"internal\"}");
            }
            catch
            {
                // 连接已断
            }
        }
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // 已停止
        }
    }
}
