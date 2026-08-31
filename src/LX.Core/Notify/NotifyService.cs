using LingXi.Core.Tray;
using LingXi.Sdk;

namespace LingXi.Core.Notify;

/// <summary>MVP：托盘气泡；v1.1 升级 Windows Toast（开发文档 7.5 / 风险表）。</summary>
public sealed class NotifyService(TrayService tray) : ILxNotify
{
    public void Show(string title, string message) => tray.ShowNotification(title, message);
}
