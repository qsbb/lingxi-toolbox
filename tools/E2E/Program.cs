using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LingXi.Monitor.Core;

// E2E 功能测试：真实 servermonitor agent.mjs（node）→ LX Hub 收数端。
// 验证协议 100% 兼容：X-SM-Token 鉴权、/servermonitor/report 路由、快照 v:1 解析、在线判定。

const string Token = "sm_e2e_token";

var port = FreePort();
var store = new SnapshotStore();
store.SetOfflineTimeout(TimeSpan.FromSeconds(30));
using var hub = new LxHub(
    new HubOptions { Port = port, Tokens = new HashSet<string>(StringComparer.Ordinal) { Token } },
    store);

var firstReport = new TaskCompletionSource<SnapshotEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
hub.SnapshotReceived += e => firstReport.TrySetResult(e);
hub.Log += msg => Console.WriteLine($"[hub] {msg}");
hub.Start();
Console.WriteLine($"LX Hub 已启动: {hub.ReportUrl}（token={Token}）");

var agentScript = args.Length > 0 ? args[0] : "/data/dsh/home/dsh/lingxi_research/servermonitor/agent/agent.mjs";
var psi = new ProcessStartInfo
{
    FileName = "node",
    UseShellExecute = false,
    CreateNoWindow = true,
};
psi.ArgumentList.Add(agentScript);
psi.Environment["SM_NAME"] = "lx-e2e";
psi.Environment["SM_TOKEN"] = Token;
psi.Environment["SM_REPORT_URL"] = hub.ReportUrl;
psi.Environment["SM_INTERVAL"] = "2";
psi.Environment["SM_SLOW_INTERVAL"] = "4";

using var agent = Process.Start(psi);
Console.WriteLine($"agent 已启动 pid={agent?.Id} script={agentScript}");

var winner = await Task.WhenAny(firstReport.Task, Task.Delay(TimeSpan.FromSeconds(25)));
if (winner != firstReport.Task)
{
    Console.WriteLine("E2E 失败：25s 内未收到 agent 上报");
    return 1;
}

var envelope = await firstReport.Task;
var snapshot = envelope.Snapshot;
await Task.Delay(1500); // 让 agent 至少上报两轮

Console.WriteLine("=== 收到快照（真实 agent 产出）===");
Console.WriteLine($"  name      = {snapshot.Name}");
Console.WriteLine($"  os        = {snapshot.Os?.Platform} / {snapshot.Os?.Distro} / {snapshot.Os?.Arch}");
Console.WriteLine($"  cpu       = {snapshot.Cpu?.Model} · {snapshot.Cpu?.Cores} 核 · {snapshot.Cpu?.Usage}% · temp={snapshot.Cpu?.Temp}");
Console.WriteLine($"  mem       = {snapshot.Mem?.Used} / {snapshot.Mem?.Total} GiB");
Console.WriteLine($"  disks     = {snapshot.Disks?.Count} 块");
Console.WriteLine($"  net       = {snapshot.Net?.Iface} ↓{snapshot.Net?.RxSec} MiB/s");
Console.WriteLine($"  token     = {envelope.Token}");
Console.WriteLine($"  在线判定  = {store.IsOnline(snapshot.Name, DateTimeOffset.Now)}（offline_timeout=30s）");
Console.WriteLine($"  库内机器  = [{string.Join(", ", store.GetNames())}]");

var passed =
    string.Equals(snapshot.Name, "lx-e2e", StringComparison.Ordinal) &&
    snapshot.Version == 1 &&
    store.IsOnline(snapshot.Name, DateTimeOffset.Now);

agent?.Kill(entireProcessTree: true);
Console.WriteLine(passed ? "E2E 通过：agent → LX Hub 协议链路正常" : "E2E 失败：快照字段与预期不符");
return passed ? 0 : 1;

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var p = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return p;
}
