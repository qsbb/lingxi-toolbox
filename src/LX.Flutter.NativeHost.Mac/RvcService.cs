using System.Diagnostics;
using System.Text;

namespace LingXi.Flutter.NativeHost.Mac;

/// <summary>
/// 控制本机（Mac）上的 rvc-mlx-server 进程。
///
/// 设计约束（2026-10-06 定）：
/// - **不要常驻进程**：服务不做开机自启，靠 launchd 的按需语义
///   （plist 注册着但去掉 RunAtLoad/KeepAlive，只有 `launchctl start` 才拉起）。
/// - 因此状态判定**不能走 HTTP** —— 服务没跑时 HTTP 本来就不通。
///   这里用 `launchctl list` 看注册状态 + 进程探测看实际运行。
/// - 令牌不在这里出现：服务由 wrapper 起，wrapper 自己从
///   `~/.config/lingxi/rvc_token`（mode 600）读。
/// </summary>
internal static class RvcService
{
    internal const string Label = "dev.lingxi.rvcServer";

    private static readonly string WrapperPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "bin", "rvc-server-launch");

    /// <summary>进程名（rvc-mlx-server 的入口是 python，这里按命令行匹配）。</summary>
    private const string ProcessHint = "rvc-mlx-server";

    private const int CommandTimeoutMs = 15_000;

    /// <summary>只读状态：进程是否在跑 + launchd 是否已注册（决定能不能启停）。</summary>
    internal static object State()
    {
        var running = IsServerRunning();
        return new
        {
            // 进程在跑 = 服务可用
            serverRunning = running,
            // launchd 里注册了才允许 start/stop（否则要先人工 load plist）
            serviceRegistered = IsRegistered(),
            // 本机可操作：Mac 有 launchctl，直接管
            controllable = true,
            // 前端据此显示「仅服务器本机可操作」之外的提示
            controlMode = "local",
            label = Label,
            wrapperPresent = File.Exists(WrapperPath),
        };
    }

    /// <summary>启动/停止服务。action 只接受 start|stop（白名单，不接受任意命令）。</summary>
    internal static object Switch(string action)
    {
        if (action is not ("start" or "stop"))
            throw new ArgumentException("action 只支持 start 或 stop");

        if (!File.Exists(WrapperPath))
            throw new FileNotFoundException($"找不到启动脚本：{WrapperPath}");

        var (code, stdout, stderr) = RunLaunchctl(action);
        // launchctl 对已启动/已停止的任务返回非零是正常的，这里如实回传不抛错。
        return new
        {
            action,
            exitCode = code,
            // 命令发出后立刻回；就绪确认交给前端轮询 rvc.state
            stdout = Clip(stdout),
            stderr = Clip(stderr),
            serverRunning = IsServerRunning(),
        };
    }

    /// <summary>服务是否在跑：按命令行匹配，避免误伤系统里别的 python 进程。</summary>
    private static bool IsServerRunning()
    {
        try
        {
            var (code, stdout, _) = Run("/bin/ps", new[] { "-Ao", "command" });
            if (code != 0) return false;
            foreach (var line in stdout.Split('\n'))
            {
                if (line.Contains(ProcessHint, StringComparison.Ordinal)) return true;
            }
        }
        catch
        {
            // ps 不可用按未运行处理
        }
        return false;
    }

    /// <summary>
    /// launchd 中是否已注册这个 label。
    ///
    /// ⚠️ **不要用 `launchctl list` 过滤**：在本进程内实测它的 stdout 会被
    /// launchd 的调试输出污染 —— 只拿到 419B 的 plist 文本
    /// （`"ProgramArguments" = (...)`），真正的服务列表根本没进来，
    /// 于是 label 匹配恒为 false。（2026-10-06 踩，查了很久）
    ///
    /// 改用 `launchctl print gui/&lt;uid&gt;/&lt;label&gt;`：注册返回 0、
    /// 未注册返回 113，输出稳定且不受污染。
    /// </summary>
    private static bool IsRegistered()
    {
        try
        {
            var (code, _, _) = Run("/bin/launchctl",
                new[] { "print", $"gui/{UserId}/{Label}" });
            return code == 0;
        }
        catch
        {
            // 读不到按未注册处理
        }
        return false;
    }

    /// <summary>当前 uid，拼 `gui/&lt;uid&gt;/&lt;label&gt;` 用。</summary>
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern uint getuid();

    private static uint UserId => getuid();

    private static (int Code, string Stdout, string Stderr) RunLaunchctl(string verb)
        => Run("/bin/launchctl", new[] { verb, Label });

    /// <summary>
    /// 无 shell 方式执行固定命令（参数以数组传递，不做字符串拼接）。
    ///
    /// ⚠️ 必须**异步读** stdout/stderr：`launchctl list` 在负载高时有几十 KB 输出，
    /// 同步 `ReadToEnd()` 会先把管道缓冲写满，与后面的 `WaitForExit` 互相等 →
    /// 实测只能读到前 ~4KB，label 恰好在被截断的部分，`serviceRegistered`
    /// 因此恒为 false。（2026-10-06 踩）
    /// </summary>
    private static (int Code, string Stdout, string Stderr) Run(string file, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动 {file}");

        // 先挂上异步读，再等退出 —— 顺序反了就会死锁。
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(CommandTimeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw new TimeoutException($"{file} 超时（{CommandTimeoutMs}ms）");
        }
        // ⚠️ 这里**不能**给 WaitAll 加超时：超时后 .Result 拿到的是
        // **部分**输出（实测 20KB 的 launchctl list 只拿到 419B），
        // 静默丢数据比拼错更难查。进程已经退出，读任务必然会完成，
        // 直接等它（外层有 CommandTimeoutMs 兜底）。
        var stdout = outTask.GetAwaiter().GetResult();
        var stderr = errTask.GetAwaiter().GetResult();
        return (proc.ExitCode, stdout, stderr);
    }

    private static string Clip(string s)
        => s.Length <= 400 ? s.Trim() : s[..400].Trim() + "…";
}
