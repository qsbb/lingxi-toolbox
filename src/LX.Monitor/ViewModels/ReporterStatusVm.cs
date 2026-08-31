using CommunityToolkit.Mvvm.ComponentModel;
using LingXi.Ui.Controls;

namespace LingXi.Monitor.ViewModels;

/// <summary>上报目标状态行 VM（双向监控页，开发文档 9.8）。</summary>
public partial class ReporterStatusVm : ObservableObject
{
    public string Url { get; }

    [ObservableProperty]
    private string _statusText = "上报中…";

    [ObservableProperty]
    private LxStatusLevel _level = LxStatusLevel.Info;

    [ObservableProperty]
    private string _detailText = "";

    public ReporterStatusVm(string url, bool ok, TimeSpan? elapsed, string? reason = null)
    {
        Url = url;
        Update(ok, elapsed, reason);
    }

    public void Update(bool ok, TimeSpan? elapsed, string? reason = null)
    {
        if (ok)
        {
            StatusText = "上报正常";
            Level = LxStatusLevel.Ok;
            DetailText = elapsed is { } e ? $"{e.TotalMilliseconds:F0}ms · {DateTime.Now:HH:mm:ss}" : DateTime.Now.ToString("HH:mm:ss");
        }
        else
        {
            StatusText = "上报失败";
            Level = LxStatusLevel.Bad;
            DetailText = string.IsNullOrWhiteSpace(reason) ? DateTime.Now.ToString("HH:mm:ss") : $"{reason} · {DateTime.Now:HH:mm:ss}";
        }
    }
}
