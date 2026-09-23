using LingXi.Monitor.Core;
using Xunit;

namespace LX.Monitor.Core.Tests;

/// <summary>
/// 内存语义回归测试。
/// 背景（2026-09-23 实机自检）：上报的 mem.used 实际是【空闲内存】——
/// QueryMemory 返回的第一项已是"已用"，调用处又做了一次 total - used，
/// 构成双重取反：61.5GB 机器上 used 上报成 47.2（= 空闲），
/// 自研 UI 于是显示 76.7% 占用，而真实占用仅 23.3%。
/// </summary>
public class MemorySemanticsTests
{
    [Fact]
    public void Used_Is_Derived_From_Available_And_Never_Equals_Free()
    {
        // 实机数字：total=61.5, 空闲=47.2（available 同值）
        const double total = 61.5;
        const double freePhysical = 47.2;
        const double available = 47.2;
        var usedPhysical = total - freePhysical; // 14.3

        var used = SystemMetricsCollector.ResolveUsedGiB(total, usedPhysical, available);

        Assert.Equal(14.3, used);
        Assert.NotEqual(available, used);            // 关键：绝不能把空闲量当已用
        Assert.Equal(total, used!.Value + available, 1); // 与 available 自洽
    }

    [Fact]
    public void Used_Prefers_Available_Over_Physical_Calculation()
    {
        // available 与物理空闲略有差异时（Windows 上常见），以 available 为准保证自洽
        var used = SystemMetricsCollector.ResolveUsedGiB(61.5, 15.0, 47.0);
        Assert.Equal(14.5, used);
        Assert.Equal(61.5, used!.Value + 47.0, 1);
    }

    [Fact]
    public void Used_Falls_Back_To_Physical_When_Available_Missing()
    {
        var used = SystemMetricsCollector.ResolveUsedGiB(16, 9.5, null);
        Assert.Equal(9.5, used);
    }

    [Fact]
    public void Used_Is_Clamped_And_Handles_Degraded_Input()
    {
        Assert.Equal(0, SystemMetricsCollector.ResolveUsedGiB(16, 9, 20));  // available > total
        Assert.Equal(16, SystemMetricsCollector.ResolveUsedGiB(16, 99, null)); // 物理已用 > 总量
        Assert.Null(SystemMetricsCollector.ResolveUsedGiB(0, 0, null));      // WMI 失败
        Assert.Null(SystemMetricsCollector.ResolveUsedGiB(-1, 5, 2));
    }
}
