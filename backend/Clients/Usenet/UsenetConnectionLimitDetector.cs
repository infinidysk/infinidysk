using System.Text.RegularExpressions;
using NzbWebDAV.Exceptions;
using UsenetSharp.Exceptions;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Detects a server-side connection-limit rejection ("502 connection limit (N) reached")
/// from an exception chain and extracts the learned limit N. Covers both stages:
/// auth (AUTHINFO) via <see cref="CouldNotLoginToUsenetException"/> and connect greeting
/// via <see cref="UsenetConnectionException"/> (wrapped in <see cref="CouldNotConnectToUsenetException"/>).
/// </summary>
public static partial class UsenetConnectionLimitDetector
{
    private const int ConnectionLimitResponseCode = 502;

    [GeneratedRegex(@"connection\s+limit\s*\((\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionLimitRegex();

    /// <summary>
    /// Returns true when the exception chain contains a 502 response whose message
    /// matches "connection limit (N)", and outputs the learned limit N.
    /// </summary>
    public static bool TryLearn(Exception exception, out int learnedLimit)
    {
        learnedLimit = 0;

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (!IsConnectionLimit502(current))
                continue;

            if (TryParseLimit(current.Message, out learnedLimit))
                return true;
        }

        return false;
    }

    private static bool IsConnectionLimit502(Exception e) => e switch
    {
        CouldNotLoginToUsenetException login => login.ResponseCode == ConnectionLimitResponseCode,
        UsenetConnectionException greeting => greeting.ResponseCode == ConnectionLimitResponseCode,
        _ => false,
    };

    private static bool TryParseLimit(string message, out int limit)
    {
        var match = ConnectionLimitRegex().Match(message);
        if (match.Success && int.TryParse(match.Groups[1].Value, out limit) && limit > 0)
            return true;

        limit = 0;
        return false;
    }
}
