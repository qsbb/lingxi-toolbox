namespace LingXi.Monitor.Core;

/// <summary>LX Hub 配置（默认值对齐 servermonitor config.example.yaml）。</summary>
public sealed class HubOptions
{
    /// <summary>监听端口（默认 2536，与官方 install 脚本示例一致；被占用自动顺延）。</summary>
    public int Port { get; set; } = 2536;

    /// <summary>
    /// 绑定全网卡（http://+:port/，局域网 agent 可直连上报）。
    /// Windows 上通配符前缀需要 URLACL 权限，被拒时 Start 自动回退仅本机并给出 netsh 提示。
    /// 库级默认保守（false）；应用层 MonitorSettings.BindLan 默认开启。
    /// </summary>
    public bool BindLan { get; set; }

    /// <summary>允许的上报 token；空集合 = 接受任意 token（本地默认收数模式）。</summary>
    public HashSet<string> Tokens { get; set; } = new(StringComparer.Ordinal);

    /// <summary>离线判定阈值（对齐 offline_timeout: 30）。</summary>
    public TimeSpan OfflineTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>转发中继目标（Yunzai 完整 report URL）；null = 不转发。</summary>
    public string? ForwardUrl { get; set; }

    public string? ForwardToken { get; set; }

    public bool EnableForward => !string.IsNullOrWhiteSpace(ForwardUrl);
}
