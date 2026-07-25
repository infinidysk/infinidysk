# Multi-provider

Add multiple NNTP accounts under **Settings → Usenet**.

## Routing

- **Pool (default)** — connections shared across enabled providers.
- **Cascade** — prefer providers in drag order; fail over down the list.

Each provider has type (pool / backup-only / disabled), SSL, connection limits, optional pipeline depth, and optional **data caps** (auto-pause near the limit).

## Circuit breakers and storage groups

Failing providers are skipped temporarily. **Storage group** labels mark resellers that share the same upstream storage — after a clean article miss on one, siblings in the group are skipped for that request (connection errors never trigger this).

Across requests, a bounded TTL **article-miss negative cache** [since 0.9.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.9.0){ .nzbdav-since } remembers definitive misses per provider or storage group so streaming retries are not spent re-probing known-missing articles. Tune TTL and max entries under **Settings → Usenet → Global settings**.

Only group providers that truly share storage and retention policy.

## Related

[Usenet settings](../configuration/usenet.md) · [Multi-provider use case](../use-cases/multi-provider-failover.md)
