using System.Windows;
using System.Windows.Controls;
using LingXi.Core.Settings;
using LingXi.Sdk;

namespace LingXi.App.Views;

/// <summary>
/// 壳级设置页（通用 / 模块 / 监控）：由壳在导航到内置设置项时创建，不依赖模块注册。
/// 主题切换经 ThemeToggleRequested 委托回壳（复用 App.ToggleTheme 既有逻辑）。
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly SettingsViewModel _vm;

    /// <summary>主题切换请求 → MainWindow 转发给 App.ToggleTheme。</summary>
    public event Action? ThemeToggleRequested;

    public SettingsView(SettingsStore settings, IReadOnlyList<ILxToolModule> modules, bool isDark)
    {
        InitializeComponent();
        _vm = new SettingsViewModel(settings, modules, isDark);
        _vm.ThemeToggleRequested += () => ThemeToggleRequested?.Invoke();
        DataContext = _vm;
    }

    /// <summary>壳在其它入口（侧栏按钮/托盘）切换主题后回同步深色开关显示。</summary>
    public void SyncThemeState(bool isDark) => _vm.SyncTheme(isDark);
}
