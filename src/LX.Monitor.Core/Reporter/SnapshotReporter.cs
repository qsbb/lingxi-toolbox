using System.Text;
using System.Text.Json;

namespace LingXi.Monitor.Core;

/// <summary>
/// 快照上报器：定时采集本机指标 → POST 给 servermonitor 协议端点（X-SM-Token）。
/// 与 LX Hub（收数端）双向并存；失败静默退避，不影响本地任何功能。
/// </summary>
public sealed class SnapshotReporter : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ReporterTarget _target;
    private readonly SystemMetricsCollector _collector;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>上报结果日志（含时间戳/状态码/延迟）。</summary>
    public event Action<string>? Log;

    /// <summary>上报成功（含目标 URL 和耗时）。</summary>
    public event Action<TimeSpan>? Reported;

    /// <summary>独立 token 首次上报收到 202 待绑定（适配文档 3.2：需在 Yunzai 私聊执行绑定命令）。</summary>
    public event Action<string>? BindPending;

    public ReporterTarget Target => _target;

    public SnapshotReporter(ReporterTarget target, SystemMetricsCollector? collector = null)
    {
        _target = target;
        _collector = collector ?? new SystemMetricsCollector
        {
            MachineName = string.IsNullOrWhiteSpace(target.Name) ? Environment.MachineName : target.Name,
        };
    }

    /// <summary>启动定时上报循环（内部退避：失败时 1→2→…→30s 封顶）。</summary>
    public void Start()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _target.IntervalSec));
        _ = Task.Run(() => RunLoopAsync(interval, _cts.Token));
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken token)
    {
        var backoffMs = 1000;
        while (!token.IsCancellationRequested)
        {
            var (ok, elapsed) = await ReportOnceAsync();
            var delay = ok ? interval : TimeSpan.FromMilliseconds(backoffMs);
            backoffMs = ok ? 1000 : Math.Min(backoffMs * 2, 30_000);
            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>单次上报（手动触发或测试用）。</summary>
    public async Task<(bool Ok, TimeSpan Elapsed)> ReportOnceAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var snapshot = _collector.Collect();
            // 空快照防护：服务端对全空快照回 422（适配文档第 5 节）——直接跳过本轮不发送
            if (snapshot is null)
            {
                Log?.Invoke($"[{_target.Url}] 采集为空，跳过本轮上报（防止 422 空快照）");
                return (false, sw.Elapsed);
            }
            var json = JsonSerializer.Serialize(snapshot, SnapshotJson.Options);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _target.Url) { Content = content };
            request.Headers.TryAddWithoutValidation("X-SM-Token", _target.Token);
            request.Headers.TryAddWithoutValidation("User-Agent", "lingxi-toolbox/2.1");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(Math.Max(1000, _target.TimeoutMs));

            using var response = await Http.SendAsync(request, cts.Token);
            var status = (int)response.StatusCode;
            var body = await ReadBodyAsync(response, cts.Token);
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                // 契约：2xx 也必须返回 JSON 且 ok:true，空 body / 非 JSON 一律视为失败
                // ——宁可报错也不要"假成功"（服务端可能根本没入库）。
                if (!IsOkTrue(body))
                {
                    Log?.Invoke($"[{_target.Url}] 上报失败：HTTP {status} 但响应不是 ok:true（body 缺失或格式不符）");
                    return (false, sw.Elapsed);
                }

                Reported?.Invoke(sw.Elapsed);
                // 202 = 待绑定（未登记的独立 token 首次上报）
                if (status == 202 || IsPending(body))
                {
                    Log?.Invoke($"[{_target.Url}] 首次上报已受理（待绑定）：请在机器人主人私聊发送 #服务器状态待绑定 取回绑定命令");
                    BindPending?.Invoke(_target.Url);
                }
                else
                {
                    Log?.Invoke($"[{_target.Url}] 上报成功 ({sw.Elapsed.TotalMilliseconds:F0}ms)");
                }
                return (true, sw.Elapsed);
            }

            var msg = $"[{_target.Url}] 上报失败 HTTP {status}";
            // 错误码语义（对齐服务端 0.1.19+ 行为）
            msg += status switch
            {
                401 => "（token 无效：未绑定 / 格式不符 / 旧共享 token 已停用）",
                403 => "（name 与已有机器冲突，请改名）",
                413 => "（请求体超限：宿主全局 JSON 上限 100 KB 先生效，插件上限 256 KB）",
                422 => "（结构错 / 空快照 / 数值非法）",
                429 => "（限速或待绑定队列已满：单 IP 每分钟最多创建 10 条 pending）",
                503 => "（服务端上报入口已关闭：report_enabled=false）",
                _ => "",
            };
            var serverMsg = ExtractServerMessage(body);
            if (serverMsg.Length > 0) msg += $" · {serverMsg}";
            Log?.Invoke(msg);
            return (false, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            Log?.Invoke($"[{_target.Url}] 上报超时（{_target.TimeoutMs}ms）");
            return (false, sw.Elapsed);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[{_target.Url}] 上报异常: {ex.Message}");
            return (false, sw.Elapsed);
        }
    }

    /// <summary>读取响应体（上限 64 KiB，防止异常大响应拖垮上报循环）。</summary>
    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[8192];
            using var ms = new MemoryStream();
            var total = 0;
            while (total < 64 * 1024)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read == 0) break;
                ms.Write(buffer, 0, read);
                total += read;
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>契约判定：响应必须是 JSON 对象且 ok:true。</summary>
    internal static bool IsOkTrue(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>body 里是否标记 pending（202 待绑定）。</summary>
    internal static bool IsPending(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("pending", out var pending)
                && pending.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>抽出服务端的 msg 字段用于诊断（如 "empty snapshot"）。</summary>
    internal static string ExtractServerMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
            if (doc.RootElement.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String)
            {
                var text = msg.GetString() ?? string.Empty;
                return text.Length > 120 ? text[..120] : text;
            }
        }
        catch (JsonException)
        {
        }
        return string.Empty;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
