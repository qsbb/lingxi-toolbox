namespace LingXi.Monitor.Models;

/// <summary>
/// 机器本地档案（settings.json → modules.lx.monitor.machineProfiles）。
/// Name = 快照机器名（唯一键）；快照未上报的档案机器显示为"未上报"。
/// </summary>
public sealed class MachineProfile
{
    /// <summary>快照机器名（唯一键，非空）。</summary>
    public string Name { get; set; } = "";

    /// <summary>友好别名；空 = 显示原名称。</summary>
    public string Alias { get; set; } = "";

    /// <summary>置顶（排序列表最前）。</summary>
    public bool Pinned { get; set; }

    /// <summary>隐藏（不进机器网格，进"已隐藏机器"折叠区可恢复）。</summary>
    public bool Hidden { get; set; }
}
