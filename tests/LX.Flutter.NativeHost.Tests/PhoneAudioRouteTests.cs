using LingXi.Flutter.NativeHost;
using Xunit;

namespace LX.Flutter.NativeHost.Tests;

public sealed class PhoneAudioRouteTests
{
    [Fact]
    public void ResolveRenderEndpoint_neverFallsBackToPhysicalSpeakers()
    {
        var devices = new[]
        {
            new WaveDevice(0, "Speakers (Realtek Audio)"),
            new WaveDevice(1, "Headphones (USB DAC)"),
        };

        Assert.Null(PhoneAudioRoute.ResolveRenderEndpoint(devices));
    }

    [Fact]
    public void ResolveRenderEndpoint_selectsOnlyUniqueLingXiEndpoint()
    {
        var devices = new[]
        {
            new WaveDevice(0, "Speakers (Realtek Audio)"),
            new WaveDevice(4, "LingXi Virtual Microphone Render"),
        };

        Assert.Equal(4, PhoneAudioRoute.ResolveRenderEndpoint(devices)?.Id);
    }

    [Fact]
    public void ResolveRenderEndpoint_rejectsAmbiguousVirtualDevices()
    {
        var devices = new[]
        {
            new WaveDevice(4, "LingXi Virtual Microphone Render"),
            new WaveDevice(5, "LingXi Virtual Microphone Backup"),
        };

        Assert.Null(PhoneAudioRoute.ResolveRenderEndpoint(devices));
    }

    [Fact]
    public void ResolveCaptureEndpoint_requiresMatchingCaptureDevice()
    {
        Assert.Null(PhoneAudioRoute.ResolveCaptureEndpoint(new[] { "Microphone (Realtek Audio)" }));
        Assert.Equal("LingXi Virtual Microphone Capture",
            PhoneAudioRoute.ResolveCaptureEndpoint(new[] { "Microphone (Realtek Audio)", "LingXi Virtual Microphone Capture" }));
    }
}
