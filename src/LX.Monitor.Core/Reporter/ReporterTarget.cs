namespace LingXi.Monitor.Core;

/// <summary>上报目标（servermonitor 协议兼容端点）。</summary>
public sealed class ReporterTarget
{
    /// <summary>完整 report URL（如 http://192.168.5.88:2536/servermonitor/report）。</summary>
    public string Url { get; set; } = "";

    public string Token { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>上报间隔（秒，最小 5，对齐官方 agent）。</summary>
    public int IntervalSec { get; set; } = 10;

    /// <summary>上报超时（毫秒，对齐官方 SM_TIMEOUT 默认）。</summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>本机对外显示名（空 = 机器名）。</summary>
    public string Name { get; set; } = "";
}
