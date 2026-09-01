using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using LingXi.Sdk;

namespace LingXi.Core.Tray;

/// <summary>
/// 托盘服务（H.NotifyIcon 包装，开发文档 7.3）：
/// - 菜单 = 壳固有项（打开/退出…） + 各模块 SetMenu 段落合并；
/// - 左键单击 → OpenRequested（壳订阅后显示主窗口）；
/// - 图标经 TrayIcon（原生 System.Drawing.Icon）通道注册，绕开跨版本转换链。
/// </summary>
public sealed class TrayService : ILxTray, IDisposable
{
    private readonly Dictionary<string, IReadOnlyList<LxTrayAction>> _sections = new(StringComparer.Ordinal);
    private readonly List<LxTrayAction> _shellActions = [];
    private TaskbarIcon? _icon;

    /// <summary>左键单击托盘图标。</summary>
    public event Action? OpenRequested;

    /// <summary>托盘图标是否真正注册到 Shell（ForceCreate 之后可查）。</summary>
    public bool IsCreated => _icon?.IsCreated ?? false;

    private IntPtr _hIcon;

    public void Initialize(System.Windows.Media.ImageSource iconSource, string tooltip)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = tooltip,
            Visibility = Visibility.Visible,
        };
        try
        {
            // PNG → Bitmap → GetHicon → Icon.FromHandle：
            // 彻底绕开 ICO 文件（自制 ICO 帧表损坏导致空 hicon → 托盘注册成功但图标空白）。
            // 尺寸取 SmallIconSize（DPI 感知），保证托盘渲染尺寸正确。
            var sri = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/LX.App;component/Assets/app.png"))
                ?? throw new InvalidOperationException("app.png 资源缺失");
            using var img = System.Drawing.Image.FromStream(sri.Stream);
            var size = System.Windows.Forms.SystemInformation.SmallIconSize;
            using var scaled = new System.Drawing.Bitmap(img, size.Width <= 0 ? 32 : size.Width, size.Height <= 0 ? 32 : size.Height);
            _hIcon = scaled.GetHicon();
            _icon.Icon = System.Drawing.Icon.FromHandle(_hIcon);
        }
        catch
        {
            // 安全回退：Windows 系统图标。
            // ⚠️ 禁止回退 IconSource（H.NotifyIcon 内部 ToSmallIcon 会因图标流
            // 无法解析抛 ArgumentException 且进程直接崩溃，见 WER 事件 08/31 18:55）
            _icon.Icon = System.Drawing.SystemIcons.Application;
        }
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

    public void Dispose()
    {
        _icon?.Dispose();
        if (_hIcon != IntPtr.Zero)
        {
            try
            {
                _ = PInvoke.DestroyIcon(_hIcon);
            }
            catch
            {
                // 清理失败不阻塞退出
            }
            _hIcon = IntPtr.Zero;
        }
    }

    private static class PInvoke
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr hIcon);
    }
}
