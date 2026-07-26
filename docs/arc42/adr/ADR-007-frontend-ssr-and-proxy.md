# ADR-007: React Router 7 SSR + hand-rolled Express server (proxy + auth + websocket relay)

**Status**: Accepted (INHERITED)
**Quality scenarios affected**: QS-4 (resource footprint), QS-1/QS-3 (indirectly — the proxy sits in the streaming path)

## Context

The frontend needs to serve an authenticated admin/queue/settings UI, reverse-proxy WebDAV/API/media
paths to the backend without breaking HTTP range requests, enforce session auth, and relay a
websocket channel — all from one Node process in the single-container deployment (§2, ADR-003).

## Decision

Use React Router 7 in SSR mode (`ssr: true`) for the UI, and a hand-rolled Express server
(`server.ts` + `server/app.ts`) instead of the stock `@react-router/serve` or a separate reverse
proxy, so that proxying, auth enforcement, and websocket relay can share the SSR process.

## Consequences

- **Positive**: one process handles everything; compression is deliberately excluded on the
  proxied/streamed paths (a real regression fix, confirmed by git history) so range requests aren't
  broken by `Content-Length`-mangling compression.
- **Negative / non-obvious finding**: SSR is **not actually in the streaming hot path** — file
  links in the Explore browser are plain `<a href>` tags, so range/seek requests bypass React
  Router loaders entirely and hit the proxy branch before `authMiddleware` even runs. This means the
  strongest argument for ripping out SSR (protecting QS-1/QS-3) doesn't actually apply — but SSR
  still adds baseline CPU/RAM (QS-4) for an entirely auth-gated UI with no SEO/first-paint
  requirement, and the proxy's route-list is hand-duplicated in two files (`server.ts`'s compression
  filter and `server/app.ts`'s routing), which must be kept manually in sync.

## Alternatives considered

| Alternative | QS-7 | QS impact | Migration cost |
|---|---|---|---|
| Flip `ssr: false` (already anticipated by an inline code comment), keep Express only for proxy/auth/websocket | Neutral-to-positive (likely a lighter image, no runtime SSR deps) | Improves QS-4 with no realistic QS-1/QS-3 downside, since SSR was never in the streaming path (hypothesis on magnitude — cheap to test, the flag already exists) | **Low-Medium** — every route's `loader` would need to move to client-side fetching, and server-side redirect-to-login logic needs a client-side equivalent |
| Move the WebDAV/API proxy out of Express into a lightweight reverse proxy (Caddy/nginx/Traefik) inside the same container | Neutral if bundled as a third supervised process in the same image; breaks QS-7 if it becomes a separate container | Modest QS-1/QS-3 upside (lower per-request overhead, no shared event loop with SSR) — ceiling is limited since the existing proxy already isn't buffering/compressing these paths | **High** — this is core INHERITED plumbing; the auth-bypass-for-streaming logic and websocket upgrade handling would need re-deriving entirely outside Node |
| Different frontend framework entirely (SvelteKit, plain Vite+React SPA, htmx served from the .NET backend) | Plain SPA/htmx: simplest single-container story, htmx-from-backend could drop the Node process from the container altogether (best-case QS-4/QS-7). SvelteKit: no clear QS movement vs. today. | Plain SPA/htmx best-case for QS-4; htmx-from-backend is the most radical option and the largest single rewrite discussed anywhere in this document | **Very high** — full rewrite of ~15 route trees; htmx option additionally moves UI logic into the .NET backend, a cross-boundary rewrite |

**Recommendation**: run the `ssr:false` experiment first (near-zero cost, config flag already
exists) before considering any larger frontend rewrite; de-duplicate the proxy route list
regardless (§11) since that risk exists independent of any of the above.
