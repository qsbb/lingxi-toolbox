using System.Runtime.InteropServices;
using System.Text;
using LingXi.Monitor.Core;

namespace LingXi.Flutter.NativeHost.Mac;

/// <summary>
/// macOS 本机指标采集，产出与 servermonitor 契约（v:1）一致的 <see cref="Snapshot"/>。
///
/// 数据源：sysctl（机型/内存/系统版本）、host_statistics64（CPU tick 与 VM 统计）、
/// statfs（磁盘）、getloadavg（负载）。CPU 占用率需要两次采样求增量，
/// 因此本类持有上一次 tick 快照 —— 调用方（前端每 10s 轮询）天然满足。
/// </summary>
internal sealed class MacMetricsCollector
{
    private const int HostCpuLoadInfo = 3;
    private const int HostVmInfo64 = 4;

    [DllImport("libSystem.B.dylib")]
    private static extern int sysctlbyname(string name, IntPtr oldValue, ref IntPtr oldLength, IntPtr newValue, IntPtr newLength);

    [DllImport("libSystem.B.dylib")]
    private static extern IntPtr mach_host_self();

    [DllImport("libSystem.B.dylib")]
    private static extern int host_statistics64(IntPtr host, int flavor, IntPtr info, ref uint count);

    [DllImport("libSystem.B.dylib")]
    private static extern int statfs(string path, IntPtr buffer);

    [DllImport("libSystem.B.dylib")]
    private static extern int getloadavg([Out] double[] loadAverage, int count);

    private ulong _prevUser, _prevSystem, _prevIdle, _prevNice;
    private bool _hasPrev;

    public Snapshot Collect(string name)
    {
        var uptime = BootUptimeSeconds();
        var snapshot = new Snapshot
        {
            Version = 1,
            Name = name,
            AgentTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Os = new SnapshotOs
            {
                Platform = "darwin",
                Distro = "macOS",
                Release = SysctlString("kern.osproductversion"),
                Arch = SysctlString("hw.machine"),
                Hostname = Environment.MachineName,
                Uptime = uptime,
            },
            Cpu = CollectCpu(),
            Mem = CollectMemory(),
            Disks = CollectDisks(),
            Load = CollectLoad(),
            Gpus = [],
        };
        return snapshot;
    }

    private SnapshotCpu CollectCpu()
    {
        var cores = (int?)SysctlLong("hw.ncpu") ?? Environment.ProcessorCount;
        var model = SysctlString("machdep.cpu.brand_string");
        if (string.IsNullOrEmpty(model)) model = SysctlString("hw.model");

        double? usage = null;
        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            var count = 16u;
            if (host_statistics64(mach_host_self(), HostCpuLoadInfo, buffer, ref count) == 0)
            {
                // host_cpu_load_info_data_t = { user, system, idle, nice }（都是 natural_t 计数）
                ulong user = (uint)Marshal.ReadInt32(buffer, 0);
                ulong system = (uint)Marshal.ReadInt32(buffer, 4);
                ulong idle = (uint)Marshal.ReadInt32(buffer, 8);
                ulong nice = (uint)Marshal.ReadInt32(buffer, 12);

                if (_hasPrev)
                {
                    var dUser = user - _prevUser;
                    var dSystem = system - _prevSystem;
                    var dIdle = idle - _prevIdle;
                    var dNice = nice - _prevNice;
                    var total = dUser + dSystem + dIdle + dNice;
                    if (total > 0)
                    {
                        usage = Math.Round((dUser + dSystem + dNice) * 100.0 / total, 1);
                    }
                }

                _prevUser = user;
                _prevSystem = system;
                _prevIdle = idle;
                _prevNice = nice;
                _hasPrev = true;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new SnapshotCpu { Model = model, Cores = cores, Usage = usage };
    }

    private static SnapshotMem CollectMemory()
    {
        var totalBytes = SysctlLong("hw.memsize") ?? 0;
        var pageSize = SysctlLong("hw.pagesize") ?? 4096;
        var totalGb = Math.Round(totalBytes / 1024.0 / 1024 / 1024, 2);

        double? availableGb = null;
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            var count = 128u;
            if (host_statistics64(mach_host_self(), HostVmInfo64, buffer, ref count) == 0)
            {
                // vm_statistics64 前 4 个字段是 natural_t，之后是 uint64；
                // speculative_count 位于偏移 92（4×4 + 9×8 + 4）。
                ulong free = (uint)Marshal.ReadInt32(buffer, 0);
                ulong inactive = (uint)Marshal.ReadInt32(buffer, 8);
                ulong speculative = (uint)Marshal.ReadInt32(buffer, 92);
                var availableBytes = (free + inactive + speculative) * (ulong)pageSize;
                availableGb = Math.Round(availableBytes / 1024.0 / 1024 / 1024, 2);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new SnapshotMem
        {
            Total = totalGb,
            Available = availableGb,
            Used = availableGb is null ? null : Math.Round(totalGb - availableGb.Value, 2),
        };
    }

    private static List<SnapshotDisk> CollectDisks()
    {
        var disks = new List<SnapshotDisk>();
        foreach (var mount in new[] { "/" })
        {
            var buffer = Marshal.AllocHGlobal(4096);
            try
            {
                if (statfs(mount, buffer) != 0) continue;
                var blockSize = (uint)Marshal.ReadInt32(buffer, 0);
                var blocks = (ulong)Marshal.ReadInt64(buffer, 8);
                var free = (ulong)Marshal.ReadInt64(buffer, 16);
                var totalGb = blocks * blockSize / 1024.0 / 1024 / 1024;
                var usedGb = (blocks - free) * blockSize / 1024.0 / 1024 / 1024;
                disks.Add(new SnapshotDisk
                {
                    Mount = mount,
                    Total = Math.Round(totalGb, 1),
                    Used = Math.Round(usedGb, 1),
                });
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return disks;
    }

    private static List<double>? CollectLoad()
    {
        var values = new double[3];
        if (getloadavg(values, 3) != 3) return null;
        return [Math.Round(values[0], 2), Math.Round(values[1], 2), Math.Round(values[2], 2)];
    }

    private static double? BootUptimeSeconds()
    {
        // kern.boottime 是 timeval { int64 sec; int32 usec; int32 pad }
        var size = (IntPtr)16;
        var buffer = Marshal.AllocHGlobal(16);
        try
        {
            if (sysctlbyname("kern.boottime", buffer, ref size, IntPtr.Zero, IntPtr.Zero) != 0) return null;
            var seconds = Marshal.ReadInt64(buffer, 0);
            if (seconds <= 0) return null;
            var uptime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds;
            return uptime > 0 ? uptime : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? SysctlString(string name)
    {
        var size = IntPtr.Zero;
        if (sysctlbyname(name, IntPtr.Zero, ref size, IntPtr.Zero, IntPtr.Zero) != 0 || size == IntPtr.Zero)
            return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (sysctlbyname(name, buffer, ref size, IntPtr.Zero, IntPtr.Zero) != 0) return null;
            var bytes = new byte[(int)size];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static long? SysctlLong(string name)
    {
        var size = (IntPtr)8;
        var buffer = Marshal.AllocHGlobal(8);
        try
        {
            if (sysctlbyname(name, buffer, ref size, IntPtr.Zero, IntPtr.Zero) != 0) return null;
            return size == (IntPtr)4 ? (uint)Marshal.ReadInt32(buffer) : Marshal.ReadInt64(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
