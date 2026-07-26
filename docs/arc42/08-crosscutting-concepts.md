# 8. Crosscutting Concepts

Concepts that recur across multiple building blocks, synthesized from all five research passes.

## 8.1 Authentication model — three independent secret tiers, no unified identity

The system has no single auth mechanism; it has four, each solving a different constraint, and none
of them interact:

| Mechanism | Protects | Scope | Tag |
|---|---|---|---|
| HTTP Basic Auth (1hr cookie cache) | Raw WebDAV protocol endpoints | `ConfigManager`-stored single user/pass | INHERITED |
| `x-api-key` / `FRONTEND_BACKEND_API_KEY` | `Api/Controllers/*`, SAB surface (alongside a rotatable `api.key`), `/ws` | Single static shared secret (env var) | INHERITED |
| Path-scoped SHA-256 download tokens (`SHA256(path + apiKey)`) | `view/{*path}` streaming endpoint | Per-path, non-revocable-but-narrow, no expiry observed | INHERITED |
| Session cookie (`__session`, signed, httpOnly, sameSite:strict) | React Router SSR pages | Frontend-only; backend has no session concept, just returns a boolean from `Authenticate` | INHERITED |

Two more mechanisms layer on top: `DISABLE_WEBDAV_AUTH` (INHERITED, despite an earlier draft of this
document mis-tagging it FORK-SPECIFIC by author-name inference — see
[ADR-009](adr/ADR-009-webdav-auth-bypass.md) — a blanket bypass for reverse-proxy setups, see §11)
and a fifth, FORK-SPECIFIC, deliberately-separate Jellyfin webhook token (kept apart
from `api.key` specifically so rotating one doesn't affect the other — good hygiene, explicitly
commented as intentional).

**Design logic that holds together well**: the path-scoped download token exists specifically so
`.strm` files and external media players never need to carry the real shared secret — a sound
narrow-credential pattern. **Where it doesn't hold together**: there is no way to tell "this request
carries the master key because it's our own frontend" apart from "this request carries the master
key because it's Sonarr" — one flat trust tier authenticates every downstream consumer identically.

## 8.2 Background work — uniform `IHostedService` convention

Every long-running backend task (cleanup services, health-check, prefetch, cache eviction, stats
aggregation) follows the same shape: a `BackgroundService.ExecuteAsync` loop, cooperative
cancellation via `SigtermUtil`, catch-all exception handling with a bounded backoff delay before
retrying. This is already exactly the convention CLAUDE.md asks new work to follow — every
fork-specific service added since (`CacheEvictionService`, `ProviderUsageStatsAggregator`,
`PrefetchCacheService`) extends it rather than inventing a new pattern. One inconsistency found:
`backend/Tasks/BaseTask.cs` uses a *process-wide static* mutual-exclusion semaphore shared across
all its subclasses (`RemoveSampleFilesTask`, `RemoveUnlinkedFilesTask`, `StrmToSymlinksTask`),
meaning only one of these three unrelated maintenance tasks can run at a time system-wide — plausibly
an intentional "one heavy sweep at a time" throttle for a homelab host, but undocumented as such (see
§11).

## 8.3 Concurrency & priority propagation

Two distinct, only-partially-composing priority mechanisms exist:

1. **Command-type priority** at the per-provider connection-pool gate (`ConnectionPool`'s
   `PrioritizedSemaphore`) — BODY/ARTICLE always High, STAT/HEAD/DATE always Low, regardless of
   caller.
2. **Interactive-vs-background priority** at the download-concurrency semaphore
   (`DownloadingNntpClient`) — resolved from an ambient `DownloadPriorityContext` attached to the
   request's `CancellationToken` (a `ConcurrentDictionary`-backed static context, not `AsyncLocal`,
   because a `MultiSegmentStream`'s background download loop runs on a *detached* task outside the
   original async-local scope). Every WebDAV read tags itself `High` before descending into the
   stack; background queue downloads default to `Low`.

These two mechanisms don't compose transparently: a background queue download's BODY command still
gets High priority at the connection-pool gate even though it's Low at the download-concurrency
semaphore one layer up — meaning once a queue download clears the concurrency budget, it competes
equally with interactive streams for the underlying connection. Subtle, unowned by any test (there
is no backend test project), and easy to regress silently.

## 8.4 Stream composition as the core streaming abstraction

Rather than one monolithic stream class, every read (plain file, RAR entry, 7z entry, multipart-mkv
part, AES-encrypted archive) composes from the same small set of primitives:
`NzbFileStream`/`MultiSegmentStream` (segment fetch + read-ahead) wrapped by
`DavMultipartFileStream`/`CombinedStream` (multi-part stitching) and optionally `AesDecoderStream`
(decrypt) or `ThrottledYencStream`/`ProviderCountingYencStream` (fork-specific decorators, following
the exact same wrap-and-override-`ReadAsync` pattern already established upstream). `Stream.Seek` on
the composed chain — not a linear scan from offset 0 — is what makes range-based seeking viable at
all; see §6.2 for why it's still slower than a fresh start.

## 8.5 Error handling — three parallel shapes on one host

`ExceptionMiddleware` (global, WebDAV/streaming-oriented: aborted requests → 499, article-not-found
→ 404, catch-all → 500) coexists with two independent REST error-response shapes:
`BaseApiController`'s `BaseApiResponse` JSON and `SabApiController`'s deliberately SABnzbd-shaped
`SabBaseResponse` JSON. This is a reasonable design given the SAB surface must stay
protocol-authentic for Sonarr/Radarr's parser, but it means a new endpoint author must know which of
three conventions applies, with nothing enforcing the choice beyond which controller base class they
pick.

## 8.6 Fork-vs-upstream attribution as a first-class concern

Every architectural decision in this document is tagged INHERITED or FORK-SPECIFIC (see §9), verified
against the upstream repo where the tag matters rather than inferred from author name alone (§9.1 —
`DISABLE_WEBDAV_AUTH` was initially mis-tagged this way and corrected). This matters practically, not
just historically: this fork's own changes to date are **overwhelmingly additive** (episode-prefetch
caching, per-provider usage stats, bandwidth throttling, one sample-file-rejection tweak) — they sit
alongside upstream's structure rather than replacing it. Confirmed FORK-SPECIFIC changes to
security-relevant code: none found in this pass — the one candidate (`DISABLE_WEBDAV_AUTH`) turned out
to be an externally-contributed, upstream-merged change (ADR-009), self-described by its own commit
message as "vibe-coded" regardless of which repo it lives in. Any future change — fork-specific *or*
a newly-merged upstream one — that touches security-relevant INHERITED code should get the same level
of scrutiny this document gives that one.

## 8.7 Naming collision worth flagging for new contributors

`backend/Services/HealthCheckService.cs` (a Usenet-article-integrity/self-repair checker) has
nothing to do with ASP.NET's own `/health` liveness endpoint (`Program.cs`'s bare
`AddHealthChecks()`/`MapHealthChecks("/health")`, zero registered checks — it returns 200 as long as
Kestrel is up, regardless of DB, provider, or queue state). "The backend passed its health check"
currently means only "the HTTP server process is alive," not "the app is actually healthy" — see
§11 for the concrete fix.
