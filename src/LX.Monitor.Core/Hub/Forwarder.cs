using System.Text;
using System.Text.Json;

namespace LingXi.Monitor.Core;

/// <summary>转发中继：同一份上报转发给 Yunzai（可选开关，开发文档 9.3 / 9.7）。</summary>
public sealed class Forwarder
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly string _url;
    private readonly string? _token;

    public Forwarder(string url, string? token)
    {
        _url = url;
        _token = token;
    }

    /// <summary>异步转发；失败静默（不影响本地收数）。</summary>
    public async Task ForwardAsync(Snapshot snapshot, string? token)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(snapshot, SnapshotJson.Options),
                Encoding.UTF8,
                "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = content };
            request.Headers.TryAddWithoutValidation("X-SM-Token", token ?? _token ?? string.Empty);
            request.Headers.TryAddWithoutValidation("User-Agent", "lingxi-toolbox/1.0");
            await Http.SendAsync(request);
        }
        catch
        {
            // 转发失败不影响本地
        }
    }
}
