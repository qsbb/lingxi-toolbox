using LingXi.Monitor.Core;

namespace LingXi.Monitor.Models;

/// <summary>lx.monitor 设置段（开发文档 9.7）。</summary>
public sealed class MonitorSettings
{
    public int HubPort { get; set; } = 2536;

    public string HubToken { get; set; } = "";

    public int OfflineTimeoutSec { get; set; } = 30;

    /// <summary>采集引擎：托管官方 agent（引擎 A）。</summary>
    public bool AgentEnabled { get; set; }

    /// <summary>agent 可执行文件（pkg exe）路径；与 ScriptPath 二选一。</summary>
    public string? AgentExePath { get; set; }

    /// <summary>node 可执行文件路径（ScriptPath 模式）。</summary>
    public string? AgentNodePath { get; set; }

    /// <summary>agent.mjs 路径（ScriptPath 模式）。</summary>
    public string? AgentScriptPath { get; set; }

    /// <summary>本机上报名。</summary>
    public string AgentName { get; set; } = Environment.MachineName;

    public bool ForwardEnabled { get; set; }

    /// <summary>Yunzai 完整 report URL。</summary>
    public string? ForwardUrl { get; set; }

    public string? ForwardToken { get; set; }

    public AlertRules Alerts { get; set; } = new();
}
