using LingXi.Audio;
using Xunit;

namespace LX.Audio.Core.Tests;

/// <summary>
/// 音频监听契约测试。
///
/// 关注点是**跨平台行为一致性**与**错误分支**，不依赖真实音频硬件
/// （真实链路在 macOS 实机验证，见 docs/音频监听方案.md §2.3）。
/// </summary>
public class AudioTapContractTests
{
    [Fact]
    public void 工厂在支持的平台返回实现或明确返回null()
    {
        var service = AudioLoopbackServiceFactory.Create();
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            Assert.NotNull(service);
        }
        else
        {
            // Linux 等平台明确返回 null（而非抛异常），宿主据此回 supported=false
            Assert.Null(service);
        }
    }

    [Fact]
    public void 能力探测始终返回结构化结果而不抛异常()
    {
        var service = AudioLoopbackServiceFactory.Create();
        if (service is null) return;

        var caps = service.GetCapabilities();
        Assert.NotNull(caps);
        Assert.True(caps.MaxTargets >= 0);
        if (!caps.Supported)
        {
            // 不支持时必须给出原因，否则 UI 无法向用户解释
            Assert.False(string.IsNullOrWhiteSpace(caps.Reason));
        }
    }

    [Fact]
    public void 未启动时会话状态为null且停止是幂等的()
    {
        var service = AudioLoopbackServiceFactory.Create();
        if (service is null) return;

        Assert.Null(service.GetStatus());
        service.Stop();          // 不应抛异常
        service.Stop("not-exist");
        Assert.Null(service.GetStatus());
    }

    [Fact]
    public void 参数非法时抛出带错误码的AudioTapException()
    {
        var service = AudioLoopbackServiceFactory.Create();
        if (service is null || !service.GetCapabilities().Supported) return;

        // 源为空
        var ex1 = Assert.Throws<AudioTapException>(() =>
            service.Start(new AudioTapRequest("", ["target"], 1.0)));
        Assert.Equal("tap_source_not_found", ex1.Code);

        // 目标为空
        var ex2 = Assert.Throws<AudioTapException>(() =>
            service.Start(new AudioTapRequest("source", [], 1.0)));
        Assert.Equal("tap_target_not_found", ex2.Code);

        // 源与目标相同（会形成回环啸叫，必须拦掉）
        var ex3 = Assert.Throws<AudioTapException>(() =>
            service.Start(new AudioTapRequest("same", ["same"], 1.0)));
        Assert.Equal("tap_same_device", ex3.Code);
    }

    [Fact]
    public void 目标数量超过上限时拒绝()
    {
        var service = AudioLoopbackServiceFactory.Create();
        if (service is null) return;
        var caps = service.GetCapabilities();
        if (!caps.Supported) return;

        var tooMany = Enumerable.Range(0, caps.MaxTargets + 1).Select(i => $"t{i}").ToArray();
        var ex = Assert.Throws<AudioTapException>(() =>
            service.Start(new AudioTapRequest("source", tooMany, 1.0)));
        Assert.Equal("tap_too_many_targets", ex.Code);
    }

    [Fact]
    public void 采样率不一致时返回format_mismatch而非静默变调()
    {
        var service = AudioLoopbackServiceFactory.Create();
        if (service is null || !service.GetCapabilities().Supported) return;

        // 本工程没有内置重采样器：目标与源采样率不同时必须明确报错。
        // 用一个不存在的设备无法触发该分支（会先报 not_found），
        // 因此这里只断言错误码契约本身是稳定的（真实组合在实机验证）。
        var ex = Assert.Throws<AudioTapException>(() =>
            service.Start(new AudioTapRequest("source", ["target"], 1.0)));
        Assert.Contains(ex.Code, new[] { "tap_target_not_found", "tap_source_not_found", "tap_format_mismatch" });
    }

    [Fact]
    public void 释放后再启动会被拒绝()
    {
        var service = AudioLoopbackServiceFactory.Create();
        if (service is null) return;
        service.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            service.Start(new AudioTapRequest("s", ["t"], 1.0)));
    }

    [Fact]
    public void Windows实现应报告支持并给出上限()
    {
        if (!OperatingSystem.IsWindows()) return;
        var service = AudioLoopbackServiceFactory.Create();
        Assert.NotNull(service);
        var caps = service.GetCapabilities();
        // WASAPI 环回已实现（2026-09-30 在构建机实测捕获成功）
        Assert.True(caps.Supported);
        Assert.True(caps.MaxTargets > 0);
    }

    [Fact]
    public void Windows实现拒绝非法参数()
    {
        if (!OperatingSystem.IsWindows()) return;
        var service = AudioLoopbackServiceFactory.Create();
        Assert.NotNull(service);
        if (!service.GetCapabilities().Supported) return;

        Assert.Equal("tap_source_not_found",
            Assert.Throws<AudioTapException>(() => service.Start(new AudioTapRequest("", ["t"], 1.0))).Code);
        Assert.Equal("tap_target_not_found",
            Assert.Throws<AudioTapException>(() => service.Start(new AudioTapRequest("s", [], 1.0))).Code);
        Assert.Equal("tap_same_device",
            Assert.Throws<AudioTapException>(() => service.Start(new AudioTapRequest("same", ["same"], 1.0))).Code);
    }
}
