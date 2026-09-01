using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LingXi.Monitor.Core;
using LingXi.Ui.Controls;

namespace LingXi.Monitor.ViewModels;

/// <summary>
/// 机器卡片 VM（开发文档 9.5 + 机器档案管理）。
/// 同时是"机器详情悬浮窗"的数据源：快照差量更新同一实例，悬浮窗打开期间内容实时刷新。
/// </summary>
public partial class MachineCardVm : ObservableObject
{
    public string Name { get; }

    /// <summary>所属仪表盘 VM（卡片菜单命令挂在仪表盘上，菜单经 PlacementTarget.DataContext 取到本卡）。</summary>
    public DashboardViewModel Owner { get; }

    [ObservableProperty]
    private bool _isOnline = true;

    [ObservableProperty]
    private string _statusText = "在线";

    [ObservableProperty]
    private LxStatusLevel _level = LxStatusLevel.Ok;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(HasAlias))]
    private string _alias = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinMenuText))]
    private bool _isPinned;

    [ObservableProperty]
    private string _osText = "";

    [ObservableProperty]
    private string _cpuText = "—";

    [ObservableProperty]
    private double _cpuUsage;

    /// <summary>CPU 温度（详情悬浮窗指标行；无传感器显示 "—"）。</summary>
    [ObservableProperty]
    private string _cpuTempText = "—";

    [ObservableProperty]
    private string _memText = "—";

    [ObservableProperty]
    private double _memUsage;

    [ObservableProperty]
    private string _netText = "";

    [ObservableProperty]
    private string _diskText = "";

    [ObservableProperty]
    private string _tempText = "";

    [ObservableProperty]
    private string _lastSeenText = "";

    /// <summary>平台 / 发行版 · 架构（详情悬浮窗概要）。</summary>
    [ObservableProperty]
    private string _platformText = "";

    /// <summary>主机名（详情悬浮窗概要）。</summary>
    [ObservableProperty]
    private string _hostText = "";

    /// <summary>上线时长（快照 uptime，详情悬浮窗概要）。</summary>
    [ObservableProperty]
    private string _uptimeText = "";

    /// <summary>是否收到过快照（区分"未上报档案机器"与"离线机器"；同时决定 ⋮ 菜单是否出现"删除"）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _hasEverReported;

    /// <summary>
    /// 是否本机（机器名与 Environment.MachineName 一致，建卡时由宿主 VM 判定一次）。
    /// 上报目标是全局配置（上报的是"本机"指标），详情悬浮窗据此决定是否显示"上报设置…"入口。
    /// </summary>
    public bool IsLocal { get; set; }

    /// <summary>详情悬浮窗每磁盘指标（每次快照整表重建，无需 INPC）。</summary>
    public ObservableCollection<DiskMetricVm> Disks { get; } = [];

    /// <summary>详情悬浮窗每网卡速率（契约单网卡行）。</summary>
    public ObservableCollection<NetMetricVm> Nets { get; } = [];

    /// <summary>卡片标题：有别名用别名，否则原机器名。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;

    /// <summary>是否设置了别名（决定是否在卡片上以小字展示原机器名）。</summary>
    public bool HasAlias => !string.IsNullOrWhiteSpace(Alias);

    /// <summary>菜单文案：置顶 / 取消置顶。</summary>
    public string PinMenuText => IsPinned ? "取消置顶" : "置顶";

    /// <summary>⋮ 菜单是否显示"删除"：仅未上报过的档案机器可删（上报过的删了会随下一包快照回来，走"隐藏"）。</summary>
    public bool CanDelete => !HasEverReported;

    public MachineCardVm(string name, DashboardViewModel owner)
    {
        Name = name;
        Owner = owner;
    }

    public void SetOnline(bool online)
    {
        if (online)
        {
            HasEverReported = true;
            IsOnline = true;
            StatusText = "在线";
            Level = LxStatusLevel.Ok;
            return;
        }
        if (HasEverReported)
        {
            IsOnline = false;
            StatusText = "离线";
            Level = LxStatusLevel.Bad;
        }
        // 从未上报过的档案机器保持"未上报"态，不被巡检翻成"离线"
    }

    /// <summary>档案里有、快照里没有的机器：显示为未上报。</summary>
    public void SetNotReported()
    {
        IsOnline = false;
        StatusText = "未上报";
        Level = LxStatusLevel.Info;
        LastSeenText = "尚无上报";
    }

    /// <summary>整表替换磁盘指标行（快照到达时调用；OnSnapshot 已 Marshal 到 UI 线程）。</summary>
    public void ReplaceDisks(IEnumerable<SnapshotDisk> disks)
    {
        Disks.Clear();
        foreach (var disk in disks)
        {
            Disks.Add(new DiskMetricVm(disk));
        }
    }

    /// <summary>整表替换网卡速率行（契约单网卡；UI 线程）。</summary>
    public void ReplaceNet(SnapshotNet? net)
    {
        Nets.Clear();
        if (net is not null)
        {
            Nets.Add(new NetMetricVm(net.Iface, net.RxSec, net.TxSec));
        }
    }
}
