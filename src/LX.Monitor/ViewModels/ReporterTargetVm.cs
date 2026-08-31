using CommunityToolkit.Mvvm.ComponentModel;
using LingXi.Monitor.Core;

namespace LingXi.Monitor.ViewModels;

/// <summary>
/// 上报目标可视化管理行 VM（双向监控，开发文档 9.8）。
/// 直接包装 settings.Reporters 里的 ReporterTarget 源对象：开关等写回源实例，
/// 新增/编辑/删除后由宿主重建行集合并落盘。
/// </summary>
public sealed partial class ReporterTargetVm : ObservableObject
{
    private readonly DashboardViewModel _owner;
    private ReporterStatusVm? _status;

    public ReporterTargetVm(ReporterTarget source, DashboardViewModel owner)
    {
        Source = source;
        _owner = owner;
    }

    public ReporterTarget Source { get; }

    public string Url => Source.Url;

    /// <summary>Token 掩码：前 6 + 掩码 + 后 6；过短整体掩码，未填显示占位。</summary>
    public string TokenMasked => MaskToken(Source.Token);

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Source.Name))
            {
                parts.Add($"名称 {Source.Name}");
            }
            parts.Add($"token {TokenMasked}");
            parts.Add($"间隔 {Source.IntervalSec}s");
            parts.Add($"超时 {Source.TimeoutMs}ms");
            if (!Source.Enabled)
            {
                parts.Add("已停用");
            }
            return string.Join(" · ", parts);
        }
    }

    /// <summary>启用开关（写回源对象并由宿主保存 + 应用到运行中的上报端）。</summary>
    public bool Enabled
    {
        get => Source.Enabled;
        set
        {
            if (Source.Enabled == value)
            {
                return;
            }
            Source.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailText));
            _owner.SetTargetEnabled(this, value);
        }
    }

    /// <summary>最近一次上报结果（上报端运行时回写；未上报为 null，pill 隐藏）。</summary>
    public ReporterStatusVm? Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status is not null;

    /// <summary>删除二次确认：第一次点击进入确认态（按钮文字变化，3 秒未确认自动复位）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteButtonText))]
    private bool _confirmingDelete;

    public string DeleteButtonText => ConfirmingDelete ? "确认删除" : "删除";

    internal void AttachStatus(ReporterStatusVm? status) => Status = status;

    internal void RefreshDetail() => OnPropertyChanged(nameof(DetailText));

    internal static string MaskToken(string? token)
    {
        var t = token?.Trim() ?? "";
        if (t.Length == 0)
        {
            return "（未设置）";
        }
        if (t.Length <= 12)
        {
            return "••••••";
        }
        return $"{t[..6]}••••••{t[^6..]}";
    }
}
