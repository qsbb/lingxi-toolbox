using LingXi.Monitor.Core;
using Xunit;

namespace LX.Monitor.Core.Tests;

public class AlertEngineTests
{
    private static SnapshotEnvelope Envelope(double? cpu = null, double? temp = null, double? diskUsed = null, double? diskTotal = null) =>
        new(new Snapshot
        {
            Name = "m",
            Cpu = new SnapshotCpu { Usage = cpu, Temp = temp },
            Disks = diskTotal is { } total
                ? [new SnapshotDisk { Mount = "C:", Used = diskUsed, Total = total }]
                : null,
        }, "t", DateTimeOffset.Now);

    [Fact]
    public void Fires_Cpu_Alert_And_Respects_Cooldown()
    {
        var engine = new AlertEngine(new AlertRules { CooldownSec = 120 });
        var now = DateTimeOffset.Now;

        var first = engine.Evaluate(Envelope(cpu: 95), true, now);
        var second = engine.Evaluate(Envelope(cpu: 95), true, now.AddSeconds(60));
        var third = engine.Evaluate(Envelope(cpu: 95), true, now.AddSeconds(121));

        Assert.Contains(first, a => a.RuleId == "cpu");
        Assert.Empty(second);
        Assert.Contains(third, a => a.RuleId == "cpu");
    }

    [Fact]
    public void Fires_Offline_Alert()
    {
        var engine = new AlertEngine(new AlertRules());
        var alerts = engine.Evaluate(Envelope(), false, DateTimeOffset.Now);
        Assert.Contains(alerts, a => a.RuleId == "offline");
    }

    [Fact]
    public void Fires_Disk_Alert()
    {
        var engine = new AlertEngine(new AlertRules { CooldownSec = 0 });
        var alerts = engine.Evaluate(Envelope(diskUsed: 92, diskTotal: 100), true, DateTimeOffset.Now);
        Assert.Contains(alerts, a => a.RuleId == "disk");
    }

    [Fact]
    public void Disabled_Rules_Return_Nothing()
    {
        var engine = new AlertEngine(new AlertRules { Enabled = false });
        Assert.Empty(engine.Evaluate(Envelope(cpu: 99), true, DateTimeOffset.Now));
    }
}
