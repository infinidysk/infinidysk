# Usenet

Configure NNTP providers, connection budgets, cascade vs pooled routing, and queue-side NNTP pipelining.

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
| Provider Connection Limit | Provider-wide ceiling for all pooled connections, including transfers, metadata, and warm sockets | do not exceed the account allowance |
| Transfer Connections | Hard cap for concurrent `BODY` / `ARTICLE` work | blank = legacy shared pool |
| Metadata Capacity | Read-only base-to-burst range calculated from the two limits | shown when Transfer Connections is set |
| Pipeline depth | Per-provider override when pipelining on | blank = global `8` |
| Type | Disabled / Pool Connections / Backup Only | Pool |
| Use SSL | TLS for NNTP | on |
| Skip TLS certificate verification | Accept an invalid provider certificate | off |
| Data Cap | Block-account limit; auto-pauses near ~95% | uncapped |
| Already Used | Seed usage when migrating mid-block | empty |
| Auto-tune | Speed test → recommend Transfer Connections + pipelining | never changes Provider Connection Limit |

Persisted as `usenet.providers` JSON.

!!! warning "Cleartext"

    Disabling SSL stores/sends credentials in cleartext on the wire — only for trusted networks.

## Connection budgets [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

**Provider Connection Limit** is the absolute number of connections InfiniDysk may use for the
provider account. Set it no higher than the provider allows, or lower when the account is shared
with another client.

**Transfer Connections** limits article-body traffic independently. Once it is set, InfiniDysk
uses the rest of the provider budget for lightweight metadata commands such as `STAT`, `HEAD`,
and `DATE`. The editor previews **Metadata Capacity** from the configured values; after saving,
provider cards use the current effective provider limit when live runtime data is available. The
runtime range is calculated as follows:

    P = effective Provider Connection Limit
    T = min(configured Transfer Connections, P)

    base metadata = P - T
    maximum metadata = min(P, max(1, base metadata + floor(T / 2)))
    metadata burst = maximum metadata - base metadata
    metadata capacity = base metadata through maximum metadata

For example:

| Provider limit | Transfer connections | Metadata capacity |
|---------------:|---------------------:|------------------:|
| 50 | 20 | 30–40 |
| 50 | 50 | 0–25 |
| 40 | 16 | 24–32 |
| 1 | 1 | 0–1 |

Transfers never exceed their hard cap, and all work combined never exceeds the effective provider
limit. Metadata may borrow only the displayed burst allowance while transfer capacity is idle. If
transfers begin waiting, currently running metadata commands finish normally; their released slots
normally return to transfers first. Under a sustained transfer backlog, one metadata waiter is
admitted after at most eight consecutive transfer grants so health and control work cannot starve.

!!! info "Existing providers stay in legacy mode"

    A blank `MaxTransferConnections` value preserves the original shared-pool admission scheduling.
    Merely opening or saving an existing provider does not enable split budgeting. Enter **Transfer
    Connections** or apply an Auto-tune recommendation to opt in. Auto-tune's provider ceiling and
    the busy-pool keep-alive safeguards below apply in both scheduling modes.

Auto-tune finds the transfer-throughput knee and applies its recommendation only to **Transfer
Connections**. It never rewrites or probes above **Provider Connection Limit**. Releases before
1.3.0 could sweep above the saved connection count; raise Provider Connection Limit first if you
want Auto-tune to test a higher count. When the result says speed was still climbing at the ceiling,
the provider may benefit from a higher limit if the account permits it. If the provider later
refuses its configured ceiling, InfiniDysk lowers the effective runtime limit without changing
either saved value; provider cards and metrics then show capacities based on that learned limit.

## Invalid provider certificates [since 0.9.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.9.0){ .nzbdav-since }

Leave **Skip TLS certificate verification** disabled unless a trusted provider has
a certificate it cannot correct. It keeps the NNTP connection encrypted but
accepts an untrusted, expired, or hostname-mismatched certificate. This permits
a man-in-the-middle attacker to impersonate the provider and read credentials.

## Routing and pipelining

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Enable cascade routing | `usenet.cascade.enabled` | off | Prefer providers in drag order; off = shared pool. Thinly-spared primaries (≤25% free) yield to idler peers; a larger Provider Connection Limit alone does not outrank priority. |
| Re-probe primary after miss | `usenet.cascade.retry-primary-on-miss` | on | After a clean 430/451 on the first batch attempt, try the primary once more before cascading (multi-node spool). Off = skip straight to backups. |
| Enable queue pipelining | `usenet.queue-pipelining.enabled` | off | Batch first-segment BODY during queue imports/benchmarks |
| Queue pipeline depth | `usenet.queue-pipelining.depth` | `8` | Requests in flight per connection (1–64) |

Legacy keys `usenet.pipelining.enabled` / `usenet.pipelining.depth` remain honored; env vars use `NZBDAV_CONFIG__USENET__QUEUE_PIPELINING__*` for the new names.

Run Auto-tune before enabling queue pipelining. WebDAV streaming batching is a **separate** toggle on [Streaming](streaming.md).

## Global download cap [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

`usenet.bandwidth-limit-mbps` (Streaming settings) caps total live Usenet payload
ingress from every provider, in megabits per second. Queue downloads and WebDAV
streaming share the cap. Cache hits, LAN delivery, and the provider speed
benchmark are not limited. Empty or `0` means unlimited; the value reloads
without restart. 1 MB/s = 8 Mbit/s. The cap cannot make a latency-limited
provider faster.

See [NNTP pipelining](../features/nntp-pipelining.md) and [Multi-provider](../features/multi-provider.md).

## Warm connections [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }

Each pooled provider keeps a small floor of pre-connected, authenticated NNTP sockets
ready so playback and queue work skip the connect/TLS/login handshake after idle
periods. Warm sockets count against the provider's connection limit but never hold
download permits. See [Connection warming](../features/connection-warming.md) for the
mechanics and the header indicator.

| Control | Config key | Default | Effect |
|---------|------------|---------|--------|
| Warm connections | `usenet.warm-connections.enabled` | on | Keep pre-connected sockets ready per provider |
| Warm floor | `usenet.warm-connections.floor` | auto | Idle sockets kept ready per provider; auto derives one sixth of Max Connections, clamped to 1–8 |

Changes take effect on the next provider save or restart — connection pools are not
rebuilt when these keys change alone.

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

## Provider circuit-breaker cooldown [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

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
