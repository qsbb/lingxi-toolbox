using System.IO;
using LingXi.Core.Settings;
using Xunit;

namespace LX.Core.Tests;

/// <summary>设置存取：roundtrip / 损坏回退 / 变更事件（在 windows-latest CI 运行）。</summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lx-settings-{Guid.NewGuid():N}.json");

    private sealed class Probe
    {
        public int Value { get; set; }

        public string? Name { get; set; }
    }

    [Fact]
    public void Roundtrip_Module_Section()
    {
        var store = new SettingsStore(_path);
        store.Set("lx.probe", new Probe { Value = 42, Name = "凌溪" });

        var back = new SettingsStore(_path).Get<Probe>("lx.probe");

        Assert.Equal(42, back.Value);
        Assert.Equal("凌溪", back.Name);
    }

    [Fact]
    public void Corrupt_File_Falls_Back_To_Default()
    {
        File.WriteAllText(_path, "not json at all");
        var store = new SettingsStore(_path);
        var probe = store.Get<Probe>("lx.probe");
        Assert.NotNull(probe);
        Assert.Equal(0, probe.Value);
    }

    [Fact]
    public void Missing_Section_Returns_Default()
    {
        var store = new SettingsStore(_path);
        var probe = store.Get<Probe>("never.set");
        Assert.NotNull(probe);
    }

    [Fact]
    public void Changed_Event_Fires_On_Set()
    {
        var store = new SettingsStore(_path);
        var fired = false;
        store.Changed += () => fired = true;
        store.Set("lx.probe", new Probe { Value = 1 });
        Assert.True(fired);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // 清理失败不致命
        }
    }
}
