namespace LingXi.Audio;

/// <summary>CoreAudio 端点数据流方向（原 QAS internal，接口化后公开）。</summary>
public enum DataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}
