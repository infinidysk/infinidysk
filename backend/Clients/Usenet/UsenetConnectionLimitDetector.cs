using System.Text.RegularExpressions;
using NzbWebDAV.Exceptions;
using UsenetSharp.Exceptions;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Detects a server-side connection-limit rejection ("502 connection limit (N) reached",
/// "481 exceeded maximum number of connections per user") from an exception chain and
/// extracts the learned limit N when stated. Covers both stages:
/// auth (AUTHINFO) via <see cref="CouldNotLoginToUsenetException"/> and connect greeting
/// via <see cref="UsenetConnectionException"/> (wrapped in <see cref="CouldNotConnectToUsenetException"/>).
/// </summary>
public static partial class UsenetConnectionLimitDetector
{
    private const int ConnectionLimitResponseCode = 502;
    // 481 is a generic auth rejection; it counts only when the text names a connection limit.
    private const int AuthRejectedResponseCode = 481;

    [GeneratedRegex(@"connection\s+limit\s*\((\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionLimitRegex();

    [GeneratedRegex(
        @"connection\s+limit|too\s+many\s+(?:connections|sessions|users|logins)|max(?:imum)?\s+(?:\w+\s+){0,2}connections",
        RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionLimitTextRegex();

    /// <summary>
    /// Returns true when the exception chain contains a 502 or 481 response whose text reports a
    /// connection limit, whether or not it states the number (e.g. "502 Too many connections").
    /// </summary>
    public static bool IsConnectionLimitRejection(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (IsLimitResponse(current) && ConnectionLimitTextRegex().IsMatch(current.Message))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when the exception chain contains a 502 or 481 response whose message
    /// matches "connection limit (N)", and outputs the learned limit N.
    /// </summary>
    public static bool TryLearn(Exception exception, out int learnedLimit)
    {
        learnedLimit = 0;

        for (var current = exception; current != null; current = current.InnerException)
        {
            if (!IsLimitResponse(current))
                continue;

            if (TryParseLimit(current.Message, out learnedLimit))
                return true;
        }

        return false;
    }

    private static bool IsLimitResponse(Exception e) => e switch
    {
        CouldNotLoginToUsenetException login => IsLimitCode(login.ResponseCode),
        UsenetConnectionException greeting => IsLimitCode(greeting.ResponseCode),
        _ => false,
    };

    private static bool IsLimitCode(int? code) =>
        code is ConnectionLimitResponseCode or AuthRejectedResponseCode;

    private static bool TryParseLimit(string message, out int limit)
    {
        var match = ConnectionLimitRegex().Match(message);
        if (match.Success && int.TryParse(match.Groups[1].Value, out limit) && limit > 0)
            return true;

        limit = 0;
        return false;
    }
}
