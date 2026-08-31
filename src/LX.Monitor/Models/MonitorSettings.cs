using LingXi.Monitor.Core;

namespace LingXi.Monitor.Models;

/// <summary>lx.monitor 设置段（开发文档 9.7）。</summary>
public sealed class MonitorSettings
{
    public int HubPort { get; set; } = 2536;

    /// <summary>Hub 绑定全网卡（局域网 agent 可直连上报；无 URLACL 权限时自动回退仅本机）。</summary>
    public bool BindLan { get; set; } = true;

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

    /// <summary>上报目标列表：本机指标按 servermonitor 协议上报给任意服务器（双向监控，开发文档 9.8）。</summary>
    public List<ReporterTarget> Reporters { get; set; } = [];

    /// <summary>机器本地档案（Name 唯一键；快照机器 ∪ 档案机器合并显示，见 DashboardViewModel）。</summary>
    public List<MachineProfile> MachineProfiles { get; set; } = [];

    public AlertRules Alerts { get; set; } = new();
}
