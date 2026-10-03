using NzbWebDAV.Models.Playback;

namespace NzbWebDAV.Services;

/// <summary>
/// Accepts authoritative lifecycle reports from InfiniDysk's Explore player.
/// The browser supplies only a short correlation token and player state; exact
/// DavItem identity must already be proven by a matching backend Active Read.
/// </summary>
public sealed class NativePlaybackSessionService(
    ActiveReadRegistry activeReadRegistry,
    PlaybackSessionRegistry playbackRegistry)
{
    public bool Report(
        string playerSession,
        PlaybackState state,
        long? positionMs,
        long? durationMs,
        string? title,
        string? mediaType,
        DateTimeOffset now)
    {
        if (!playbackRegistry.TryGetNativeDavItemId(playerSession, out var davItemId)
            && !activeReadRegistry.TryResolveDavItemIdForPlayerSession(playerSession, out davItemId))
            return false;

        playbackRegistry.UpsertNative(
            playerSession,
            davItemId,
            title,
            mediaType,
            state,
            positionMs,
            durationMs,
            now);
        return true;
    }

    public void End(string playerSession) => playbackRegistry.EndNative(playerSession);
}
