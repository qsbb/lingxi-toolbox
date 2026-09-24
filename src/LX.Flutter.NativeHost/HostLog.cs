using System.IO;
using System.Text;

namespace LingXi.Flutter.NativeHost;

/// <summary>
/// NativeHost 自身的诊断日志（滚动文件）。
///
/// 为什么需要：宿主是 GUI 子系统（WinExe，不分配控制台），原来写往
/// <c>Console.Error</c> 的信息在实机上等同丢弃 —— 用户遇到"启动没反应"时
/// 没有任何可看的东西。这些记录改写到独立文件，并由 <c>llm.log</c> 一并回传给
/// Flutter 的「模型服务 → 日志」弹窗，前端不依赖任何外部文件读取权限。
///
/// 位置：%LocalAppData%\LingXi\logs\nativehost-YYYYMMDD.log（保留 7 天）。
/// 只记录协议级事件与异常类型，不记录请求参数（避免 API Key 等敏感值落盘）。
/// </summary>
internal static class HostLog
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int KeepDays = 7;

    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LingXi", "logs");

    private static string CurrentPath =>
        Path.Combine(Dir, $"nativehost-{DateTime.Now:yyyyMMdd}.log");

    internal static void Info(string message) => Write("INF", message, null);

    internal static void Error(string message, Exception? ex = null) =>
        Write("ERR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var path = CurrentPath;
                var line = new StringBuilder()
                    .Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append(' ')
                    .Append(level).Append("] ").Append(message);
                if (ex is not null) line.Append(" :: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
                line.Append('\n');

                if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                {
                    var old = path + ".1";
                    try { File.Delete(old); } catch { /* best effort */ }
                    try { File.Move(path, old); } catch { /* best effort */ }
                }

                File.AppendAllText(path, line.ToString(), Encoding.UTF8);
                PruneOld();
            }
        }
        catch
        {
            // 日志本身绝不能影响协议主流程。
        }
    }

    private static void PruneOld()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in Directory.GetFiles(Dir, "nativehost-*.log*"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>读取当天宿主日志尾部若干行（供 llm.log 回传）。</summary>
    internal static object ReadTail(int maxLines)
    {
        try
        {
            var path = CurrentPath;
            if (!File.Exists(path))
                return new { path, exists = false, modifiedAt = (string?)null, lines = Array.Empty<string>() };
            var all = File.ReadAllLines(path);
            var take = Math.Min(maxLines, all.Length);
            return new
            {
                path,
                exists = true,
                modifiedAt = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss"),
                lines = all[^take..],
            };
        }
        catch
        {
            return new { path = "", exists = false, modifiedAt = (string?)null, lines = Array.Empty<string>() };
        }
    }
}
