# Usenet

Configure NNTP providers, cascade vs pooled routing, and queue-side NNTP pipelining.

!!! tip "Headless ENV"

    Each config key below maps to `NZBDAV_CONFIG__...` via the
    [naming algorithm](headless.md#naming-algorithm) (for example
    `usenet.providers` → `NZBDAV_CONFIG__USENET__PROVIDERS`).

## Providers

Add one or more accounts. Each provider supports:

| Control | What it does | Default / notes |
|---------|--------------|-----------------|
| Nickname | Friendly label instead of hostname | optional |
| Storage group | Same label → skip siblings after a clean article miss | optional; only same upstream |
| Host / Port | NNTP endpoint | port often `563` |
| Username / Password | Credentials | prefer SSL |
| Max Connections | Concurrent NNTP connections for this account | ≤ plan limit |
| Pipeline depth | Per-provider override when pipelining on | blank = global `8` |
| Type | Disabled / Pool Connections / Backup Only | Pool |
| Use SSL | TLS for NNTP | on |
| Skip TLS certificate verification | Accept an invalid provider certificate | off |
| Data Cap | Block-account limit; auto-pauses near ~95% | uncapped |
| Already Used | Seed usage when migrating mid-block | empty |
| Auto-tune | Speed test → recommend connections + pipelining | action |

Persisted as `usenet.providers` JSON.

!!! warning "Cleartext"

    Disabling SSL stores/sends credentials in cleartext on the wire — only for trusted networks.

## Invalid provider certificates [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since }

Leave **Skip TLS certificate verification** disabled unless a trusted provider has
a certificate it cannot correct. It keeps the NNTP connection encrypted but
accepts an untrusted, expired, or hostname-mismatched certificate. This permits
a man-in-the-middle attacker to impersonate the provider and read credentials.

## Routing and pipelining

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Enable cascade routing | `usenet.cascade.enabled` | off | Prefer providers in drag order; off = shared pool. Thinly-spared primaries (≤25% free) yield to idler peers; larger MaxConnections alone does not outrank priority. |
| Re-probe primary after miss | `usenet.cascade.retry-primary-on-miss` | on | After a clean 430/451 on the first batch attempt, try the primary once more before cascading (multi-node spool). Off = skip straight to backups. |
| Enable queue pipelining | `usenet.queue-pipelining.enabled` | off | Batch first-segment BODY during queue imports/benchmarks |
| Queue pipeline depth | `usenet.queue-pipelining.depth` | `8` | Requests in flight per connection (1–64) |

Legacy keys `usenet.pipelining.enabled` / `usenet.pipelining.depth` remain honored; env vars use `NZBDAV_CONFIG__USENET__QUEUE_PIPELINING__*` for the new names.

Run Auto-tune before enabling queue pipelining. WebDAV streaming batching is a **separate** toggle on [Streaming](streaming.md).

See [NNTP pipelining](../features/nntp-pipelining.md) and [Multi-provider](../features/multi-provider.md).

## Article-miss negative cache [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since }

After a provider (or [storage group](../features/multi-provider.md)) reports a definitive article miss
(NNTP 430 or provider 451), InfiniDysk remembers that miss so later streaming/batch reads skip
re-probing the same provider for the same article until the TTL expires. Transient failures
(timeouts, network, corrupt articles) are never cached.

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Miss-cache TTL (seconds) | `usenet.article-miss-cache-ttl-seconds` | `300` | How long a miss stays cached (clamped 30–86400) |
| Miss-cache max entries | `usenet.article-miss-cache-max-entries` | `10000` | Cap before oldest entries are evicted (clamped 100–1000000) |

The cache clears automatically when Usenet providers are reconfigured.

## Provider circuit-breaker cooldown

When a provider's circuit trips, that provider is skipped for a cooldown and traffic goes to
the remaining providers. Each consecutive trip doubles the cooldown up to a ceiling. A
successful article body resets it to the initial value.

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Initial cooldown (seconds) | `usenet.circuit-breaker.initial-cooldown-seconds` | `60` | Cooldown applied on the first trip (clamped 5 to 300) |
| Maximum cooldown (seconds) | `usenet.circuit-breaker.max-cooldown-seconds` | `300` | Ceiling the doubling stops at (clamped 5 to 3600) |

Lower the initial cooldown when a single pool provider carries the traffic and the backups are
metered blocks. A brief wobble then spends fewer backup bytes before the primary is re-probed.
A maximum below the initial value is raised to it. The connection pools read both values when
they are built, so a change applies on the next restart or provider save. Neither has a
Settings control. Set them through config or the environment.

Once the cooldown lapses, one request is admitted as a half-open probe. If that request is
abandoned before it returns an outcome, the probe slot stays claimed for up to 60 seconds
before another request can retake it. A cooldown shorter than that will not always re-probe as
quickly as the number suggests.
