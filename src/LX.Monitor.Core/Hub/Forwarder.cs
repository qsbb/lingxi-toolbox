using System.Text;
using System.Text.Json;

namespace LingXi.Monitor.Core;

/// <summary>转发中继：同一份上报转发给 Yunzai（可选开关）。</summary>
public sealed class Forwarder : IDisposable
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
    }) { Timeout = TimeSpan.FromSeconds(5) };
    private readonly string _url;
    private readonly string? _token;
    private readonly CancellationTokenSource _cts = new();

    public Forwarder(string url, string? token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") || parsed.Host.Length == 0)
            throw new ArgumentException("Forward URL must be an absolute HTTP(S) URL", nameof(url));
        _url = parsed.ToString();
        _token = token?.Trim();
    }

    /// <summary>异步转发；失败静默且不会使用入站 Hub token。</summary>
    public async Task ForwardAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(snapshot, SnapshotJson.Options),
                Encoding.UTF8,
                "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = content };
            if (!string.IsNullOrWhiteSpace(_token)) request.Headers.TryAddWithoutValidation("X-SM-Token", _token);
            request.Headers.TryAddWithoutValidation("User-Agent", "lingxi-toolbox/2.0");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            var buffer = new byte[8192];
            var total = 0;
            while (total <= 64 * 1024)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(), linked.Token).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Hub 停止或请求超时不影响本地收数。
        }
        catch
        {
            // 转发失败不影响本地。
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
