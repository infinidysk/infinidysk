<p align="center">
  <img src="ui/public/logo.svg" width="128" alt="infinidysk logo" />
</p>

<h1 align="center">infinidysk</h1>

<p align="center"><strong>Your media library, streamed from Usenet.</strong></p>

<p align="center">
  <a href="https://github.com/infinidysk/infinidysk/releases"><img alt="Latest 2.x release" src="https://img.shields.io/github/v/release/infinidysk/infinidysk?include_prereleases&filter=v2.*&label=2.x" /></a>
  <a href="https://github.com/infinidysk/infinidysk/pkgs/container/infinidysk-next"><img alt="Docker image" src="https://img.shields.io/badge/ghcr.io-infinidysk%2Finfinidysk--next-blue?logo=docker&logoColor=white" /></a>
  <a href="https://github.com/infinidysk/infinidysk/actions/workflows/ci.yml"><img alt="CI status" src="https://img.shields.io/github/actions/workflow/status/infinidysk/infinidysk/ci.yml?branch=main&label=CI" /></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/infinidysk/infinidysk" /></a>
  <a href="https://discord.gg/DAya7W6QMa"><img alt="Discord" src="https://img.shields.io/badge/Discord-chat-5865F2?logo=discord&logoColor=white" /></a>
</p>

<p align="center">
  <a href="https://www.infinidysk.com/">Docs</a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="https://www.infinidysk.com/v2/upgrade/">Upgrade from 1.x</a> ·
  <a href="https://discord.gg/DAya7W6QMa">Discord</a> ·
  <a href="https://github.com/infinidysk/infinidysk/issues">Issues</a>
</p>

infinidysk is a Usenet-backed virtual filesystem. Sonarr, Radarr and other Arr apps add it as a SABnzbd download client. It never downloads the release: your media server reads the files over a native FUSE mount or WebDAV, and infinidysk streams them from your Usenet providers on demand.

This is infinidysk **2.0**, rewritten in Rust and now in **beta**. On 1.x? See [Looking for 1.x?](#looking-for-1x)

> [!WARNING]
> **Moving from 1.x to 2.x is a breaking change.** Changing the image name alone is not an upgrade. Follow [Upgrade from 1.x](https://www.infinidysk.com/v2/upgrade/), and check [what is not in 2.x yet](https://www.infinidysk.com/v2/whats-new/#feature-comparison).

<picture>
  <source media="(prefers-color-scheme: dark)" srcset=".github/assets/overview-dark.png" />
  <img src=".github/assets/overview-light.png" alt="The infinidysk Overview page: files being read right now, download activity for the last 24 hours, and summary tiles for reads, stalls, provider misses, imports, health checks and cache" />
</picture>

## Why infinidysk

- **No download.** The library streams straight from your Usenet providers, so it takes no disk space.
- **One container.** A single Rust binary with a built-in FUSE mount. There is no rclone sidecar.
- **Drop-in SABnzbd replacement.** Arr apps add it as a SABnzbd download client. No plugin is needed.
- **Files stay playable.** Health checks and PAR2 repair find and fix damaged files over time.
- **Playback first.** Throughput and prefetch have priority over memory usage.
- **Problems you can fix from the UI.** A failure shows one clear reason and a next step, and a read trace shows where playback stalled.

## Quick start

A new install. Coming from 1.x? Follow [Upgrade from 1.x](https://www.infinidysk.com/v2/upgrade/) instead, so your library and history carry over.

```yaml
services:
  infinidysk:
    image: ghcr.io/infinidysk/infinidysk-next:edge
    container_name: infinidysk
    restart: unless-stopped
    environment:
      PUID: 1000
      PGID: 1000
      TZ: Etc/UTC # your time zone, e.g. Europe/Oslo; the maintenance window follows it
    ports:
      - "3000:3000"
    volumes:
      - ./config:/config
      - /mnt:/mnt:rshared
      - /sys/devices/virtual/bdi:/sys/devices/virtual/bdi:rw
    devices:
      - /dev/fuse:/dev/fuse
    cap_add:
      - SYS_ADMIN
    security_opt:
      - apparmor:unconfined
```

Open `http://<host>:3000`, create the admin account, then add your providers. Do this before exposing the port to an untrusted network: until an admin exists, anyone who reaches the page can create one.

| | |
|---|---|
| **Port** | `3000`, shared by the UI, the SABnzbd API and WebDAV |
| **Mount** | The library is mounted at `/mnt/infinidysk` |
| **Image** | `ghcr.io/infinidysk/infinidysk-next`, for Linux x64 and arm64 |
| **Tags** | `edge` is the newest 2.x release. Every release also has its own `v2.x.y` tag. There is no `latest` tag until 2.0 ships. |

Next steps in the docs:

1. [Run with Docker](https://www.infinidysk.com/v2/install/docker/)
2. [Providers](https://www.infinidysk.com/v2/install/providers/)
3. [Connect your Arr apps](https://www.infinidysk.com/v2/install/connect-arr/)
4. [Settings](https://www.infinidysk.com/v2/reference/settings/)

## Status

2.0 is in beta. It is a from-scratch rewrite, so it does not have every 1.x feature yet.

**In 2.x now**

- Streaming over the FUSE mount and WebDAV
- SABnzbd API for Arr apps, with symlink import
- NZBs with plain files, multipart files and stored RAR4, RAR5 and 7z members
- PAR2 verify and repair, health checks and scheduled repair
- SQLite and PostgreSQL
- Accounts and sign-in
- A one-time import of your 1.x config, library and history

**Not in 2.x yet**

Some 1.x features are still missing. The [feature comparison](https://www.infinidysk.com/v2/whats-new/#feature-comparison) lists them.

Compressed archives cannot be streamed. infinidysk detects them and fails the import with a clear reason.

PAR2 repair is an experiment: enable it in **Settings → Experiments**, then restart. **Settings → PAR2 repair** contains worker limits, storage use, live article progress and an existing-library backlog. Health shows the same backlog immediately before **Rejected releases**. Overview shows current work and progress in **Right now**. Its **History** tile shows the latest repair outcomes updated within the selected window; retries replace earlier results, and availability checks are excluded. Both tiles link to Health for details. Choose **Check existing library** to check every stored article for availability, or **Check and repair existing library** to queue PAR2 reconstruction when damage is found. Both queue small, resumable batches. Repair mode skips files without an original NZB; files from the same source set share one repair job. Stop adding files to end enumeration, then cancel individual pending jobs from the backlog if needed. Explorer file details offer **Check availability** and **Repair with PAR2**, separately from **Repair via Arr**.

Availability checks use small NNTP STAT requests plus first, middle and last article body spot checks. They need no PAR2 proof and do not download a whole file to hash it. Queued verification jobs from earlier experiment builds resume as availability checks. An available result does not prove every byte is intact. Jobs in one scan share bounded article observations; a later manual check is fresh. Manual requests get priority, with every fourth turn reserved for waiting scan work. Availability and repair have separate workers, so reconstruction does not block cheap checks. Each worker can use its per-attempt memory allowance. Availability leaves provider connections free and yields when playback starts, then resumes without consuming a retry. Playback keeps priority over repair.

When enabled, confirmed health damage tries local repair before Arr replacement. Damaged imports wait for repair, then retry through verified saved articles. Unsupported compression and missing passwords still fail directly. Reconstruction still reads and verifies source data before publishing repairs. One repair runs at a time. Defaults are 64 concurrent availability requests while idle, 256 MB working memory per attempt (including metadata), 100 GB decoded source reads per attempt, one hour per attempt, three attempts and 10 GB saved repair capacity. Change these in **Settings → PAR2 repair**, then save; the next attempt uses the new limits. Environment overrides use `INFINIDYSK__REPAIR__CHECK_CONCURRENCY`, `MEMORY_MB`, `TRANSFER_GB`, `TIMEOUT_MINUTES`, `ATTEMPTS` and `STORAGE_GB` under the same prefix. Playback pauses availability checks. Body spot checks have a separate memory bound. Transport overhead and provider retries can add traffic. Referenced articles are never evicted to make room. Lowering capacity preserves existing repairs and blocks new writes when full; backups need additional disk space. Throughput release checks remain pending.

Saved repair articles live in `repairs/` inside your config folder (`/config/repairs` by default) for SQLite and PostgreSQL. Built-in backup and restore supports SQLite only. Those backups include verified repair bytes and pending C# repair candidates. For PostgreSQL, stop infinidysk, take a database dump with `pg_dump` and copy that repair directory as a matched pair. Restore both before restarting infinidysk. A dump alone cannot recover the repair bytes. Older database-only backups stay readable, and missing repairs trigger health rechecks. Cleanup waits while a restore undo or the SQLite copy from a PostgreSQL switch remains available. Back up config before upgrading.

To move an install, stop infinidysk and copy the whole config directory, including `repairs/`, before starting it at the new location.

Repairs for articles omitted from an NZB require a saved PAR2 recovery-set identity and trusted byte ranges. Older metadata stays readable; repair recovers missing proofs lazily from the original NZB when possible. Availability checks need stored article metadata; reconstruction needs the original NZB. C# repair files are preserved as candidates and become normal saved repairs only after full PAR2 verification. The importer retires its old reader after a complete rewrite pass and seven days without use; original C# files are retained.

Health and import checks use saved repairs when their source identity is known. A repair counts as available only after its bytes pass verification.

Repair progress shows completed provider reads and missing results, including repeated reads and cached misses. Missing replies do not consume the decoded source-byte allowance; uncertain transfer failures still reserve the maximum article size. Request, time and memory limits still apply. Back up config before upgrading for the additive request-progress migration.

The backlog separates files found damaged from checks that could not finish and unsuccessful repair attempts. Totals count jobs, so a file can appear in both checks and repairs. A failed attempt does not prove a file is unrecoverable. Old archive/proof or memory failures can be retried with **Check availability**. Shutdown resumes unfinished work without consuming a retry; explicit cancellation remains cancelled. Back up config before upgrading: the cancellation migration corrects old checks recorded as failed cancellations.

Repair reads PAR2 metadata even when checksums follow recovery packets. Saved, trusted article ranges can resolve adjacent missing parts, and a saved identity matching fresh PAR2 metadata can identify a fully missing source. Every reconstructed source still passes full checksum verification before publication. Without trustworthy byte boundaries or enough recovery data, repair stops with an explanation. Memory-limit errors include the failed phase, requested bytes, available allowance and retry instructions; changing limits applies to the next attempt.

## Reporting problems

See [Reporting a bug](https://www.infinidysk.com/v2/help/reporting/): the image tag, a support pack from the System page, and for playback problems a read trace. Never post provider passwords or API keys. Release names are not needed; describe the file instead (for example "stored RAR5 episode").

## Development

1.x bug fixes go to the `v1` branch; everything else targets `main`. Read [AGENTS.md](AGENTS.md) for the project rules and [DESIGN.md](DESIGN.md) before changing the UI.

Requires [rustup](https://rustup.rs); the toolchain is pinned in `rust-toolchain.toml`.

```sh
cargo fmt --all --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
```

On Linux, also run `cargo test --manifest-path vendor/infinidysk-fuse/Cargo.toml --features tokio-runtime,unprivileged --locked` for notification tests.
For its opt-in kernel tests, add `-- --ignored --test-threads=1`; concurrent test mounts can fail to unmount.
With `/dev/fuse` and mount permission, run `cargo test -p infinidysk --test repair_mount -- --ignored` to check repair publication through the kernel.

With `par2` installed, run `cargo run --release --locked -p infinidysk-bench -- --scenario par2-` to measure playback with idle, active, cancelled, and failed repairs on loopback NNTP. The benchmark uses `par2` to generate the fixture before timing.

`vendor/infinidysk-fuse` contains infinidysk-fuse 0.10.1 with checked inode invalidation and the current Tokio descriptor registration API. Keep the invalidation patch until upstream reports notification write errors. Its original license is retained.
The workspace is a set of crates under [`crates/`](crates/). The product binary is [`crates/infinidysk`](crates/infinidysk); it embeds the web UI from `ui/dist`. The other crates are libraries (NNTP, yEnc, streaming, archives, PAR2, queue, health, VFS, FUSE, WebDAV, database), and dependencies point downward only.

To work on the UI, run the backend and the Vite dev server side by side. Vite proxies API calls to `http://127.0.0.1:3000`; set `BACKEND_URL` to point it elsewhere.

```sh
INFINIDYSK_CONFIG_DIR="$PWD/scrap/config" cargo run -p infinidysk
cd ui && npm ci && npm run dev
```

## Looking for 1.x?

1.x (the C# version) lives on the [`v1`](https://github.com/infinidysk/infinidysk/tree/v1) branch and keeps the `ghcr.io/infinidysk/infinidysk` image. It gets bug fixes only. Its docs are at [infinidysk.com/v1](https://www.infinidysk.com/v1/).

<details>
<summary>My fork tracks 1.x. How do I move it to <code>v1</code>?</summary>

```sh
git fetch upstream
git push origin upstream/v1:refs/heads/v1
```

Then set your fork's default branch to `v1` and sync that branch from now on. A fork with its own commits merges `upstream/v1` instead of `upstream/main`; it is the same history, so there are no new conflicts.

</details>

## License

MIT
