namespace LingXi.Sdk;

/// <summary>
/// 一个工具模块 = 一个实现本接口的程序集。
/// 壳只认识本接口（开发文档公理 A1：加工具不改壳）。
/// </summary>
public interface ILxToolModule
{
    /// <summary>稳定 ID，形如 "lx.audioswitch"。</summary>
    string Id { get; }

    /// <summary>导航栏显示名。</summary>
    string DisplayName { get; }

    /// <summary>图标名（优先 WPF-UI SymbolRegular 成员名，如 "Speaker24"；解析失败回退通用图标）。</summary>
    string IconGlyph { get; }

    /// <summary>模块版本（语义化）。</summary>
    Version Version { get; }

    /// <summary>注入服务、读设置、注册托盘/热键。壳在加载时调用一次。</summary>
    void Initialize(ILxModuleContext context);

    /// <summary>惰性创建主视图（首次切到该模块时调用）。</summary>
    System.Windows.FrameworkElement CreateMainView();

    /// <summary>模块托盘菜单段（与其它模块段落合并）。</summary>
    IReadOnlyList<LxTrayAction> GetTrayActions() => [];

    /// <summary>模块声明的全局热键（描述用；实际注册走 ILxHotkeys）。</summary>
    IReadOnlyList<LxHotkeyBinding> GetHotkeys() => [];

    /// <summary>退出前清理（逆序调用）。</summary>
    void Shutdown() { }
}
