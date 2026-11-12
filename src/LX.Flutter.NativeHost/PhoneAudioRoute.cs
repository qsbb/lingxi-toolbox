namespace LingXi.Flutter.NativeHost;

/// <summary>
/// Resolves only the dedicated LingXi virtual microphone pair. Never fall back
/// to the Windows default speaker: this route is for a system microphone, not monitoring.
/// </summary>
internal static class PhoneAudioRoute
{
    internal const string FriendlyName = "LingXi Virtual Microphone";

    internal static WaveDevice? ResolveRenderEndpoint(IEnumerable<WaveDevice> devices)
    {
        var matches = devices
            .Where(x => x.Name.Contains(FriendlyName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static string? ResolveCaptureEndpoint(IEnumerable<string> deviceNames)
    {
        var matches = deviceNames
            .Where(x => x.Contains(FriendlyName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

internal sealed record WaveDevice(int Id, string Name);
