using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;

namespace LingXi.Monitor.Core;

/// <summary>
/// Windows 系统指标采集器（WMI + PerformanceCounter）。
/// 输出 servermonitor 协议 v:1 快照（开发文档附录 D）：
/// 单位 GiB(1 位)/MiB/s(2 位)/%(1 位)；load 在 Windows 上恒 null。
/// </summary>
public sealed class SystemMetricsCollector
{
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _memAvailCounter;
    private readonly Dictionary<string, (PerformanceCounter Rx, PerformanceCounter Tx)> _netCounters = new();
    private ulong _lastRx, _lastTx;
    private DateTime _lastNetSample = DateTime.MinValue;
    private (string Model, int Cores)? _cpuInfo;
    private (double Used, double Total)? _memInfo;
    private DateTime _lastStaticAt = DateTime.MinValue;

    public string MachineName { get; set; } = Environment.MachineName;

    /// <summary>
    /// 采集一帧完整快照（含静态信息缓存 + 动态指标）。
    /// 名称经服务端同款清洗（≤32 字符、剔除 &lt;&gt;&amp;"'`\、空白→下划线），
    /// 全空快照返回 null（服务端对空快照回 422，见适配文档）。
    /// </summary>
    public Snapshot? Collect()
    {
        var snap = CollectRaw();
        if (snap is null)
        {
            return null;
        }
        snap.Name = SanitizeName(snap.Name);
        return snap;
    }

    /// <summary>按第三方适配文档契约清洗上报名称（对齐服务端 sanitizeServerName）。</summary>
    public static string SanitizeName(string raw)
    {
        var cleaned = new string(raw.Where(c => c is not '<' and not '>' and not '&' and not '"' and not '\'' and not '`' and not '\\').ToArray());
        // 连续空白折叠成单下划线（与服务端 replace(/\s+/g, "_") 一致）
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", "_");
        return cleaned.Length > 32 ? cleaned[..32] : cleaned;
    }

    private Snapshot? CollectRaw()
    {
        var now = DateTime.Now;

        // 静态信息 5 分钟刷新一次
        if (_cpuInfo is null || (now - _lastStaticAt).TotalSeconds > 300)
        {
            _cpuInfo = QueryCpuInfo();
            _memInfo = QueryMemory();
            _lastStaticAt = now;
        }

        var cpuUsage = GetCpuUsage();
        var mem = _memInfo ?? (0, 0);
        var net = GetNetwork();

        var snap = new Snapshot
        {
            Version = 1,
            Name = string.IsNullOrWhiteSpace(MachineName) ? Environment.MachineName : MachineName,
            AgentTs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            Os = new SnapshotOs
            {
                Platform = "win32",
                Distro = QueryWindowsName(),
                Release = Environment.OSVersion.VersionString,
                Arch = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                Hostname = Environment.MachineName,
                Uptime = Environment.TickCount64 / 1000.0,
            },
            Cpu = new SnapshotCpu
            {
                Model = _cpuInfo?.Model,
                Cores = _cpuInfo?.Cores,
                Usage = cpuUsage,
                Temp = QueryCpuTemp(),
                Power = null,
            },
            Gpus = QueryGpus(),
            Mem = new SnapshotMem
            {
                Used = Math.Round(mem.Total - mem.Used, 1),
                Total = Math.Round(mem.Total, 1),
                SwapUsed = null,
                SwapTotal = null,
            },
            Net = net,
            Disks = QueryDisks(),
            Load = null, // 协议规定：Windows 恒 null
        };

        // 空快照防护（适配文档：全空快照 422 拒收）——
        // 至少一项动态指标可用才发出：cpu.usage / mem 总量 / disks 条数 / net
        var hasAnyMetric = snap.Cpu?.Usage is not null
            || (snap.Mem?.Total is > 0)
            || (snap.Disks is { Count: > 0 })
            || snap.Net is not null;
        return hasAnyMetric ? snap : null;
    }

    private (string Model, int Cores) QueryCpuInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                var model = obj["Name"]?.ToString()?.Trim() ?? "";
                var cores = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? obj["NumberOfCores"] ?? 0);
                return (model, cores);
            }
        }
        catch
        {
            // WMI 不可用时降级
        }
        return (Environment.ProcessorCount + " cores", Environment.ProcessorCount);
    }

    private (double Used, double Total) QueryMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                var total = Convert.ToDouble(obj["TotalVisibleMemorySize"]) / 1024 / 1024; // KB→GiB
                var free = Convert.ToDouble(obj["FreePhysicalMemory"]) / 1024 / 1024;
                return (total - free, total);
            }
        }
        catch
        {
            // 降级
        }
        return (0, 0);
    }

    private double GetCpuUsage()
    {
        try
        {
            _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
            return Math.Round(_cpuCounter.NextValue(), 1);
        }
        catch
        {
            return 0;
        }
    }

    private SnapshotNet? GetNetwork()
    {
        try
        {
            var iface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            if (iface is null)
            {
                return null;
            }

            var stats = iface.GetIPv4Statistics();
            var now = DateTime.Now;
            if (_lastNetSample == DateTime.MinValue)
            {
                _lastRx = (ulong)Math.Max(0, stats.BytesReceived);
                _lastTx = (ulong)Math.Max(0, stats.BytesSent);
                _lastNetSample = now;
                return new SnapshotNet { Iface = iface.Name, RxSec = null, TxSec = null };
            }

            var elapsed = (now - _lastNetSample).TotalSeconds;
            if (elapsed < 1)
            {
                elapsed = 1;
            }

            var rxSec = ((ulong)Math.Max(0, stats.BytesReceived) - _lastRx) / elapsed / 1024 / 1024;
            var txSec = ((ulong)Math.Max(0, stats.BytesSent) - _lastTx) / elapsed / 1024 / 1024;
            _lastRx = (ulong)Math.Max(0, stats.BytesReceived);
            _lastTx = (ulong)Math.Max(0, stats.BytesSent);
            _lastNetSample = now;

            return new SnapshotNet
            {
                Iface = iface.Name,
                RxSec = Math.Round(rxSec, 2),
                TxSec = Math.Round(txSec, 2),
            };
        }
        catch
        {
            return null;
        }
    }

    private List<SnapshotDisk> QueryDisks()
    {
        var result = new List<SnapshotDisk>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                result.Add(new SnapshotDisk
                {
                    Mount = drive.Name,
                    Used = Math.Round((drive.TotalSize - drive.AvailableFreeSpace) / 1024.0 / 1024 / 1024, 1),
                    Total = Math.Round(drive.TotalSize / 1024.0 / 1024 / 1024, 1),
                });
            }
        }
        catch
        {
            // 降级
        }
        return result;
    }

    private static string QueryWindowsName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                return obj["Caption"]?.ToString() ?? "Windows";
            }
        }
        catch
        {
            // 降级
        }
        return "Windows";
    }

    private static double? QueryCpuTemp()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (var obj in searcher.Get())
            {
                var kelvin = Convert.ToDouble(obj["CurrentTemperature"]);
                var celsius = (kelvin - 2732) / 10.0;
                if (celsius > 0 && celsius < 120)
                {
                    return Math.Round(celsius, 1);
                }
            }
        }
        catch
        {
            // 多数消费级主板 WMI 温度不可用（官方 agent 同样处理）
        }
        return null;
    }

    private static List<SnapshotGpu> QueryGpus()
    {
        var result = new List<SnapshotGpu>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                var ram = Convert.ToDouble(obj["AdapterRAM"] ?? 0) / 1024 / 1024 / 1024;
                result.Add(new SnapshotGpu
                {
                    Model = obj["Name"]?.ToString(),
                    MemTotal = Math.Round(ram, 1),
                });
            }
        }
        catch
        {
            // 降级
        }

        // GPU 占用率：Windows 10+ 通用方案（不挑厂商）——
        // "GPU Engine" 性能计数器按引擎分片，聚合成每卡最大引擎占用率
        //（对齐 servermonitor《Windows-GPU占用率修复说明》的扩展方向）。
        var usageByGpu = QueryGpuEngineUsage();
        if (usageByGpu.Count > 0)
        {
            var usages = usageByGpu.Values.ToList();
            if (result.Count == 1)
            {
                result[0].Usage = usages.Max();
            }
            else
            {
                // 多卡：按 phys_N 序号对齐 WMI 列表顺序
                for (var i = 0; i < result.Count; i++)
                {
                    if (usageByGpu.TryGetValue(i.ToString(), out var u))
                    {
                        result[i].Usage = u;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// GPU 引擎占用率采集（Windows 10+ "GPU Engine" 计数器族）：
    /// 每个物理 GPU 在实例名 "phys_N_eng_..." 下挂多个引擎（3D/Copy/VideoDecode…），
    /// 取每卡全部引擎 Utilization Percentage 的最大值作为该卡占用率。
    /// 计数器缺失（旧系统/无权限）返回空字典，调用方保持 usage=null 不伪造。
    /// </summary>
    private static Dictionary<string, double> QueryGpuEngineUsage()
    {
        var byGpu = new Dictionary<string, double>();
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in category.GetInstanceNames())
            {
                var marker = "phys_";
                var idx = instance.IndexOf(marker, StringComparison.Ordinal);
                if (idx < 0)
                {
                    continue;
                }
                var rest = instance[(idx + marker.Length)..];
                var gpuId = rest.Contains('_') ? rest[..rest.IndexOf('_')] : rest;

                using var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                var value = Math.Round(counter.NextValue(), 1);
                if (value <= 0)
                {
                    continue;
                }
                if (byGpu.TryGetValue(gpuId, out var current))
                {
                    if (value > current)
                    {
                        byGpu[gpuId] = value;
                    }
                }
                else
                {
                    byGpu[gpuId] = value;
                }
            }
        }
        catch
        {
            // 计数器不可用 → 保持空，不伪造
        }
        return byGpu;
    }
}
