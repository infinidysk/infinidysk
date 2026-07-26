# 12. Glossary

## Domain terms

| Term | Meaning |
|---|---|
| **NZB** | An XML document listing the Usenet article IDs that together make up a file (or set of files), used by Usenet indexers/downloaders instead of distributing the content directly. |
| **NNTP** | Network News Transfer Protocol — the protocol used to fetch articles from a Usenet provider. |
| **yEnc** | A binary-to-text-safe encoding historically used to post binary content to Usenet; must be decoded back to raw bytes after fetching an article. |
| **PAR2** | Parity Archive volume format used for Usenet release verification/repair; also frequently repurposed by obfuscated releases to recover real filenames (see `Queue/DeobfuscationSteps`). |
| **Obfuscated release** | A Usenet release whose real filenames/structure are deliberately hidden or randomized; recovered via PAR2 metadata inspection before normal file processing can proceed. |
| **SABnzbd API** | The REST API surface of the SABnzbd download client; Sonarr/Radarr integrate with any download client that implements this API shape, which is why NzbDav implements it rather than a bespoke API. |
| **WebDAV** | Web Distributed Authoring and Versioning — an HTTP extension for remote filesystem-like access (PROPFIND, ranged GET/HEAD, etc.), used here to expose the virtual filesystem. |
| **Rclone** | A general-purpose tool that can mount WebDAV (and many other backends) as a native local filesystem mount point. |
| **`.strm` file** | A small text file containing a URL/path, used by media servers (Jellyfin/Plex/Kodi) to reference remote/streamable content without a local media file. |

## Project-specific terms

| Term | Meaning |
|---|---|
| `DavItem` | Base persisted model for a virtual filesystem entry — directories and files alike, discriminated by `Type`/`SubType`, with a denormalized materialized `Path` (see §5.2.2). |
| `DavRarFile` / `DavMultipartFile` / `DavNzbFile` | Specialized `DavItem`-adjacent entities for content assembled from RAR volumes, multipart containers (7z/multipart-mkv), and plain NZB-backed files respectively (see §5.2.2). Their SQLite JSON-column mappings are legacy read-fallback only — actual metadata lives in the blob store (see `BlobStore` below). |
| `BlobStore` | Flat-file, zstd-compressed, MemoryPack-serialized store for per-segment file metadata, sharded 2 levels deep by GUID under `CONFIG_PATH/blobs/`. The primary metadata store; SQLite only keeps a GUID pointer (see §5.2.2, ADR-001). |
| `QueueItem` / `HistoryItem` | SABnzbd-style queue/history entities driving `Api/SabControllers` responses (see §5.2.1). |
| `ArticleCachingNntpClient` | Per-queue-item-scoped NNTP client layer that caches fetched articles for the duration of processing one queue item. Used **only** by Queue ingestion, never by the WebDAV streaming/seek path (see §5.2.5, §6.2). |
| `UsenetStreamingClient` | Top-level singleton NNTP client used by both WebDAV reads and the queue processor; hot-swaps its entire underlying provider stack on config change without an app restart (see §5.2.5). |
| `MultiSegmentStream` | Fetches and yEnc-decodes NZB segments on demand with read-ahead via a bounded channel; the core mechanism behind both fresh playback starts and (via `Stream.Seek`) mid-playback seeks (see §6.2). |
| `InterpolationSearch` | The algorithm used to locate which NZB segment contains an arbitrary target byte offset during a seek, assuming roughly-uniform segment sizes — converges in ~1-3 probes rather than `O(log n)` (see §6.2). |
| `ConnectionPool` / `ConnectionLock` | Per-provider bounded NNTP connection pool and RAII borrow/return wrapper; explicitly code-comment-marked as ChatGPT-authored, with no accompanying tests (see §5.2.5, §11 P2-4). |
| `ProviderCircuitBreaker` | Per-provider failure isolation: trips after 3 consecutive failures, cooldown 60s doubling to a 5-minute cap (see §5.2.5, §11 P2-6). |
| `PrioritizedSemaphore` | Two-queue (high/low), odds-based semaphore used both at the connection-pool gate (priority = NNTP command type) and the download-concurrency budget (priority = interactive-vs-background); the two uses don't compose transparently (see §8.3). |
| `DownloadPriorityContext` | Ambient, `CancellationToken`-keyed context flag set to `High` by every WebDAV read before it descends into the Usenet stack, so interactive streaming is prioritized over background queue downloads (see §8.3). |
| `DavFileStreamFactory` | The single place that turns a `DavItem` + its blob metadata into a live, decoded `Stream`; shared between the WebDAV read path and the fork's prefetch-cache warmer (see §5.2.4). |
| `DISABLE_WEBDAV_AUTH` | Env var (INHERITED — externally contributed, merged upstream) that disables WebDAV Basic Auth entirely for reverse-proxy setups, with no compensating trusted-proxy check (see ADR-009, §11 P1-5). |
| `FRONTEND_BACKEND_API_KEY` / `api.key` | The two layered shared secrets gating the backend's REST/SAB/WS surfaces — the former is the immutable frontend↔backend trust boundary, the latter a rotatable key layered on top for Sonarr/Radarr (see §8.1, ADR-005). |
| `downloadKey` | Path-scoped `SHA256(path + apiKey)` capability token embedding stream access in a URL, so `.strm` files/external media players never need the raw shared secret (see §8.1). |
