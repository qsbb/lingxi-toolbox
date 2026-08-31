using LingXi.Monitor.Core;
using Xunit;

namespace LX.Monitor.Core.Tests;

/// <summary>契约测试：用真实 agent.mjs v0.1.13 dry-run 输出作为 golden 样本（开发文档 13.1）。</summary>
public class SnapshotContractTests
{
    private static string FixtureJson()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "agent-snapshot-linux.json");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Parses_Real_Agent_Snapshot()
    {
        var snapshot = SnapshotJson.Parse(FixtureJson());

        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot!.Version);
        Assert.Equal("lx-fixture", snapshot.Name);
        Assert.Equal("linux", snapshot.Os?.Platform);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Os?.Distro));
        Assert.True(snapshot.Os?.Uptime > 0);
        Assert.True(snapshot.Cpu?.Cores > 0);
        Assert.NotNull(snapshot.Cpu?.Usage);
        // 真实 agent v0.1.13 的 cpu.temp 是数值（开发文档附录 D 已按真实 fixture 修正）
        Assert.True(snapshot.Cpu?.Temp is null or >= 0);
        Assert.NotNull(snapshot.Mem?.Total);
        Assert.NotEmpty(snapshot.Disks!);
        Assert.NotNull(snapshot.Disks![0].Mount);
        Assert.True((snapshot.Disks[0].Total ?? 0) > 0);
        // Linux 有 load；Windows 恒为 null（UI 需隐藏）
        Assert.Equal(3, snapshot.Load?.Count);
    }

    [Fact]
    public void Parses_String_Numbers_And_Ignores_Unknown_Fields()
    {
        const string json = """{"v":"1","name":"x","cpu":{"usage":"42.5"},"future_field":{"a":1}}""";

        var snapshot = SnapshotJson.Parse(json);

        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot!.Version);
        Assert.Equal(42.5, snapshot.Cpu?.Usage);
    }

    [Fact]
    public void Returns_Null_On_Garbage()
    {
        Assert.Null(SnapshotJson.Parse("not json at all"));
        Assert.Null(SnapshotJson.Parse(""));
    }

    [Fact]
    public void Empty_Name_Falls_Back_To_Hostname()
    {
        var store = new SnapshotStore();
        var snapshot = new Snapshot { Name = "", Os = new SnapshotOs { Hostname = "host-1" } };
        store.Upsert(new SnapshotEnvelope(snapshot, "tok", DateTimeOffset.Now));

        Assert.Contains("host-1", store.GetNames());
    }
}
