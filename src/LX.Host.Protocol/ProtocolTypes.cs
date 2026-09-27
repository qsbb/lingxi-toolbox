using System.Text.Json.Serialization;

namespace LingXi.Host.Protocol;

/// <summary>一条 JSON Lines 请求。</summary>
public sealed class Request
{
    public string? Id { get; set; }
    public string? Method { get; set; }
    public Dictionary<string, object?>? Params { get; set; }

    [JsonIgnore]
    public Dictionary<string, object?> Parameters => Params ??= new(StringComparer.Ordinal);
}

/// <summary>一条响应；<c>Result</c> 与 <c>Error</c> 互斥。</summary>
public sealed record Response(string? Id, bool Ok, object? Result, ErrorBody? Error)
{
    public static Response Success(string? id, object result) => new(id, true, result, null);

    public static Response Failure(string? id, string code, string message) =>
        new(id, false, null, new ErrorBody(code, message));
}

public sealed record ErrorBody(string Code, string Message);

/// <summary>带机器可读错误码的协议异常（错误码会原样回给前端）。</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}
