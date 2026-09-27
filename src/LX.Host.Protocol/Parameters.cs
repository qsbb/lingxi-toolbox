namespace LingXi.Host.Protocol;

/// <summary>请求参数读取（两端共用，容忍 JSON 数字/布尔的字符串形式）。</summary>
public static class Parameters
{
    public static string? Optional(IReadOnlyDictionary<string, object?> parameters, string key) =>
        parameters.TryGetValue(key, out var value) ? value?.ToString() : null;

    public static int? OptionalInt(IReadOnlyDictionary<string, object?> parameters, string key) =>
        int.TryParse(Optional(parameters, key), out var value) ? value : null;

    public static bool? OptionalBool(IReadOnlyDictionary<string, object?> parameters, string key) =>
        bool.TryParse(Optional(parameters, key), out var value) ? value : null;
}
