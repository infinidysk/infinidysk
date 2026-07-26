# ADR-008: Hand-rolled shell-script process supervision, no supervisor framework

**Status**: Accepted (INHERITED), **flagged as the highest-priority deployment-view gap** — see §11
**Quality scenarios affected**: QS-5 (startup/recovery), QS-8 (crash-safety)

## Context

Both runtimes (backend, frontend) run in one container (ADR-003) and must be started in the right
order, have signals forwarded correctly, and be cleaned up together on shutdown.

## Decision

`entrypoint.sh` is PID 1 and implements this itself: resolve PUID/PGID → migrate → start backend →
poll `/health` → start frontend → busy-poll both PIDs every 0.5s (`wait_either`) → if either exits,
kill the other and exit with its code; `trap terminate TERM INT` forwards signals to both children
on shutdown. No s6-overlay, supervisord, tini, or dumb-init is used.

## Consequences

- **Positive**: no added dependency; the ordered-startup logic (migrate-then-serve,
  backend-healthy-then-frontend) is explicit and easy to read in one script.
- **Negative**: **a crash in either process takes down the entire container** — there is no
  independent restart of just the crashed half; recovery is 100% delegated to Docker's `--restart`
  policy. As PID 1 with no subreaper, orphaned grandchild processes aren't reaped (likely benign in
  practice, unverified). Compounded by the complete absence of a Docker `HEALTHCHECK` instruction
  (confirmed absent) — Docker has no visibility into post-startup health at all; `docker ps` always
  shows the container running regardless of internal state.

## Alternatives considered

| Alternative | QS-7 | QS impact | Migration cost |
|---|---|---|---|
| **Add a Docker `HEALTHCHECK`** targeting the frontend's exposed port | Neutral — purely additive, no `docker run` change | Improves QS-8 (visible unhealthy state, enables `--restart` policies and Compose `depends_on: service_healthy` for anyone using compose) | **Low** — one Dockerfile line |
| **s6-overlay or supervisord** for real independent process supervision | Still single image/single `docker run` | Substantially improves QS-8 (a Node crash no longer force-kills a healthy backend and vice versa); marginal QS-5 cost (tens of ms init overhead) | **Medium** — replaces `entrypoint.sh`'s logic with service definitions; must re-derive the migration-gate-before-start and backend-healthy-before-frontend-start sequencing as supervisor dependency ordering, and needs thorough manual restart/signal testing since there's no CI test gate to catch a regression here |

**Recommendation**: add the `HEALTHCHECK` immediately (near-zero cost/risk). Treat the supervisor
migration as a deliberate, carefully-tested change — it's the part of the container homelab users
depend on working exactly as-is, and the codebase's complete lack of a test suite means any
regression here would only be caught by hand.
