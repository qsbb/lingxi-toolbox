using System.Text.Json;
using System.Text.Json.Serialization;

namespace LingXi.Monitor.Core;

// 快照 DTO：与 servermonitor agent.mjs snapshot() 输出 1:1 对应（契约 v:1，开发文档附录 D）。
// 全字段可空宽容解析；未知字段忽略（向前兼容）；数值允许字符串形式。

public sealed class Snapshot
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("agent_ts")] public long? AgentTs { get; set; }

    [JsonPropertyName("os")] public SnapshotOs? Os { get; set; }

    [JsonPropertyName("cpu")] public SnapshotCpu? Cpu { get; set; }

    [JsonPropertyName("gpus")] public List<SnapshotGpu>? Gpus { get; set; }

    [JsonPropertyName("mem")] public SnapshotMem? Mem { get; set; }

    [JsonPropertyName("net")] public SnapshotNet? Net { get; set; }

    [JsonPropertyName("disks")] public List<SnapshotDisk>? Disks { get; set; }

    [JsonPropertyName("load")] public List<double>? Load { get; set; }
}

public sealed class SnapshotOs
{
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("distro")] public string? Distro { get; set; }
    [JsonPropertyName("release")] public string? Release { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    [JsonPropertyName("uptime")] public double? Uptime { get; set; }
}

public sealed class SnapshotCpu
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("cores")] public int? Cores { get; set; }
    [JsonPropertyName("usage")] public double? Usage { get; set; }
    [JsonPropertyName("temp")] public double? Temp { get; set; }
    [JsonPropertyName("power")] public double? Power { get; set; }
}

public sealed class SnapshotGpu
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("usage")] public double? Usage { get; set; }
    [JsonPropertyName("temp")] public double? Temp { get; set; }
    [JsonPropertyName("memUsed")] public double? MemUsed { get; set; }
    [JsonPropertyName("memTotal")] public double? MemTotal { get; set; }
    [JsonPropertyName("power")] public double? Power { get; set; }
}

public sealed class SnapshotMem
{
    [JsonPropertyName("used")] public double? Used { get; set; }
    [JsonPropertyName("total")] public double? Total { get; set; }
    [JsonPropertyName("swapUsed")] public double? SwapUsed { get; set; }
    [JsonPropertyName("swapTotal")] public double? SwapTotal { get; set; }
}

public sealed class SnapshotNet
{
    [JsonPropertyName("iface")] public string? Iface { get; set; }
    [JsonPropertyName("rxSec")] public double? RxSec { get; set; }
    [JsonPropertyName("txSec")] public double? TxSec { get; set; }
    [JsonPropertyName("rxTotal")] public double? RxTotal { get; set; }
    [JsonPropertyName("txTotal")] public double? TxTotal { get; set; }
}

public sealed class SnapshotDisk
{
    [JsonPropertyName("mount")] public string? Mount { get; set; }
    [JsonPropertyName("used")] public double? Used { get; set; }
    [JsonPropertyName("total")] public double? Total { get; set; }
}

/// <summary>反序列化选项（宽容解析：大小写不敏感 + 字符串数值）。</summary>
public static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>解析快照；任何失败返回 null（收数端绝不因脏数据崩溃）。</summary>
    public static Snapshot? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Snapshot>(json, Options);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>入库条目：快照 + token + 收到时刻。</summary>
public sealed record SnapshotEnvelope(Snapshot Snapshot, string? Token, DateTimeOffset ReceivedAt);
