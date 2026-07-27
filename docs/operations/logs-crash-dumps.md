# Logs and crash dumps

## Container logs

```bash
docker compose logs --tail=200 -f nzbdav
docker compose logs --tail=200 -f nzbdav_rclone
```

When a process exits, the entrypoint logs the exit code. Values above `128` encode a fatal signal (`128+N`) — e.g. `139` is SIGSEGV, `132` is SIGILL.

The admin UI also offers a live log viewer.

## Stream traces [since 0.10.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.10.0){ .nzbdav-since }

Capture per-segment playback events while reproducing buffering or seek stalls:

1. Open **Settings → Support → Developer stream tracing**.
2. Choose 15, 30, or 60 minutes and turn tracing on. A warning banner appears while it is active.
3. Reproduce the issue (Explore `/view` playback is ideal because it skips rclone and your media server).
4. Download a support pack from the same page while tracing is still on — `stream-traces/` rides along in the archive.
5. Tracing auto-disables when the timer elapses, and UI-enabled tracing always resets on restart. It is memory-only (capped at 20,000 events) and never written to disk outside the support pack.

Local backends started with `scripts/run-backend.sh` still enable `STREAM_TRACE_EVENTS` with no expiry. Dump a single session with `./scripts/dump-stream-trace.sh` — see [Contributing](../community/contributing.md).

## .NET crash dumps (opt-in)

Minidumps are **off** by default (large; may contain article data). To capture on the next backend crash:

```yaml
environment:
  - DOTNET_DbgEnableMiniDump=1
  - DOTNET_DbgMiniDumpType=2
  - DOTNET_DbgMiniDumpName=/config/dump.%p
```

Analyze with `dotnet-dump`, then remove the env vars and dump files.

## Rclone mount failures

Verify `/dev/fuse`, sidecar start order, WebDAV credentials, and `user_allow_other` if `--allow-other` is rejected. RC Test Conn needs `--rc*` flags and matching host/user/pass.
