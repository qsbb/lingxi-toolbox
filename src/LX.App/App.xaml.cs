using System.Windows;
using System.Windows.Media.Imaging;
using LingXi.AudioSwitch;
using LingXi.Core;
using LingXi.Core.AutoStart;
using LingXi.Core.Hotkeys;
using LingXi.Core.Logging;
using LingXi.Core.Notify;
using LingXi.Core.Settings;
using LingXi.Core.Tray;
using LingXi.Core.Update;
using LingXi.Monitor;
using LingXi.Sdk;
using LingXi.Ui.Theme;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;

namespace LingXi.App;

/// <summary>壳：组合根（开发文档 4.1 / 7 章 / 附录 B）。</summary>
public partial class App : Application
{
    private readonly SingleInstance _single = new();
    private readonly List<ILxToolModule> _modules = [];
    private TrayService _tray = new();
    private SettingsStore _settings = null!;
    private MainWindow _mainWindow = null!;
    private bool _dark;

    /// <summary>当前是否深色主题（设置页开关初始态用）。</summary>
    public bool IsDarkNow => _dark;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常钩子：记录后尽量存活（开发文档 10.3 健壮性）
        DispatcherUnhandledException += (_, args) =>
        {
            Serilog.Log.Error(args.Exception, "UI 线程未处理异常");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Serilog.Log.Error(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()),
                "非 UI 线程未处理异常");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Serilog.Log.Error(args.Exception, "未观察的任务异常");
            args.SetObserved();
        };

        // 软件渲染：GameViewer 等虚拟显示适配器上 milcore/D3D 合成会静默失败
        //（实证：UI 线程正常、hwnd 有效、UIA 子树 nodes=0、窗口纯白）。
        // 工具箱为低帧率 UI，软件渲染无感知差异（开发文档 15 章兼容性对策）。
        System.Windows.Media.RenderOptions.ProcessRenderMode =
            System.Windows.Interop.RenderMode.SoftwareOnly;

        // Velopack 更新钩子（未打包运行时为无害 no-op）
        global::Velopack.VelopackApp.Build().Run();

        // 单实例：二次启动 → 转发激活请求后退出
        if (!_single.TryAcquire())
        {
            Shutdown();
            return;
        }

        // 日志 + 设置
        var fileLogger = LxLog.CreateFileLogger();
        ILxLog log = new LxLog(fileLogger);
        _settings = new SettingsStore();
        var shell = _settings.Get<ShellSettings>("shell");

        // 主题（shell.theme = light/dark/system）
        _dark = shell.Theme switch
        {
            "dark" => true,
            "light" => false,
            _ => IsSystemDark(),
        };
        ApplicationThemeManager.Apply(
            _dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            Wpf.Ui.Controls.WindowBackdropType.None);
        LxThemeManager.Apply(_dark);

        // 托盘
        _tray = new TrayService();
        try
        {
            _tray.Initialize(
                new BitmapImage(new Uri("pack://application:,,,/LX.App;component/Assets/app.png")),
                "凌溪工具箱");
            log.Info($"托盘注册结果 IsCreated={_tray.IsCreated}");
            // 启动气泡：强制托盘图标短暂浮现到主托盘区（Win11 默认新图标进折叠区，
            // 气泡是用户确认图标存在的最直接信号）
            _ = Task.Delay(1500).ContinueWith(_ =>
                Dispatcher.BeginInvoke((Action)(() =>
                    _tray.ShowNotification("凌溪工具箱", "已在后台运行，点此气泡旁的图标可打开主窗口"))));
        }
        catch (Exception ex)
        {
            log.Error("托盘图标创建失败", ex);
        }
        _tray.OpenRequested += ShowShell;

        // 组合根（开发文档 7 章：平台服务经接口注入）
        ILxNotify notify = new NotifyService(_tray);
        ILxHotkeys hotkeys = new HotkeyService();
        var services = new ServiceCollection();
        services.AddSingleton<ILxSettings>(_settings);
        services.AddSingleton<ILxLog>(log);
        services.AddSingleton<ILxTray>(_tray);
        services.AddSingleton<ILxHotkeys>(hotkeys);
        services.AddSingleton<ILxNotify>(notify);
        services.AddSingleton<UpdateService>();
        // 注册顺序即导航顺序：监控为首页排第一，音频其后
        services.AddTransient<ILxToolModule, MonitorModule>();
        services.AddTransient<ILxToolModule, AudioModule>();
        var provider = services.BuildServiceProvider();

        _mainWindow = new MainWindow(_settings);
        _mainWindow.ThemeToggleRequested += ToggleTheme;
        // 关窗且未驻留托盘 → 走壳完整退出（模块逆序清理 + 托盘销毁）
        _mainWindow.ExitRequested += ExitApp;

        ILxModuleContext MakeContext(string moduleId) => new ModuleContext(
            _settings, log, _tray, hotkeys, notify,
            id => _mainWindow.NavigateTo(id));

        // 模块装载：单模块失败不拖垮壳（开发文档 10.3）；
        // 设置页禁用的模块（shell.disabledModules）在装载前直接跳过——
        // 未 Initialize 即无托盘/热键等已注册资源，无需清理路径；运行中模块禁用则"重启后生效"。
        var disabledModules = new HashSet<string>(shell.DisabledModules ?? [], StringComparer.Ordinal);
        foreach (var module in provider.GetServices<ILxToolModule>())
        {
            if (disabledModules.Contains(module.Id))
            {
                log.Info($"模块 {module.Id} 已在设置中禁用，跳过加载");
                continue;
            }
            try
            {
                module.Initialize(MakeContext(module.Id));
                _modules.Add(module);
                _mainWindow.AddModule(module);
            }
            catch (Exception ex)
            {
                log.Error($"模块 {module.Id} 初始化失败", ex);
            }
        }

        _tray.SetShellActions(
        [
            new LxTrayAction("打开主窗口", ShowShell),
            new LxTrayAction("", IsSeparator: true),
            new LxTrayAction("切换主题", ToggleTheme),
            new LxTrayAction(AutoStartService.IsEnabled() ? "开机自启（开）" : "开机自启（关）", ToggleAutoStart),
            new LxTrayAction("", IsSeparator: true),
            new LxTrayAction("退出", ExitApp),
        ]);

        _single.ActivateRequested += ShowShell;

        _mainWindow.Show();
        // 启动导航：有 LastModule 恢复上次模块；首次启动（无记录）默认打开监控页。
        // AddModule 已把导航首位的监控页选中，这里仅在目标不同（或首启无监控模块）时补导航，避免重复建视图。
        string? target;
        if (!string.IsNullOrWhiteSpace(shell.LastModule))
        {
            target = shell.LastModule;
        }
        else
        {
            target = _modules.Any(m => m.Id == "lx.monitor")
                ? "lx.monitor"
                : _modules.FirstOrDefault()?.Id;
        }
        if (!string.IsNullOrWhiteSpace(target) && target != _mainWindow.CurrentModuleId)
        {
            _mainWindow.NavigateTo(target);
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void ShowShell()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ToggleTheme()
    {
        _dark = !_dark;
        ApplicationThemeManager.Apply(
            _dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
            Wpf.Ui.Controls.WindowBackdropType.None);
        LxThemeManager.Apply(_dark);
        var shell = _settings.Get<ShellSettings>("shell");
        shell.Theme = _dark ? "dark" : "light";
        _settings.Set("shell", shell);
    }

    private void ToggleAutoStart()
    {
        var enable = !AutoStartService.IsEnabled();
        AutoStartService.Set(enable, Environment.ProcessPath ?? string.Empty);
        var shell = _settings.Get<ShellSettings>("shell");
        shell.AutoStart = enable;
        _settings.Set("shell", shell);
        _tray.ShowNotification("凌溪工具箱", enable ? "已开启开机自启" : "已关闭开机自启");
    }

    private void ExitApp()
    {
        foreach (var module in _modules.AsEnumerable().Reverse())
        {
            try
            {
                module.Shutdown();
            }
            catch
            {
                // 单模块清理失败不阻塞退出
            }
        }
        _tray.Dispose();
        _mainWindow.AllowClose = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single.Dispose();
        Serilog.Log.CloseAndFlush();
        base.OnExit(e);
    }
}
