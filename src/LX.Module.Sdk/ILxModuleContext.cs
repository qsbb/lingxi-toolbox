namespace LingXi.Sdk;

/// <summary>模块可用的全部平台能力（开发文档公理 A2：平台能力只从这里来）。</summary>
public interface ILxModuleContext
{
    ILxSettings Settings { get; }

    ILxLog Log { get; }

    ILxTray Tray { get; }

    ILxHotkeys Hotkeys { get; }

    ILxNotify Notify { get; }

    /// <summary>请求壳切到指定模块页（托盘菜单等场景）。</summary>
    void RequestNavigation(string moduleId);
}
