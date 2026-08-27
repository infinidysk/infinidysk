# Prometheus metrics [since 1.2.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.2.0){ .nzbdav-since }

InfiniDysk exposes Prometheus metrics at `/metrics`. The endpoint includes standard
.NET process/runtime metrics plus `nzbdav_` metrics for active streaming, seek
latency, NNTP provider pools, circuit breakers, article outcomes, PAR2 repair,
streaming-confirmed corrupt articles, and internal metrics-pipeline health.

Metric labels are deliberately bounded. Provider metrics use the configured provider
identity; other labels are fixed enums such as `region`, `kind`, `state`, and
`status`. Paths, release names, filenames, client addresses, and article IDs are
never exported as labels.

## Authentication

By default, direct backend scrapes are anonymous. Set
`METRICS_REQUIRE_API_KEY=true` to require the normal `x-api-key` header for direct
scrapes. Requests through the frontend `/metrics` proxy always require an
authenticated InfiniDysk UI session and automatically receive the internal key.

Do not publish the backend port to untrusted networks when direct scraping is
anonymous.

## Provider connection budgets

Operation-aware providers expose their live budget through the existing bounded-label
NNTP gauge families:

| Metric | Label | Values |
|--------|-------|--------|
| `nzbdav_nntp_pool_connections` | `state` | `transfer_active`, `metadata_active`, `transfer_waiting`, `metadata_waiting` |
| `nzbdav_nntp_pool_max_connections` | `limit` | `transfer_configured`, `transfer_effective`, `metadata_base`, `metadata_burst`, `metadata_max` |

Both metric families also carry `provider_key`, the provider's stable normalized identifier.
The existing pool states (`live`, `idle`, `active`, `available`, and `pending`) and limits
(`configured`, `effective`, and optional `learned`) remain available alongside the budget labels.

Providers with no configured `MaxTransferConnections` remain in legacy shared-pool mode and do
not export the transfer/metadata label values. Their absence means “budgeting disabled,” not zero
capacity.

For example, these queries compare current operation admission with its effective limits:

```promql
nzbdav_nntp_pool_connections{state=~"transfer_active|metadata_active"}
```

```promql
nzbdav_nntp_pool_max_connections{limit=~"transfer_effective|metadata_max"}
```

## Health-check admission [since 1.3.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.3.0){ .nzbdav-since }

The process-wide verification gate exports a bounded `state` label for current
admission and a bounded `limit` label for its effective connection ceiling:

| Metric | Label | Values |
|--------|-------|--------|
| `nzbdav_health_check_gate_operations` | `state` | `active`, `waiting_queue`, `waiting_background` |
| `nzbdav_health_check_gate_limit` | `limit` | `effective` |

Queue article validation and background library checks share this gate. Queue
waiters receive released capacity first, so compare `waiting_queue` and
`waiting_background` when diagnosing a saturated verification budget.

## Prometheus configuration

For the normal frontend endpoint:

```yaml
scrape_configs:
  - job_name: infinidysk
    static_configs:
      - targets: ["infinidysk:3000"]
```

On the same Docker network, scrape the backend directly on port 8080. When
`METRICS_REQUIRE_API_KEY=true`, Prometheus 3 can send the API key:

```yaml
scrape_configs:
  - job_name: infinidysk-backend
    static_configs:
      - targets: ["infinidysk:8080"]
    http_headers:
      x-api-key:
        values: ["replace-with-your-api-key"]
```
