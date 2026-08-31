using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LingXi.Core.Settings;
using LingXi.Sdk;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LingXi.App;

/// <summary>主窗口：玻璃侧栏导航 + 模块视图宿主（开发文档 4.1 / 6 章）。</summary>
public partial class MainWindow : FluentWindow
{
    private readonly SettingsStore _settings;
    private readonly List<ILxToolModule> _modules = [];
    private bool _navigating;

    /// <summary>退出前允许真正关闭；默认关窗 = 最小化到托盘。</summary>
    public bool AllowClose { get; set; }

    public event Action? ThemeToggleRequested;

    public MainWindow(SettingsStore settings)
    {
        _settings = settings;
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Mica 窗体材质（Win11）；XAML 里的 LxPageBgBrush 是保底实体背景，
        // 只有 backdrop 应用成功才透明化，避免 Mica 失败时整窗纯白（开发文档 15 章风险对策）
        try
        {
            if (WindowBackdrop.ApplyBackdrop(this, WindowBackdropType.Mica))
            {
                SetCurrentValue(BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            }
        }
        catch
        {
            // 降级：保留实体背景
        }
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
        e.Cancel = true;
        Hide();
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
        var module = _modules.FirstOrDefault(m => m.Id == moduleId);
        if (module is null)
        {
            return;
        }

        try
        {
            ModuleHost.Content = module.CreateMainView();
        }
        catch (Exception ex)
        {
            ModuleHost.Content = new System.Windows.Controls.TextBlock
            {
                Text = $"模块 {module.DisplayName} 加载失败：{ex.Message}",
                Margin = new Thickness(20),
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

    private void ThemeToggle_Click(object sender, RoutedEventArgs e) =>
        ThemeToggleRequested?.Invoke();
}
