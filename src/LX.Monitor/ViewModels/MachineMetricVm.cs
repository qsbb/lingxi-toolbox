using LingXi.Monitor.Core;

namespace LingXi.Monitor.ViewModels;

/// <summary>
/// 详情悬浮窗的磁盘指标行。每次快照整表重建实例（绑定随 Items 更新），无需 INPC。
/// </summary>
public sealed class DiskMetricVm
{
    public DiskMetricVm(SnapshotDisk disk)
    {
        Mount = string.IsNullOrWhiteSpace(disk.Mount) ? "未知挂载点" : disk.Mount!;
        var total = disk.Total ?? 0;
        var used = disk.Used ?? 0;
        if (total > 0)
        {
            UsageText = $"{used:F1} / {total:F1} GiB";
            Percent = Math.Clamp(used / total * 100, 0, 100);
        }
        else
        {
            UsageText = used > 0 ? $"{used:F1} GiB" : "—";
            Percent = 0;
        }
    }

    /// <summary>挂载点。</summary>
    public string Mount { get; }

    /// <summary>用量 / 总量文案。</summary>
    public string UsageText { get; }

    /// <summary>使用率 0-100（LxMetricBar）。</summary>
    public double Percent { get; }
}

/// <summary>
/// 详情悬浮窗的网卡速率行（servermonitor 契约单网卡快照；每次整表重建）。
/// </summary>
public sealed class NetMetricVm
{
    public NetMetricVm(string? iface, double? rxSec, double? txSec)
    {
        Iface = string.IsNullOrWhiteSpace(iface) ? "默认网卡" : iface!;
        DownText = FormatRate(rxSec, "↓");
        UpText = FormatRate(txSec, "↑");
    }

    /// <summary>网卡名。</summary>
    public string Iface { get; }

    /// <summary>下行速率文案。</summary>
    public string DownText { get; }

    /// <summary>上行速率文案。</summary>
    public string UpText { get; }

    private static string FormatRate(double? value, string arrow) =>
        value is { } rate ? $"{arrow} {rate:F1} MiB/s" : $"{arrow} —";
}
