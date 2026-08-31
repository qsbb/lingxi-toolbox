using System.IO;
using LingXi.Sdk;
using Serilog;
using Serilog.Events;

namespace LingXi.Core.Logging;

/// <summary>Serilog 适配 ILxLog（SDK 门面 → 文件滚动日志）。</summary>
public sealed class LxLog : ILxLog
{
    private readonly ILogger _logger;

    public LxLog(ILogger logger) => _logger = logger;

    public void Debug(string message) => _logger.Debug(message);

    public void Info(string message) => _logger.Information(message);

    public void Warn(string message) => _logger.Warning(message);

    public void Error(string message, Exception? exception = null) =>
        _logger.Write(exception is null ? LogEventLevel.Error : LogEventLevel.Error, exception, message);

    /// <summary>创建滚动文件日志：%LocalAppData%\LingXi\logs\lx-YYYYMMDD.log，保留 7 天。</summary>
    public static ILogger CreateFileLogger()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LingXi", "logs");
        Directory.CreateDirectory(dir);
        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(dir, "lx-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
