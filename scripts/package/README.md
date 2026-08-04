# InfiniDysk prebuilt release

This archive contains a ready-to-run InfiniDysk frontend and backend for Linux.
The application and its native libraries are already built.

## Requirements

- Node.js 24 or newer
- ASP.NET Core Runtime 10
- `curl`

You do not need the .NET SDK, npm dependencies, CMake, Ninja, or the rapidyenc
source submodule.

## Run

From the extracted directory:

```bash
./run.sh
```

Open <http://localhost:3000>. Persistent configuration is stored in `./config`
unless `CONFIG_PATH` is set.

The launcher accepts the same environment variables as the container. Common
overrides include:

```bash
CONFIG_PATH=/var/lib/infinidysk PORT=3000 ./run.sh
```

`run.sh` starts the frontend, applies database migrations, starts the backend,
and shuts both processes down together.

## Upgrade

1. Stop the current process.
2. Back up the configuration directory.
3. Extract the new archive into a new directory.
4. Start the new `run.sh` with `CONFIG_PATH` pointing to the existing
   configuration directory.

Do not copy an old `backend/`, `frontend/`, or `node_modules/` directory over a
new release.
