# NzbDav Library Canary Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a bounded, read-only NzbDav migration source and prove 20–50 representative production-library leaves through a local, unscanned `/mnt/plex2` canary without changing legacy NzbDav, `/mnt/plex`, Plex, Arr, or existing rclone services.

**Architecture:** A standalone host tool inventories selected legacy symlinks and exports only their required NZBs into a checksummed package using a read-only PostgreSQL role. InfiniDysk imports that package through its existing migration runner, but source scanning, payload construction, and post-import correlation become source-dispatched. Correlation uses release membership, ordered article identity, and exact size; names are supporting evidence only. InfiniDysk emits a reviewed canary-link plan, and the host tool applies or rolls back only the plan's links beneath `/mnt/plex2`.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core/SQLite migration ledger, Npgsql read-only export, React Router 8, Vitest, xUnit, JSON + SHA-256 package manifests, POSIX symlinks, rclone/WebDAV, ffprobe.

---

## Non-negotiable boundaries

- Legacy NzbDav stays authoritative and online throughout the canary.
- Treat `/mnt/plex`, `/mnt/remote/nzbdav`, the legacy PostgreSQL database, and the legacy blob store as read-only.
- Do not register `/mnt/plex2` with Plex or expose it to Arr.
- Do not copy segment-cache bytes or transplant the legacy database.
- Do not use filename-only correlation and do not create a link for an ambiguous mapping.
- Keep the NzbDav package input read-only inside the parallel InfiniDysk container. Do not give the container write access to `/mnt/plex2`.
- Default the canary to one submission worker and a queue depth of five.
- Performance acceptance is blocked until `rclone-infinidysk` targets the direct backend path. Correctness testing may run through the current frontend-proxied mount, but its throughput is not evidence.

## Phase A — product implementation

### Task 0: Prove the identity evidence exists before building the adapter

**Files:**
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavIdentityFeasibilityTests.cs`
- Create: `tests/NzbWebDAV.Tests/Fixtures/UsenetMigration/direct-sample.nzb`
- Create: `tests/NzbWebDAV.Tests/Fixtures/UsenetMigration/archive-sample.nzb`

- [ ] Import one direct-file fixture and one archive-backed fixture through the existing queue pipeline into disposable databases, then inspect the resulting `DavItem`, NZB blob, multipart, and archive-member records.
- [ ] Prove a deterministic release-level ordered article digest can be derived before and after import without fetching article bodies. For direct leaves, also prove the leaf segment digest. For archive members, prove the release digest plus normalized inner path and exact uncompressed size survives processing.
- [ ] Add a characterization test that fails if those evidence fields are absent or reordered. Do not implement filename-only fallback to make the test pass.
- [ ] Record the exact source and target fields in the test name/comments. If either fixture cannot meet the approved identity contract, stop implementation and amend the design before adding schema or UI.
- [ ] Run:

  ```bash
  dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
    --filter 'FullyQualifiedName~NzbDavIdentityFeasibilityTests'
  ```

  Expected: both direct and archive-backed evidence paths are proven from persisted metadata only.

- [ ] Commit:

  ```bash
  git add tests/NzbWebDAV.Tests/UsenetMigration/NzbDavIdentityFeasibilityTests.cs \
    tests/NzbWebDAV.Tests/Fixtures/UsenetMigration
  git commit -m "chore(test): prove NzbDav migration identity evidence"
  ```

### Task 1: Define and test the immutable export-package contract

**Files:**
- Create: `backend/UsenetMigration/NzbDav/NzbDavExportManifest.cs`
- Create: `backend/UsenetMigration/NzbDav/NzbDavArticleIdentity.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavExportManifestTests.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavArticleIdentityTests.cs`

- [ ] Write failing serialization tests for manifest version `1`, deterministic property values, relative package paths, and rejection of unknown major versions.
- [ ] Write failing identity tests for direct NZB leaves and archive-backed leaves. Canonicalize each contributing segment as `number`, `bytes`, and normalized message-id, preserve segment and contributing-part order, then SHA-256 the length-delimited tuple stream. For an archive member, combine the proven release digest from Task 0 with its normalized inner path and exact uncompressed size.
- [ ] Define records for package metadata, source release, source leaf, selected library link, payload file, extraction status, and checksum inventory. Each source leaf must carry the legacy DavItem ID, legacy path, size, parent/release identity, optional history ID, optional NZB blob ID, identity kind, identity digest, and a reason when identity is unavailable.
- [ ] Keep host-only paths out of payload records except the explicitly reviewed original library link and legacy target. Never serialize connection strings, provider settings, API keys, or credentials.
- [ ] Implement path validation: package payload paths are normalized relative paths with no root, `..`, empty component, or alternate-directory-separator escape.
- [ ] Run:

  ```bash
  dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
    --filter 'FullyQualifiedName~NzbDavExportManifestTests|FullyQualifiedName~NzbDavArticleIdentityTests'
  ```

  Expected: the new package and identity tests pass.

- [ ] Commit:

  ```bash
  git add backend/UsenetMigration/NzbDav tests/NzbWebDAV.Tests/UsenetMigration
  git commit -m "feat(migration): define NzbDav export package"
  ```

### Task 2: Add the read-only legacy inventory and export CLI

**Files:**
- Create: `tools/NzbDavMigration/NzbDavMigration.csproj`
- Create: `tools/NzbDavMigration/Program.cs`
- Create: `tools/NzbDavMigration/Legacy/LegacyNzbDavReader.cs`
- Create: `tools/NzbDavMigration/Legacy/LegacyBlobResolver.cs`
- Create: `tools/NzbDavMigration/Inventory/LibraryInventoryService.cs`
- Create: `tools/NzbDavMigration/Export/CanaryPackageWriter.cs`
- Create: `tools/NzbDavMigration/Export/SelectionFile.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/LegacyNzbDavReaderTests.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/LibraryInventoryServiceTests.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/CanaryPackageWriterTests.cs`
- Modify: `NzbWebDAV.sln`

- [ ] Add failing tests using a disposable PostgreSQL fixture that represents the observed legacy schema (`DavItems.Type`, not an assumed `SubType` column). Cover a selected symlink whose item has history, one without history, one missing from the database, and one whose NZB blob cannot be resolved.
- [ ] Add an `inventory` command that walks symlinks only beneath an explicit `--library-root`, parses legacy `/.ids/.../<guid>` targets, queries only those IDs, and writes candidate JSON. It must not recursively follow link targets.
- [ ] Read the connection string from `NZBDAV_MIGRATION_LEGACY_DB`, not a command-line argument. At connection start, begin a read-only transaction and fail unless `SHOW transaction_read_only` is `on`. Production also uses a dedicated login granted `CONNECT` and `SELECT` only.
- [ ] Resolve legacy NZB blobs through an explicit `--blob-root`. Validate resolved paths remain beneath that root, open files read-only, parse XML with DTD processing prohibited, and impose per-file and aggregate byte ceilings.
- [ ] Make selection an explicit reviewed input file containing library-relative paths and legacy DavItem IDs. Never select automatically from all candidates. Reject duplicates, paths outside the inventory, failed/repairing candidates, and a requested count outside the configured canary bound.
- [ ] Add an `export` command that stages into a sibling temporary directory, copies only selected NZB XML, writes `manifest.json` and `SHA256SUMS`, fsyncs/flushes files, then atomically renames the directory. Refuse to overwrite an existing package.
- [ ] Write both success and exclusion reports. An unresolved blob, ambiguous release, or missing identity must be an exclusion, not a guessed export.
- [ ] Add the tool project to `NzbWebDAV.sln` and give it an explicit Npgsql package reference. It may reference the backend project for the shared manifest contract but must not start the ASP.NET host or run application migrations.
- [ ] Run:

  ```bash
  dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
    --filter 'FullyQualifiedName~LegacyNzbDavReaderTests|FullyQualifiedName~LibraryInventoryServiceTests|FullyQualifiedName~CanaryPackageWriterTests'
  dotnet build tools/NzbDavMigration/NzbDavMigration.csproj -c Release
  ```

  Expected: focused tests and tool build pass; test logs contain no fixture connection string.

- [ ] Commit:

  ```bash
  git add NzbWebDAV.sln tools/NzbDavMigration tests/NzbWebDAV.Tests/UsenetMigration
  git commit -m "feat(migration): export bounded legacy NzbDav packages"
  ```

### Task 3: Generalize the migration ledger without disturbing AltMount

**Files:**
- Modify: `backend/UsenetMigration/MigrationSourceTypes.cs`
- Modify: `backend/Database/Models/UsenetMigration/UsenetMigrationEntities.cs`
- Modify: `backend/Database/UsenetMigrationDbContext.cs`
- Create: `backend/Database/UsenetMigrations/<timestamp>_AddNzbDavCanarySource.cs`
- Create: `backend/Database/UsenetMigrations/<timestamp>_AddNzbDavCanarySource.Designer.cs`
- Modify: `backend/Database/UsenetMigrations/UsenetMigrationDbContextModelSnapshot.cs`
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/MigrationTestHarness.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavMigrationSchemaTests.cs`

- [ ] Add failing upgrade tests that create the existing migration schema, insert AltMount state/provenance, apply the new migration, and prove all old rows and defaults survive.
- [ ] Add `MigrationSourceTypes.NzbDav = "nzbdav"`, plus `SourceType`, `SourcePackageRoot`, and `CanaryLibraryRoot` to session/preferences where appropriate. Preserve `altmount` as the default for existing rows.
- [ ] Add nullable `SourceFileId`, `ArticleIdentityKind`, and `ArticleIdentityDigest` to `MigrationReleaseFile` and `MigratedFile`. Add an index suitable for `(MigratedReleaseId, SourceFileId)` lookups.
- [ ] Add `MigrationCanaryLink` with run ID, library-relative path, original legacy target, new relative `.ids` target, correlation status/evidence, source package digest, apply status, error, and timestamps. Do not reuse `MigrationSymlinkRewrite`; that table represents mutation of an existing library.
- [ ] Retain the existing `AltmountCategory` column as the stored source-category key for compatibility in this phase; document the legacy name in code instead of rebuilding the table solely to rename it.
- [ ] Generate the EF migration using the migration context and inspect the generated SQL. This must be additive; it must not drop or rewrite existing tables.
- [ ] Run:

  ```bash
  dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
    --filter 'FullyQualifiedName~NzbDavMigrationSchemaTests|FullyQualifiedName~UsenetMigrationStoreTests'
  ```

  Expected: both fresh-create and upgrade paths pass, including retained AltMount rows.

- [ ] Commit:

  ```bash
  git add backend/UsenetMigration/MigrationSourceTypes.cs backend/Database tests/NzbWebDAV.Tests/UsenetMigration
  git commit -m "feat(migration): record NzbDav source identity"
  ```

### Task 4: Dispatch scanning by migration source

**Files:**
- Create: `backend/UsenetMigration/Runner/IUsenetMigrationScanRunner.cs`
- Create: `backend/UsenetMigration/Runner/MigrationScanDispatcher.cs`
- Modify: `backend/UsenetMigration/Runner/AltmountScanRunner.cs`
- Modify: `backend/UsenetMigration/Runner/UsenetMigrationRunner.cs`
- Modify: `backend/Program.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/MigrationScanDispatcherTests.cs`
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/UsenetMigrationRunnerTests.cs`

- [ ] Write failing tests proving an AltMount session still selects `AltmountScanRunner`, an NzbDav session selects the new source, and an unknown source fails before clearing existing scan artifacts.
- [ ] Extract the narrow scan contract (`SourceType`, `ScanAsync`) and move runner construction to dependency injection. Do not generalize unrelated queue/reconcile logic.
- [ ] Update `UsenetMigrationRunner` to ask the dispatcher for the active scanner. Preserve current pause/cancel gates and existing test seams.
- [ ] Register both scanners and the dispatcher explicitly in `Program.cs`.
- [ ] Run:

  ```bash
  dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
    --filter 'FullyQualifiedName~MigrationScanDispatcherTests|FullyQualifiedName~UsenetMigrationRunnerTests'
  ```

  Expected: source dispatch passes and the existing AltMount runner tests remain green.

- [ ] Commit:

  ```bash
  git add backend/UsenetMigration/Runner backend/Program.cs tests/NzbWebDAV.Tests/UsenetMigration
  git commit -m "refactor(migration): dispatch source scans"
  ```

### Task 5: Scan and validate NzbDav packages

**Files:**
- Create: `backend/UsenetMigration/Source/NzbDavPackageReader.cs`
- Create: `backend/UsenetMigration/Runner/NzbDavScanRunner.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavPackageReaderTests.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavScanRunnerTests.cs`

- [ ] Write failing tests for a valid package, a checksum mismatch, traversal path, symlinked payload file, unknown manifest version, duplicate legacy ID, inconsistent release membership, and missing payload.
- [ ] Verify `SHA256SUMS` before reading payload XML. Open package files with no-write semantics, reject symlinks/reparse points, and re-check the resolved path after open where the platform permits.
- [ ] Populate ordinary `MigrationRelease`, `MigrationReleaseFile`, category-map, and submission rows. Use `nzbdav:<source-release-id>` as `StoreRef`; populate stable source IDs and identity digests from the manifest.
- [ ] Derive target category from the reviewed category map. For phase one, require a dedicated migration-only target category and reject a category currently bound to an enabled Arr instance.
- [ ] Preserve exclusions and malformed entries as scan errors/verdict reasons. Scanning must never silently reduce the selected set.
- [ ] Run:

  ```bash
  dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
    --filter 'FullyQualifiedName~NzbDavPackageReaderTests|FullyQualifiedName~NzbDavScanRunnerTests'
  ```

  Expected: valid packages scan deterministically and all tampering cases fail closed.

- [ ] Commit:

  ```bash
  git add backend/UsenetMigration/Source/NzbDavPackageReader.cs backend/UsenetMigration/Runner/NzbDavScanRunner.cs tests/NzbWebDAV.Tests/UsenetMigration
  git commit -m "feat(migration): scan NzbDav export packages"
  ```

### Task 6: Dispatch submission payload construction

**Files:**
- Create: `backend/UsenetMigration/Runner/IMigrationPayloadBuilder.cs`
- Create: `backend/UsenetMigration/Runner/AltmountPayloadBuilder.cs`
- Create: `backend/UsenetMigration/Runner/NzbDavPayloadBuilder.cs`
- Create: `backend/UsenetMigration/Runner/MigrationPayloadBuilderDispatcher.cs`
- Modify: `backend/UsenetMigration/Runner/SubmissionWorkerPool.cs`
- Modify: `backend/Program.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavPayloadBuilderTests.cs`
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/SubmissionWorkerPoolTests.cs`

- [ ] Move existing AltMount `.nzbz`/v1 reconstruction behind an `IMigrationPayloadBuilder` without changing its output. Lock that behavior with existing and focused regression tests.
- [ ] Add an NzbDav builder that resolves only the package payload recorded at scan, rechecks its checksum and package root confinement at submit time, and returns the original NZB bytes unchanged.
- [ ] Select the builder from session `SourceType`. Unknown/mixed sources fail before creating a durable submission claim.
- [ ] Preserve the existing claim-before-AddFile ordering, claim recovery, one-worker default, queue-depth gate, and pause semantics.
- [ ] Run:

  ```bash
  dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
    --filter 'FullyQualifiedName~NzbDavPayloadBuilderTests|FullyQualifiedName~SubmissionWorkerPoolTests'
  ```

  Expected: byte-for-byte NzbDav payload tests and all existing worker-boundary tests pass.

- [ ] Commit:

  ```bash
  git add backend/UsenetMigration/Runner backend/Program.cs tests/NzbWebDAV.Tests/UsenetMigration
  git commit -m "feat(migration): submit NzbDav package payloads"
  ```

### Task 7: Add strong, source-specific post-import correlation

**Files:**
- Create: `backend/UsenetMigration/Provenance/IMigrationCorrelationProvider.cs`
- Create: `backend/UsenetMigration/Provenance/MigrationCorrelationDispatcher.cs`
- Create: `backend/UsenetMigration/Provenance/AltmountCorrelationProvider.cs`
- Create: `backend/UsenetMigration/Provenance/NzbDavCorrelationProvider.cs`
- Create: `backend/UsenetMigration/Provenance/DavItemArticleIdentityReader.cs`
- Modify: `backend/UsenetMigration/Provenance/MigrationProvenanceService.cs`
- Modify: `backend/UsenetMigration/Provenance/AlreadyMigratedDetector.cs`
- Modify: `backend/UsenetMigration/Symlinks/SymlinkPlanner.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavCorrelationProviderTests.cs`
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/MigrationProvenanceServiceTests.cs`

- [ ] Write failing tests for one-to-one exact matches, equivalent duplicates, non-equivalent ambiguity, missing source identity, completed import with no target match, same-name/different-article rejection, same-article/different-size rejection, direct files, and archive members.
- [ ] Read imported identity from the persisted DavItem/NZB metadata without downloading article bodies. Use the same canonical digest routine as the exporter.
- [ ] Match within the imported release only. Require compatible identity kind, equal digest, and exact size. Use normalized path/name only to disambiguate candidates that already satisfy all strong fields.
- [ ] Persist one of `exact`, `duplicate`, `ambiguous`, `missing-source`, `import-failed`, or `unmatched-target`, plus machine-readable evidence. Only `exact` creates/updates `MigratedFile` with the legacy DavItem ID as `SourceFileId`.
- [ ] Put existing AltMount matching behind its provider and preserve its current behavior. Replace hard-coded `MigrationSourceTypes.Altmount` queries with the active run's source type only where the logic is genuinely source-neutral.
- [ ] Keep the existing production symlink rewrite planner AltMount-only. NzbDav uses the create-only canary plan in the next task.
- [ ] Run:

  ```bash
  dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
    --filter 'FullyQualifiedName~NzbDavCorrelationProviderTests|FullyQualifiedName~MigrationProvenanceServiceTests|FullyQualifiedName~AlreadyMigratedDetectorTests|FullyQualifiedName~SymlinkPlannerTests'
  ```

  Expected: strong-match classifications pass and AltMount behavior is unchanged.

- [ ] Commit:

  ```bash
  git add backend/UsenetMigration/Provenance backend/UsenetMigration/Symlinks tests/NzbWebDAV.Tests/UsenetMigration
  git commit -m "feat(migration): correlate NzbDav imports by article identity"
  ```

### Task 8: Generate a create-only canary-link plan

**Files:**
- Create: `backend/UsenetMigration/Canary/NzbDavCanaryLinkPlanner.cs`
- Create: `backend/UsenetMigration/Canary/NzbDavCanaryPlanWriter.cs`
- Create: `backend/UsenetMigration/Canary/NzbDavCanaryPlanModels.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavCanaryLinkPlannerTests.cs`

- [ ] Write failing tests proving only `exact` mappings are emitted, every selected library-relative path is accounted for, duplicate output paths are rejected, and no absolute host target is embedded.
- [ ] Build each target with the same canonical `.ids` splitting logic used by `DatabaseStoreSymlinkFile.GetTargetPath`, but store it as a relative path beneath an operator-supplied InfiniDysk mount root.
- [ ] Persist every selected link in `MigrationCanaryLink`, including non-linkable statuses, package digest, correlation evidence, and expected file size. A plan is valid only when all selected rows are accounted for and every linkable row is `exact`.
- [ ] Emit immutable JSON plus `SHA256SUMS` beneath `CONFIG_PATH/migration-output/nzbdav` using atomic staging. Refuse to overwrite an existing plan.
- [ ] The backend does not create host symlinks and does not need a writable `/mnt/plex2` mount.
- [ ] Run:

  ```bash
  dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
    --filter 'FullyQualifiedName~NzbDavCanaryLinkPlannerTests'
  ```

  Expected: plan generation passes and all ambiguous/unmatched cases remain non-actionable.

- [ ] Commit:

  ```bash
  git add backend/UsenetMigration/Canary tests/NzbWebDAV.Tests/UsenetMigration
  git commit -m "feat(migration): plan isolated NzbDav canary links"
  ```

### Task 9: Apply, validate, and roll back canary links from the host CLI

**Files:**
- Create: `tools/NzbDavMigration/Canary/CanaryLinkApplier.cs`
- Create: `tools/NzbDavMigration/Canary/CanaryLinkRollback.cs`
- Create: `tools/NzbDavMigration/Canary/CanaryValidator.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/CanaryLinkApplierTests.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/CanaryLinkRollbackTests.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/CanaryValidatorTests.cs`

- [ ] Write failing filesystem tests in a temporary root for traversal, symlinked parents, an existing regular file, a differently targeted existing symlink, an already-correct link, a target that disappears between plan and apply, and rollback after partial apply.
- [ ] Add `apply-links --plan ... --library-root /mnt/plex2 --target-root /mnt/remote/infinidysk`. Resolve and verify both roots before work; require the library root to be local rather than NFS/FUSE; reject any parent component that is a symlink; and never overwrite an existing object.
- [ ] Immediately before each `symlink(2)`, revalidate plan checksum, root confinement, exact expected target, target existence, and exact target size. Write a durable apply journal after every successful link so interruption is recoverable.
- [ ] Treat an already-correct link recorded by the same plan as idempotent success. A link not in the journal is never owned by the tool.
- [ ] Add `rollback-links --journal ...`. Re-read each link without following it, remove it only when its current target still equals the recorded target, then prune only empty directories created by that run. Never delete targets.
- [ ] Add `validate-links` with bounded `stat`, beginning/middle/end reads, optional bounded `ffprobe`, and JSON results. Range/stream tests must have explicit byte and time limits.
- [ ] Run:

  ```bash
  dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
    --filter 'FullyQualifiedName~CanaryLinkApplierTests|FullyQualifiedName~CanaryLinkRollbackTests|FullyQualifiedName~CanaryValidatorTests'
  ```

  Expected: safe apply/rollback cases pass, including the interrupted-run journal case.

- [ ] Commit:

  ```bash
  git add tools/NzbDavMigration/Canary tests/NzbWebDAV.Tests/UsenetMigration
  git commit -m "feat(migration): manage local NzbDav canary links"
  ```

### Task 10: Expose NzbDav migration APIs without changing AltMount routes

**Files:**
- Create: `backend/Api/Controllers/UsenetMigration/NzbDavMigrationController.cs`
- Modify: `backend/Api/Controllers/UsenetMigration/UsenetMigrationController.cs`
- Create: `tests/NzbWebDAV.Tests/UsenetMigration/NzbDavMigrationControllerTests.cs`
- Modify: `tests/NzbWebDAV.Tests/UsenetMigration/UsenetMigrationApiAuthTests.cs`

- [ ] Add failing authorization and state-transition tests for connect/package validation, scan, category mapping, run/pause/resume/cancel, correlation report, and canary-plan download.
- [ ] Add `/api/migration/nzbdav/...` routes in a separate controller using `UsenetMigrationBaseController.GuardedAsync`. Reuse source-neutral store transitions; do not alias or break `/api/migration/altmount/...`.
- [ ] Package configuration accepts a container path only beneath `CONFIG_PATH/migration-input`. Return package digest and selection counts after validation. Production supplies that nested directory as a read-only bind mount, so this requires no new persisted setting or setup-wizard field.
- [ ] Require terminal import reconciliation before correlation and require zero actionable ambiguities before canary-plan generation.
- [ ] Return every selected source leaf in report responses, including exclusions and failures, so the UI cannot imply a reduced set succeeded.
- [ ] Run:

  ```bash
  dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
    --filter 'FullyQualifiedName~NzbDavMigrationControllerTests|FullyQualifiedName~UsenetMigrationApiAuthTests'
  ```

  Expected: all new routes require auth and illegal transitions fail closed.

- [ ] Commit:

  ```bash
  git add backend/Api/Controllers/UsenetMigration tests/NzbWebDAV.Tests/UsenetMigration
  git commit -m "feat(api): expose NzbDav canary migration"
  ```

### Task 11: Add the NzbDav source to the migration UI

**Files:**
- Modify: `frontend/app/routes/settings/migration/migration.tsx`
- Create: `frontend/app/routes/settings/migration/nzbdav/nzbdav-migration.tsx`
- Create: `frontend/app/routes/settings/migration/nzbdav/use-nzbdav-migration.ts`
- Create: `frontend/app/routes/settings/migration/nzbdav/use-nzbdav-migration.test.ts`
- Create: `frontend/app/routes/settings/migration/nzbdav/nzbdav-migration.test.tsx`

- [ ] Write failing hook/component tests for package validation, selected/excluded counts, mandatory category isolation warning, one-worker/five-depth defaults, correlation status table, disabled plan generation with ambiguity, and plan download.
- [ ] Add an `NzbDav` source tab beside AltMount. Preserve AltMount's existing route and component unchanged except for source selection wiring.
- [ ] Present the flow as Package → Review → Import → Correlate → Canary plan. Show persistent warnings that `/mnt/plex2` must remain outside Plex and that the downloaded plan must be applied on nuc-1.
- [ ] Require explicit confirmation of the immutable package digest and exact selection count before Run. Do not add a “continue despite ambiguity” control.
- [ ] Show failure/exclusion reason codes and make report/plan downloads available before any host-side apply.
- [ ] Run:

  ```bash
  cd frontend
  npm test -- --run app/routes/settings/migration/nzbdav/use-nzbdav-migration.test.ts \
    app/routes/settings/migration/nzbdav/nzbdav-migration.test.tsx
  ```

  Expected: focused NzbDav UI tests pass.

- [ ] Commit:

  ```bash
  git add frontend/app/routes/settings/migration
  git commit -m "feat(ui): guide NzbDav canary imports"
  ```

### Task 12: Document operator workflow and security model

**Files:**
- Create: `docs/guides/nzbdav-migration.md`
- Modify: `docs/getting-started/migration.md`
- Modify: `zensical.toml`
- Create: `docs/reference/nzbdav-export-package.md`

- [ ] Document the two-stage trust boundary: read-only export from legacy, then ordinary InfiniDysk import from an immutable package.
- [ ] Document dedicated category creation, the non-Plex `/mnt/plex2` rule, source/target roots, checksums, plan review, apply journal, validation, rollback, and report retention.
- [ ] Add the introducing-release `since` pill used by nearby docs.
- [ ] State the setup-wizard impact review explicitly: this is an advanced migration tool, not a new-install critical setting, so `frontend/app/routes/setup/` and `SetupWizardService.CurrentWizardVersion` do not change.
- [ ] State that frontend-proxied rclone is acceptable only for functional canary work; direct backend WebDAV is required before making throughput claims.
- [ ] Run:

  ```bash
  python -m zensical build --clean --strict
  ```

  Expected: strict docs build succeeds with the new navigation entry.

- [ ] Commit:

  ```bash
  git add docs zensical.toml
  git commit -m "chore(docs): document NzbDav canary migration"
  ```

### Task 13: Perform focused integration verification and open the implementation PR

**Files:**
- Modify only files needed to correct failures found by the checks below.

- [ ] Create an end-to-end test fixture containing one direct NZB leaf and one archive-backed leaf. Exercise package scan → submission stub → reconciliation → strong correlation → canary plan → temporary-root link apply → validation → rollback.
- [ ] Run focused backend, architecture, frontend, and tool checks. Do not run CI-covered full suites unless a failure requires broader diagnosis:

  ```bash
  dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release --filter 'FullyQualifiedName~UsenetMigration'
  dotnet test tests/NzbWebDAV.ArchitectureTests/NzbWebDAV.ArchitectureTests.csproj -c Release
  dotnet build tools/NzbDavMigration/NzbDavMigration.csproj -c Release
  cd frontend && npm test -- --run app/routes/settings/migration
  ```

  Expected: all focused migration, architecture, tool, and migration-UI checks pass.

- [ ] Run `git diff --check`, inspect `git status --short`, and confirm no credentials, production package, apply journal, or generated canary report is tracked.
- [ ] Push a `feat/nzbdav-canary-migration` branch and open a PR titled `feat(migration): safely test legacy NzbDav libraries in parallel` with the safety boundary and focused test evidence. Do not merge it.

## Phase B — production canary runbook

This phase is a separate, explicitly approved production operation after the implementation PR is reviewed, merged by a maintainer, released, and deployed to the parallel InfiniDysk instance. Each gate produces an artifact; stop on any mismatch.

### Task 14: Prepare the parallel environment

- [ ] Confirm legacy NzbDav health, `/mnt/remote/nzbdav` readability, and current restart counts. Record them as the before snapshot.
- [ ] Confirm `/mnt/plex` remains the NFS production library and `/mnt/plex2` does not exist. Create `/mnt/plex2` as a local nuc-1 directory only after checking the parent mount with `findmnt -T /mnt` and the exact destination with `findmnt -T /mnt/plex2`.
- [ ] Create a host package directory such as `/opt/infinidysk-migration/input` and bind it read-only over the parallel container's `CONFIG_PATH/migration-input`. Container recreation is allowed only for the parallel InfiniDysk service; do not restart legacy NzbDav, Plex, Arr, or either existing mount.
- [ ] Create dedicated InfiniDysk migration categories and verify no enabled Sonarr/Radarr instance consumes them.
- [ ] Publish the host CLI from the reviewed release commit and record its SHA-256 digest.
- [ ] Decide the validation level:
  - functional-only may proceed with `rclone-infinidysk` still using frontend port `3004`;
  - throughput/seek acceptance requires first completing and verifying the separately scoped direct-backend rclone change.

### Task 15: Produce and approve the exact canary selection

- [ ] Provision a temporary legacy PostgreSQL role with only `CONNECT` and `SELECT`, and verify a write attempt is denied before using it. Store its connection string outside shell history and logs.
- [ ] Run the CLI `inventory` command against `/mnt/plex`, the legacy database, and blob root. Preserve candidate JSON and checksum it.
- [ ] Select approximately 12–20 releases yielding 20–50 leaves across TV, movies, anime, HD, 4K, MKV, MP4, direct NZB, RAR/multipart, warm, cold, small, medium, and large cases.
- [ ] Exclude known broken, missing-article, quarantined, and repairing records from the success set. Record potential negative tests separately.
- [ ] Review the exact selection artifact before export. The review checks every library-relative path, legacy ID, expected size, representation, and reason for inclusion.

### Task 16: Export, import, and correlate without touching Plex

- [ ] Export the approved selection into a new immutable package. Verify `SHA256SUMS`, manifest count, aggregate size, and that a secret scan finds no connection string, provider credential, API key, or config dump.
- [ ] Place the completed package beneath the read-only migration input directory and verify the container sees it read-only.
- [ ] In the NzbDav migration UI, validate the package digest, map only to dedicated migration categories, keep one worker and queue depth five, then scan and review every release.
- [ ] Start import. Sample queue/history repeatedly until terminal; a temporarily unchanged count is not a reason to intervene. Pause on provider instability, source validation failures, unexpected category activity, or any effect on legacy service health.
- [ ] Reconcile all submissions. Require completed history, expected leaf count, exact size, and `exact` correlation for every linkable canary leaf. Download and review the full correlation report.
- [ ] Generate the canary-link plan only when there are zero ambiguous, duplicate, missing-source, import-failed, or unmatched-target rows in the success set. Verify its checksum and review every proposed relative path and `.ids` target.

### Task 17: Build and validate `/mnt/plex2`

- [ ] Run `apply-links` on nuc-1 with exact roots `/mnt/plex2` and `/mnt/remote/infinidysk`. Preserve the apply journal and checksum it.
- [ ] Confirm link count equals the reviewed plan count, all links remain beneath `/mnt/plex2`, and no inode/mtime beneath `/mnt/plex` changed during the window.
- [ ] Run bounded automated validation for every leaf: `lstat`, target existence, exact size, beginning/middle/end reads, WebDAV HEAD/range probes, and supported `ffprobe`.
- [ ] Manually test representative direct, RAR/multipart, large, cold, warm, and 4K cases without adding `/mnt/plex2` to Plex. Record playback method, timestamps, seek points, and outcome.
- [ ] Recheck legacy NzbDav health, mount readability, and restart counts against the before snapshot. Account for every difference.
- [ ] Accept phase one only if 20–50 links exist, every intended link has an exact mapping, all failures/exclusions are explained, and no production-library/Plex/Arr mutation occurred.

### Task 18: Roll back or retain the isolated canary

- [ ] On failure, pause the migration runner and run `rollback-links` with the recorded journal. Verify it removed only still-matching owned links and pruned only directories created by the run.
- [ ] Do not delete imported InfiniDysk content, packages, reports, or migration state until diagnosis is complete and separate deletion is approved.
- [ ] On success, leave `/mnt/plex2` unregistered and retain the immutable package, plan, journal, reports, and before/after snapshots for the production-cutover design.
- [ ] Revoke the temporary legacy database role after export work is complete.
- [ ] Stop. Full library import, Plex registration, legacy-ID aliasing, production symlink rewriting, and rclone cutover require a new approved design.

## Evidence required before declaring the canary successful

- Reviewed selection JSON and checksum.
- Export manifest, payload checksum inventory, and secret-scan result.
- Migration scan report, category map, run ID, submission outcomes, and correlation report.
- Canary link plan, apply journal, and their checksums.
- Per-leaf automated validation report and representative manual playback notes.
- Before/after legacy service health, mount readability, and restart counts.
- Proof that `/mnt/plex` was unchanged and `/mnt/plex2` was never registered with Plex or Arr.
