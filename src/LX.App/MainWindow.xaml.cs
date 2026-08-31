using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LingXi.App.Views;
using LingXi.Core.Settings;
using LingXi.Sdk;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LingXi.App;

/// <summary>主窗口：玻璃侧栏导航 + 模块视图宿主（开发文档 4.1 / 6 章）。</summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>内置设置页导航 Id（壳固定导航项，非模块）。</summary>
    public const string SettingsNavId = "__settings__";

    private readonly SettingsStore _settings;
    private readonly List<ILxToolModule> _modules = [];
    private SettingsView? _settingsView;
    private bool _navigating;

    /// <summary>退出前允许真正关闭；默认关窗 = 最小化到托盘。</summary>
    public bool AllowClose { get; set; }

    /// <summary>当前导航到的模块 Id（启动导航去重用；未导航时为 null）。</summary>
    public string? CurrentModuleId { get; private set; }

    public event Action? ThemeToggleRequested;

    /// <summary>关窗且未驻留托盘时请求壳完整退出（模块逆序清理 + 托盘销毁，ShutdownMode=OnExplicitShutdown 下必须显式）。</summary>
    public event Action? ExitRequested;

    public MainWindow(SettingsStore settings)
    {
        _settings = settings;
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Mica/backdrop 已整体停用：WPF-UI 的 ApplyBackdrop 会在 Loaded 后接管窗口背景，
        // 在部分环境（远程会话/驱动差异）下材质不渲染 → 窗口先渲染首帧再被刷成透明白。
        // 玻璃质感改由 LxPageBgBrush 实体底 + LxGlassFillBrush 半透明控制层表达（开发文档 6.2）。
        // 如需恢复：WindowBackdrop.ApplyBackdrop(this, WindowBackdropType.Mica);
        try
        {
            Icon = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/LX.App;component/Assets/app.png"));
        }
        catch
        {
            // 图标缺失不致命
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose)
        {
            return;
        }
        // 关窗行为跟随设置（每次实时读取）：驻留托盘 → 仅隐藏；否则走壳完整退出
        e.Cancel = true;
        var shell = _settings.Get<ShellSettings>("shell");
        if (shell.CloseToTray)
        {
            Hide();
            return;
        }
        ExitRequested?.Invoke();
    }

    /// <summary>注册模块 → 侧栏项（首个模块自动选中）。</summary>
    public void AddModule(ILxToolModule module)
    {
        _modules.Add(module);
        var item = new ListBoxItem { Tag = module.Id, Content = BuildNavContent(module) };
        NavList.Items.Add(item);
        if (NavList.SelectedItem is null)
        {
            _navigating = true;
            NavList.SelectedItem = item;
            _navigating = false;
            NavigateTo(module.Id);
        }
    }

    private UIElement BuildNavContent(ILxToolModule module)
    {
        UIElement icon;
        if (Enum.TryParse<SymbolRegular>(module.IconGlyph, out var symbol))
        {
            icon = new SymbolIcon { Symbol = symbol, FontSize = 18 };
        }
        else
        {
            icon = new System.Windows.Controls.TextBlock
            {
                Text = "◆",
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Border { Width = 26, Child = icon });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = module.DisplayName,
            FontSize = 13,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigating)
        {
            return;
        }
        if (NavList.SelectedItem is ListBoxItem { Tag: string id })
        {
            NavigateTo(id);
        }
    }

    /// <summary>切换到指定模块（模块/托盘均可调用；记忆 LastModule）。</summary>
    public void NavigateTo(string moduleId)
    {
        if (moduleId == SettingsNavId)
        {
            NavigateToSettings();
            return;
        }
        var module = _modules.FirstOrDefault(m => m.Id == moduleId);
        if (module is null)
        {
            return;
        }
        CurrentModuleId = moduleId;
        _settingsView = null;
        SettingsNavButton.IsChecked = false;

        try
        {
            ModuleHost.Content = module.CreateMainView();
        }
        catch (Exception ex)
        {
            // 完整异常链落日志 + 显示（内层异常才是 XAML 资源问题根因）
            try
            {
                Serilog.Log.Error(ex, "模块 {ModuleId} 视图创建失败", moduleId);
            }
            catch
            {
                // 日志失败不影响降级 UI
            }
            ModuleHost.Content = new System.Windows.Controls.TextBlock
            {
                Text = $"模块 {module.DisplayName} 加载失败：{ex.Message}\n\n内层：{ex.InnerException?.Message}\n\n再内层：{ex.InnerException?.InnerException?.Message}",
                Margin = new Thickness(20),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Foreground = (Brush)FindResource("LxTextPrimaryBrush"),
            };
        }

        if (NavList.SelectedItem is not ListBoxItem { Tag: string current } || current != moduleId)
        {
            _navigating = true;
            foreach (ListBoxItem item in NavList.Items)
            {
                if (item.Tag as string == moduleId)
                {
                    NavList.SelectedItem = item;
                    break;
                }
            }
            _navigating = false;
        }

        var shell = _settings.Get<ShellSettings>("shell");
        shell.LastModule = moduleId;
        _settings.Set("shell", shell);
    }

    /// <summary>切换到内置设置页（侧栏底部固定项，不依赖模块注册；记忆 LastModule）。</summary>
    public void NavigateToSettings()
    {
        if (_navigating)
        {
            return;
        }
        CurrentModuleId = SettingsNavId;
        SettingsNavButton.IsChecked = true;

        // 清除模块导航高亮（静默改选，避免 SelectionChanged 递归导航）
        if (NavList.SelectedItem is not null)
        {
            _navigating = true;
            NavList.SelectedItem = null;
            _navigating = false;
        }

        try
        {
            var isDark = Application.Current is App app && app.IsDarkNow;
            var view = new SettingsView(_settings, _modules, isDark);
            view.ThemeToggleRequested += () => ThemeToggleRequested?.Invoke();
            _settingsView = view;
            ModuleHost.Content = view;
        }
        catch (Exception ex)
        {
            _settingsView = null;
            // 完整异常链落日志 + 显示（内层异常才是 XAML 资源问题根因）
            try
            {
                Serilog.Log.Error(ex, "设置页创建失败");
            }
            catch
            {
                // 日志失败不影响降级 UI
            }
            ModuleHost.Content = new System.Windows.Controls.TextBlock
            {
                Text = $"设置页加载失败：{ex.Message}\n\n内层：{ex.InnerException?.Message}\n\n再内层：{ex.InnerException?.InnerException?.Message}",
                Margin = new Thickness(20),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Foreground = (Brush)FindResource("LxTextPrimaryBrush"),
            };
        }

        var shell = _settings.Get<ShellSettings>("shell");
        shell.LastModule = SettingsNavId;
        _settings.Set("shell", shell);
    }

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        // ToggleButton 点击已翻转选中态，导航会把它校正为 true
        NavigateToSettings();
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ThemeToggleRequested?.Invoke();
        // App 已应用新主题并写回 shell.theme；同步设置页的深色开关显示（其它入口切换时保持一致）
        _settingsView?.SyncThemeState(Application.Current is App app && app.IsDarkNow);
    }
}
