---
description: "In-repo code boundaries for backend namespaces and frontend route features, enforced by ArchUnitNET and ESLint."
---

# Code boundaries

Status: accepted
Date: 2026-08-20

## Context

InfiniDysk is a single .NET assembly plus a React Router admin UI. Namespaces
are not separate deployable projects. Reverse dependencies had grown from
convenience: Queue and WebDAV instantiated SAB controllers, Metrics mutated
admin response types, and frontend route features imported each other's UI.

Those edges make refactors unsafe and hide the real adapter/application split.
The current `Services` namespace is mixed (hosted workers, metrics, repairs,
observability) and must not be treated as one clean layer.

## Decision

Document and enforce the boundaries that match the running application.

### Backend

1. API controllers, middleware, WebDAV HTTP handlers, and `Program` are inbound
   or composition layers.
2. No non-API namespace may depend on `NzbWebDAV.Api.Controllers` or
   `NzbWebDAV.Api.SabControllers`. Shared behavior lives in Queue, Auth,
   Utils, or Metrics as transport-neutral types. Controllers stay thin adapters.
3. Clients may depend on configuration, database models, utilities, and
   explicitly cross-cutting metrics/observability contracts, but not API,
   Queue, Tasks, WebDAV, or migration orchestration.
4. Database code may not depend on API, Queue, or Tasks.
5. `Services` stays mixed. Introduce narrower namespaces over time rather than
   an unenforceable all-services rule.
6. WebDAV is both an inbound adapter and a streaming implementation.
7. EF migrations, generated OpenAPI types, and vendored SharpCompress are
   excluded where reflection cannot classify them usefully.
8. `Program` is the composition root and may depend on API types.

### Frontend

1. Route features may import shared `clients` / `auth` / `components` /
   `navigation` / `utils` and files within the same feature, but not another
   route feature.
2. Shared code must not import route modules.
3. `app/routes.ts` and Express server composition edges into app auth/client
   code are explicit exceptions.

Enforcement:

- Backend: `tests/NzbWebDAV.ArchitectureTests` (ArchUnitNET) loaded from
  `typeof(ConfigManager).Assembly`.
- Frontend: ESLint rule `import-boundaries/no-cross-feature-imports`, which
  resolves both `~/` aliases and relative paths.

## Consequences

Extracting NZB submission, queue removal, download keys, range parsing, NZB
filenames, history websocket payloads, and provider-overview rows from
controllers is required before the ArchUnit rules can pass. Further Client or
Database splits wait for those graphs to stay clean. `Services` cleanup is
follow-up work, not this ADR.
