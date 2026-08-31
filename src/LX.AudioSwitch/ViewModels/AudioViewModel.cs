using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LingXi.Audio;
using LingXi.AudioSwitch.Models;
using LingXi.Sdk;
using LingXi.Ui.Controls;

namespace LingXi.AudioSwitch.ViewModels;

/// <summary>音频快切 VM（QAS MainWindow.xaml.cs 的逻辑迁入，改 MVVM，开发文档 8.2/8.6）。</summary>
public partial class AudioViewModel : ObservableObject
{
    private readonly IAudioEndpointService _audio;
    private readonly ILxModuleContext _ctx;
    private int _cycleIndex = -1;
    private DispatcherTimer? _hudTimer;

    /// <summary>已保存设备（主数据源：托盘菜单 / 热键轮播 / 设置持久化仍以它为准）。</summary>
    public ObservableCollection<DeviceVm> Devices { get; } = [];

    /// <summary>输出设备分组（呈现层投影，由 Refresh 重建，不影响 Devices 主序）。</summary>
    public ObservableCollection<DeviceVm> OutputDevices { get; } = [];

    /// <summary>输入设备分组（呈现层投影，由 Refresh 重建）。</summary>
    public ObservableCollection<DeviceVm> InputDevices { get; } = [];

    [ObservableProperty]
    private string _currentDeviceName = "未检测到默认设备";

    [ObservableProperty]
    private int _outputCount;

    [ObservableProperty]
    private int _inputCount;

    [ObservableProperty]
    private bool _hasOutputDevices;

    [ObservableProperty]
    private bool _hasInputDevices;

    [ObservableProperty]
    private string _switchHudText = "";

    [ObservableProperty]
    private bool _isSwitchHudOpen;

    /// <summary>热键键帽序列（kbd 风格逐键显示，来自 lx.audioswitch.CycleHotkey）。</summary>
    public IReadOnlyList<string> HotkeyKeys { get; }

    /// <summary>热键组合原始文本（兜底展示/无障碍读值）。</summary>
    public string CycleHotkeyText { get; }

    public AudioViewModel(IAudioEndpointService audio, ILxModuleContext ctx)
    {
        _audio = audio;
        _ctx = ctx;

        var saved = ctx.Settings.Get<AudioSettings>("lx.audioswitch");
        CycleHotkeyText = string.IsNullOrWhiteSpace(saved.CycleHotkey)
            ? "Ctrl+Alt+A"
            : saved.CycleHotkey.Trim();
        HotkeyKeys = [.. CycleHotkeyText.Split('+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        if (HotkeyKeys.Count == 0)
        {
            HotkeyKeys = ["Ctrl", "Alt", "A"];
        }

        _audio.DevicesChanged += OnAudioChanged;
        LoadSettings(saved);
    }

    private void OnAudioChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.Invoke(Refresh);

    /// <summary>刷新设备列表状态、输出/输入方向分组与当前默认设备（对应 QAS RefreshDevices）。</summary>
    public void Refresh()
    {
        try
        {
            var renderById = _audio.GetDevices(DataFlow.Render)
                .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            var captureById = _audio.GetDevices(DataFlow.Capture)
                .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            var renderDefaultId = _audio.GetDefaultId(DataFlow.Render);
            var captureDefaultId = _audio.GetDefaultId(DataFlow.Capture);

            foreach (var item in Devices)
            {
                if (renderById.TryGetValue(item.Id, out var endpoint))
                {
                    item.IsOutput = true;
                    ApplyEndpointState(item, endpoint, renderDefaultId);
                }
                else if (captureById.TryGetValue(item.Id, out var captureEndpoint))
                {
                    item.IsOutput = false;
                    ApplyEndpointState(item, captureEndpoint, captureDefaultId);
                }
                else
                {
                    // 已拔出：方向沿用上次刷新结果，状态置 Warn
                    item.IsAvailable = false;
                    item.IsCurrent = false;
                    (item.StatusText, item.Level) = ("已拔出", LxStatusLevel.Warn);
                }
                ApplyDeviceKind(item);
            }

            RebuildGroups();

            var current = renderDefaultId is not null &&
                          renderById.TryGetValue(renderDefaultId, out var renderCurrent)
                ? renderCurrent
                : null;
            CurrentDeviceName = current?.Name ?? "未检测到默认设备";
            _ctx.Tray.SetTooltip($"当前：{CurrentDeviceName}");
        }
        catch (Exception ex)
        {
            CurrentDeviceName = $"读取设备失败：{ex.Message}";
            _ctx.Log.Error("刷新音频设备失败", ex);
        }
    }

    /// <summary>单设备状态：可用性 + 是否当前默认 + 语义色（切换逻辑行为不变）。</summary>
    private static void ApplyEndpointState(DeviceVm item, AudioEndpoint endpoint, string? defaultId)
    {
        item.DisplayName = endpoint.Name;
        item.IsAvailable = endpoint.State == AudioDeviceState.Active;
        item.IsCurrent = item.IsAvailable &&
            string.Equals(item.Id, defaultId, StringComparison.OrdinalIgnoreCase);
        (item.StatusText, item.Level) = item.IsCurrent
            ? ("正在使用", LxStatusLevel.Ok)
            : ("可用", LxStatusLevel.Info);
    }

    /// <summary>按设备名与方向解析类型图标与端点类型副标题（纯呈现层语义）。</summary>
    private static void ApplyDeviceKind(DeviceVm item)
    {
        var (glyph, kindLabel) = ResolveDeviceKind(item.DisplayName, isCapture: !item.IsOutput);
        item.IconGlyph = glyph;
        item.EndpointKindText = $"{(item.IsOutput ? "输出" : "输入")} · {kindLabel}";
    }

    private static (string Glyph, string KindLabel) ResolveDeviceKind(string name, bool isCapture)
    {
        var n = name ?? string.Empty;
        bool Has(params string[] keys) =>
            keys.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (isCapture)
        {
            return ("\uE720", Has("麦克风", "mic", "话筒") ? "麦克风" : "录音设备");
        }
        if (Has("耳机", "耳麦", "headphone", "headset", "earphone"))
        {
            return ("\uE7F3", "耳机");
        }
        if (Has("显示器", "monitor", "hdmi", "displayport", "电视", "tv"))
        {
            return ("\uE7F4", "显示器音频");
        }
        if (Has("数字输出", "spdif", "s/pdif", "光纤", "optical"))
        {
            return ("\uE767", "数字输出");
        }
        if (Has("扬声器", "音箱", "音响", "speaker"))
        {
            return ("\uE767", "扬声器");
        }
        return ("\uE767", "播放设备");
    }

    /// <summary>按方向重建分组投影与数量徽章（呈现层；Devices 主序与持久化不受影响）。</summary>
    private void RebuildGroups()
    {
        OutputDevices.Clear();
        InputDevices.Clear();
        foreach (var d in Devices)
        {
            (d.IsOutput ? OutputDevices : InputDevices).Add(d);
        }
        OutputCount = OutputDevices.Count;
        InputCount = InputDevices.Count;
        HasOutputDevices = OutputDevices.Count > 0;
        HasInputDevices = InputDevices.Count > 0;
    }

    [RelayCommand]
    private void Switch(DeviceVm? item)
    {
        if (item is null || !item.IsAvailable)
        {
            return;
        }
        try
        {
            _audio.SetDefault(item.Id);
            Refresh();
            _ctx.Notify.Show("凌溪·音频", $"已切换到「{item.DisplayName}」");
            ShowSwitchHud(item.DisplayName);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("切换默认设备失败", ex);
            _ctx.Notify.Show("凌溪·音频", $"切换失败：{ex.Message}");
        }
    }

    /// <summary>页内 HUD 反馈（开发文档 8.4 的 HUD 语义；开关复用 ShowSwitchHud 设置）。</summary>
    private void ShowSwitchHud(string deviceName)
    {
        if (!_ctx.Settings.Get<AudioSettings>("lx.audioswitch").ShowSwitchHud)
        {
            return;
        }
        SwitchHudText = $"已切换到「{deviceName}」";
        IsSwitchHudOpen = true;

        if (_hudTimer is null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                IsSwitchHudOpen = false;
            };
            _hudTimer = timer;
        }
        _hudTimer.Stop();
        _hudTimer.Start();
    }

    [RelayCommand]
    private void Remove(DeviceVm? item)
    {
        if (item is null)
        {
            return;
        }
        Devices.Remove(item);
        SaveSettings();
        Refresh();
    }

    [RelayCommand]
    private void Add()
    {
        try
        {
            var existing = Devices.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = _audio.GetDevices(DataFlow.Render, activeOnly: true)
                .Where(x => !existing.Contains(x.Id))
                .ToList();

            var dialog = new Views.AddDeviceWindow(candidates)
            {
                Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive),
            };
            dialog.ShowDialog();
            if (dialog.Selected is { } picked)
            {
                Devices.Add(new DeviceVm(picked.Id, picked.Name));
                SaveSettings();
                Refresh();
            }
        }
        catch (Exception ex)
        {
            _ctx.Notify.Show("凌溪·音频", $"无法打开添加设备窗口：{ex.Message}");
        }
    }

    /// <summary>全局热键：在保存列表里轮播下一个可用设备（跳过已拔出，行为不变）。</summary>
    public void CycleNext()
    {
        var order = Devices.ToList();
        if (order.Count == 0)
        {
            return;
        }
        if (_cycleIndex >= order.Count || _cycleIndex < -1)
        {
            _cycleIndex = -1;
        }
        for (var step = 1; step <= order.Count; step++)
        {
            var idx = (_cycleIndex + step) % order.Count;
            if (order[idx].IsAvailable)
            {
                _cycleIndex = idx;
                Switch(order[idx]);
                return;
            }
        }
    }

    private void LoadSettings(AudioSettings saved)
    {
        if (saved.SavedDevices.Count == 0)
        {
            var migrated = Models.LegacyMigrator.TryLoad();
            if (migrated is { Count: > 0 })
            {
                saved.SavedDevices = migrated;
                _ctx.Settings.Set("lx.audioswitch", saved);
                _ctx.Log.Info($"已从 QuickAudioSwitch 迁移 {migrated.Count} 个已保存设备");
            }
        }
        foreach (var d in saved.SavedDevices.Where(d => !string.IsNullOrWhiteSpace(d.Id)))
        {
            Devices.Add(new DeviceVm(d.Id, string.IsNullOrWhiteSpace(d.Alias) ? "设备" : d.Alias));
        }
        RebuildGroups();
    }

    private void SaveSettings()
    {
        var saved = new Models.AudioSettings
        {
            SavedDevices = Devices
                .Select(d => new Models.SavedDevice { Id = d.Id, Alias = d.DisplayName })
                .ToList(),
            CycleHotkey = _ctx.Settings.Get<Models.AudioSettings>("lx.audioswitch").CycleHotkey,
            ShowSwitchHud = _ctx.Settings.Get<Models.AudioSettings>("lx.audioswitch").ShowSwitchHud,
        };
        _ctx.Settings.Set("lx.audioswitch", saved);
    }

    internal void Shutdown()
    {
        _audio.DevicesChanged -= OnAudioChanged;
        _hudTimer?.Stop();
        _hudTimer = null;
    }
}
