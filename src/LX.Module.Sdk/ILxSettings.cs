namespace LingXi.Sdk;

/// <summary>按模块分键的强类型设置存取（原子落盘，见 LX.Core.SettingsStore）。</summary>
public interface ILxSettings
{
    /// <summary>读取模块设置段；缺失或损坏时返回 new(T)。</summary>
    T Get<T>(string moduleId) where T : class, new();

    /// <summary>写入模块设置段（原子写 + 触发 Changed）。</summary>
    void Set<T>(string moduleId, T value) where T : class;

    /// <summary>任一设置段落变更（含其它模块写入）。</summary>
    event Action? Changed;
}
