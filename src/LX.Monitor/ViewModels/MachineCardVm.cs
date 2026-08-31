using CommunityToolkit.Mvvm.ComponentModel;
using LingXi.Ui.Controls;

namespace LingXi.Monitor.ViewModels;

/// <summary>机器卡片 VM（开发文档 9.5 + 机器档案管理）。</summary>
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

    /// <summary>是否收到过快照（区分"未上报档案机器"与"离线机器"）。</summary>
    public bool HasEverReported { get; private set; }

    /// <summary>卡片标题：有别名用别名，否则原机器名。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;

    /// <summary>是否设置了别名（决定是否在卡片上以小字展示原机器名）。</summary>
    public bool HasAlias => !string.IsNullOrWhiteSpace(Alias);

    /// <summary>菜单文案：置顶 / 取消置顶。</summary>
    public string PinMenuText => IsPinned ? "取消置顶" : "置顶";

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
}
