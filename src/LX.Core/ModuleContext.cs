using LingXi.Sdk;

namespace LingXi.Core;

/// <summary>平台服务聚合，注入每个模块（开发文档 10.1）。</summary>
public sealed class ModuleContext(
    ILxSettings settings,
    ILxLog log,
    ILxTray tray,
    ILxHotkeys hotkeys,
    ILxNotify notify,
    Action<string> requestNavigation) : ILxModuleContext
{
    public ILxSettings Settings { get; } = settings;

    public ILxLog Log { get; } = log;

    public ILxTray Tray { get; } = tray;

    public ILxHotkeys Hotkeys { get; } = hotkeys;

    public ILxNotify Notify { get; } = notify;

    public void RequestNavigation(string moduleId) => requestNavigation(moduleId);
}
