using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LingXi.Monitor.Core;
using LingXi.Monitor.Models;
using LingXi.Sdk;
using LingXi.Ui.Controls;

namespace LingXi.Monitor.ViewModels;

/// <summary>
/// 监控仪表盘 VM（开发文档 9.5 + 机器列表管理）：
/// - 订阅快照表，机器卡片差量更新；
/// - 快照机器 ∪ 档案机器（MachineProfile）合并显示；排序：置顶 → 有快照按名 → 未上报在后；
/// - 档案改动（别名/置顶/隐藏）即时写回 settings 并刷新。
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly SnapshotStore _store;
    private readonly MonitorSettings _settings;
    private readonly Action _saveSettings;
    private readonly ILxLog _log;
    private readonly Action? _reportersChanged;
    private readonly Dictionary<string, MachineCardVm> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MachineProfile> _profiles = new(StringComparer.Ordinal);

    public ObservableCollection<MachineCardVm> Machines { get; } = [];

    /// <summary>已隐藏机器（折叠区，可恢复显示）。</summary>
    public ObservableCollection<MachineCardVm> HiddenMachines { get; } = [];

    [ObservableProperty]
    private string _hubReportUrl = "";

    /// <summary>本机 LAN IP 形式的 report URL（添加机器引导卡用）。</summary>
    [ObservableProperty]
    private string _lanReportUrl = "";

    [ObservableProperty]
    private string _hubToken = "";

    /// <summary>官方 agent 一行部署命令模板（PowerShell）。</summary>
    [ObservableProperty]
    private string _deployCommand = "";

    [ObservableProperty]
    private bool _hasMachines;

    [ObservableProperty]
    private bool _hasReporters;

    [ObservableProperty]
    private bool _hasHiddenMachines;

    [ObservableProperty]
    private bool _guideExpanded;

    [ObservableProperty]
    private string _guideToggleText = "展开 ▾";

    [ObservableProperty]
    private bool _hiddenExpanded;

    [ObservableProperty]
    private string _hiddenToggleText = "展开 ▾";

    [ObservableProperty]
    private string _hiddenHeaderText = "已隐藏机器";

    /// <summary>输入对话框（title, 当前值） → 新值或 null=取消；由模块层接入 InputBoxWindow。</summary>
    public Func<string, string, string?>? Prompt { get; set; }

    /// <summary>上报目标编辑对话框：入参 null=新增（内部克隆展示），返回编辑结果或 null=取消；由模块层接入 ReporterEditorWindow。</summary>
    public Func<ReporterTarget?, ReporterTarget?>? EditReporter { get; set; }

    /// <summary>上报目标可视化管理行（与 settings.Reporters 源对象一一对应）。</summary>
    public ObservableCollection<ReporterTargetVm> ReporterTargets { get; } = [];

    [ObservableProperty]
    private bool _hasReporterTargets;

    public DashboardViewModel(SnapshotStore store, MonitorSettings settings, Action saveSettings, ILxLog log,
        Action? reportersChanged = null)
    {
        _store = store;
        _settings = settings;
        _saveSettings = saveSettings;
        _log = log;
        _reportersChanged = reportersChanged;

        // 档案加载（Name 唯一键；重复名保留首个，脏档案跳过）
        foreach (var profile in settings.MachineProfiles)
        {
            var key = profile.Name?.Trim() ?? "";
            if (key.Length == 0)
            {
                continue;
            }
            if (!_profiles.TryAdd(key, profile))
            {
                _log.Warn($"机器档案存在重复 Name，已忽略后项：{key}");
            }
        }

        // 无档案机器时引导卡默认展开（首次使用引导）
        GuideExpanded = _profiles.Count == 0;
        GuideToggleText = GuideExpanded ? "收起 ▴" : "展开 ▾";

        // 档案机器先建卡（快照为空 → 未上报态）；后续快照到达经 ApplySnapshot 补数据
        foreach (var name in _profiles.Keys)
        {
            EnsureCard(name);
        }

        // 上报目标可视化管理行（双向监控）
        RebuildReporterTargets();
    }

    /// <summary>上报成功回调（来自 SnapshotReporter.Reported）。</summary>
    public void MarkReporterOk(string url, TimeSpan elapsed)
    {
        var existing = ReporterStatuses.FirstOrDefault(r => r.Url == url);
        if (existing is null)
        {
            existing = new ReporterStatusVm(url, true, elapsed);
            ReporterStatuses.Add(existing);
        }
        else
        {
            existing.Update(true, elapsed);
        }
        HasReporters = true;
        ReporterTargets.FirstOrDefault(r => r.Source.Url == url)?.AttachStatus(existing);
    }

    /// <summary>独立 token 首次上报 202 待绑定：状态行改为待绑定指引（适配文档 3.2）。</summary>
    public void MarkReporterBindPending(string url)
    {
        var existing = ReporterStatuses.FirstOrDefault(r => r.Url == url);
        if (existing is null)
        {
            ReporterStatuses.Add(new ReporterStatusVm(url, false, null, "待绑定：在 Yunzai 主人私聊发送 #服务器状态待绑定 获取绑定命令"));
        }
        else
        {
            existing.Update(false, null, "待绑定：#服务器状态待绑定");
        }
        HasReporters = true;
    }

    /// <summary>上报失败回调（UI 层可调用）。</summary>
    public void MarkReporterFail(string url, string reason)
    {
        var existing = ReporterStatuses.FirstOrDefault(r => r.Url == url);
        if (existing is null)
        {
            existing = new ReporterStatusVm(url, false, null, reason);
            ReporterStatuses.Add(existing);
        }
        else
        {
            existing.Update(false, null, reason);
        }
        ReporterTargets.FirstOrDefault(r => r.Source.Url == url)?.AttachStatus(existing);
    }

    /// <summary>上报目标状态行（URL → 最近一次上报结果）。</summary>
    public ObservableCollection<ReporterStatusVm> ReporterStatuses { get; } = [];

    public void ApplySnapshot(SnapshotEnvelope envelope)
    {
        var snapshot = envelope.Snapshot;
        var card = EnsureCard(snapshot.Name);
        card.SetOnline(true);

        if (snapshot.Os is { } os)
        {
            card.OsText = string.Join(" · ", new[] { os.Distro ?? os.Platform, os.Arch }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        if (snapshot.Cpu is { } cpu)
        {
            card.CpuUsage = Math.Clamp(cpu.Usage ?? 0, 0, 100);
            card.CpuText = cpu.Usage is { } usage ? $"{usage:F0}%" : "—";
            card.TempText = cpu.Temp is { } temp ? $"CPU {temp:F0}℃" : "";
        }

        if (snapshot.Mem is { } mem && mem.Total is { } total && total > 0)
        {
            var used = mem.Used ?? 0;
            card.MemUsage = Math.Clamp(used / total * 100, 0, 100);
            card.MemText = $"{used:F1} / {total:F1} GiB";
        }

        if (snapshot.Net is { } net)
        {
            var down = net.RxSec is { } rx ? $"↓ {rx:F1} MiB/s" : null;
            var up = net.TxSec is { } tx ? $"↑ {tx:F1} MiB/s" : null;
            card.NetText = string.Join("   ", new[] { down, up }.Where(x => x is not null));
        }

        if (snapshot.Disks is { Count: > 0 })
        {
            var worst = snapshot.Disks
                .Where(d => d.Total is { } t && t > 0)
                .OrderByDescending(d => (d.Used ?? 0) / d.Total!.Value)
                .FirstOrDefault();
            if (worst is { } disk && disk.Total is { } diskTotal)
            {
                card.DiskText = $"磁盘 {disk.Mount}: {(disk.Used ?? 0):F0}/{diskTotal:F0} GiB";
            }
        }

        card.LastSeenText = $"最后上报 {envelope.ReceivedAt:HH:mm:ss}";
        // 新机器插卡后按规则归位（EnsureCard 内已重排）
    }

    public void SetOnline(string name, bool online)
    {
        if (_byName.TryGetValue(name, out var card))
        {
            card.SetOnline(online);
        }
    }

    /// <summary>周期巡检：离线超时翻转状态（未上报档案机器保持"未上报"）。</summary>
    public void Sweep()
    {
        var now = DateTimeOffset.Now;
        foreach (var (name, card) in _byName)
        {
            card.SetOnline(_store.IsOnline(name, now));
        }
    }

    // ============ 命令：引导卡 / 剪贴板 ============

    [RelayCommand]
    private void ToggleGuide()
    {
        GuideExpanded = !GuideExpanded;
        GuideToggleText = GuideExpanded ? "收起 ▴" : "展开 ▾";
    }

    [RelayCommand]
    private void ToggleHidden()
    {
        HiddenExpanded = !HiddenExpanded;
        HiddenToggleText = HiddenExpanded ? "收起 ▴" : "展开 ▾";
    }

    [RelayCommand]
    private void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
            _log.Info("已复制到剪贴板");
        }
        catch (Exception ex)
        {
            // 剪贴板可能被占用/会话限制，失败不中断
            _log.Error("复制到剪贴板失败", ex);
        }
    }

    // ============ 命令：机器档案管理 ============

    [RelayCommand]
    private void SetAlias(MachineCardVm? card)
    {
        if (card is null)
        {
            return;
        }
        if (Prompt is null)
        {
            _log.Warn("别名输入对话框未接入，忽略操作");
            return;
        }
        var profile = EnsureProfile(card.Name);
        var input = Prompt.Invoke($"设置别名 · {card.Name}", profile.Alias)?.Trim();
        if (input is null)
        {
            return; // 用户取消
        }
        profile.Alias = input;
        card.Alias = input;
        SaveAndLog($"机器 {card.Name} 别名 → {(input.Length == 0 ? "（恢复原名称）" : input)}");
    }

    [RelayCommand]
    private void TogglePin(MachineCardVm? card)
    {
        if (card is null)
        {
            return;
        }
        var profile = EnsureProfile(card.Name);
        profile.Pinned = !profile.Pinned;
        card.IsPinned = profile.Pinned;
        SaveAndLog($"{(profile.Pinned ? "置顶" : "取消置顶")}机器 {card.Name}");
        ReorderMachines();
    }

    [RelayCommand]
    private void HideMachine(MachineCardVm? card)
    {
        if (card is null)
        {
            return;
        }
        var profile = EnsureProfile(card.Name);
        profile.Hidden = true;
        Machines.Remove(card);
        if (!HiddenMachines.Contains(card))
        {
            HiddenMachines.Add(card);
        }
        UpdateFlags();
        SaveAndLog($"隐藏机器 {card.Name}（可在已隐藏机器区恢复）");
    }

    [RelayCommand]
    private void UnhideMachine(MachineCardVm? card)
    {
        if (card is null)
        {
            return;
        }
        var profile = EnsureProfile(card.Name);
        profile.Hidden = false;
        HiddenMachines.Remove(card);
        if (!Machines.Contains(card))
        {
            Machines.Add(card);
        }
        ReorderMachines();
        SaveAndLog($"恢复显示机器 {card.Name}");
    }

    // ============ 命令：上报目标管理（双向监控） ============

    [RelayCommand]
    private void AddReporterTarget()
    {
        if (EditReporter is null)
        {
            _log.Warn("上报目标编辑对话框未接入，忽略操作");
            return;
        }
        var result = EditReporter.Invoke(null);
        if (result is null)
        {
            return; // 用户取消
        }
        _settings.Reporters.Add(result);
        RebuildReporterTargets();
        SaveAndLog($"新增上报目标 {result.Url}（间隔 {result.IntervalSec}s，{(result.Enabled ? "启用" : "停用")}）");
        _reportersChanged?.Invoke();
    }

    [RelayCommand]
    private void EditReporterTarget(ReporterTargetVm? row)
    {
        if (row is null)
        {
            return;
        }
        if (EditReporter is null)
        {
            _log.Warn("上报目标编辑对话框未接入，忽略操作");
            return;
        }
        var result = EditReporter.Invoke(CloneTarget(row.Source));
        if (result is null)
        {
            return; // 用户取消
        }
        row.Source.Url = result.Url;
        row.Source.Token = result.Token;
        row.Source.Name = result.Name;
        row.Source.IntervalSec = result.IntervalSec;
        row.Source.TimeoutMs = result.TimeoutMs;
        row.Source.Enabled = result.Enabled;
        RebuildReporterTargets();
        SaveAndLog($"修改上报目标 → {row.Source.Url}（间隔 {row.Source.IntervalSec}s，{(row.Source.Enabled ? "启用" : "停用")}）");
        _reportersChanged?.Invoke();
    }

    [RelayCommand]
    private void DeleteReporterTarget(ReporterTargetVm? row)
    {
        if (row is null)
        {
            return;
        }
        // 二次确认：第一次点击进入确认态，3 秒未确认自动复位
        if (!row.ConfirmingDelete)
        {
            row.ConfirmingDelete = true;
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (row.ConfirmingDelete)
                    {
                        row.ConfirmingDelete = false;
                    }
                })));
            return;
        }
        _settings.Reporters.Remove(row.Source);
        RebuildReporterTargets();
        SaveAndLog($"删除上报目标 {row.Url}");
        _reportersChanged?.Invoke();
    }

    /// <summary>启用开关写回（行 VM 调用；不重建行集合，避免打断绑定）。</summary>
    internal void SetTargetEnabled(ReporterTargetVm row, bool enabled)
    {
        row.RefreshDetail();
        SaveAndLog($"上报目标 {row.Url} → {(enabled ? "启用" : "停用")}");
        _reportersChanged?.Invoke();
    }

    /// <summary>从 settings.Reporters 重建目标行（新增/编辑/删除后调用；同时清掉已删目标的陈旧状态）。</summary>
    public void RebuildReporterTargets()
    {
        ReporterTargets.Clear();
        foreach (var target in _settings.Reporters)
        {
            var row = new ReporterTargetVm(target, this);
            row.AttachStatus(ReporterStatuses.FirstOrDefault(s => s.Url == target.Url));
            ReporterTargets.Add(row);
        }
        foreach (var status in ReporterStatuses.ToList())
        {
            if (!ReporterTargets.Any(r => r.Source.Url == status.Url))
            {
                ReporterStatuses.Remove(status);
            }
        }
        HasReporterTargets = ReporterTargets.Count > 0;
    }

    private static ReporterTarget CloneTarget(ReporterTarget t) => new()
    {
        Url = t.Url,
        Token = t.Token,
        Name = t.Name,
        Enabled = t.Enabled,
        IntervalSec = t.IntervalSec,
        TimeoutMs = t.TimeoutMs,
    };

    // ============ 内部：卡片构建 / 排序 / 档案 ============

    private MachineProfile? Profile(string name) =>
        _profiles.TryGetValue(name, out var profile) ? profile : null;

    private MachineProfile EnsureProfile(string name)
    {
        if (_profiles.TryGetValue(name, out var profile))
        {
            return profile;
        }
        profile = new MachineProfile { Name = name };
        _profiles[name] = profile;
        _settings.MachineProfiles.Add(profile);
        return profile;
    }

    private MachineCardVm EnsureCard(string name)
    {
        if (_byName.TryGetValue(name, out var existing))
        {
            return existing;
        }
        var card = new MachineCardVm(name, this);
        var profile = Profile(name);
        card.Alias = profile?.Alias ?? "";
        card.IsPinned = profile?.Pinned == true;
        if (_store.Get(name) is null)
        {
            card.SetNotReported();
        }
        _byName[name] = card;
        PlaceCard(card);
        ReorderMachines();
        return card;
    }

    /// <summary>按档案归位：Hidden 进折叠区，否则进机器网格。</summary>
    private void PlaceCard(MachineCardVm card)
    {
        var hidden = Profile(card.Name)?.Hidden == true;
        if (hidden)
        {
            if (!HiddenMachines.Contains(card))
            {
                HiddenMachines.Add(card);
            }
        }
        else if (!Machines.Contains(card))
        {
            Machines.Add(card);
        }
    }

    /// <summary>排序：Pinned 优先 → 有快照的按名字 → 未上报的在后；就地 Move 减少重建。</summary>
    private void ReorderMachines()
    {
        SortInPlace(Machines);
        UpdateFlags();
    }

    private void UpdateFlags()
    {
        HasMachines = Machines.Count > 0;
        HasHiddenMachines = HiddenMachines.Count > 0;
        HiddenHeaderText = HiddenMachines.Count > 0 ? $"已隐藏机器（{HiddenMachines.Count}）" : "已隐藏机器";
    }

    private static void SortInPlace(ObservableCollection<MachineCardVm> collection)
    {
        if (collection.Count < 2)
        {
            return;
        }
        var sorted = collection
            .OrderBy(c => c.IsPinned ? 0 : 1)
            .ThenBy(c => c.HasEverReported ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (!ReferenceEquals(collection[i], sorted[i]))
            {
                collection.Move(collection.IndexOf(sorted[i]), i);
            }
        }
    }

    private void SaveAndLog(string message)
    {
        try
        {
            _saveSettings();
            _log.Info(message);
        }
        catch (Exception ex)
        {
            _log.Error("机器档案写回 settings 失败", ex);
        }
    }
}
