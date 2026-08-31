using System.Windows;
using LingXi.Monitor.Core;
using LingXi.Monitor.ViewModels;
using LingXi.Sdk;

namespace LingXi.Monitor;

/// <summary>凌溪·监控模块（开发文档 9 章）：LX Hub + agent 托管 + 仪表盘 + 告警。</summary>
public sealed class MonitorModule : ILxToolModule
{
    private ILxModuleContext _ctx = null!;
    private readonly SnapshotStore _store = new();
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

        // 首次生成 Hub token（sm_ + 32hex，与官方规格一致）
        if (string.IsNullOrWhiteSpace(settings.HubToken))
        {
            settings.HubToken = TokenGen.NewToken();
            ctx.Settings.Set("lx.monitor", settings);
        }

        var options = new HubOptions
        {
            Port = settings.HubPort,
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

        _vm = new DashboardViewModel(_store)
        {
            HubReportUrl = _hub.ReportUrl,
            HubToken = settings.HubToken,
        };

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

    public void Shutdown()
    {
        _sweepTimer?.Stop();
        _agent?.Dispose();
        _hub?.Dispose();
    }
}
