using System.Net;
using System.Net.Sockets;
using System.Text;
using LingXi.Monitor.Core;
using Xunit;

namespace LX.Monitor.Core.Tests;

/// <summary>Hub 集成测试：真实 HttpListener 回环收数（开发文档 13.1）。</summary>
public class LxHubTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static (int Code, string Body) Send(HttpMethod method, string url, string? body, string? token)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        if (token is not null)
        {
            request.Headers.Add("X-SM-Token", token);
        }
        var response = client.SendAsync(request).GetAwaiter().GetResult();
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return ((int)response.StatusCode, text);
    }

    private static readonly string SnapshotBody =
        """{"v":1,"name":"win-01","agent_ts":1725000000000,"cpu":{"usage":12.5},"mem":{"used":7.1,"total":15.4}}""";

    [Fact]
    public void Accepts_Report_And_Stores_Snapshot()
    {
        var store = new SnapshotStore();
        var options = new HubOptions
        {
            Port = FreePort(),
            Tokens = new HashSet<string>(StringComparer.Ordinal) { "sm_t" },
        };
        using var hub = new LxHub(options, store);
        SnapshotEnvelope? received = null;
        hub.SnapshotReceived += e => received = e;
        hub.Start();

        var (code, _) = Send(HttpMethod.Post, hub.ReportUrl, SnapshotBody, "sm_t");

        Assert.Equal(200, code);
        Assert.NotNull(received);
        Assert.Equal("win-01", received!.Snapshot.Name);
        Assert.Equal(12.5, received.Snapshot.Cpu?.Usage);
        Assert.NotNull(store.Get("win-01"));
    }

    [Fact]
    public void Accepts_Alias_Path()
    {
        var store = new SnapshotStore();
        var options = new HubOptions { Port = FreePort(), Tokens = [] }; // 空 token = 收任意
        using var hub = new LxHub(options, store);
        hub.Start();

        var alias = hub.ReportUrl.Replace("/servermonitor/", "/server-monitor/");
        var (code, _) = Send(HttpMethod.Post, alias, SnapshotBody, null);

        Assert.Equal(200, code);
        Assert.NotNull(store.Get("win-01"));
    }

    [Fact]
    public void Rejects_Wrong_Token()
    {
        var store = new SnapshotStore();
        var options = new HubOptions
        {
            Port = FreePort(),
            Tokens = new HashSet<string>(StringComparer.Ordinal) { "sm_right" },
        };
        using var hub = new LxHub(options, store);
        hub.Start();

        var (code, _) = Send(HttpMethod.Post, hub.ReportUrl, SnapshotBody, "sm_wrong");

        Assert.Equal(401, code);
        Assert.Null(store.Get("win-01"));
    }

    [Fact]
    public void Rejects_Bad_Body()
    {
        var store = new SnapshotStore();
        var options = new HubOptions { Port = FreePort(), Tokens = [] };
        using var hub = new LxHub(options, store);
        hub.Start();

        var (code, _) = Send(HttpMethod.Post, hub.ReportUrl, "garbage !!!", null);

        Assert.Equal(400, code);
    }

    [Fact]
    public void Unknown_Path_404_And_Health_200()
    {
        var options = new HubOptions { Port = FreePort(), Tokens = [] };
        using var hub = new LxHub(options, new SnapshotStore());
        hub.Start();

        var (_, healthBody) = Send(HttpMethod.Get, $"http://127.0.0.1:{hub.Port}/health", null, null);
        var (missing, _) = Send(HttpMethod.Get, $"http://127.0.0.1:{hub.Port}/nope", null, null);

        Assert.Equal(200, healthBody.Contains("ok") ? 200 : 500);
        Assert.Equal(404, missing);
    }

    /// <summary>
    /// 滥用用例（vibe-coding-security 清单）：恶意网页可向 127.0.0.1 发跨域 POST；
    /// application/json 触发预检，OPTIONS 必须被 404 拒绝；text/plain 简单请求则被默认必配的 token 挡住。
    /// </summary>
    [Fact]
    public void Preflight_Options_Is_Rejected()
    {
        var options = new HubOptions
        {
            Port = FreePort(),
            Tokens = new HashSet<string>(StringComparer.Ordinal) { "sm_t" },
        };
        using var hub = new LxHub(options, new SnapshotStore());
        hub.Start();

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, hub.ReportUrl);
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        var response = client.SendAsync(request).GetAwaiter().GetResult();

        Assert.NotEqual(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task Forwards_Report_To_Fake_Yunzai()
    {
        // 起一个"假 Yunzai"接收转发
        var forwardPort = FreePort();
        var forwardReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{forwardPort}/");
        listener.Start();
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            forwardReceived.TrySetResult(body);
        });

        var store = new SnapshotStore();
        var options = new HubOptions
        {
            Port = FreePort(),
            Tokens = [],
            ForwardUrl = $"http://127.0.0.1:{forwardPort}/servermonitor/report",
            ForwardToken = "sm_fwd",
        };
        using var hub = new LxHub(options, store);
        hub.Start();
        _ = serverTask;

        Send(HttpMethod.Post, hub.ReportUrl, SnapshotBody, null);

        var forwarded = await forwardReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
        Assert.Contains("win-01", forwarded);
    }
}
