namespace LingXi.Flutter.NativeHost;

/// <summary>
/// RVC 服务进程控制的 **Windows 侧降级实现**。
///
/// 服务跑在 Mac 上，Windows 工具箱管不到它的进程 —— 要远程启停就得开 SSH，
/// 而实测（2026-10-06）Windows 的 `ssh.exe` 在 .NET 里三种调用方式三种结果
/// （ArgumentList 被拒 exit=255 / Arguments 卡 30s+ / cmd 手动正常 0.6s），
/// 配合桥的 15s 请求超时会把 NativeHost 反复杀掉重启。
///
/// 权衡后决定：**Windows 端只读**。
/// - 服务状态仍能通过 HTTP（/api/models）看到 —— 那是控制面的正常通路。
/// - 进程级启停只在 Mac 本机做（`LX.Flutter.NativeHost.Mac` 走 launchctl）。
/// - 前端把这个区域置灰并说明「仅服务器本机可操作」，与 lx.llm 的降级同一套路。
///
/// 这样 Windows 端**不引入任何常驻依赖**（不用 sshd、不用密钥、不用 known_hosts），
/// 也不会再出现超时杀宿主的问题。
/// </summary>
internal static class RvcService
{
    internal static object State() => new
    {
        // 进程状态未知（Windows 探不到 Mac 的进程表）；
        // 前端据 controllable=false 走降级分支，不据 serverRunning 做判断。
        serverRunning = false,
        serviceRegistered = false,
        controllable = false,
        controlMode = "unsupported",
        reason = "仅服务器本机（Mac）可操作",
    };

    /// <summary>
    /// Windows 上不支持启停。**抛异常而不是静默返回 false** ——
    /// 前端若误调（例如按钮没禁用干净）要能立刻发现，而不是以为操作成功了。
    /// </summary>
    internal static object Switch(string action) => throw new NotSupportedException(
        $"Windows 端不支持远程启停（收到 action={action}）；请在 Mac 上操作，或用 Mac 版工具箱");
}
