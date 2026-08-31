using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LingXi.Core.AutoStart;
using LingXi.Core.Settings;
using LingXi.Monitor.Models;
using LingXi.Sdk;

namespace LingXi.App.Views;

/// <summary>
/// 壳设置页 VM（通用 / 模块 / 监控三组）：
/// - 开关改动即时落盘 settings.json；
/// - 模块开关写 shell 段 DisabledModules（启动时装载前过滤，重启后生效）；
/// - 主题切换经事件委托回壳（App.ToggleTheme 现有逻辑）。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;

    /// <summary>主题切换请求 → 壳（MainWindow 转发给 App.ToggleTheme）。</summary>
    public event Action? ThemeToggleRequested;

    /// <summary>每个已注册模块一行开关。</summary>
    public ObservableCollection<ModuleToggleRow> ModuleRows { get; } = [];

    private bool _isDark;

    /// <summary>深色主题开关（切换走壳的 ToggleTheme 既有逻辑）。</summary>
    public bool IsDark
    {
        get => _isDark;
        set
        {
            if (_isDark == value)
            {
                return;
            }
            _isDark = value;
            OnPropertyChanged();
            ThemeToggleRequested?.Invoke();
        }
    }

    /// <summary>壳在其它入口（侧栏按钮/托盘）切换主题后回同步开关显示（不触发再切换）。</summary>
    public void SyncTheme(bool isDark)
    {
        if (_isDark == isDark)
        {
            return;
        }
        _isDark = isDark;
        OnPropertyChanged(nameof(IsDark));
    }

    private bool _autoStart;

    /// <summary>开机自启（复用 LX.Core AutoStartService，写 HKCU Run 键，免管理员）。</summary>
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (_autoStart == value)
            {
                return;
            }
            _autoStart = value;
            OnPropertyChanged();
            try
            {
                AutoStartService.Set(value, Environment.ProcessPath ?? string.Empty);
                SaveShell(shell => shell.AutoStart = value);
                Serilog.Log.Information("壳设置：开机自启 → {State}", value);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "壳设置：开机自启写入失败");
            }
        }
    }

    private bool _closeToTray;

    /// <summary>关窗驻留托盘（shell 段；MainWindow.OnClosing 每次实时读取）。</summary>
    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (_closeToTray == value)
            {
                return;
            }
            _closeToTray = value;
            OnPropertyChanged();
            try
            {
                SaveShell(shell => shell.CloseToTray = value);
                Serilog.Log.Information("壳设置：关窗驻留托盘 → {State}", value);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "壳设置：关窗驻留托盘写入失败");
            }
        }
    }

    private bool _bindLan;

    /// <summary>Hub 绑定全网卡（lx.monitor.BindLan；Hub 监听在启动时建立，重启后生效）。</summary>
    public bool BindLan
    {
        get => _bindLan;
        set
        {
            if (_bindLan == value)
            {
                return;
            }
            _bindLan = value;
            OnPropertyChanged();
            try
            {
                var monitor = _settings.Get<MonitorSettings>("lx.monitor");
                monitor.BindLan = value;
                _settings.Set("lx.monitor", monitor);
                Serilog.Log.Information("壳设置：Hub 绑定全网卡 → {State}（重启后生效）", value);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "壳设置：BindLan 写入失败");
            }
        }
    }

    public SettingsViewModel(SettingsStore settings, IReadOnlyList<ILxToolModule> modules, bool isDark)
    {
        _settings = settings;
        _isDark = isDark;

        var shell = settings.Get<ShellSettings>("shell");
        _closeToTray = shell.CloseToTray;
        // 自启以注册表实际状态为准（与托盘菜单同源），避免 settings 与 Run 键漂移
        _autoStart = AutoStartService.IsEnabled();

        // 已注册模块逐行：Enabled = 未在 DisabledModules 中
        var disabled = new HashSet<string>(shell.DisabledModules ?? [], StringComparer.Ordinal);
        foreach (var module in modules)
        {
            ModuleRows.Add(new ModuleToggleRow(module.Id, module.DisplayName, !disabled.Contains(module.Id), this));
        }

        try
        {
            _bindLan = settings.Get<MonitorSettings>("lx.monitor").BindLan;
        }
        catch
        {
            _bindLan = true; // 监控设置段损坏时按默认值展示
        }
    }

    /// <summary>模块启用开关写回（行 VM 调用）：更新 DisabledModules 并即时落盘。</summary>
    internal void SetModuleEnabled(ModuleToggleRow row, bool enabled)
    {
        try
        {
            SaveShell(shell =>
            {
                var list = shell.DisabledModules ??= [];
                if (enabled)
                {
                    list.Remove(row.Id);
                }
                else if (!list.Contains(row.Id))
                {
                    list.Add(row.Id);
                }
            });
            Serilog.Log.Information("壳设置：模块 {Id} → {State}（重启后生效）", row.Id, enabled ? "启用" : "禁用");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "壳设置：模块开关写入失败 {Id}", row.Id);
        }
    }

    /// <summary>重读 shell 段 → 改字段 → 落盘（避免持有旧快照覆盖其它入口的并发改动）。</summary>
    private void SaveShell(Action<ShellSettings> mutate)
    {
        var shell = _settings.Get<ShellSettings>("shell");
        mutate(shell);
        _settings.Set("shell", shell);
    }
}

/// <summary>设置页模块行：一行一个已注册模块的启停开关。</summary>
public sealed partial class ModuleToggleRow : ObservableObject
{
    private readonly SettingsViewModel _owner;

    public ModuleToggleRow(string id, string displayName, bool enabled, SettingsViewModel owner)
    {
        Id = id;
        DisplayName = displayName;
        _enabled = enabled;
        _owner = owner;
    }

    /// <summary>稳定模块 Id（如 lx.monitor），即 DisabledModules 的存储值。</summary>
    public string Id { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private bool _enabled;

    partial void OnEnabledChanged(bool value) => _owner.SetModuleEnabled(this, value);
}
