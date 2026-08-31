namespace LingXi.App;

/// <summary>shell 设置段（开发文档 11.2）。</summary>
public sealed class ShellSettings
{
    /// <summary>light / dark / system</summary>
    public string Theme { get; set; } = "system";

    public bool CloseToTray { get; set; } = true;

    public bool AutoStart { get; set; }

    public string? LastModule { get; set; }

    /// <summary>
    /// 已禁用模块 Id 列表（"lx.monitor" / "lx.audioswitch"…）。
    /// 设置页写入、即时落盘；壳启动时装载模块前过滤，重启后生效（不做运行时热卸载）。
    /// </summary>
    public List<string> DisabledModules { get; set; } = [];
}
