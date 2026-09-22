using LingXi.Monitor.Core;
using Xunit;

namespace LX.Monitor.Core.Tests;

/// <summary>
/// Windows GPU 采集的纯函数测试：nvidia-smi 解析 + AdapterRAM 32 位溢出防护。
/// 背景（2026-09-21 实机自检）：RTX 4070 12GB 被 WMI 截断成 4293918720（4GB），
/// 且虚拟显示适配器混进了上报列表。
/// </summary>
public class WindowsGpuParsingTests
{
    [Fact]
    public void AdapterRam_32bit_Overflow_Is_Rejected()
    {
        // 实测值：RTX 4070 12GB 在 WMI 里返回 4293918720
        Assert.True(SystemMetricsCollector.IsBogusAdapterRam(4293918720d));
        // 恰好 4GiB 也是同一溢出模式
        Assert.True(SystemMetricsCollector.IsBogusAdapterRam(4294967296d));
        Assert.True(SystemMetricsCollector.IsBogusAdapterRam(8.0 * 1024 * 1024 * 1024));
    }

    [Fact]
    public void Real_AdapterRam_Is_Trusted()
    {
        // 真实 2GB / 4GB-1 内的值应被接受（4GiB 以下老卡）
        Assert.False(SystemMetricsCollector.IsBogusAdapterRam(2.0 * 1024 * 1024 * 1024));
        Assert.False(SystemMetricsCollector.IsBogusAdapterRam(3.5 * 1024 * 1024 * 1024));
        Assert.False(SystemMetricsCollector.IsBogusAdapterRam(0));
    }

    [Fact]
    public void NvidiaSmi_Fields_Parse_Across_Locales()
    {
        // 英文区域输出
        Assert.Equal(24, SystemMetricsCollector.ParseNvidiaNumber("24 %"));
        Assert.Equal(41, SystemMetricsCollector.ParseNvidiaNumber("41"));
        Assert.Equal(35.4, SystemMetricsCollector.ParseNvidiaNumber("35.42 W"));
        Assert.Equal(1.9, SystemMetricsCollector.ParseMibNumber("1968 MiB"));
        Assert.Equal(12, SystemMetricsCollector.ParseMibNumber("12282 MiB"));

        // 中文区域：nvidia-smi 用逗号作小数点（踩过的坑，会导致功耗恒为 null）
        Assert.Equal(35.4, SystemMetricsCollector.ParseNvidiaNumber("35,42 W"));
        Assert.Equal(22, SystemMetricsCollector.ParseNvidiaNumber("22"));
        // MiB→GB 保留 1 位小数，与上报字段精度一致
        Assert.Equal(1.9, SystemMetricsCollector.ParseMibNumber("1970 MiB"));

        // 不可得字段保持 null，不伪造
        Assert.Null(SystemMetricsCollector.ParseNvidiaNumber("N/A"));
        Assert.Null(SystemMetricsCollector.ParseNvidiaNumber(""));
        Assert.Null(SystemMetricsCollector.ParseMibNumber("[N/A]"));
    }

    [Fact]
    public void Luid_Is_Extracted_From_Engine_Instance_Names()
    {
        // 实机样本：AMD 核显
        Assert.Equal("0x00000000_0x00014fc6",
            SystemMetricsCollector.ExtractLuid("pid_35316_luid_0x00000000_0x00014FC6_phys_0_eng_7_engtype_VideoEncode"));
        // NVIDIA 独显
        Assert.Equal("0x00000000_0x00016181",
            SystemMetricsCollector.ExtractLuid("pid_2428_luid_0x00000000_0x00016181_phys_0_eng_5_engtype_3D"));
        // 高位非零（多适配器场景）
        Assert.Equal("0x00000001_0x0000abcd",
            SystemMetricsCollector.ExtractLuid("pid_1_luid_0x00000001_0x0000ABCD_phys_0_eng_0_engtype_3D"));

        // 非法输入不抛异常
        Assert.Null(SystemMetricsCollector.ExtractLuid("not-a-gpu-instance"));
        Assert.Null(SystemMetricsCollector.ExtractLuid("pid_1_luid_0x00000000"));
    }

    [Fact]
    public void Adapter_Names_Match_Driver_Descriptions()
    {
        Assert.True(SystemMetricsCollector.NamesLikelyMatch(
            "AMD Radeon(TM) Graphics", "AMD Radeon(TM) Graphics"));
        Assert.True(SystemMetricsCollector.NamesLikelyMatch(
            "NVIDIA GeForce RTX 4070", "NVIDIA GeForce RTX 4070"));
        // 厂商描述常带 (TM)/(R) 与多余空格
        Assert.True(SystemMetricsCollector.NamesLikelyMatch(
            "AMD Radeon Graphics", "AMD Radeon(TM) Graphics"));
        Assert.False(SystemMetricsCollector.NamesLikelyMatch(
            "AMD Radeon(TM) Graphics", "NVIDIA GeForce RTX 4070"));
        Assert.False(SystemMetricsCollector.NamesLikelyMatch("", "NVIDIA GeForce RTX 4070"));
    }

    [Fact]
    public void Virtual_Adapters_Are_Filtered_Out()
    {
        // 本次实机出现的 5 个虚拟适配器
        Assert.True(SystemMetricsCollector.IsVirtualDisplayAdapter("GameViewer Virtual Display Adapter"));
        Assert.True(SystemMetricsCollector.IsVirtualDisplayAdapter("Zako Display Adapter"));
        Assert.True(SystemMetricsCollector.IsVirtualDisplayAdapter("Virtual Desktop Monitor"));
        Assert.True(SystemMetricsCollector.IsVirtualDisplayAdapter("Meta Virtual Monitor"));
        Assert.True(SystemMetricsCollector.IsVirtualDisplayAdapter("MuMu Virtual Display Adapter"));
        Assert.True(SystemMetricsCollector.IsVirtualDisplayAdapter("Microsoft Basic Display Adapter"));
        Assert.True(SystemMetricsCollector.IsVirtualDisplayAdapter(""));
    }

    [Fact]
    public void Real_Gpus_Are_Kept()
    {
        Assert.False(SystemMetricsCollector.IsVirtualDisplayAdapter("NVIDIA GeForce RTX 4070"));
        Assert.False(SystemMetricsCollector.IsVirtualDisplayAdapter("AMD Radeon(TM) Graphics"));
        Assert.False(SystemMetricsCollector.IsVirtualDisplayAdapter("Intel(R) UHD Graphics 770"));
        Assert.False(SystemMetricsCollector.IsVirtualDisplayAdapter("Intel Arc A770"));
    }
}
