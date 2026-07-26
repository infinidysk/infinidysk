# 2. Constraints

## 2.1 Technical constraints

**A note on what belongs in this table**: a constraint is something this analysis must design
*within* — not a description of what the codebase currently happens to be built with. Reviewer
feedback on an earlier draft correctly flagged that this section had blurred that line (e.g. listing
"backend is .NET, frontend is React" as if it were a constraint on the same footing as "must fit in
one Docker container"). **Programming language, framework, and runtime topology are explicitly NOT
constraints in this analysis — they are decisions, open to being reversed, and §13
(Redesign Proposals) takes that seriously rather than treating the status quo as load-bearing.**
Only the rows below are genuine constraints — things a redesign must still satisfy regardless of
what language/framework it's built in.

| Constraint | Detail | Source |
|---|---|---|
| Single-container Docker deployment | Deployment target is one local Docker container on a homelab host — not Kubernetes, not a multi-container compose stack with managed dependencies. This is a standing constraint given by the user for this analysis and matches the project's actual `docker run -p 3000:3000 nzbdav/nzbdav` distribution model. **Note**: this constrains *topology* (how many containers/processes), not *language* — a single-container deployment is achievable in any of the languages/frameworks discussed in §13. | User instruction; `README.md` Getting Started |
| SABnzbd API compatibility is a hard external contract | Sonarr/Radarr talk to NzbDav as if it were SABnzbd. The wire shape of that API (the `mode`-param dispatch, JSON response format) cannot be freely redesigned without breaking that integration for every user — regardless of what implements it. | `CLAUDE.md` |
| No formal test suite exists today | There is no backend test project and no frontend test suite; `npm run typecheck` is the only automated gate, and it isn't CI-enforced. This is a **fact about the current state**, not a constraint on the future — see §13.4 for a concrete plan to close this gap, which the reviewer flagged as worth pursuing. | `CLAUDE.md` |

The following are **current implementation choices, not constraints** — listed here only so this
document doesn't have to re-derive them in every section, but every one of them is explicitly
in scope for replacement per §13:

| Current choice | Detail | Discussed as a decision in |
|---|---|---|
| .NET 10 / ASP.NET Core backend, Node/Express+React Router frontend, both in one image | The two runtimes currently share environment configuration (`CONFIG_PATH`, `FRONTEND_BACKEND_API_KEY`, `BACKEND_URL`) inside one container. | §9 (ADR-003, ADR-007), §13 |
| SQLite as the datastore | The virtual filesystem tree, queue, and history are modeled in SQLite via EF Core today. | §9 (ADR-001), §13 |
| Manual migration gate | `NzbWebDAV --db-migration` must be run before first start / after upgrade in the current .NET implementation. | §9 (ADR-010) |

## 2.2 Organizational constraints

| Constraint | Detail |
|---|---|
| This repo is a fork | `git log` shows 415 commits authored by `nzbdav-dev` (upstream) vs. a handful by `habenspass` (this fork). Fork-specific work to date: per-provider Usenet usage statistics, predictive episode-prefetch caching driven by a Jellyfin webhook, and a websocket handler addition. Diverging from upstream architecture (rewriting a language, swapping a datastore, restructuring modules) forfeits the ability to cleanly merge future upstream fixes/features — a real, recurring cost that must be priced into any "big" alternative, not just its one-time build cost. **Update (§13.1)**: this cost was originally sized against the historical 415-commit cadence; upstream has independently been verified (via the GitHub API) to have merged nothing to `main` in the last two months, with several substantive PRs closed unmerged in that window. The *mechanism* of this constraint is unchanged, but its *size* is currently much smaller than the historical commit count implies — see §13 for the full analysis and what this changes. |
| Small/solo maintenance model | No dedicated QA, no CI test gate, no formal release process beyond release-please version bumps. Any recommendation that assumes a team (e.g., "add a dedicated ops rotation") is out of scope. |
| No formal SLAs | This is self-hosted software for individuals, not a hosted service with uptime commitments — quality goals in §10 are framed as "good enough for a home streaming setup," not "five nines." |

## 2.3 Conventions

- Repository is split into two independently-run apps (`backend/`, `frontend/`) that must share
  environment configuration but are not a monorepo build in the Nx/Turborepo sense — see
  `CONTRIBUTING.md` for the manual env-var contract.
- Long-running backend work is modeled as `IHostedService`s registered in `Program.cs`, not ad hoc
  timers/threads — an established convention this document's optimization proposals must follow.
- This document's own convention: every claim is `file:line`-cited or marked `(hypothesis)`; every
  weak point/alternative/optimization is tagged against a QS-# from §10.
