using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Auth;

/// <summary>
/// HMAC download keys for STRM and <c>/view</c> URLs. Transport-neutral so Queue
/// post-processors and WebDAV handlers do not depend on API request types.
/// </summary>
public static class DownloadKey
{
    public static string Generate(string apiKey, string path)
    {
        var keyBytes = Encoding.UTF8.GetBytes(apiKey);
        var pathBytes = Encoding.UTF8.GetBytes(path);
        var hashBytes = HMACSHA256.HashData(keyBytes, pathBytes);
        return Convert.ToHexStringLower(hashBytes);
    }
}
