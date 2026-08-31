using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using LingXi.Sdk;

namespace LingXi.Core.Tray;

/// <summary>
/// 托盘服务（H.NotifyIcon 包装，开发文档 7.3）：
/// - 菜单 = 壳固有项（打开/退出…） + 各模块 SetMenu 段落合并；
/// - 左键单击 → OpenRequested（壳订阅后显示主窗口）。
/// </summary>
public sealed class TrayService : ILxTray, IDisposable
{
    private readonly Dictionary<string, IReadOnlyList<LxTrayAction>> _sections = new(StringComparer.Ordinal);
    private readonly List<LxTrayAction> _shellActions = [];
    private TaskbarIcon? _icon;

    /// <summary>左键单击托盘图标。</summary>
    public event Action? OpenRequested;

    public void Initialize(System.Windows.Media.ImageSource iconSource, string tooltip)
    {
        _icon = new TaskbarIcon
        {
            IconSource = iconSource,
            ToolTipText = tooltip,
            Visibility = Visibility.Visible,
        };
        // 显式创建 Shell_NotifyIcon 条目（H.NotifyIcon 默认延迟创建，部分环境不触发）
        _icon.ForceCreate();
        _icon.TrayLeftMouseDown += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>设置壳固有菜单项（App 启动时调用一次）。</summary>
    public void SetShellActions(IReadOnlyList<LxTrayAction> actions)
    {
        _shellActions.Clear();
        _shellActions.AddRange(actions);
        Rebuild();
    }

    /// <inheritdoc />
    public void SetMenu(string moduleId, IReadOnlyList<LxTrayAction> actions)
    {
        _sections[moduleId] = actions;
        Rebuild();
    }

    /// <inheritdoc />
    public void SetTooltip(string text)
    {
        if (_icon is not null)
        {
            _icon.ToolTipText = text;
        }
    }

    /// <summary>托盘气泡通知。</summary>
    public void ShowNotification(string title, string message)
    {
        try
        {
            _icon?.ShowNotification(title, message);
        }
        catch
        {
            // 气泡失败不影响主流程
        }
    }

    private void Rebuild()
    {
        if (_icon is null)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var action in _shellActions.Concat(_sections.Values.SelectMany(s => s)))
        {
            if (action.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            menu.Items.Add(BuildItem(action));
        }
        _icon.ContextMenu = menu;
    }

    private static MenuItem BuildItem(LxTrayAction action)
    {
        var item = new MenuItem
        {
            Header = action.Header,
            IsEnabled = action.IsEnabled,
        };
        if (action.Children is { Count: > 0 })
        {
            foreach (var child in action.Children)
            {
                if (child.IsSeparator)
                {
                    item.Items.Add(new Separator());
                    continue;
                }
                item.Items.Add(BuildItem(child));
            }
        }
        else if (action.OnClick is { } onClick)
        {
            item.Click += (_, _) => onClick();
        }
        return item;
    }

    public void Dispose() => _icon?.Dispose();
}
