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
            var json = JsonSerializer.Serialize(snapshot, SnapshotJson.Options);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _target.Url) { Content = content };
            request.Headers.TryAddWithoutValidation("X-SM-Token", _target.Token);
            request.Headers.TryAddWithoutValidation("User-Agent", "lingxi-toolbox/1.0");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(Math.Max(1000, _target.TimeoutMs));

            using var response = await Http.SendAsync(request, cts.Token);
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                Reported?.Invoke(sw.Elapsed);
                Log?.Invoke($"[{_target.Url}] 上报成功 ({sw.Elapsed.TotalMilliseconds:F0}ms)");
                return (true, sw.Elapsed);
            }

            Log?.Invoke($"[{_target.Url}] 上报失败 HTTP {(int)response.StatusCode}");
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

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
