namespace LingXi.App;

/// <summary>shell 设置段（开发文档 11.2）。</summary>
public sealed class ShellSettings
{
    /// <summary>light / dark / system</summary>
    public string Theme { get; set; } = "system";

    public bool CloseToTray { get; set; } = true;

    public bool AutoStart { get; set; }

    public string? LastModule { get; set; }
}
