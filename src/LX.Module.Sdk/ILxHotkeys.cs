namespace LingXi.Sdk;

/// <summary>全局热键注册。</summary>
public interface ILxHotkeys
{
    /// <summary>注册全局热键（gesture 形如 "Ctrl+Alt+A"）；冲突或解析失败返回 false。</summary>
    bool Register(string id, string gesture, Action onPressed);

    void Unregister(string id);
}
