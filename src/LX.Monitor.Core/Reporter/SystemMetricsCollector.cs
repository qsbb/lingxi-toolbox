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

    /// <summary>采集一帧完整快照（含静态信息缓存 + 动态指标）。</summary>
    public Snapshot Collect()
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

        return new Snapshot
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
        return result;
    }
}
