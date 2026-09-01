using System.Windows;
using LingXi.Audio;
using LingXi.AudioSwitch.ViewModels;
using LingXi.Sdk;

namespace LingXi.AudioSwitch;

/// <summary>音频设备切换模块（开发文档 8 章）。</summary>
public sealed class AudioModule : ILxToolModule
{
    private ILxModuleContext _ctx = null!;
    private AudioViewModel? _vm;

    public string Id => "lx.audioswitch";

    public string DisplayName => "音频设备切换";

    public string IconGlyph => "Speaker24";

    public Version Version => new(1, 0, 0);

    public void Initialize(ILxModuleContext ctx)
    {
        _ctx = ctx;
        try
        {
            var audio = AudioEndpointServiceFactory.Create();
            _vm = new AudioViewModel(audio, ctx);
            audio.DevicesChanged += (_, _) =>
                Application.Current?.Dispatcher.BeginInvoke(
                    () => _ctx.Tray.SetMenu(Id, BuildTrayActions()));
        }
        catch (Exception ex)
        {
            ctx.Log.Error("音频服务初始化失败（可能无音频设备/系统音频服务未运行）", ex);
        }
        _ctx.Tray.SetMenu(Id, BuildTrayActions());

        // 全局热键：默认不启用（空手势）；应用内录制修改后热重载
        var gesture = _ctx.Settings.Get<Models.AudioSettings>("lx.audioswitch").CycleHotkey;
        ApplyHotkey(string.IsNullOrWhiteSpace(gesture) ? null : gesture.Trim());

        // VM 保存热键后通知模块重新注册（含清空 → 注销）
        if (_vm is not null)
        {
            _vm.HotkeyChanged += g => Application.Current?.Dispatcher.BeginInvoke(() => ApplyHotkey(g));
        }
    }

    /// <summary>注册/重注册轮播热键；null 或空 = 注销（不启用）。</summary>
    private void ApplyHotkey(string? gesture)
    {
        var id = $"{Id}.cycle";
        _ctx.Hotkeys.Unregister(id);
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return;
        }
        var ok = _ctx.Hotkeys.Register(id, gesture, () => _vm?.CycleNext());
        if (!ok)
        {
            _ctx.Notify.Show("音频设备切换", $"热键 {gesture} 注册失败（可能被其他程序占用）");
        }
    }


    private IReadOnlyList<LxTrayAction> BuildTrayActions()
    {
        var vm = _vm;
        if (vm is null)
        {
            return [new LxTrayAction("音频服务不可用", IsEnabled: false)];
        }

        var items = new List<LxTrayAction>();
        foreach (var device in vm.Devices)
        {
            var d = device;
            items.Add(new LxTrayAction(
                (d.IsCurrent ? "● " : "") + d.DisplayName,
                () => vm.SwitchCommand.Execute(d),
                IsEnabled: d.IsAvailable));
        }
        if (items.Count == 0)
        {
            items.Add(new LxTrayAction("暂无已保存设备（在主界面添加）", IsEnabled: false));
        }
        return items;
    }

    public FrameworkElement CreateMainView()
    {
        _vm?.Refresh();
        return new Views.AudioView { DataContext = _vm };
    }

    public void Shutdown()
    {
        _vm?.Shutdown();
        _ctx.Hotkeys.Unregister($"{Id}.cycle");
    }
}
