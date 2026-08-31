using LingXi.Audio;

// 音频切换功能验证（Windows 运行）：
// 1) 枚举播放设备 → 2) 记住当前默认 → 3) 切到另一可用设备并验证 → 4) 切回原默认。
// 全程通过 LX.Audio.Core 的真实 COM 内核（IPolicyConfig 三角色），即凌溪·音频的切换链路。

using var audio = new AudioDeviceService();

Console.WriteLine("=== 当前播放设备 ===");
var devices = audio.GetDevices(DataFlow.Render);
foreach (var d in devices)
{
    var mark = d.Id == audio.GetDefaultId(DataFlow.Render) ? "  ← 默认" : "";
    Console.WriteLine($"  [{d.State}] {d.Name}{mark}");
}

var originalId = audio.GetDefaultId(DataFlow.Render);
if (originalId is null)
{
    Console.WriteLine("未检测到默认设备，退出。");
    return 1;
}

var candidates = devices
    .Where(d => d.State == AudioDeviceState.Active && !d.Id.Equals(originalId, StringComparison.OrdinalIgnoreCase))
    .ToList();
if (candidates.Count == 0)
{
    Console.WriteLine("只有一个可用设备，无法验证切换；枚举与默认读取已验证通过。");
    return 0;
}

var target = candidates[0];
Console.WriteLine($"\n=== 切换到：{target.Name} ===");
audio.SetDefault(target.Id);
Thread.Sleep(300);
var afterSwitch = audio.GetDefaultId(DataFlow.Render);
Console.WriteLine($"切换后默认 = {devices.FirstOrDefault(x => x.Id == afterSwitch)?.Name ?? afterSwitch}");
var switchedOk = string.Equals(afterSwitch, target.Id, StringComparison.OrdinalIgnoreCase);

Console.WriteLine($"\n=== 切回：{devices.FirstOrDefault(x => x.Id == originalId)?.Name} ===");
audio.SetDefault(originalId);
Thread.Sleep(300);
var restored = audio.GetDefaultId(DataFlow.Render);
Console.WriteLine($"回滚后默认 = {devices.FirstOrDefault(x => x.Id == restored)?.Name ?? restored}");
var restoredOk = string.Equals(restored, originalId, StringComparison.OrdinalIgnoreCase);

Console.WriteLine();
Console.WriteLine(switchedOk && restoredOk
    ? "音频切换功能验证通过：切换 + 回滚均成功"
    : "验证失败：" + (switchedOk ? "" : "切换未生效 ") + (restoredOk ? "" : "回滚未生效"));
return switchedOk && restoredOk ? 0 : 1;
