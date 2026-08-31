using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LingXi.Monitor.Core;
using LingXi.Ui.Controls;

namespace LingXi.Monitor.ViewModels;

/// <summary>监控仪表盘 VM：订阅快照表，机器卡片差量更新（开发文档 9.5）。</summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly SnapshotStore _store;
    private readonly Dictionary<string, MachineCardVm> _byName = new(StringComparer.Ordinal);

    public ObservableCollection<MachineCardVm> Machines { get; } = [];

    [ObservableProperty]
    private string _hubReportUrl = "";

    [ObservableProperty]
    private string _hubToken = "";

    [ObservableProperty]
    private bool _hasMachines;

    public DashboardViewModel(SnapshotStore store) => _store = store;

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
        HasMachines = Machines.Count > 0;
    }

    public void SetOnline(string name, bool online)
    {
        if (_byName.TryGetValue(name, out var card))
        {
            card.SetOnline(online);
        }
    }

    /// <summary>周期巡检：离线超时翻转状态。</summary>
    public void Sweep()
    {
        var now = DateTimeOffset.Now;
        foreach (var (name, card) in _byName)
        {
            card.SetOnline(_store.IsOnline(name, now));
        }
    }

    private MachineCardVm EnsureCard(string name)
    {
        if (_byName.TryGetValue(name, out var existing))
        {
            return existing;
        }
        var card = new MachineCardVm(name);
        _byName[name] = card;
        Machines.Add(card);
        HasMachines = true;
        return card;
    }
}
