using System.Diagnostics;
using System.Globalization;
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
    private (double Used, double Total, double? Available)? _memInfo;
    private List<SnapshotGpu> _nvidiaGpus = new();
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
            _nvidiaGpus = QueryNvidiaSmiGpus();
            _lastStaticAt = now;
        }

        var cpuUsage = GetCpuUsage();
        var mem = _memInfo ?? (0, 0, null);
        var net = GetNetwork();

        var snap = new Snapshot
        {
            Version = 1,
            Name = string.IsNullOrWhiteSpace(MachineName) ? Environment.MachineName : MachineName,
            AgentTs = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            Os = new SnapshotOs
            {
                Platform = "windows",
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
            Gpus = QueryGpus(_nvidiaGpus),
            Mem = new SnapshotMem
            {
                Used = Math.Round(mem.Total - mem.Used, 1),
                Total = Math.Round(mem.Total, 1),
                Available = mem.Available is { } available ? Math.Round(available, 1) : null,
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

    private (double Used, double Total, double? Available) QueryMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get())
            {
                var total = Convert.ToDouble(obj["TotalVisibleMemorySize"]) / 1024 / 1024; // KB→GiB
                var free = Convert.ToDouble(obj["FreePhysicalMemory"]) / 1024 / 1024;

                // available 必须对应"可立即供新进程使用"的语义（服务器按 total-available
                // 计算内存压力）。取不到就传 null，绝不用 total-used 反推（会把 page cache
                // 重新包装成新字段，误导服务端）。
                double? available = TryPerfFormattedAvailableGiB() ?? TryPerformanceCounterGiB();
                return (total - free, total, available);
            }
        }
        catch
        {
            // 降级
        }
        return (0, 0, null);
    }

    /// <summary>
    /// 首选 Win32_PerfFormattedData_PerfOS_Memory.AvailableMBytes：与任务管理器"可用内存"
    /// 同源；Win32_OperatingSystem.AvailableKBytes 在 Win10/11 上已废弃恒为 0，不能用。
    /// </summary>
    private static double? TryPerfFormattedAvailableGiB()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT AvailableMBytes FROM Win32_PerfFormattedData_PerfOS_Memory");
            foreach (var obj in searcher.Get())
            {
                var mb = Convert.ToDouble(obj["AvailableMBytes"]);
                return mb <= 0 ? null : mb / 1024;
            }
        }
        catch
        {
            // 降级到性能计数器
        }
        return null;
    }

    private double? TryPerformanceCounterGiB()
    {
        try
        {
            _memAvailCounter ??= new PerformanceCounter("Memory", "Available Bytes");
            var bytes = _memAvailCounter.NextValue();
            return bytes <= 0 ? null : bytes / 1024 / 1024 / 1024;
        }
        catch
        {
            return null;
        }
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

    /// <summary>
    /// GPU 采集：nvidia-smi 优先（占用率/温度/显存/功耗），其余走 WMI + 性能计数器兜底。
    /// 注意 Win32_VideoController.AdapterRAM 是 32 位字段，显存 ≥4GB 会被截断成
    /// 2^32-1（表现为恒为 4.0GB），因此该值只作"有无显存"判断，绝不采信具体数值。
    /// 虚拟显示适配器（串流/模拟器/AR）不参与上报。
    /// </summary>
    private static List<SnapshotGpu> QueryGpus(List<SnapshotGpu> nvidiaGpus)
    {
        var result = new List<SnapshotGpu>();
        var usedNvidia = new bool[nvidiaGpus.Count];
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim() ?? "";
                if (name.Length == 0) continue;

                var ramRaw = Convert.ToDouble(obj["AdapterRAM"] ?? 0);
                double? ram = IsBogusAdapterRam(ramRaw) || ramRaw <= 0
                    ? null
                    : Math.Round(ramRaw / 1024 / 1024 / 1024, 1);

                // 与 nvidia-smi 结果合并（NVIDIA 卡拿到精确值）
                var matchIndex = -1;
                for (var i = 0; i < nvidiaGpus.Count; i++)
                {
                    if (usedNvidia[i]) continue;
                    var target = nvidiaGpus[i].Model ?? "";
                    if (target.Length > 0 &&
                        (name.Contains(target, StringComparison.OrdinalIgnoreCase) ||
                         target.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        matchIndex = i;
                        break;
                    }
                }

                if (matchIndex >= 0)
                {
                    usedNvidia[matchIndex] = true;
                    var nv = nvidiaGpus[matchIndex];
                    result.Add(new SnapshotGpu
                    {
                        Model = name,
                        Usage = nv.Usage,
                        Temp = nv.Temp,
                        MemUsed = nv.MemUsed,
                        MemTotal = nv.MemTotal ?? ram,
                        Power = nv.Power,
                        Readable = true, // nvidia-smi 提供精确指标
                    });
                    continue;
                }

                // 虚拟/不可用适配器（串流/模拟器/AR）不参与上报
                if (IsVirtualDisplayAdapter(name)) continue;

                result.Add(new SnapshotGpu { Model = name, MemTotal = ram, Readable = false });
            }
        }
        catch
        {
            // 降级
        }

        // nvidia-smi 报告但 WMI 未列出的卡（少见）
        for (var i = 0; i < nvidiaGpus.Count; i++)
        {
            if (!usedNvidia[i]) result.Add(nvidiaGpus[i]);
        }

        // GPU 占用率兜底：性能计数器按 LUID 聚合（多显卡机器上比 phys_N 序号可靠）。
        // 核显/独显并存时 phys_0 会重复，只有 LUID 能唯一对上适配器。
        // 只有 LUID 精确映射到适配器才采信占用率；映射不到就标注不可读，
        // 绝不用序号猜测（多显卡机器上会张冠李戴）。
        var usageByLuid = QueryGpuEngineUsage();
        if (usageByLuid.Count > 0)
        {
            var byDescription = QueryAdapterDescriptionsByLuid();
            foreach (var gpu in result)
            {
                if (gpu.Usage is not null) continue; // nvidia-smi 已给精确值
                var model = gpu.Model ?? "";
                foreach (var (luid, description) in byDescription)
                {
                    if (!NamesLikelyMatch(model, description)) continue;
                    if (usageByLuid.TryGetValue(luid, out var exact))
                    {
                        gpu.Usage = exact;
                        gpu.Readable = true;
                    }
                    break;
                }
            }
        }

        // 仍未拿到占用率且非 nvidia-smi 的显卡：明确标记不可读
        foreach (var gpu in result)
        {
            gpu.Readable ??= gpu.Usage is not null;
        }
        return result;
    }

    /// <summary>
    /// 调 nvidia-smi 取精确 GPU 指标（不依赖厂商 SDK；非 NVIDIA 机器直接返回空）。
    /// 字段不可得（如被动散热卡无风扇/功耗）保持 null，不伪造。
    /// </summary>
    private static List<SnapshotGpu> QueryNvidiaSmiGpus()
    {
        var result = new List<SnapshotGpu>();
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(exe)) return result;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total,power.draw");
            psi.ArgumentList.Add("--format=csv,noheader");
            using var process = Process.Start(psi);
            if (process is null) return result;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',');
                if (parts.Length < 6) continue;
                var name = parts[0].Trim();
                if (name.Length == 0) continue;
                result.Add(new SnapshotGpu
                {
                    Model = name,
                    Usage = ParseNvidiaNumber(parts[1]),
                    Temp = ParseNvidiaNumber(parts[2]),
                    MemUsed = ParseMibNumber(parts[3]),
                    MemTotal = ParseMibNumber(parts[4]),
                    Power = ParseNvidiaNumber(parts[5]),
                });
            }
        }
        catch
        {
            // nvidia-smi 缺失/超时：交给 WMI + 性能计数器兜底
        }
        return result;
    }

    /// <summary>
    /// AdapterRAM 是 32 位字段：显存 ≥4GiB 的卡会被 WMI 截断成 0xFFFFFFxx 区间
    /// （实测 RTX 4070 12GB 返回 4293918720）。命中该区间一律视为不可信。
    /// </summary>
    internal static bool IsBogusAdapterRam(double bytes) =>
        bytes >= 4.0 * 1024 * 1024 * 1024 - 1024 * 1024;

    /// <summary>虚拟显示适配器（串流/模拟器/VR）判定：这类"显卡"没有真实显存与占用率。</summary>
    internal static bool IsVirtualDisplayAdapter(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var isKnownVendor =
            name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Arc", StringComparison.OrdinalIgnoreCase);
        if (isKnownVendor) return false;

        foreach (var marker in VirtualAdapterMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static readonly string[] VirtualAdapterMarkers =
    {
        "Virtual", "Virtio", "VMware", "VirtualBox", "Hyper-V", "Microsoft Basic",
        "GameViewer", "MuMu", "Meta Virtual", "Zako", "Sunshine", "Parsec",
        "DameWare", "DisplayLink", "USB Display",
    };

    /// <summary>
    /// 解析 nvidia-smi 的数值字段。注意中文区域下 nvidia-smi 会用逗号作小数点
    ///（"35,03 W"），必须两种小数点都接受，否则功耗永远是 null。
    /// </summary>
    internal static double? ParseNvidiaNumber(string raw)
    {
        var text = raw.Trim().TrimEnd('%').Trim();
        if (text.Length == 0 || text.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return null;

        // 取前导数字（含 . 或 , 小数），丢弃单位（W / C / MiB / %）
        var end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] is '.' or ',' or '-' or '+'))
        {
            end++;
        }
        if (end == 0) return null;
        var numeric = text[..end].Replace(',', '.');
        return double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Round(value, 1)
            : null;
    }

    /// <summary>MiB → GiB（nvidia-smi 的 memory.* 单位是 MiB）。</summary>
    internal static double? ParseMibNumber(string raw)
    {
        var mib = ParseNvidiaNumber(raw);
        return mib is null ? null : Math.Round(mib.Value / 1024, 1);
    }

    /// <summary>
    /// 性能计数器 GPU Engine 按适配器 LUID 聚合占用率（取各引擎实例的最大值）。
    /// instance 形如 pid_1234_luid_0x00000000_0x00014fc6_phys_0_eng_2_engtype_3D。
    /// </summary>
    private static Dictionary<string, double> QueryGpuEngineUsage()
    {
        var byLuid = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in category.GetInstanceNames())
            {
                var luid = ExtractLuid(instance);
                if (luid is null) continue;

                using var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                var value = Math.Round(counter.NextValue(), 1);
                if (value <= 0) continue;

                if (!byLuid.TryGetValue(luid, out var current) || value > current)
                {
                    byLuid[luid] = value;
                }
            }
        }
        catch
        {
            // 计数器不可用 → 保持空，不伪造
        }
        return byLuid;
    }

    /// <summary>从 GPU Engine 实例名提取 LUID（低 32 位十六进制，与注册表 AdapterLuidLowPart 对齐）。</summary>
    internal static string? ExtractLuid(string instanceName)
    {
        var marker = "_luid_0x";
        var start = instanceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var rest = instanceName[(start + marker.Length)..];

        var underscore = rest.IndexOf('_');
        if (underscore < 0) return null;
        var high = rest[..underscore];
        rest = rest[(underscore + 1)..];
        if (!rest.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;

        var end = rest.IndexOf('_', 2);
        var low = end < 0 ? rest : rest[..end];
        return $"0x{high.ToLowerInvariant()}_{low.ToLowerInvariant()}";
    }

    /// <summary>
    /// 读取 HKLM\SYSTEM\CurrentControlSet\Control\Video\*\0000 的 DirectX 适配器：
    /// 得到 LUID → 驱动描述（AdapterString）的映射，用于与 WMI 显卡名对位。
    /// </summary>
    private static Dictionary<string, string> QueryAdapterDescriptionsByLuid()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var video = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Video");
            if (video is null) return map;

            foreach (var guidKeyName in video.GetSubKeyNames())
            {
                using var guidKey = video.OpenSubKey(guidKeyName);
                using var adapterKey = guidKey?.OpenSubKey("0000");
                if (adapterKey is null) continue;

                var description = adapterKey.GetValue("HardwareInformation.AdapterString") as string;
                var low = adapterKey.GetValue("HardwareInformation.AdapterLuidLowPart");
                var high = adapterKey.GetValue("HardwareInformation.AdapterLuidHighPart");
                if (string.IsNullOrWhiteSpace(description) || low is null) continue;

                var highValue = Convert.ToInt64(high ?? 0);
                var lowValue = Convert.ToInt64(low);
                map[$"0x{highValue:x8}_0x{lowValue:x8}"] = description.Trim();
            }
        }
        catch
        {
            // 注册表不可读 → 只走序号退路
        }
        return map;
    }

    /// <summary>显卡名与驱动描述模糊匹配（"AMD Radeon(TM) Graphics" vs "AMD Radeon(TM) Graphics"）。</summary>
    internal static bool NamesLikelyMatch(string adapterName, string description)
    {
        static string Normalize(string value) => value
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Trim();

        if (string.IsNullOrWhiteSpace(adapterName) || string.IsNullOrWhiteSpace(description)) return false;
        var a = Normalize(adapterName);
        var b = Normalize(description);
        return a.Contains(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }
}
