using System.Diagnostics;

namespace LingXi.Flutter.NativeHost;

/// <summary>
/// 本地大模型服务（llama.cpp）管理。
/// 安全约束：仅允许操作两个固定计划任务名与一个固定进程名，
/// 不接受任意命令行；所有子进程以无 shell 方式拉起。
/// 任务状态读取走 Task Scheduler COM（Schedule.Service），与系统区域设置无关。
/// </summary>
internal static class LlmService
{
    internal const string TaskSrv = "llama_srv";
    internal const string TaskFn = "llama_fn";
    private const string ServerImage = "llama-server";
    private static readonly string[] KnownTasks = { TaskSrv, TaskFn };

    internal static object State()
    {
        var tasks = new Dictionary<string, string>(StringComparer.Ordinal);
        var available = false;
        foreach (var name in KnownTasks)
        {
            var state = QueryTaskState(name);
            tasks[name] = state;
            if (state != "missing") available = true;
        }

        var running = Process.GetProcessesByName(ServerImage).Length > 0;
        return new { processRunning = running, tasksAvailable = available, tasks };
    }

    internal static object Execute(string action, string? task)
    {
        if (action is not ("start" or "stop" or "switch"))
            throw new ArgumentException("Invalid action");
        if (action != "stop" && !KnownTasks.Contains(task))
            throw new ArgumentException("Invalid task");

        var issued = new List<object>();
        if (action == "stop")
        {
            foreach (var name in KnownTasks)
                issued.Add(Run("schtasks.exe", "/end", "/tn", name));
            issued.Add(KillServer());
            WaitForServerExit(TimeSpan.FromSeconds(5));
            return new { action, task = string.Empty, issued, stopped = !ServerRunning() };
        }

        var other = task == TaskSrv ? TaskFn : TaskSrv;
        issued.Add(Run("schtasks.exe", "/end", "/tn", other));
        issued.Add(KillServer());
        // 等旧进程完全退出，避免显存/文件锁未释放导致新实例启动失败。
        WaitForServerExit(TimeSpan.FromSeconds(5));
        issued.Add(Run("schtasks.exe", "/run", "/tn", task!));

        // 计划任务的 /run 可能被静默丢弃（例如上一实例仍在停止、策略为 IgnoreNew）。
        // 验证任务进入 Running，否则 /end 清理残留实例后重试一次。
        var started = WaitForTaskRunning(task!, TimeSpan.FromSeconds(8));
        if (!started)
        {
            issued.Add(Run("schtasks.exe", "/end", "/tn", task!));
            Thread.Sleep(1000);
            issued.Add(Run("schtasks.exe", "/run", "/tn", task!));
            started = WaitForTaskRunning(task!, TimeSpan.FromSeconds(8));
        }

        return new { action, task = task ?? string.Empty, issued, started };
    }

    private static bool ServerRunning() =>
        Process.GetProcessesByName(ServerImage).Length > 0;

    private static bool WaitForServerExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!ServerRunning()) return true;
            Thread.Sleep(500);
        }
        return !ServerRunning();
    }

    private static bool WaitForTaskRunning(string name, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (QueryTaskState(name) == "running") return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static object KillServer() => Run("taskkill.exe", "/f", "/im", $"{ServerImage}.exe");

    private static object Run(string fileName, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(startInfo)!;
            if (!process.WaitForExit(milliseconds: 15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new { cmd = fileName, ok = false, exitCode = -1 };
            }

            // taskkill 在进程不存在时返回 128，对幂等停止/切换而言视为成功。
            var ok = process.ExitCode == 0 ||
                     (fileName == "taskkill.exe" && process.ExitCode == 128);
            return new { cmd = fileName, ok, exitCode = process.ExitCode };
        }
        catch
        {
            return new { cmd = fileName, ok = false, exitCode = -1 };
        }
    }

    private static string QueryTaskState(string name)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return "unknown";
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            dynamic task = folder.GetTask(name);
            // TASK_STATE: 0 Unknown, 1 Disabled, 2 Queued, 3 Ready, 4 Running
            return (int)task.State switch
            {
                1 => "disabled",
                2 => "queued",
                3 => "ready",
                4 => "running",
                _ => "unknown",
            };
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002))
        {
            return "missing";
        }
        catch
        {
            return "unknown";
        }
    }
}
