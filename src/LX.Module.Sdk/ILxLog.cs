namespace LingXi.Sdk;

/// <summary>最小日志门面（SDK 零依赖；由 LX.Core 用 Serilog 实现）。</summary>
public interface ILxLog
{
    void Debug(string message);

    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}
