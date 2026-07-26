# 2. Constraints

## 2.1 Technical constraints

| Constraint | Detail | Source |
|---|---|---|
| Single-container Docker deployment | Deployment target is one local Docker container on a homelab host — not Kubernetes, not a multi-container compose stack with managed dependencies. This is a standing constraint given by the user for this analysis and matches the project's actual `docker run -p 3000:3000 nzbdav/nzbdav` distribution model. | User instruction; `README.md` Getting Started |
| Two runtimes, one image | Backend is .NET 10 (ASP.NET Core); frontend is a Node.js/Express (React Router 7 SSR) app. Both must run inside the same container and share environment configuration (`CONFIG_PATH`, `FRONTEND_BACKEND_API_KEY`, `BACKEND_URL`). | `CLAUDE.md`; `Dockerfile` |
| SQLite as sole datastore | The entire virtual filesystem tree, queue, and history are modeled in SQLite via EF Core (`DavDatabaseContext`) — no external database service. | `CLAUDE.md`; `backend/Database/` |
| SABnzbd API compatibility is a hard external contract | Sonarr/Radarr talk to NzbDav as if it were SABnzbd. The shape of `Api/SabControllers/*` cannot be freely redesigned without breaking that integration for every user. | `CLAUDE.md` |
| Manual migration gate | The app "deliberately refuses to auto-migrate on normal startup" — `NzbWebDAV --db-migration` must be run before first start / after upgrade. | `CLAUDE.md`; `Program.cs` (`BlockUpgradesToV06X`) |
| No CI test/lint gate | `.github/workflows/` only builds/pushes Docker images on branch pushes, main, and releases (via release-please). There is no backend test project and no frontend test suite — `npm run typecheck` is the only automated gate, and it isn't CI-enforced per the documented commands. | `CLAUDE.md` |

## 2.2 Organizational constraints

| Constraint | Detail |
|---|---|
| This repo is a fork | `git log` shows 415 commits authored by `nzbdav-dev` (upstream) vs. a handful by `habenspass` (this fork). Fork-specific work to date: per-provider Usenet usage statistics, predictive episode-prefetch caching driven by a Jellyfin webhook, and a websocket handler addition. Diverging from upstream architecture (rewriting a language, swapping a datastore, restructuring modules) forfeits the ability to cleanly merge future upstream fixes/features — a real, recurring cost that must be priced into any "big" alternative, not just its one-time build cost. |
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
