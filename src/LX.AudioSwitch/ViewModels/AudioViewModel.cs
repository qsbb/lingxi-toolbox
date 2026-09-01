using System.Windows.Input;
using System.Collections.Generic;
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

    /// <summary>热键键帽序列（kbd 风格逐键显示；空 = 未启用热键）。</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _hotkeyKeys = [];

    /// <summary>热键组合原始文本（兜底展示/无障碍读值；空 = 未启用）。</summary>
    [ObservableProperty]
    private string _cycleHotkeyText = "";

    /// <summary>是否处于录制态（点击"修改热键"后按键直接捕获）。</summary>
    [ObservableProperty]
    private bool _isRecordingHotkey;

    /// <summary>录制提示文字。</summary>
    [ObservableProperty]
    private string _recordingHint = "点击键帽修改热键（按退格清空=停用；Esc 取消）。热键在已保存设备间循环切换。";

    /// <summary>热键修改回调（模块注册重载用，VM 保存后通知模块重新 Register）。</summary>
    public event Action<string?>? HotkeyChanged;

    /// <summary>把 "Ctrl+Alt+A" 文本应用到 UI（键帽拆分 + 文本）。</summary>
    private void ApplyHotkeyText(string text)
    {
        CycleHotkeyText = text;
        HotkeyKeys = string.IsNullOrEmpty(text)
            ? []
            : [.. text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    public AudioViewModel(IAudioEndpointService audio, ILxModuleContext ctx)
    {
        _audio = audio;
        _ctx = ctx;

        var saved = ctx.Settings.Get<AudioSettings>("lx.audioswitch");
        ApplyHotkeyText(string.IsNullOrWhiteSpace(saved.CycleHotkey) ? "" : saved.CycleHotkey.Trim());

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
            _ctx.Notify.Show("音频设备切换", $"已切换到「{item.DisplayName}」");
            ShowSwitchHud(item.DisplayName);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("切换默认设备失败", ex);
            _ctx.Notify.Show("音频设备切换", $"切换失败：{ex.Message}");
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
            _ctx.Notify.Show("音频设备切换", $"无法打开添加设备窗口：{ex.Message}");
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
            CycleHotkey = CycleHotkeyText,
            ShowSwitchHud = _ctx.Settings.Get<Models.AudioSettings>("lx.audioswitch").ShowSwitchHud,
        };
        _ctx.Settings.Set("lx.audioswitch", saved);
    }

    // ============ 热键录制（应用内修改，退格=清空/不启用） ============

    /// <summary>进入录制态（UI 点击"修改热键"）。</summary>
    [RelayCommand]
    private void EditHotkey()
    {
        IsRecordingHotkey = true;
        RecordingHint = "请按下新组合键（可含 Ctrl/Alt/Shift/Win）；按退格键清空并停用；Esc 取消";
    }

    /// <summary>取消录制（保留原热键）。</summary>
    [RelayCommand]
    private void CancelHotkeyRecording()
    {
        IsRecordingHotkey = false;
        RecordingHint = "点击键帽修改热键（按退格清空=停用；Esc 取消）。热键在已保存设备间循环切换。";
    }

    /// <summary>
    /// 录制态按键处理（由视图 PreviewKeyDown 转发）。
    /// 修饰键本身忽略（等待组合完成）；退格=清空停用；Esc=取消；其余=组合键落地。
    /// </summary>
    internal void HandleRecordingKey(Key key, ModifierKeys mods)
    {
        if (!IsRecordingHotkey)
        {
            return;
        }
        if (key == Key.Escape)
        {
            CancelHotkeyRecording();
            return;
        }
        if (key == Key.Back)
        {
            // 退格 = 清空（不开启热键）
            SaveHotkeyAndReload("");
            IsRecordingHotkey = false;
            RecordingHint = "点击键帽修改热键（按退格清空=停用；Esc 取消）。热键在已保存设备间循环切换。";
            return;
        }
        // 单独按修饰键不算完成
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.Left or Key.Right or Key.Up or Key.Down)
        {
            return;
        }
        // 无修饰键的功能键/字母也可接受，但纯单键易误触——允许（用户显式选择）
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        SaveHotkeyAndReload(string.Join("+", parts));
        IsRecordingHotkey = false;
        RecordingHint = "点击键帽修改热键（按退格清空=停用；Esc 取消）。热键在已保存设备间循环切换。";
    }

    /// <summary>保存热键到 settings 并触发模块重载注册；空串=停用。</summary>
    private void SaveHotkeyAndReload(string gesture)
    {
        ApplyHotkeyText(gesture);
        SaveSettings();
        HotkeyChanged?.Invoke(string.IsNullOrEmpty(gesture) ? null : gesture);
    }

    internal void Shutdown()
    {
        _audio.DevicesChanged -= OnAudioChanged;
        _hudTimer?.Stop();
        _hudTimer = null;
    }
}
