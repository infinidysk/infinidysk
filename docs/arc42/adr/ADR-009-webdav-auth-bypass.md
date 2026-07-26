# ADR-009: `DISABLE_WEBDAV_AUTH` blanket auth bypass for reverse-proxy setups

**Status**: Accepted, but **explicitly flagged in this document for reconsideration** (INHERITED)
**Quality scenarios affected**: security posture (not a listed QS, but a real gap adjacent to QS-7)

**Correction from an earlier draft of this document**: this was initially mis-tagged
FORK-SPECIFIC on the assumption that its author, "David Young," was a fork-only contributor. Querying
the upstream repo directly (`GET api.github.com/repos/nzbdav-dev/nzbdav/commits/b696079` → `200`,
committer `nzbdav-dev`) confirms the commit is merged into upstream — David Young is an external
contributor whose PR was accepted upstream, the same pattern the core-domain research pass
independently found for a different contributor's PR #351. **This decision is INHERITED, not this
fork's own choice.** The underlying technical finding below is unchanged and equally valid; only the
"whose decision is this to revisit" framing changes — see Consequences.

## Context

Users running NzbDav behind an authenticating reverse proxy (Authelia, Traefik forward-auth, etc.)
don't want two stacked logins — one from the proxy, one from NzbDav's own WebDAV Basic Auth.

## Decision

Add `DISABLE_WEBDAV_AUTH=true`, an env var that, when set, makes `UseWebdavBasicAuthentication()` a
no-op for the entire NWebDav pipeline — no authentication is required on the WebDAV surface at all.
Commit `b696079`, authored by external contributor "David Young" and merged upstream, with a commit
message that self-describes the change as "vibe-coded."

## Consequences

- **Positive**: solves the real double-login problem for authenticating-reverse-proxy setups, with
  minimal code (a boolean flag).
- **Negative**: there is **no compensating control** — no trusted-proxy IP allowlist, no
  shared-secret header the proxy must inject and NzbDav verifies. The only guard-rail is a log line
  stating auth is disabled. If the container's WebDAV port is ever reachable without the reverse
  proxy in front of it (misconfigured port mapping, proxy restart racing container start, a user
  forgetting the proxy is required), **the entire virtual filesystem is unauthenticated.** This is
  the single most significant security finding across all five research passes. Being INHERITED
  rather than fork-specific doesn't make the gap less real — it means fixing it locally would be a
  (small, worthwhile) divergence from upstream rather than simply reverting a fork-local change,
  and it's worth raising with upstream directly given its own commit message admits it wasn't
  carefully reviewed.

## Alternatives considered

| Alternative | Effort | Verdict |
|---|---|---|
| Keep the blanket boolean bypass (status quo) | — | Solves the stated problem but leaves the gap above open |
| Require a second signal: a `TRUSTED_PROXY_HEADER_SECRET` env var that must match a header the proxy injects, checked before the auth bypass takes effect | S-M | **Recommended** — closes the accidental-exposure gap while still solving the original double-login problem; purely additive and opt-in |
| Restrict the bypass to loopback/verified `X-Forwarded-For` requests only | S-M | Alternative to the header-secret approach; either works, pick whichever fits the target reverse-proxy setups better |

**Recommendation**: this is not settled design to leave alone — it should get a deliberate second
look and one of the two hardening options above before being considered "done," given both its
security blast radius and its own commit message's admission of how it was written.
