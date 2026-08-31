using CommunityToolkit.Mvvm.ComponentModel;
using LingXi.Ui.Controls;

namespace LingXi.Monitor.ViewModels;

/// <summary>机器卡片 VM（开发文档 9.5）。</summary>
public partial class MachineCardVm : ObservableObject
{
    public string Name { get; }

    [ObservableProperty]
    private bool _isOnline = true;

    [ObservableProperty]
    private string _statusText = "在线";

    [ObservableProperty]
    private LxStatusLevel _level = LxStatusLevel.Ok;

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

    public MachineCardVm(string name) => Name = name;

    public void SetOnline(bool online)
    {
        IsOnline = online;
        StatusText = online ? "在线" : "离线";
        Level = online ? LxStatusLevel.Ok : LxStatusLevel.Bad;
    }
}
