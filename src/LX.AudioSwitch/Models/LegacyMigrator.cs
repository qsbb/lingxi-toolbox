using System.IO;
using System.Text.Json;

namespace LingXi.AudioSwitch.Models;

/// <summary>QuickAudioSwitch 旧版 devices.json 一次性迁移（读后改名 .migrated 保留回滚，开发文档 8.5）。</summary>
public static class LegacyMigrator
{
    private sealed record LegacyDevice(string Id, string Name);

    public static List<SavedDevice>? TryLoad()
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QuickAudioSwitch", "devices.json");
            if (!File.Exists(legacy))
            {
                return null;
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = JsonSerializer.Deserialize<List<LegacyDevice>>(File.ReadAllText(legacy), options);
            var result = list?
                .Where(d => !string.IsNullOrWhiteSpace(d.Id))
                .Select(d => new SavedDevice { Id = d.Id, Alias = d.Name })
                .ToList();

            File.Move(legacy, legacy + ".migrated", overwrite: true);
            return result;
        }
        catch
        {
            // 迁移失败只警告不丢数据：旧文件原样保留
            return null;
        }
    }
}
