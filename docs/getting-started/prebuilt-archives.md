# Prebuilt Linux archives [since 0.10.0](https://github.com/infinidysk/infinidysk/releases/tag/v0.10.0){ .nzbdav-since }

Prebuilt release archives run InfiniDysk directly on Linux without Docker or a
source build. They include the compiled backend, frontend, production Node.js
dependencies, and rapidyenc native library.

## Requirements

- Linux x64 or ARM64
- Node.js 24 or newer
- ASP.NET Core Runtime 10
- `curl`

The .NET SDK, CMake, Ninja, npm install, and Git submodules are not required.

## Install

Download the archive for your system from the
[latest GitHub release](https://github.com/infinidysk/infinidysk/releases/latest):

- `infinidysk-v<version>-linux-x64.tar.gz`
- `infinidysk-v<version>-linux-arm64.tar.gz`

Extract it and start the launcher:

```bash
tar -xzf infinidysk-v<version>-linux-<architecture>.tar.gz
cd infinidysk-v<version>-linux-<architecture>
./run.sh
```

Open `http://localhost:3000`. The launcher starts the frontend, applies database
migrations, starts the backend, and stops both processes together.

By default, persistent state is stored in `config/` inside the extracted
directory. Set an external path for easier upgrades:

```bash
CONFIG_PATH=/var/lib/infinidysk ./run.sh
```

Other container environment variables are supported, including `PORT`,
`LOG_LEVEL`, and the
[headless Settings overlay](../configuration/headless.md).

## Pre-release testing

Each release candidate gets a versioned GitHub Pre-release and archives such as
`infinidysk-v0.10.0-rc.1-linux-x64.tar.gz`. Those versioned pre-releases and
their image tags are removed automatically when the next stable release ships.

For automation, the rolling `rc` pre-release always exposes stable asset URLs:

- [linux-x64](https://github.com/infinidysk/infinidysk/releases/download/rc/infinidysk-rc-linux-x64.tar.gz)
- [linux-arm64](https://github.com/infinidysk/infinidysk/releases/download/rc/infinidysk-rc-linux-arm64.tar.gz)

The archive's `version.txt` identifies the exact release candidate. The rolling
`rc` release remains after a stable release ships (it is not deleted), so check
its version before assuming it is newer than `latest`.

## Upgrade

1. Stop the running launcher.
2. Back up the directory selected by `CONFIG_PATH`.
3. Download and extract the new archive into a new directory.
4. Start its `run.sh` with the same `CONFIG_PATH`.

Do not copy the old `backend/`, `frontend/`, or `node_modules/` directories into
the new release.
