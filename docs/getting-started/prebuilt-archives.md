# Prebuilt Linux archives [since 0.10.0](https://github.com/nzbdav/nzbdav/releases/tag/v0.10.0){ .nzbdav-since }

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
[latest GitHub release](https://github.com/nzbdav/nzbdav/releases/latest):

- `nzbdav-v<version>-linux-x64.tar.gz`
- `nzbdav-v<version>-linux-arm64.tar.gz`

Extract it and start the launcher:

```bash
tar -xzf nzbdav-v<version>-linux-<architecture>.tar.gz
cd nzbdav-v<version>-linux-<architecture>
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

## Upgrade

1. Stop the running launcher.
2. Back up the directory selected by `CONFIG_PATH`.
3. Download and extract the new archive into a new directory.
4. Start its `run.sh` with the same `CONFIG_PATH`.

Do not copy the old `backend/`, `frontend/`, or `node_modules/` directories into
the new release.
