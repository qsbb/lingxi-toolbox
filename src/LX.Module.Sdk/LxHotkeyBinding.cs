namespace LingXi.Sdk;

/// <summary>全局热键声明（描述元数据；实际注册由模块经 ILxHotkeys 完成）。</summary>
public sealed record LxHotkeyBinding(string Id, string Gesture, string Description, Action OnPressed);
