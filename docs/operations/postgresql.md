# PostgreSQL [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }

InfiniDysk defaults to SQLite. For deployments where concurrent metadata writers
need a server database, the main operational database can use an externally
managed PostgreSQL instance.

PostgreSQL support is for **new installations only**. Do not point an existing
SQLite installation at PostgreSQL: automatic SQLite-to-PostgreSQL data migration
is not available yet. Track that migration tooling in
[issue #1012](https://github.com/infinidysk/infinidysk/issues/1012).

## Configuration

Set both variables on the backend container:

```yaml
environment:
  DATABASE_PROVIDER: postgres
  DATABASE_CONNECTION_STRING: Host=postgres;Port=5432;Database=infinidysk;Username=infinidysk;Password=change-me
```

`DATABASE_PROVIDER` defaults to `sqlite`, so existing installations continue to
use `/config/db.sqlite` unchanged.

Only the main operational store uses PostgreSQL. `metrics.sqlite`, `warden.db`,
and `usenet-migration.db` remain in `/config`.

## Database contract file [since 1.2.6](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.6){ .nzbdav-since }

The runtime writes a machine-readable database contract to
`/config/db-contract.json` after every successful migration pass (startup and
`--db-migration`) and whenever the usenet-migration ledger is created. External
migrators — such as [DUMB](https://dumbarr.com/)'s SQLite-to-PostgreSQL
migrator — can pin against this contract instead of reverse-engineering schema
identity from `__EFMigrationsHistory`, backup manifests, or the app version.

The file is world-readable (`0644`) and replaced atomically on each write, so
readers never observe a partial document and upgrades or reinstalls running as
a different user can always overwrite it.

```json
{
  "contract": "infinidysk-db-v1",
  "appVersion": "1.2.6",
  "generatedAtUtc": "2026-08-25T19:00:00.0000000Z",
  "provider": "sqlite",
  "terminalMigration": "20260824143000_Add-Generated-Symlink-Metadata",
  "migrationCount": 51,
  "migrationHistoryHash": "sha256:…",
  "transientObjects": ["TMP_LINKED_FILES", "TMP_LINKED_FILES_UNIQUE"],
  "databases": {
    "main": { "provider": "sqlite", "terminalMigration": "…", "migrationCount": 51, "migrationHistoryHash": "sha256:…", "transientObjects": ["TMP_LINKED_FILES", "TMP_LINKED_FILES_UNIQUE"] },
    "metrics": { "provider": "sqlite", "terminalMigration": "…", "migrationCount": 12, "migrationHistoryHash": "sha256:…", "transientObjects": [] },
    "usenetMigration": { "provider": "sqlite", "terminalMigration": null, "migrationCount": 0, "migrationHistoryHash": null, "transientObjects": [] }
  }
}
```

| Field | Meaning |
|-------|---------|
| `contract` | Stable contract identifier (`infinidysk-db-v1`); bumped only when the contract shape or semantics change. Pin against this. |
| `appVersion` | Running InfiniDysk version. Informational only — the binary and the database can be on different schemas, so do not use it as a schema proxy. |
| `generatedAtUtc` | When the contract was written. |
| `provider` | Main database provider: `sqlite` or `postgres`. |
| `terminalMigration` | Newest applied EF migration id on the main database. |
| `migrationCount` | Number of applied migrations on the main database. |
| `migrationHistoryHash` | `sha256:` hex fingerprint of the full applied migration history (ordinal-sorted ids joined with `\n`) — not just the tip. |
| `transientObjects` | Runtime tables that may exist but are not part of the stable schema (e.g. `TMP_LINKED_FILES` from unlinked-file cleanup). Exclude these when comparing schemas. |
| `databases` | Per-database entries (`main`, `metrics`, `usenetMigration`) with the same five fields. The top-level fields mirror `databases.main`. |

The `usenetMigration` ledger is created lazily; until it exists, its entry
reports `terminalMigration: null`, `migrationCount: 0`, and
`migrationHistoryHash: null`.

## Backups

The Settings backup page continues to back up the SQLite auxiliary stores, but
does not back up the PostgreSQL main database. Back it up independently with
your normal PostgreSQL tooling:

```bash
pg_dump --format=custom --file=infinidysk.dump \
  --host=postgres --username=infinidysk infinidysk
```

Restore with `pg_restore` while the application is stopped. Keep backing up
`/config/blobs` separately; blob content is not stored in PostgreSQL.

## Example compose service

```yaml
postgres:
  image: postgres:17-alpine
  environment:
    POSTGRES_DB: infinidysk
    POSTGRES_USER: infinidysk
    POSTGRES_PASSWORD: change-me
  volumes:
    - postgres-data:/var/lib/postgresql/data
```

The application runs its normal `--db-migration` startup flow against this
database. PostgreSQL itself is not bundled in the InfiniDysk image.
