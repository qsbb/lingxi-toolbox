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

        // 全局热键：Ctrl+Alt+A 轮播切换（可在 settings.json 改 CycleHotkey）
        var gesture = _ctx.Settings.Get<Models.AudioSettings>("lx.audioswitch").CycleHotkey;
        ctx.Hotkeys.Register($"{Id}.cycle", gesture, () => _vm?.CycleNext());
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
