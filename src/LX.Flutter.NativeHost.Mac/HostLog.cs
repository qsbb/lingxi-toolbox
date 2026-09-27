using System.Text;

namespace LingXi.Flutter.NativeHost.Mac;

/// <summary>
/// macOS 宿主的诊断日志（滚动文件）。
///
/// 与 Windows 宿主的 HostLog 职责相同，只是落盘位置按 macOS 约定改为
/// <c>~/Library/Logs/LingXi/</c>（Flutter 侧的 flutter-*.log 也在同一目录）。
/// 只记协议级事件与异常类型，不记请求参数（避免 API Key 等敏感值落盘）。
/// </summary>
internal static class HostLog
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int KeepDays = 7;

    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Logs", "LingXi");

    private static string CurrentPath => Path.Combine(Dir, $"nativehost-{DateTime.Now:yyyyMMdd}.log");

    internal static void Info(string message) => Write("INF", message, null);

    internal static void Error(string message, Exception? ex = null) => Write("ERR", message, ex);

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
            // 日志失败绝不影响协议主循环。
        }
    }

    private static void PruneOld()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in Directory.EnumerateFiles(Dir, "nativehost-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch { /* best effort */ }
    }
}
