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
