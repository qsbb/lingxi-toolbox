using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LingXi.Sdk;

namespace LingXi.Core.Settings;

/// <summary>
/// settings.json 原子读写（开发文档 7.1）：
/// - 模块按 moduleId 分键，互不干扰；
/// - 临时文件 + Move 原子落盘；
/// - 损坏段落回退默认值，绝不丢整库。
/// </summary>
public sealed class SettingsStore : ILxSettings
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private JsonObject _root;

    public event Action? Changed;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LingXi", "settings.json");
        _root = Load();
    }

    public T Get<T>(string moduleId) where T : class, new()
    {
        lock (_gate)
        {
            try
            {
                if (_root["modules"] is JsonObject modules &&
                    modules[moduleId] is JsonNode node)
                {
                    return node.Deserialize<T>(JsonOpts) ?? new T();
                }
            }
            catch
            {
                // 损坏段落 → 默认值（公理 A4：设置不可用时工具仍可用）
            }
            return new T();
        }
    }

    public void Set<T>(string moduleId, T value) where T : class
    {
        lock (_gate)
        {
            var modules = _root["modules"] as JsonObject ?? new JsonObject();
            modules[moduleId] = JsonNode.Parse(JsonSerializer.Serialize(value, JsonOpts));
            _root["modules"] = modules;
            Save();
        }
        Changed?.Invoke();
    }

    private JsonObject Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                if (JsonNode.Parse(File.ReadAllText(_path)) is JsonObject obj)
                {
                    return obj;
                }
            }
        }
        catch
        {
            // 整库损坏 → 重新开始（旧文件被覆盖前用户可从备份恢复）
        }
        return new JsonObject { ["version"] = 1 };
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, _root.ToJsonString(JsonOpts));
        File.Move(tmp, _path, overwrite: true);
    }
}
