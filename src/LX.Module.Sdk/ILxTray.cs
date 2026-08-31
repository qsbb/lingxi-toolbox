namespace LingXi.Sdk;

/// <summary>托盘能力：模块菜单段 + 提示文案。</summary>
public interface ILxTray
{
    /// <summary>设置本模块的托盘菜单段（以 moduleId 为键，重复调用整段替换）。</summary>
    void SetMenu(string moduleId, IReadOnlyList<LxTrayAction> actions);

    /// <summary>更新托盘悬浮提示。</summary>
    void SetTooltip(string text);
}
