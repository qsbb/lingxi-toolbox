using System.Diagnostics;
using System.Management;
using System.Text;

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

    /// <summary>llama-server 启动脚本固定把 stdout/stderr 重定向到这个文件。</summary>
    private const string LogFileName = "server.log";
    private const string FallbackLogPath = @"D:\AI\llamacpp\server.log";
    private const int MaxLogBytes = 96 * 1024;
    private const int DefaultMaxLogLines = 400;
    private const int HardMaxLogLines = 2000;

    /// <summary>
    /// 只读探测：从运行中的 llama-server.exe 进程命令行解析连接信息
    /// （host/port/api-key/别名/模型路径），供 Flutter 侧"一键配置"。
    /// </summary>
    internal static object Detect()
    {
        string? commandLine = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE Name = 'llama-server.exe'");
            foreach (var item in searcher.Get())
            {
                commandLine = item["CommandLine"]?.ToString();
                if (!string.IsNullOrWhiteSpace(commandLine)) break;
            }
        }
        catch
        {
            // WMI 不可用视为未检测到，不抛错。
        }

        if (string.IsNullOrWhiteSpace(commandLine)) return new { found = false };

        var tokens = Tokenize(commandLine);
        var portRaw = ExtractValue(tokens, "--port") ?? "8080";
        var port = int.TryParse(portRaw, out var p) && p is > 0 and <= 65535 ? p : 8080;
        var hostRaw = ExtractValue(tokens, "--host") ?? "127.0.0.1";
        // 0.0.0.0 / :: 是监听语义，客户端连接要用回环地址。
        var host = hostRaw is "0.0.0.0" or "::" or "" ? "127.0.0.1" : hostRaw;
        return new
        {
            found = true,
            baseUrl = $"http://{host}:{port}/v1",
            apiKey = ExtractValue(tokens, "--api-key") ?? string.Empty,
            alias = ExtractValue(tokens, "-a") ?? string.Empty,
            modelPath = ExtractValue(tokens, "-m") ?? string.Empty,
        };
    }

    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static string? ExtractValue(List<string> tokens, string flag)
    {
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i] == flag) return tokens[i + 1];
        }
        var prefix = flag + "=";
        foreach (var token in tokens)
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal))
                return token[prefix.Length..];
        }
        return null;
    }

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

        HostLog.Info($"llm.switch action={action} task={task ?? "-"}");
        var issued = new List<object>();
        if (action == "stop")
        {
            foreach (var name in KnownTasks)
                issued.Add(Run("schtasks.exe", "/end", "/tn", name));
            issued.Add(KillServer());
            WaitForServerExit(TimeSpan.FromSeconds(5));
            var stopped = !ServerRunning();
            HostLog.Info($"llm.switch stop finished: stopped={stopped}");
            return new { action, task = string.Empty, issued, stopped };
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
            HostLog.Info($"task {task} 未进入 Running，清理后重试一次");
            issued.Add(Run("schtasks.exe", "/end", "/tn", task!));
            Thread.Sleep(1000);
            issued.Add(Run("schtasks.exe", "/run", "/tn", task!));
            started = WaitForTaskRunning(task!, TimeSpan.FromSeconds(8));
        }

        HostLog.Info($"llm.switch {action} {task} finished: started={started}");
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

        var rendered = fileName + " " + string.Join(' ', args);
        try
        {
            using var process = Process.Start(startInfo)!;
            if (!process.WaitForExit(milliseconds: 15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                HostLog.Error($"timeout: {rendered}");
                return new { cmd = fileName, ok = false, exitCode = -1 };
            }

            // taskkill 在进程不存在时返回 128，对幂等停止/切换而言视为成功。
            var ok = process.ExitCode == 0 ||
                     (fileName == "taskkill.exe" && process.ExitCode == 128);
            HostLog.Info($"exec {(ok ? "ok" : "fail")} exit={process.ExitCode}: {rendered}");
            return new { cmd = fileName, ok, exitCode = process.ExitCode };
        }
        catch (Exception ex)
        {
            HostLog.Error($"exec threw: {rendered}", ex);
            return new { cmd = fileName, ok = false, exitCode = -1 };
        }
    }

    /// <summary>
    /// 读取模型服务日志尾部（只读）。路径优先从运行中的 llama-server.exe 所在目录推导，
    /// 服务未运行时回退到默认部署路径，便于排查"启动失败/崩溃退出"。
    /// </summary>
    internal static object Log(int? maxLines)
    {
        var lines = maxLines is >= 1 and <= HardMaxLogLines ? maxLines.Value : DefaultMaxLogLines;
        var path = ResolveLogPath();
        if (!File.Exists(path))
        {
            return new
            {
                path,
                exists = false,
                running = ServerRunning(),
                sizeBytes = 0L,
                modifiedAt = (string?)null,
                truncated = false,
                lines = Array.Empty<string>(),
                host = HostLog.ReadTail(Math.Min(lines, 200)),
            };
        }

        byte[] buffer;
        long size;
        DateTime modified;
        bool bytesTruncated;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            size = stream.Length;
            modified = File.GetLastWriteTime(path);
            var take = (int)Math.Min(size, MaxLogBytes);
            bytesTruncated = take < size;
            buffer = new byte[take];
            var offset = 0;
            if (take > 0)
            {
                stream.Seek(-take, SeekOrigin.End);
                while (offset < take)
                {
                    var read = stream.Read(buffer, offset, take - offset);
                    if (read <= 0) break;
                    offset += read;
                }
            }
            if (offset != buffer.Length) Array.Resize(ref buffer, offset);
        }

        // 尾部截断可能落在多字节字符中间，用替换字符兜底即可（只影响首行）。
        var text = Encoding.UTF8.GetString(buffer).Replace("\r\n", "\n").Replace('\r', '\n');
        var all = text.Split('\n');
        var tail = all.Length > lines ? all[^lines..] : all;
        return new
        {
            path,
            exists = true,
            running = ServerRunning(),
            sizeBytes = size,
            modifiedAt = modified.ToString("yyyy-MM-dd HH:mm:ss"),
            truncated = bytesTruncated || all.Length > lines,
            lines = tail,
            // 宿主自身的操作轨迹（启停/切换执行了哪条命令、结果如何），
            // 与 llama-server 的输出分开返回，前端在日志弹窗里分两段展示。
            host = HostLog.ReadTail(Math.Min(lines, 200)),
        };
    }

    private static string ResolveLogPath()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ExecutablePath FROM Win32_Process WHERE Name = 'llama-server.exe'");
            foreach (var item in searcher.Get())
            {
                var exe = item["ExecutablePath"]?.ToString();
                if (string.IsNullOrWhiteSpace(exe)) continue;
                var dir = Path.GetDirectoryName(exe);
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir, LogFileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch
        {
            // WMI 不可用时回退到默认路径。
        }
        return FallbackLogPath;
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
