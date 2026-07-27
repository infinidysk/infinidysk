using NzbWebDAV.Logging;
using Serilog;

namespace NzbWebDAV.WebDav.Base;

/// <summary>
/// Throttled logging for writes refused by the read-only parts of the WebDAV tree.
/// Clients that write metadata sidecars into the mount (media scanners, *Arr metadata
/// writers) re-attempt on every scan, so a large library can produce many rejections
/// per second indefinitely. Logging each one at Warning drowns the Warning-only ring
/// buffer that support packs are collected from, so the individual rejection goes to
/// Debug and one aggregated Warning names the path per window.
/// </summary>
internal static class ReadonlyWriteRejectionLog
{
    private static readonly LogThrottle Throttle = new();
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    internal static void Rejected(string operation, string itemName, string scopeName, string scopeKey)
    {
        Log.Debug("Refused to {Operation} {ItemName} under {Scope}: read-only", operation, itemName, scopeName);

        if (!Throttle.ShouldLog($"{scopeKey}/{operation}", Interval, out var suppressed))
            return;

        Log.Warning(
            "Refused to {Operation} under read-only path {Scope} — {Attempts} attempt(s) in the last {Minutes} minutes, " +
            "most recently {ItemName}. A client is trying to write into the NzbDav mount, which does not accept writes.",
            operation, scopeName, suppressed + 1, Interval.TotalMinutes, itemName);
    }
}
