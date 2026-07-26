# ADR-005: Single static shared secret as the frontend/backend and Arr-client trust boundary

**Status**: Accepted (INHERITED)
**Quality scenarios affected**: QS-7 (single-command deployability), security posture (not a listed QS but load-bearing)

## Context

The frontend proxy, Sonarr, Radarr, and (via a rotatable secondary key) any other SAB-speaking
client all need to authenticate against the backend REST/SAB/WS surfaces, in a deployment with no
external identity provider and a single trusted operator.

## Decision

Gate the REST API, SAB surface, and `/ws` with one static shared secret
(`FRONTEND_BACKEND_API_KEY`, required env var), with a separate rotatable `api.key` accepted
additionally on the SAB surface specifically so users can rotate what they hand Sonarr/Radarr
without redeploying the container. Streamed file URLs use a derived, path-scoped SHA-256 token
instead of the raw secret (ADR-independent good hygiene, see §8.1).

## Consequences

- **Positive**: zero-config — one env var, no session store, no token issuance/refresh flow. Fits
  QS-7 exactly.
- **Negative**: one flat trust tier authenticates every downstream consumer identically — there is
  no way to scope a key to "read-only" or distinguish "this is our own frontend" from "this is an
  arr instance with the same key." A leaked key (e.g. from a Radarr config backup) grants the same
  privileges as the frontend itself. In a single-user homelab threat model this is a low-severity
  gap, not a measured incident.

## Alternatives considered

| Alternative | QS-7 | Verdict |
|---|---|---|
| Per-client API keys (one per Sonarr/Radarr/frontend) | Medium migration cost — needs a keys table + management UI; the existing multi-value compare (`IsAny`) generalizes easily | Worth it only if a user runs multiple arr instances and wants blast-radius containment; low payoff for the common single-Sonarr+single-Radarr case |
| mTLS between frontend and backend | High cost, awkward for `docker run` (cert issuance/rotation) | **Not recommended** — over-engineered for a same-host, same-container trust boundary |
| OAuth2/OIDC for the REST API | Actively wrong fit — needs an external IdP, contradicting the whole point of QS-7 | **Not recommended** |
| Constant-time key comparison (`CryptographicOperations.FixedTimeEquals`) instead of `==` | No cost | Cheap, no downside — worth doing regardless of the above, low priority given the single-user threat model |

**Recommendation**: keep the status quo trust model; the constant-time-comparison fix is free and
worth taking; per-client keys are a reasonable future addition only if actual multi-arr-instance
usage is observed, not preemptively.
