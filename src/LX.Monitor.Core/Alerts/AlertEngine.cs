namespace LingXi.Monitor.Core;

/// <summary>告警规则（默认值对齐 config.example.yaml：cooldown 120s，开发文档 9.6）。</summary>
public sealed class AlertRules
{
    public bool Enabled { get; set; } = true;

    public double CpuPct { get; set; } = 90;

    public double DiskPct { get; set; } = 90;

    public double TempC { get; set; } = 85;

    public int CooldownSec { get; set; } = 120;
}

/// <summary>一条告警。</summary>
public sealed record Alert(string Machine, string RuleId, string Message, DateTimeOffset At);

/// <summary>阈值告警 + 冷却：同机器同规则在冷却窗口内不重复触发。</summary>
public sealed class AlertEngine
{
    private readonly AlertRules _rules;
    private readonly Dictionary<(string Machine, string RuleId), DateTimeOffset> _lastFired = new();
    private readonly object _gate = new();

    public AlertEngine(AlertRules rules) => _rules = rules;

    /// <summary>对一帧机器状态求值，返回需要通知的告警（已过滤冷却）。</summary>
    public IReadOnlyList<Alert> Evaluate(SnapshotEnvelope envelope, bool isOnline, DateTimeOffset now)
    {
        if (!_rules.Enabled)
        {
            return [];
        }

        var found = new List<Alert>();
        var snapshot = envelope.Snapshot;
        var name = snapshot.Name;

        if (!isOnline)
        {
            found.Add(new Alert(name, "offline", $"{name} 已离线", now));
        }
        else
        {
            if (snapshot.Cpu?.Usage is { } cpu && cpu >= _rules.CpuPct)
            {
                found.Add(new Alert(name, "cpu", $"{name} CPU {cpu:F0}% ≥ {_rules.CpuPct:F0}%", now));
            }

            if (snapshot.Disks is { Count: > 0 })
            {
                foreach (var disk in snapshot.Disks)
                {
                    if (disk.Total is { } total && total > 0 &&
                        disk.Used is { } used && used / total * 100 >= _rules.DiskPct)
                    {
                        found.Add(new Alert(name, "disk",
                            $"{name} 磁盘 {disk.Mount} 使用率 {used / total * 100:F0}%", now));
                    }
                }
            }

            if (snapshot.Cpu?.Temp is { } temp && temp >= _rules.TempC)
            {
                found.Add(new Alert(name, "temp", $"{name} CPU 温度 {temp:F0}℃ ≥ {_rules.TempC:F0}℃", now));
            }
        }

        lock (_gate)
        {
            var cooldown = TimeSpan.FromSeconds(_rules.CooldownSec);
            var result = new List<Alert>();
            foreach (var alert in found)
            {
                var key = (alert.Machine, alert.RuleId);
                if (_lastFired.TryGetValue(key, out var last) && now - last < cooldown)
                {
                    continue;
                }
                _lastFired[key] = now;
                result.Add(alert);
            }
            return result;
        }
    }
}
