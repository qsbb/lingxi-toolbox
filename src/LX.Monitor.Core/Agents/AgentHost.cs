using System.Diagnostics;

namespace LingXi.Monitor.Core;

/// <summary>agent 托管参数（对齐官方 SM_* 环境变量，开发文档 9.2）。</summary>
public sealed class AgentOptions
{
    /// <summary>agent 可执行文件（pkg exe）路径；为空时用 NodePath + ScriptPath。</summary>
    public string? ExePath { get; set; }

    public string? NodePath { get; set; }

    public string? ScriptPath { get; set; }

    public string Name { get; set; } = Environment.MachineName;

    public string Token { get; set; } = "";

    public string ReportUrl { get; set; } = "";

    public int IntervalSec { get; set; } = 10;

    public int SlowIntervalSec { get; set; } = 30;
}

/// <summary>
/// 托管 servermonitor agent 子进程：SM_* 环境变量注入 + 崩溃退避重启（1s→2s→…→30s 封顶）。
/// agent 二进制不改：与 Yunzai 部署 100% 同源（开发文档 9.2）。
/// </summary>
public sealed class AgentHost : IDisposable
{
    private readonly AgentOptions _options;
    private Process? _process;
    private CancellationTokenSource? _cts;

    public event Action<string>? Log;

    public AgentHost(AgentOptions options) => _options = options;

    public bool IsRunning => _process is { HasExited: false };

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _ = RunWithRestartAsync(_cts.Token);
    }

    private async Task RunWithRestartAsync(CancellationToken token)
    {
        var backoffMs = 1000;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var psi = BuildStartInfo();
                _process = Process.Start(psi);
                Log?.Invoke($"agent 已启动 (pid={_process?.Id})");
                if (_process is not null)
                {
                    await _process.WaitForExitAsync(token);
                    Log?.Invoke($"agent 退出 (code={_process.ExitCode})");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log?.Invoke("agent 启动失败：" + ex.Message);
            }

            try
            {
                await Task.Delay(backoffMs, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoffMs = Math.Min(backoffMs * 2, 30_000);
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(_options.ExePath))
        {
            psi.FileName = _options.ExePath;
        }
        else
        {
            psi.FileName = _options.NodePath ?? "node";
            psi.ArgumentList.Add(_options.ScriptPath ?? "agent.mjs");
        }
        psi.Environment["SM_NAME"] = _options.Name;
        psi.Environment["SM_TOKEN"] = _options.Token;
        psi.Environment["SM_REPORT_URL"] = _options.ReportUrl;
        psi.Environment["SM_INTERVAL"] = _options.IntervalSec.ToString();
        psi.Environment["SM_SLOW_INTERVAL"] = _options.SlowIntervalSec.ToString();
        return psi;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 已退出/无权限
        }
    }

    public void Dispose() => Stop();
}
