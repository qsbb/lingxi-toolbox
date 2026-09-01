using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using LingXi.Monitor.Core;
using LingXi.Monitor.ViewModels;
using LingXi.Sdk;

namespace LingXi.Monitor;

/// <summary>凌溪·监控模块（开发文档 9 章）：LX Hub + agent 托管 + 仪表盘 + 告警。</summary>
public sealed class MonitorModule : ILxToolModule
{
    private ILxModuleContext _ctx = null!;
    private Models.MonitorSettings _settings = null!;
    private readonly SnapshotStore _store = new();
    private readonly List<SnapshotReporter> _reporters = [];
    private LxHub? _hub;
    private AgentHost? _agent;
    private AlertEngine? _alerts;
    private DashboardViewModel? _vm;
    private System.Windows.Threading.DispatcherTimer? _sweepTimer;

    public string Id => "lx.monitor";

    public string DisplayName => "凌溪·监控";

    public string IconGlyph => "DataUsage24";

    public Version Version => new(1, 0, 0);

    public void Initialize(ILxModuleContext ctx)
    {
        _ctx = ctx;
        var settings = ctx.Settings.Get<Models.MonitorSettings>("lx.monitor");
        _settings = settings;

        // 首次生成 Hub token（sm_ + 32hex，与官方规格一致）
        if (string.IsNullOrWhiteSpace(settings.HubToken))
        {
            settings.HubToken = TokenGen.NewToken();
            ctx.Settings.Set("lx.monitor", settings);
        }

        var options = new HubOptions
        {
            Port = settings.HubPort,
            BindLan = settings.BindLan,
            Tokens = new HashSet<string>(StringComparer.Ordinal) { settings.HubToken },
            OfflineTimeout = TimeSpan.FromSeconds(Math.Max(5, settings.OfflineTimeoutSec)),
        };
        if (settings.ForwardEnabled && !string.IsNullOrWhiteSpace(settings.ForwardUrl))
        {
            options.ForwardUrl = settings.ForwardUrl;
            options.ForwardToken = settings.ForwardToken;
        }

        _store.SetOfflineTimeout(options.OfflineTimeout);
        _store.OnlineChanged += (name, online) =>
            Application.Current?.Dispatcher.BeginInvoke(() => _vm?.SetOnline(name, online));

        _hub = new LxHub(options, _store);
        _hub.Log += msg => ctx.Log.Info(msg);
        _hub.SnapshotReceived += OnSnapshot;
        try
        {
            _hub.Start();
        }
        catch (Exception ex)
        {
            ctx.Log.Error("LX Hub 启动失败", ex);
        }

        _alerts = new AlertEngine(settings.Alerts);

        // 引擎 A：托管官方 agent（可选，settings.json 配置路径后开启）
        if (settings.AgentEnabled &&
            (!string.IsNullOrWhiteSpace(settings.AgentExePath) ||
             !string.IsNullOrWhiteSpace(settings.AgentScriptPath)))
        {
            _agent = new AgentHost(new AgentOptions
            {
                ExePath = settings.AgentExePath,
                NodePath = settings.AgentNodePath,
                ScriptPath = settings.AgentScriptPath,
                Name = string.IsNullOrWhiteSpace(settings.AgentName) ? Environment.MachineName : settings.AgentName,
                Token = settings.HubToken,
                ReportUrl = _hub.ReportUrl,
                IntervalSec = 10,
                SlowIntervalSec = 30,
            });
            _agent.Log += msg => ctx.Log.Info(msg);
            _agent.Start();
        }

        // 引导卡地址：LAN 形式 report URL（第一个非回环 IPv4；端口取 Hub 实际端口，含顺延）
        var lanReportUrl = $"http://{GetLanIpv4()}:{_hub.Port}/servermonitor/report";

        _vm = new DashboardViewModel(
            _store, _settings,
            // 档案改动即时写回 settings（同 HubToken 首次生成的写法）
            () => _ctx.Settings.Set("lx.monitor", _settings),
            ctx.Log,
            // 上报目标改动（新增/编辑/删除/启停）→ 重启全部 Reporter 立即生效
            ApplyReporters)
        {
            HubReportUrl = _hub.ReportUrl,
            LanReportUrl = lanReportUrl,
            HubToken = _settings.HubToken,
            // 官方 agent 一行部署命令模板：仅 <机器名> 需替换，token/url 已填本机实际值
            DeployCommand = $"node agent.mjs --name <机器名> --token {_settings.HubToken} --report-url {lanReportUrl}",
        };
        _vm.Prompt = (title, current) =>
            Views.InputBoxWindow.Show(title, "输入该机器的显示别名（留空恢复原名称）", current);
        _vm.EditReporter = target => Views.ReporterEditorWindow.Show(target);

        // 上报端（双向监控）：把本机指标按 servermonitor 协议上报给各目标服务器
        StartReporters();

        _sweepTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _sweepTimer.Tick += (_, _) =>
        {
            _store.Sweep(DateTimeOffset.Now);
            _vm.Sweep();
        };
        _sweepTimer.Start();

        ctx.Tray.SetMenu(Id, [new LxTrayAction("打开监控", () => ctx.RequestNavigation(Id))]);
    }

    private void OnSnapshot(SnapshotEnvelope envelope)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_vm is null || _alerts is null)
            {
                return;
            }
            _vm.ApplySnapshot(envelope);

            var now = DateTimeOffset.Now;
            var online = _store.IsOnline(envelope.Snapshot.Name, now);
            foreach (var alert in _alerts.Evaluate(envelope, online, now))
            {
                _ctx.Notify.Show("凌溪·监控", alert.Message);
            }
        });
    }

    public System.Windows.FrameworkElement CreateMainView() =>
        new Views.DashboardView { DataContext = _vm };

    /// <summary>
    /// 按当前配置重启全部上报端（上报目标可视化管理保存后调用，立即生效）：
    /// Dispose 旧的 SnapshotReporter，再按 settings.Reporters 重新 Start。
    /// </summary>
    public void ApplyReporters()
    {
        if (_settings is null || _ctx is null)
        {
            return; // 尚未 Initialize（回调只会经 VM 在初始化后触发）
        }
        StopReporters();
        StartReporters();
    }

    private void StartReporters()
    {
        foreach (var target in _settings.Reporters.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Url)))
        {
            try
            {
                var reporter = new SnapshotReporter(target);
                reporter.Log += msg => _ctx.Log.Info(msg);
                // 回调来自后台线程，Marshal 回 UI 线程再动 ObservableCollection
                var url = target.Url;
                reporter.Reported += elapsed => Application.Current?.Dispatcher.BeginInvoke(
                    (Action)(() => _vm?.MarkReporterOk(url, elapsed)));
                // 独立 token 首次上报 202 待绑定：状态行提示绑定指引（适配文档 3.2）
                reporter.BindPending += pendingUrl => Application.Current?.Dispatcher.BeginInvoke(
                    (Action)(() => _vm?.MarkReporterBindPending(pendingUrl)));
                reporter.Start();
                _reporters.Add(reporter);
                _ctx.Log.Info($"上报端已启动 → {url}（间隔 {target.IntervalSec}s）");
            }
            catch (Exception ex)
            {
                _ctx.Log.Error($"上报端启动失败 {target.Url}", ex);
            }
        }
    }

    private void StopReporters()
    {
        foreach (var reporter in _reporters)
        {
            try
            {
                reporter.Dispose();
            }
            catch
            {
                // 单个上报端清理失败不阻塞其余
            }
        }
        _reporters.Clear();
    }

    /// <summary>第一个非回环 IPv4（Up 状态、非 Loopback 接口）；取不到或异常回退 127.0.0.1。</summary>
    private static string GetLanIpv4()
    {
        try
        {
            var address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork &&
                                     !IPAddress.IsLoopback(a));
            return address?.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    public void Shutdown()
    {
        _sweepTimer?.Stop();
        StopReporters();
        _agent?.Dispose();
        _hub?.Dispose();
    }
}
