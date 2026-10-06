using System.Diagnostics;

namespace LingXi.Flutter.NativeHost;

/// <summary>
/// 远程控制 Mac 上的 rvc-mlx-server。
///
/// 为什么走 SSH：服务跑在 Mac，而 Windows 工具箱要能启停它；
/// 服务没跑时 HTTP 本来就不通，所以不能用 HTTP 下发启停。
/// Mac 侧用 launchd 的**按需语义**（plist 注册但无 RunAtLoad/KeepAlive），
/// 不产生常驻进程。（2026-10-06 定）
///
/// 安全约束：
/// - 只允许 start / stop 两个动作（白名单），不接受任意远程命令。
/// - 主机/用户/密钥来自设置，**不硬编码、不进仓库**。
/// - 子进程无 shell（参数用 ArgumentList 传），避免拼接注入。
/// - 隐藏窗口（CreateNoWindow）不弹黑框。
/// </summary>
internal static class RvcService
{
    internal const string DefaultHost = "192.168.5.66";
    internal const string DefaultUser = "lingxi";
    internal const string Label = "dev.lingxi.rvcServer";

    private const int TimeoutMs = 20_000;

    /// <summary>
    /// 只读状态。走 SSH 问 Mac 三件事：进程在不在、label 注册没、能不能启停。
    /// 一次 ssh 调用拿全部（比三次往返快，也少一次握手）。
    /// </summary>
    internal static object State(string? host, string? user)
    {
        var h = Normalize(host, DefaultHost);
        var u = Normalize(user, DefaultUser);

        // 远端一段固定脚本：输出 KEY=VALUE，本地解析。
        // 只读，不改任何状态。
        var remote =
            "pgrep -f rvc-mlx-server >/dev/null && echo RUNNING=1 || echo RUNNING=0; " +
            $"launchctl print gui/$(id -u)/{Label} >/dev/null 2>&1 && echo REGISTERED=1 || echo REGISTERED=0";
        var (code, stdout, stderr) = Ssh(h, u, remote);
        if (code != 0)
            throw new InvalidOperationException($"SSH 连接失败（{u}@{h}）：{Clip(stderr.Length > 0 ? stderr : stdout)}");

        var running = stdout.Contains("RUNNING=1", StringComparison.Ordinal);
        var registered = stdout.Contains("REGISTERED=1", StringComparison.Ordinal);
        return new
        {
            serverRunning = running,
            serviceRegistered = registered,
            controllable = registered,
            controlMode = "ssh",
            host = h,
            user = u,
            label = Label,
        };
    }

    /// <summary>远程启停。action 只接受 start|stop。</summary>
    internal static object Switch(string action, string? host, string? user)
    {
        if (action is not ("start" or "stop"))
            throw new ArgumentException("action 只支持 start 或 stop");

        var h = Normalize(host, DefaultHost);
        var u = Normalize(user, DefaultUser);
        var (code, stdout, stderr) = Ssh(h, u, $"launchctl {action} {Label}");
        // launchctl 对「已经在这个状态」返回非零属正常，如实回传不抛错。
        return new
        {
            action,
            exitCode = code,
            stdout = Clip(stdout),
            stderr = Clip(stderr),
            host = h,
            user = u,
        };
    }

    /// <summary>
    /// 调用系统 ssh。用 BatchMode 强制密钥认证（不弹密码框、不阻塞），
    /// 用 ConnectTimeout 防止网络不通时干等。
    /// </summary>
    private static (int Code, string Stdout, string Stderr) Ssh(string host, string user, string remote)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-o", "BatchMode=yes",
                     "-o", "StrictHostKeyChecking=accept-new",
                     "-o", "ConnectTimeout=8",
                     $"{user}@{host}",
                     remote,
                 })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 ssh.exe（Windows 是否自带 OpenSSH 客户端？）");

        // 异步读，避免大输出时与 WaitForExit 死锁。
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(TimeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw new TimeoutException($"SSH 超时（{TimeoutMs}ms）");
        }
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    /// <summary>
    /// 只允许主机名/用户名里的安全字符，挡住把参数当命令注入的可能
    /// （虽然走 ArgumentList 不拼 shell，但主机名会进 ssh 的目标串）。
    /// </summary>
    private static string Normalize(string? value, string fallback)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return fallback;
        foreach (var ch in v)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not ('.' or '-' or '_'))
                throw new ArgumentException("主机名/用户名含非法字符");
        }
        return v;
    }

    private static string Clip(string s)
        => s.Length <= 400 ? s.Trim() : s[..400].Trim() + "…";
}
