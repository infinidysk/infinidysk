# infinidysk Helm chart

Installs infinidysk 2.x on Kubernetes: one pod, one config volume and one Service on port 3000 for the UI, the SABnzbd API and WebDAV. [`values.yaml`](values.yaml) describes every value.

By default the pod is non-root and serves WebDAV only. The FUSE mount is an explicit, privileged opt-in; see [Sharing the FUSE mount](#sharing-the-fuse-mount).

## Requirements

- Helm 4. Helm 3 is not tested.
- Linux x64 or arm64 nodes.
- Persistent storage for `/config`; see [Storage](#storage).
- For FUSE: a namespace and node policy that allow privileged pods and hostPath volumes, and `/dev/fuse` on the node.
- For the optional resources: an Ingress controller, the Gateway API CRDs with a Gateway, or the Prometheus Operator. The chart installs none of them.

The chart sets no minimum Kubernetes version. CI renders it with Helm 4.3.2 against Kubernetes 1.31.0. A successful render does not prove that your container runtime, storage driver or node security setup works.

The default security context can satisfy the Restricted Pod Security standard, subject to your extra volumes and other cluster policies. Turning FUSE on gives the container full privileges, including `SYS_ADMIN`, and privileged containers are not confined by AppArmor. Rootless FUSE and unprivileged FUSE through a device plugin are not supported.

## Install

Pick a chart version from the [releases](https://github.com/infinidysk/infinidysk/releases). The chart version is the release tag without the `v`. There is no `latest`.

```sh
CHART_VERSION= # the release you picked, without the leading v
helm upgrade --install infinidysk oci://ghcr.io/infinidysk/charts/infinidysk \
  --version "${CHART_VERSION:?set the chart version}" \
  --namespace infinidysk --create-namespace \
  --values values.yaml --timeout 15m --wait
```

The release name becomes the name of the resources. It must start with a lowercase letter, contain only lowercase letters, digits and hyphens, and end in a letter or digit.

Open the UI through a private port-forward:

```sh
kubectl --namespace infinidysk port-forward service/infinidysk 3000:3000
```

The port-forward picks a running pod directly, so it also works while the pod is not ready. Use it in a second terminal to follow or retry a 1.x import while Helm waits.

**Long imports.** Helm 4's default watcher fails when the Deployment passes its 600-second progress deadline, even with a longer `--timeout`. For an import you expect to take long, replace `--timeout 15m --wait` with `--wait=legacy --timeout "$IMPORT_WAIT_TIMEOUT"` and choose the budget, for example `60m`. A Helm timeout fails the Helm operation only; the pod keeps importing. Do not combine a long import with rollback on failure.

A ClusterIP Service is reachable by other workloads in the cluster. It does not replace authentication.

## Credentials and first run

Until an admin account exists, anyone who reaches the UI can create one. Either supply the first account below, or finish setup through the port-forward before you enable a route or change the Service type.

Keep credentials in Secrets you manage, and name them in the values:

| Value | What the Secret holds |
|---|---|
| `existingEnvSecret` | Application environment variables. Each key is the variable name, verbatim. |
| `database.existingSecret` | The PostgreSQL URL under `database.key` (default `url`). |
| `adminApiKey.existingSecret` | The admin API key (`auth.api_key`) under `adminApiKey.key` (default `api-key`). |

Typical `existingEnvSecret` keys are `INFINIDYSK__PROVIDERS` and `INFINIDYSK__ARR_INSTANCES` (JSON lists), and `INFINIDYSK_ADMIN_USERNAME` with `INFINIDYSK_ADMIN_PASSWORD` for the first account. The two admin variables create an account only when none exists. Changing them later does not reset a password. See the [environment reference](https://www.infinidysk.com/v2/reference/environment/).

Create the Secret from files, so no credential lands in your shell history:

```sh
kubectl --namespace infinidysk create secret generic infinidysk-env \
  --from-file=INFINIDYSK__PROVIDERS=./providers.json \
  --from-file=INFINIDYSK_ADMIN_USERNAME=./admin-username \
  --from-file=INFINIDYSK_ADMIN_PASSWORD=./admin-password
```

A trailing newline in a file becomes part of the value. Never pass a credential with `--set`.

`secretSettings` is the alternative for values the chart should store itself. The chart writes them to a Secret as base64 `data`. Base64 is not encryption: the values also appear in rendered output and in Helm's release storage. Do not commit real credentials in a plaintext values file.

The chart cannot look inside your Secrets. They must not set a variable the chart also sets (`INFINIDYSK__MOUNT__FUSE`, and with the matching values `INFINIDYSK__MOUNT__DIR`, `INFINIDYSK__METRICS__PROMETHEUS` and `INFINIDYSK__AUTH__API_KEY`), a whole section that contains one, or its `_FILE` form.

## Sharing the FUSE mount

A FUSE mount reaches other pods only through a shared directory on the **same node**. It does not cross nodes. The chart therefore requires a host directory and a hostname selector:

```yaml
fuse:
  enabled: true
  hostMountPath: /var/lib/infinidysk-mount
nodeSelector:
  kubernetes.io/hostname: your-linux-node
```

Add this to the pod spec of each existing media server or Arr that reads the mount:

```yaml
nodeSelector:
  kubernetes.io/hostname: your-linux-node
containers:
  - name: existing-consumer
    volumeMounts:
      - name: infinidysk-mount
        mountPath: /mnt
        mountPropagation: HostToContainer
volumes:
  - name: infinidysk-mount
    hostPath:
      path: /var/lib/infinidysk-mount
      type: Directory
```

The library is at `/mnt/infinidysk` in both pods and at `/var/lib/infinidysk-mount/infinidysk` on the host. Use `/mnt/infinidysk` in every Arr and media app, so symlink targets resolve everywhere.

The node operator creates the host directory and makes sure it is on a shared mount. The chart does not create it or change the node's mount propagation. A healthy infinidysk pod does not prove that consumers see the mount; check from a consumer.

### Before the first install or a migration

At startup infinidysk detaches whatever is mounted at its mount path, to recover from its own stale mounts. Before it uses a host directory for the first time:

1. Stop the previous owner of the mount, and keep it stopped through the handoff.
2. Confirm that the `infinidysk` child of the host directory holds no unrelated data.
3. Run this read-only check on the node, in the host mount namespace. It needs `findmnt` (util-linux), `realpath` and `jq`. Set `HOST_PARENT` to the host directory, and use the same resolved path in `fuse.hostMountPath`.

```bash
#!/usr/bin/env bash
set -euo pipefail
: "${HOST_PARENT:?set the prepared host parent}"
host_parent=$(realpath -e -- "$HOST_PARENT") || exit 1
host_child="$host_parent/infinidysk"
if [[ -L "$host_child" ]]; then
  printf '%s\n' 'The intended child is a symlink; stop and resolve its ownership.' >&2
  exit 1
fi
mount_table=$(findmnt --json --list --output TARGET) || exit 1
occupied=$(printf '%s' "$mount_table" | jq -er --arg target "$host_child" '
  if (.filesystems | type) != "array" then error("invalid mount table")
  else any(.filesystems[]; .target == $target) | tostring end
') || exit 1
if [[ "$occupied" != false ]]; then
  printf '%s\n' 'The intended child is already mounted; stop before installing.' >&2
  exit 1
fi
printf '%s\n' 'No mount is present at the exact intended child.'
```

The check changes nothing and reserves nothing. Do not run it as a hook or on restarts: once this install owns the path, infinidysk must be able to recover its own stale mount.

## WebDAV only

`fuse.enabled: false` is the default. The chart then adds no privileges, no hostPath and no `/dev/fuse`. It does not create a mount elsewhere, and it does not change the import strategy. Choose how your apps read the library:

- Point the app at WebDAV.
- Use STRM imports: set `imports.strategy` and `imports.strm_dir`, and add the library claims through `extraVolumes` and `extraVolumeMounts`.
- Use a mount that something else maintains on the node. Add it at `/mnt` or `/mnt/infinidysk` with `mountPropagation: HostToContainer`, and set `mount.dir`:

```yaml
settings:
  mount.dir: /mnt/external
extraVolumes:
  - name: mount
    hostPath:
      path: /var/lib/external-mount
      type: Directory
extraVolumeMounts:
  - name: mount
    mountPath: /mnt
    mountPropagation: HostToContainer
```

`HostToContainer` needs no privileged container, but your Pod Security policy may still forbid hostPath volumes.

## Storage

`/config` is needed with PostgreSQL too. It holds metrics, logs, repair files, backup state and the database selection.

- SQLite needs working file locks. Use a local or block storage class. An arbitrary NFS or ReadWriteMany claim is not automatically suitable.
- Without FUSE the container runs as `puid`/`pgid`, which must be able to write the config volume and any library volume. The pod sets `fsGroup` to `pgid` with `fsGroupChangePolicy: OnRootMismatch`, so a supporting volume is not walked on every start. CSI drivers differ. Fix restrictive existing files yourself while infinidysk is stopped.
- The claim the chart creates (`<release>-config`) is kept on uninstall. To use it again under another release, set `persistence.existingClaim`. Delete it yourself when you no longer need the data.
- Back up before upgrading. `helm rollback` does not roll back the database.
- Never run two releases against the same config volume or host directory. ReadWriteOnce is not a lock: pods on the same node can share the volume. Do not scale the Deployment above one replica.

## Readiness and shutdown

The readiness probe calls `/healthz`, which checks the database, the import worker and the mount. While infinidysk is not ready, the Service, Ingress and HTTPRoute send it no traffic.

There is no startup or liveness probe. `/healthz` reports unhealthy during a 1.x import and while a failed import waits for you to retry it, so an HTTP restart deadline would interrupt both. The port opens before the import starts, so a TCP probe would add nothing.

On shutdown infinidysk drains requests, stops its workers and unmounts. `terminationGracePeriodSeconds` gives it 60 seconds. A force delete or a node failure skips this and can leave a stale mount, which the next start on that node recovers.

## Ingress, Gateway API and metrics

All three are off by default. Ingress and HTTPRoute can be on together. The templates render without a cluster, so install the CRDs and controllers first.

```yaml
settings:
  network.public_url: https://infinidysk.example.invalid
ingress:
  enabled: true
  className: your-class
  host: infinidysk.example.invalid
  tlsSecret: infinidysk-tls
httpRoute:
  enabled: true
  parentRefs:
    - name: your-gateway
      namespace: your-gateway-namespace
  hostnames:
    - infinidysk.example.invalid
```

- The chart does not set `network.public_url`. Set it to the address Arrs and browsers use.
- For the HTTPRoute, TLS belongs to the Gateway listener.
- Playback uses long responses. Raise the request and idle timeouts in your controller and turn off response buffering; each controller has its own annotations or policies for this.
- The Gateway listener must allow routes from this namespace. Check that the route reports `Accepted` and `ResolvedRefs`.

For Prometheus:

```yaml
adminApiKey:
  existingSecret: infinidysk-admin-api
serviceMonitor:
  enabled: true
  labels:
    release: your-prometheus
```

This turns on `/metrics` and scrapes it with the admin API key as a Bearer token. It is `auth.api_key`, not the SAB API key. The same Secret sets the key in the application. The Secret is in the release namespace, and the Prometheus Operator must be allowed to read it there. Your Prometheus instance must select this namespace and the monitor's labels.

## Configuration changes

`settings` and `secretSettings` are flat maps. Each key is a dotted setting path, and the chart turns it into an environment variable:

```yaml
settings:
  queue.concurrency: 3 # INFINIDYSK__QUEUE__CONCURRENCY=3
  maintenance.window: # INFINIDYSK__MAINTENANCE__WINDOW={"end":"06:00","start":"03:00"}
    start: "03:00"
    end: "06:00"
secretSettings:
  webdav.password: "0123456789" # a string stays exactly as written
```

- Strings are passed as written. Every other value becomes compact JSON. Quote a string that looks like a number.
- Keys use dots. `__` and a `_file` ending are rejected.
- A `$` stays a literal `$`. The chart escapes it in plain environment values, so `$(NAME)` is not expanded. Secret values are stored verbatim.
- A path may appear once, in one of the two maps, and must not contain another path that is also set. Settings that hold credentials are rejected in `settings`.
- A setting that comes from the environment is locked in the UI. Set single fields, not a broad section, unless you want to own the whole section.

To check the settings before a rollout, run `infinidysk config validate` with the same environment.

Changing `secretSettings` restarts the pod on the next `helm upgrade`. Changing a Secret you manage does not; restart it yourself:

```sh
kubectl --namespace infinidysk rollout restart deployment/infinidysk
kubectl --namespace infinidysk rollout status deployment/infinidysk --timeout=15m
```

A restart does not change the password of an existing admin account.

## Versioning

Each release publishes the chart and the image together, and the chart installs the image with the same version tag. This pins a version; it does not make the registry tag immutable. `image.tag` overrides it and can pair the chart with an application version it was not tested with.

The chart in the source tree has version `0.0.0` and installs `nightly`. It is for development only. Releases from before the chart was added have no chart package.
