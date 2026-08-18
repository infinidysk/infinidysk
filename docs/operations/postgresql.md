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
