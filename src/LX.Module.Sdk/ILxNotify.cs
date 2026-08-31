namespace LingXi.Sdk;

/// <summary>系统通知（MVP 为托盘气泡）。</summary>
public interface ILxNotify
{
    void Show(string title, string message);
}
