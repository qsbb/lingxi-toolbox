using System.Windows.Input;
using LingXi.Sdk;
using NHotkey;
using NHotkey.Wpf;

namespace LingXi.Core.Hotkeys;

/// <summary>NHotkey 包装：RegisterHotKey 注册全局热键；冲突时注册失败返回 false（开发文档 7.4）。</summary>
public sealed class HotkeyService : ILxHotkeys
{
    public bool Register(string id, string gesture, Action onPressed)
    {
        if (!TryParse(gesture, out var key, out var mods))
        {
            return false;
        }
        try
        {
            HotkeyManager.Current.AddOrReplace(id, key, mods, (_, _) => onPressed());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Unregister(string id)
    {
        try
        {
            HotkeyManager.Current.Remove(id);
        }
        catch
        {
            // 未注册过/已失效：忽略
        }
    }

    /// <summary>解析 "Ctrl+Alt+A" 形式手势；最后一节必须是键名。</summary>
    internal static bool TryParse(string gesture, out Key key, out ModifierKeys mods)
    {
        key = Key.None;
        mods = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }
        foreach (var raw in gesture.Split('+'))
        {
            var part = raw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    mods |= ModifierKeys.Control;
                    break;
                case "alt":
                    mods |= ModifierKeys.Alt;
                    break;
                case "shift":
                    mods |= ModifierKeys.Shift;
                    break;
                case "win" or "windows":
                    mods |= ModifierKeys.Windows;
                    break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out key))
                    {
                        return false;
                    }
                    break;
            }
        }
        return key != Key.None;
    }
}
