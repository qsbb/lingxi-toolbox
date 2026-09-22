using System.Net;
using System.Text;
using LingXi.Monitor.Core;
using Xunit;

namespace LX.Monitor.Core.Tests;

/// <summary>
/// 上报器契约测试。
/// 背景：服务端 0.1.19+ 明确"2xx 但空 body 视为失败"，
/// 只判状态码会把假成功当成功（最难排查的一类故障）。
/// </summary>
public class SnapshotReporterTests
{
    [Theory]
    [InlineData("{\"ok\":true}", true)]
    [InlineData("{\"ok\":true,\"name\":\"LXYY\",\"auto\":false}", true)]
    [InlineData("{\"ok\":false,\"msg\":\"empty snapshot\"}", false)]
    [InlineData("{\"pending\":true}", false)]        // 缺 ok
    [InlineData("", false)]                          // 空 body ← 契约要求判失败
    [InlineData("   ", false)]
    [InlineData("OK", false)]                        // 非 JSON
    [InlineData("[{\"ok\":true}]", false)]           // 非对象
    public void IsOkTrue_Follows_Server_Contract(string body, bool expected)
        => Assert.Equal(expected, SnapshotReporter.IsOkTrue(body));

    [Theory]
    [InlineData("{\"ok\":true,\"pending\":true}", true)]
    [InlineData("{\"ok\":true,\"pending\":false}", false)]
    [InlineData("{\"ok\":true}", false)]
    [InlineData("", false)]
    public void IsPending_Detects_Bind_Pending(string body, bool expected)
        => Assert.Equal(expected, SnapshotReporter.IsPending(body));

    [Theory]
    [InlineData("{\"ok\":false,\"msg\":\"empty snapshot\"}", "empty snapshot")]
    [InlineData("{\"ok\":false,\"msg\":\"token invalid\"}", "token invalid")]
    [InlineData("{\"ok\":false}", "")]
    [InlineData("not json", "")]
    public void ExtractServerMessage_Pulls_Msg_Field(string body, string expected)
        => Assert.Equal(expected, SnapshotReporter.ExtractServerMessage(body));

    // ---- 真实 HTTP 往返：覆盖"200 空 body 必须判失败"这条关键契约 ----

    [Fact]
    public async Task Report_Fails_When_Server_Returns_200_With_Empty_Body()
    {
        var result = await RunAgainstServerAsync(HttpStatusCode.OK, string.Empty);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Report_Succeeds_When_Server_Returns_Ok_True()
    {
        var result = await RunAgainstServerAsync(HttpStatusCode.OK,
            "{\"ok\":true,\"name\":\"probe\",\"auto\":false}");
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Report_Fails_When_Body_Says_Not_Ok()
    {
        var result = await RunAgainstServerAsync(HttpStatusCode.OK, "{\"ok\":false}");
        Assert.False(result.Ok);
    }

    private static async Task<(bool Ok, TimeSpan Elapsed)> RunAgainstServerAsync(
        HttpStatusCode status, string body)
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/servermonitor/report/");
        listener.Start();

        var serving = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var payload = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = payload.Length;
            if (payload.Length > 0) await context.Response.OutputStream.WriteAsync(payload);
            context.Response.Close();
        });

        var collector = new SystemMetricsCollector { MachineName = "reporter-test" };
        using var reporter = new SnapshotReporter(new ReporterTarget
        {
            Url = $"http://127.0.0.1:{port}/servermonitor/report",
            Token = "sm_0123456789abcdef0123456789abcdef",
            Name = "reporter-test",
            TimeoutMs = 5000,
        }, collector);

        var result = await reporter.ReportOnceAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        return result;
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
