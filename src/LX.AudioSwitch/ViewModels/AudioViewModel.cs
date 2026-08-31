using System.Collections.ObjectModel;
using System.Windows;
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

    public ObservableCollection<DeviceVm> Devices { get; } = [];

    [ObservableProperty]
    private string _currentDeviceName = "未检测到默认设备";

    [ObservableProperty]
    private string _deviceCountText = "0 个已保存设备";

    public AudioViewModel(IAudioEndpointService audio, ILxModuleContext ctx)
    {
        _audio = audio;
        _ctx = ctx;
        _audio.DevicesChanged += OnAudioChanged;
        LoadSettings();
    }

    private void OnAudioChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.Invoke(Refresh);

    /// <summary>刷新设备列表状态与当前默认设备（对应 QAS RefreshDevices）。</summary>
    public void Refresh()
    {
        try
        {
            var discovered = _audio.GetDevices(DataFlow.Render);
            var byId = discovered.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            var currentId = _audio.GetDefaultId(DataFlow.Render);

            foreach (var item in Devices)
            {
                if (byId.TryGetValue(item.Id, out var endpoint))
                {
                    item.DisplayName = endpoint.Name;
                    item.IsAvailable = endpoint.State == AudioDeviceState.Active;
                    item.IsCurrent = item.IsAvailable &&
                        string.Equals(item.Id, currentId, StringComparison.OrdinalIgnoreCase);
                    (item.StatusText, item.Level) = item.IsCurrent
                        ? ("正在使用", LxStatusLevel.Ok)
                        : ("可用", LxStatusLevel.Info);
                }
                else
                {
                    item.IsAvailable = false;
                    item.IsCurrent = false;
                    (item.StatusText, item.Level) = ("已拔出", LxStatusLevel.Warn);
                }
            }

            var current = discovered.FirstOrDefault(x =>
                x.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase));
            CurrentDeviceName = current?.Name ?? "未检测到默认设备";
            DeviceCountText = $"{Devices.Count} 个已保存设备";
            _ctx.Tray.SetTooltip($"当前：{CurrentDeviceName}");
        }
        catch (Exception ex)
        {
            CurrentDeviceName = $"读取设备失败：{ex.Message}";
            _ctx.Log.Error("刷新音频设备失败", ex);
        }
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
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("切换默认设备失败", ex);
            _ctx.Notify.Show("凌溪·音频", $"切换失败：{ex.Message}");
        }
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

    /// <summary>全局热键：在保存列表里轮播下一个可用设备（跳过已拔出）。</summary>
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

    private void LoadSettings()
    {
        var saved = _ctx.Settings.Get<AudioSettings>("lx.audioswitch");
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
        DeviceCountText = $"{Devices.Count} 个已保存设备";
    }

    internal void Shutdown() => _audio.DevicesChanged -= OnAudioChanged;
}
