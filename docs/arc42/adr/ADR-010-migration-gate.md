# ADR-010: Manual migration gate, no auto-migrate-on-boot

**Status**: Accepted (INHERITED)
**Quality scenarios affected**: QS-5 (startup/recovery), QS-8 (crash-safety / data safety)

## Context

Schema migrations need to run before the app serves traffic against a possibly-outdated database,
in a deployment model where the only backup mechanism is "the user copies the `/config` directory
themselves" (there's no automated backup/rollback built into the app).

## Decision

Migrations only run via explicit `NzbWebDAV --db-migration [target]` invocation — never implicitly
on normal ASP.NET Core startup. `entrypoint.sh` runs this once, blocking, before starting the real
server (§7.3). One specific breaking migration additionally has a hardcoded hard-stop,
`BlockUpgradesToV06X` (`Program.cs:136-168`): if that migration is still pending, the process
refuses to start *at all* (even to run `--db-migration`) unless the user sets `UPGRADE=0.6.0`,
forcing them to acknowledge a warning and (implicitly) back up first.

## Consequences

- **Positive**: a migration failing mid-way can't happen while the host is also trying to bind
  Kestrel and accept traffic — a strictly safer failure mode than migrate-on-boot. CLAUDE.md
  explicitly credits this as deliberate.
- **Negative**: adds one extra process spin-up/teardown to every container start (small QS-5 cost);
  the hard-stop mechanism is a one-off, hand-keyed-to-one-migration-name check, not a generalized
  "confirm risky migration" framework — a future breaking migration would need a similarly bespoke
  gate added by hand rather than reusing existing machinery.

## Alternatives considered

| Alternative | QS-7 | QS impact | Verdict |
|---|---|---|---|
| Auto-migrate-on-boot inside the ASP.NET host | Neutral (still one command) | Slightly improves QS-5 (one fewer process spin-up) but **weakens the safety property this ADR exists for** — a migration failing mid-way while the host is also trying to serve traffic is strictly worse | **Not recommended** — this is a deliberate, documented tradeoff, not an oversight |
| Generalize `BlockUpgradesToV06X` into a reusable "confirm breaking migration" mechanism | N/A | No QS change, but reduces future engineering cost for the next breaking migration | Reasonable low-priority cleanup, not urgent — only pays off when the next breaking migration actually arrives |

**Recommendation**: keep the current gate as-is; it's a deliberate safety-over-convenience choice
that's already well-suited to this deployment model, not a gap to close.
