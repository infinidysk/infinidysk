---
description: "Decisions behind the /watch catalog: metadata-only providers, loopback transcode source, HLS by default, one continuous encoder, and a shadow-only player OSD."
---

# Watch catalog and catalog playback

Status: accepted
Date: 2026-10-28

## Context

Users asked for a Netflix-style browse-and-play surface instead of exporting search
results into another tool. InfiniDysk already implements indexer search, candidate
verification and failover, mounting, archive reads, and byte-range `/view` streaming.
Building a second pipeline would duplicate the hardest and least stable parts of the
product.

Playback in browsers is the weak point. The common 1080p release is Matroska with AC-3
audio, which Firefox cannot play directly and cannot seek cheaply. An earlier
compatibility path encoded one MPEG-TS segment per request; it played, but produced an
audible and visible hitch at every six-second boundary.

## Decision

### 1. Metadata providers only; playback reuses the existing engine

TMDB supplies browsing and movie identity, TVDB supplies episode data. Neither is
consulted for release selection. A play action resolves an external identity
(`tt…` for movies, `tvdb:{id}:{s}:{e}` for series), calls
`SearchProfileService.SearchByImdbAsync` with the configured profile, and uses
`PlayTokens[0]`. The browser never supplies identities, tokens, or URLs.

### 2. The compatibility encoder never fetches the public URL

The play controller answers with a redirect to signed `/view` bytes. The transcode
service reads only the response head, accepts a rooted `/view/…` location (optionally
behind the public path prefix), strips the prefix, and rebuilds the URI on the loopback
backend. Anything else — absolute, network-path, traversal, non-`/view` — is rejected.
This keeps FFprobe/FFmpeg off public DNS/TLS and prevents the endpoint from becoming an
arbitrary URL fetcher.

### 3. HLS is the default for catalog playback

Firefox cannot decode the typical release, and direct seeks on a remote container are
slow. The player therefore starts in HLS when a manifest exists, keeps `direct` as an
explicit choice, and only switches automatically from HLS to direct when HLS is
structurally impossible. Recovery is bounded: transient fragments resume rather than
terminating playback, and no automatic mode ping-pong exists.

### 4. One continuous encoder per run, absolute timestamps

Each independent per-segment encode re-primes the AAC encoder and rebases timestamps.
The measured result was a one-packet audio hole plus a decoder re-init at every
boundary — a 4–6 s microstutter. Connections, bitrate, and buffer sizes were all
healthy, so no amount of tuning removed it.

A run now encodes a contiguous span with FFmpeg's own HLS muxer, cutting on forced
six-second keyframes on one timeline (`-output_ts_offset`, `-avoid_negative_ts
disabled`). Segments share one encoder, so audio and video meet exactly. The manifest
carries no discontinuity tags. Seeks start a new run at the boundary; because both runs
use the same absolute timeline, their segments still splice.

Overlapping audio is forbidden: native HLS rejects a DTS regression as
`CHUNK_DEMUXER_ERROR_APPEND_FAILED`. The continuous encoder achieves continuity without
overlap, which is why the earlier trimming workarounds were removed with it.

### 5. The player OSD must not paint over the video

During 1080p playback the visible chrome cost 9–11 dropped frames per 20 s, while the
identical chrome with its translucent fills removed cost zero. The OSD therefore uses
text shadows for contrast and keeps a single near-opaque card for the diagnostics
panel. This was a measured compositing cost, not a stylistic preference.

## Consequences

- Catalog playback inherits every existing search, verification, failover, mount, and
  range-request behavior for free; catalog-only bugs stay in metadata or presentation.
- The compatibility path costs CPU: one `veryfast` 1080p encode per active session,
  deprioritized so playback wins scheduling.
- Sessions are in memory, so a backend restart requires clicking Play again.
- Segment cuts follow the keyframe grid, so a run's first video frame can be a few
  milliseconds off nominal. Native HLS and hls.js both accept it.
- The cache bound, eviction rules, and cleanup sweep exist because runs publish ahead
  of playback and must not delete a segment a response is reading.
