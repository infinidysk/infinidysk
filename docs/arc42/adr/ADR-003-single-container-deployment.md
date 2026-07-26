# ADR-003: Single combined Docker image bundling both language runtimes

**Status**: Accepted (INHERITED)
**Quality scenarios affected**: QS-7 (single-command deployability), QS-4 (resource footprint)

## Context

The system is two independently-run apps (.NET backend, Node frontend) that must ship as something
a self-hoster can run with one `docker run` command against one config volume, per the standing
deployment constraint (§2).

## Decision

Build both runtimes into a single final image (`dotnet/aspnet:10.0-alpine` base + `nodejs`/`npm`
installed alongside), started and supervised together by a hand-written `entrypoint.sh` (see
ADR-008). One `EXPOSE 3000`, one volume (`/config`).

## Consequences

- **Positive**: maximizes QS-7 — exactly the `docker run -p 3000:3000 -v ... nzbdav/nzbdav` UX the
  README advertises; no compose file, no service discovery, no separate DB/broker/cache container.
- **Negative**: the image carries two full language runtimes' worth of base footprint
  (ASP.NET Alpine + full Node/npm + `frontend/node_modules`) side by side — a direct, if unmeasured,
  cost to QS-4. Also couples the two processes' lifecycle (ADR-008): a crash in either currently
  takes down both.

## Alternatives considered

| Alternative | QS-7 impact | QS impact | Migration cost |
|---|---|---|---|
| Separate backend + frontend containers via docker-compose | **Directly breaks QS-7** — no longer a single `docker run`; needs a compose file, two build/push jobs, explicit `BACKEND_URL` service discovery | Could improve QS-8 (Docker restarts each service independently) and QS-4 (no double-runtime bundling) | **High** — touches CI (2 build/push jobs per workflow instead of 1) and is a breaking change for the entire existing self-hosting user base |
| Slimmer/distroless base for the final stage | Neutral to `docker run` UX | Improves QS-4 (smaller image, smaller CVE surface) | **Medium-High** — chiseled/distroless images typically drop `sh`/`bash`, which `entrypoint.sh`'s `su-exec`/`getent`/`addgroup` PUID-remap logic depends on; would need a redesigned non-root-user strategy, not a drop-in swap |
| Unify onto one runtime entirely (see §9.3, whole-system language analysis) | Best possible QS-7/QS-4 outcome (one runtime, no double-bundling) | Largest possible win, but requires rewriting either the backend or the frontend | **Very high** — see §9.3 |

**Recommendation**: reject the compose split outright for this deployment target — it trades away
the product's stated core value proposition (single-command homelab deploy) for benefits that matter
more at a scale this project isn't targeting. The slimmer-base and single-runtime options are
legitimate future directions but are meaningfully larger efforts than anything else in the
deployment-view backlog (§11).
