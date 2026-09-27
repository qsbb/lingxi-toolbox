using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LingXi.Host.Protocol;

/// <summary>
/// JSON Lines 读写的公共实现（两个宿主必须逐字节一致的部分）。
///
/// 约定：stdout 只承载协议；每行请求最多 256 KiB；响应序列化后不得超过 2 MiB。
/// </summary>
public static class JsonLineProtocol
{
    public const int MaxRequestLineLength = 256 * 1024;
    public const int MaxMethodLength = 80;
    public const int MaxRequestIdLength = 128;
    public const int MaxParameterCount = 32;
    public const int MaxResponseLineLength = 2 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteResponseAsync(StreamWriter writer, Response response)
    {
        var json = JsonSerializer.Serialize(response, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaxResponseLineLength)
        {
            json = JsonSerializer.Serialize(
                Response.Failure(response.Id, "response_too_large", "Response exceeds size limit"), Json);
        }

        await writer.WriteLineAsync(json);
    }

    public static async Task<string?> ReadLineLimitedAsync(TextReader reader, int maxLength)
    {
        var line = new StringBuilder(Math.Min(maxLength, 4096));
        var oneChar = new char[1];
        var readAny = false;

        while (true)
        {
            var count = await reader.ReadAsync(oneChar.AsMemory(0, 1));
            if (count == 0)
            {
                if (!readAny) return null;
                if (line.Length > maxLength)
                    throw new ProtocolException("request_too_large", "request exceeds size limit");
                return line.ToString().TrimEnd('\r');
            }

            readAny = true;
            var c = oneChar[0];
            if (c == '\n')
            {
                if (line.Length > maxLength)
                    throw new ProtocolException("request_too_large", "request exceeds size limit");
                return line.ToString().TrimEnd('\r');
            }

            // 多留一个字符，超长行可以被判失败而不必把整行留在内存里。
            if (line.Length <= maxLength) line.Append(c);
        }
    }

    public static void Validate(Request request)
    {
        var method = request.Method?.Trim() ?? string.Empty;
        if (method.Length == 0 || method.Length > MaxMethodLength)
            throw new ProtocolException("invalid_method", "method is invalid");
        request.Method = method;

        if (request.Id is { Length: > MaxRequestIdLength })
            throw new ProtocolException("invalid_request_id", "id is too long");
        if (request.Parameters.Count > MaxParameterCount)
            throw new ProtocolException("too_many_parameters", "too many parameters");
    }

    /// <summary>异常 → 回给前端的错误码。</summary>
    public static string ErrorCode(Exception ex) => ex switch
    {
        ProtocolException protocol => protocol.Code,
        ArgumentException => "invalid_parameters",
        _ => "native_error",
    };

    /// <summary>异常 → 回给前端的文案（不外泄内部细节）。</summary>
    public static string PublicError(Exception ex) => ex switch
    {
        ProtocolException => "Invalid request",
        ArgumentException => "Invalid parameters",
        _ => "Native operation failed",
    };
}
