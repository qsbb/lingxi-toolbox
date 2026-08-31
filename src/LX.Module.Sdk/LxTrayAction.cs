namespace LingXi.Sdk;

/// <summary>托盘菜单动作（壳固有项与各模块段共用同一模型）。</summary>
public sealed record LxTrayAction(
    string Header,
    Action? OnClick = null,
    IReadOnlyList<LxTrayAction>? Children = null,
    bool IsSeparator = false,
    bool IsEnabled = true);
