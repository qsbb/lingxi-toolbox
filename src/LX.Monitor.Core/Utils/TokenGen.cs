using System.Security.Cryptography;

namespace LingXi.Monitor.Core;

/// <summary>上报 token 生成：sm_ + 32 位小写 hex（与官方安装脚本同规格：16 字节随机）。</summary>
public static class TokenGen
{
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        return "sm_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
