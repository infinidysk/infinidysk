import { type Dispatch, type SetStateAction, useState } from "react";
import {
  Alert,
  Badge,
  Input,
  InputGroup,
  Label,
  ManagedSetting,
  Select,
  SettingsCard,
  SettingsIntro,
  SettingsPage,
  Toggle,
  Tooltip,
} from "~/components/ui";
import { className } from "~/utils/styling";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { isPositiveInteger } from "../validation";

type StreamingSettingsProps = {
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
  effectiveArticleBudgetBytes?: number | null;
};

type BandwidthLimitLiveStats = {
  enabled: boolean;
  limitBytesPerSecond?: number;
  currentBytesPerSecond?: number;
};

export function StreamingSettings({
  config,
  setNewConfig,
  effectiveArticleBudgetBytes = null,
}: StreamingSettingsProps) {
  const [bandwidthLive, setBandwidthLive] = useState<BandwidthLimitLiveStats | null>(null);
  useWebsocketTopic("bwl", "state", (message) => {
    try {
      const parsed = JSON.parse(message) as BandwidthLimitLiveStats;
      setBandwidthLive(parsed);
    } catch {
      // Ignore malformed frames; the next tick replaces them.
    }
  });

  const bandwidthLimit = config["usenet.bandwidth-limit-mbps"] ?? "";
  const parsedBandwidthLimit = Number(bandwidthLimit);
  const showLowLimitWarning =
    bandwidthLimit.trim() !== "" &&
    Number.isFinite(parsedBandwidthLimit) &&
    parsedBandwidthLimit > 0 &&
    parsedBandwidthLimit < 2;
  const effectiveArticleBudgetMiB =
    effectiveArticleBudgetBytes !== null && effectiveArticleBudgetBytes > 0
      ? Math.round(effectiveArticleBudgetBytes / (1024 * 1024))
      : null;

  return (
    <SettingsPage>
      <SettingsIntro>
        Tune how WebDAV playback uses provider connections, memory, caching, and retries. Queue
        import capacity is configured separately under Queue.
      </SettingsIntro>

      <SettingsCard
        icon="tune"
        title="Connection allocation"
        description="Control how playback shares provider capacity with other streams and queue imports."
      >
        <ManagedSetting configKey="usenet.max-download-connections">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="max-download-connections-auto-checkbox"
            >
              Max Download Connections
            </label>
            <Tooltip content="Connections used for WebDAV streaming. Auto uses the combined Pool provider limit; turn off to set a fixed number. Queue imports use their own budget under Queue settings.">
              <Toggle
                id="max-download-connections-auto-checkbox"
                className="cursor-pointer gap-2 p-0"
                checked={isAutoMaxDownloadConnections(config["usenet.max-download-connections"])}
                onChange={(e) =>
                  setNewConfig({
                    ...config,
                    "usenet.max-download-connections": e.target.checked ? "0" : "15",
                  })
                }
                label={
                  <span className="text-sm text-base-content">
                    Auto — use all Pool provider connections
                  </span>
                }
              />
            </Tooltip>
            {!isAutoMaxDownloadConnections(config["usenet.max-download-connections"]) && (
              <Input
                {...className([
                  "w-full max-w-48",
                  !isValidMaxDownloadConnections(config["usenet.max-download-connections"]) &&
                    "input-error",
                ])}
                type="text"
                inputMode="numeric"
                id="max-download-connections-input"
                placeholder="15"
                value={config["usenet.max-download-connections"]}
                onChange={(e) =>
                  setNewConfig({ ...config, "usenet.max-download-connections": e.target.value })
                }
              />
            )}
          </div>
        </ManagedSetting>

        <ManagedSetting
          configKeys={[
            "usenet.max-download-connections-per-stream",
            "usenet.max-download-connections-per-stream-preset",
          ]}
        >
          <div className="space-y-2">
            <Tooltip
              className="tooltip-start"
              content="By default, the budget above is shared across streams. Enable this to give each concurrent stream its own budget, sized by the preset below. Provider limits still cap total connections."
            >
              <Toggle
                id="max-download-connections-per-stream-checkbox"
                className="cursor-pointer gap-2 p-0"
                checked={config["usenet.max-download-connections-per-stream"] === "true"}
                onChange={(e) =>
                  setNewConfig({
                    ...config,
                    "usenet.max-download-connections-per-stream": String(e.target.checked),
                  })
                }
                label={<span className="text-sm text-base-content">Apply limit per stream</span>}
              />
            </Tooltip>
            {config["usenet.max-download-connections-per-stream"] === "true" && (
              <div className="space-y-2 border-l border-base-content/10 pl-4">
                <label
                  className="block text-sm font-medium text-base-content"
                  htmlFor="max-download-connections-per-stream-preset-select"
                >
                  Per-stream performance
                </label>
                <Select
                  className="w-full"
                  id="max-download-connections-per-stream-preset-select"
                  aria-describedby="max-download-connections-per-stream-preset-help"
                  value={config["usenet.max-download-connections-per-stream-preset"] || "high"}
                  onChange={(e) =>
                    setNewConfig({
                      ...config,
                      "usenet.max-download-connections-per-stream-preset": e.target.value,
                    })
                  }
                >
                  <option value="low">Low — 25% of the budget per stream</option>
                  <option value="medium">Medium — 50% of the budget per stream</option>
                  <option value="high">High — 75% of the budget per stream</option>
                  <option value="max">Max — 100% (full budget per stream)</option>
                </Select>
                <p
                  className="text-[11px] leading-relaxed text-base-content/45"
                  id="max-download-connections-per-stream-preset-help"
                >
                  Higher settings fill and seek faster per stream; lower settings keep more
                  connections free for other simultaneous streams.
                </p>
              </div>
            )}
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.streaming-priority">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="streaming-priority-input"
            >
              Streaming Priority (vs Queue)
            </label>
            <InputGroup
              className={`w-full max-w-48 ${
                !isValidStreamingPriority(config["usenet.streaming-priority"] ?? "")
                  ? "input-error"
                  : ""
              }`}
              suffix="%"
              type="text"
              inputMode="numeric"
              id="streaming-priority-input"
              aria-describedby="streaming-priority-help"
              placeholder="80"
              value={config["usenet.streaming-priority"]}
              onChange={(e) =>
                setNewConfig({ ...config, "usenet.streaming-priority": e.target.value })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="streaming-priority-help"
            >
              When playback and queue imports are active together, this percentage of available
              bandwidth is favored for streaming.
            </p>
          </div>
        </ManagedSetting>
      </SettingsCard>

      <SettingsCard
        icon="speed"
        title="Streaming performance"
        description="Tune buffering, caching, retries, and timeout behavior for WebDAV playback."
      >
        <ManagedSetting
          configKeys={[
            "usenet.segment-cache.enabled",
            "usenet.segment-cache.path",
            "usenet.segment-cache.max-gb",
          ]}
        >
          <div className="space-y-2">
            <Tooltip
              placement="bottom"
              content="Cache decoded segments on disk so repeat reads and seeks avoid provider traffic. Takes effect after restart. Enable it only when the cache path is on storage that can handle the extra writes."
            >
              <Toggle
                id="segment-cache-enabled-checkbox"
                className="cursor-pointer gap-2 p-0"
                checked={config["usenet.segment-cache.enabled"] === "true"}
                onChange={(e) =>
                  setNewConfig({
                    ...config,
                    "usenet.segment-cache.enabled": String(e.target.checked),
                  })
                }
                label={
                  <span className="text-sm text-base-content">
                    Enable Segment Cache (fast storage)
                  </span>
                }
              />
            </Tooltip>
            <Alert className="alert-soft items-start text-xs" variant="warning">
              InfiniDysk cannot automatically determine whether the configured path is slow storage
              or flash with limited write endurance. Segment Cache is enabled by default; disable it
              or set Cache path to local SSD/NVMe or other storage where the additional writes are
              acceptable.
            </Alert>
            {config["usenet.segment-cache.enabled"] === "true" && (
              <div className="grid gap-4 border-l border-base-content/10 pl-4 sm:grid-cols-2">
                <label className="flex flex-col gap-2 text-sm text-base-content/80">
                  <span>Cache path</span>
                  <Input
                    className={`w-full ${!isValidSegmentCachePath(config["usenet.segment-cache.path"] ?? "") ? "input-error" : ""}`}
                    value={config["usenet.segment-cache.path"]}
                    placeholder="/config/segment-cache"
                    onChange={(e) =>
                      setNewConfig({ ...config, "usenet.segment-cache.path": e.target.value })
                    }
                  />
                </label>
                <label className="flex flex-col gap-2 text-sm text-base-content/80">
                  <span>Maximum size (GB)</span>
                  <Input
                    className={`w-full max-w-48 ${!isPositiveInteger(config["usenet.segment-cache.max-gb"] ?? "") ? "input-error" : ""}`}
                    inputMode="numeric"
                    value={config["usenet.segment-cache.max-gb"]}
                    onChange={(e) =>
                      setNewConfig({ ...config, "usenet.segment-cache.max-gb": e.target.value })
                    }
                  />
                </label>
              </div>
            )}
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.streaming-segment-timeout-seconds">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="streaming-segment-timeout-input"
            >
              Streaming Segment Timeout
            </label>
            <InputGroup
              className={`w-full max-w-48 ${
                !isValidStreamingSegmentTimeout(
                  config["usenet.streaming-segment-timeout-seconds"] ?? "",
                )
                  ? "input-error"
                  : ""
              }`}
              suffix="sec"
              type="text"
              inputMode="numeric"
              id="streaming-segment-timeout-input"
              aria-describedby="streaming-segment-timeout-help"
              placeholder="8"
              value={config["usenet.streaming-segment-timeout-seconds"]}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.streaming-segment-timeout-seconds": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="streaming-segment-timeout-help"
            >
              Per-segment deadline for WebDAV playback (2–40s). Stalled connections are replaced and
              retried before waiting for the provider&apos;s roughly 40-second read timeout.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.streaming-read-timeout-seconds">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="streaming-read-timeout-input"
            >
              Streaming Read Timeout
            </label>
            <InputGroup
              className={`w-full max-w-48 ${
                !isValidStreamingReadTimeout(config["usenet.streaming-read-timeout-seconds"] ?? "")
                  ? "input-error"
                  : ""
              }`}
              suffix="sec"
              type="text"
              inputMode="numeric"
              id="streaming-read-timeout-input"
              aria-describedby="streaming-read-timeout-help"
              placeholder="30"
              value={config["usenet.streaming-read-timeout-seconds"]}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.streaming-read-timeout-seconds": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="streaming-read-timeout-help"
            >
              Initial backend wait budget to open a WebDAV or /view read (5–120s, default 30). Once
              bytes start flowing, the per-segment timeout above applies instead.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.streaming-write-timeout-seconds">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="streaming-write-timeout-input"
            >
              Streaming Write Timeout
            </label>
            <InputGroup
              className={`w-full max-w-48 ${
                !isValidStreamingWriteTimeout(
                  config["usenet.streaming-write-timeout-seconds"] ?? "",
                )
                  ? "input-error"
                  : ""
              }`}
              suffix="sec"
              type="text"
              inputMode="numeric"
              id="streaming-write-timeout-input"
              aria-describedby="streaming-write-timeout-help"
              placeholder="60"
              value={config["usenet.streaming-write-timeout-seconds"]}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.streaming-write-timeout-seconds": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="streaming-write-timeout-help"
            >
              Per-write deadline for streaming bytes to the client (0–600s, default 60; 0 disables).
              Also cancels a stream that transfers less than 64 KB per timeout window while other
              streams are waiting on Article RAM, so a paused or trickling client cannot wedge
              playback.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.streaming-segment-retries">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="streaming-segment-retries-input"
            >
              Streaming Segment Retries
            </label>
            <Input
              className={`w-full max-w-48 ${
                !isValidStreamingSegmentRetries(config["usenet.streaming-segment-retries"] ?? "")
                  ? "input-error"
                  : ""
              }`}
              type="text"
              inputMode="numeric"
              id="streaming-segment-retries-input"
              aria-describedby="streaming-segment-retries-help"
              placeholder="3"
              value={config["usenet.streaming-segment-retries"]}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.streaming-segment-retries": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="streaming-segment-retries-help"
            >
              Extra attempts on a fresh connection after a streaming segment timeout (0–5). Queue
              and health checks are unaffected.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.article-buffer-size">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="article-buffer-size-input"
            >
              Article Buffer Size
            </label>
            <Input
              {...className([
                "w-full max-w-48",
                !isValidArticleBufferSize(config["usenet.article-buffer-size"] ?? "") &&
                  "input-error",
              ])}
              type="text"
              inputMode="numeric"
              id="article-buffer-size-input"
              aria-describedby="article-buffer-size-help"
              placeholder="40"
              value={config["usenet.article-buffer-size"]}
              onChange={(e) =>
                setNewConfig({ ...config, "usenet.article-buffer-size": e.target.value })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="article-buffer-size-help"
            >
              Articles buffered ahead per stream. Host-wide decoded-byte retention is capped
              separately by the in-flight article budget.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.in-flight-article-budget-mb">
          <div className="space-y-2">
            <div className="flex flex-wrap items-center gap-2">
              <label
                className="block text-sm font-medium text-base-content"
                htmlFor="in-flight-article-budget-input"
              >
                In-flight article budget (MiB)
              </label>
              {effectiveArticleBudgetMiB !== null && (
                <Badge className="badge-ghost badge-sm font-mono">
                  Effective now: {effectiveArticleBudgetMiB.toLocaleString()} MiB
                </Badge>
              )}
            </div>
            <Input
              {...className([
                "w-full max-w-48",
                !isValidInFlightArticleBudget(config["usenet.in-flight-article-budget-mb"]) &&
                  "input-error",
              ])}
              type="text"
              inputMode="numeric"
              id="in-flight-article-budget-input"
              aria-describedby="in-flight-article-budget-help"
              placeholder="auto"
              value={config["usenet.in-flight-article-budget-mb"] ?? ""}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.in-flight-article-budget-mb": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="in-flight-article-budget-help"
            >
              Host-wide cap on decoded article bytes retained across concurrent WebDAV streams
              (64–8192 MiB). Leave empty for an automatic budget based on container memory.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.bandwidth-limit-mbps">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="bandwidth-limit-input"
            >
              Global Usenet bandwidth limit (Mbit/s)
            </label>
            <Input
              {...className([
                "w-full max-w-48",
                !isValidUsenetBandwidthLimitMbps(bandwidthLimit) && "input-error",
              ])}
              type="text"
              inputMode="decimal"
              id="bandwidth-limit-input"
              aria-describedby="bandwidth-limit-help"
              placeholder="Unlimited"
              value={bandwidthLimit}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.bandwidth-limit-mbps": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="bandwidth-limit-help"
            >
              Caps live download from all providers. 1 MB/s = 8 Mbit/s. Applies to queue downloads
              and streaming. Speed tests, cache hits, and LAN delivery are never limited. Leave
              empty or 0 for unlimited.
            </p>
            {showLowLimitWarning && (
              <Alert className="alert-soft items-start text-xs" variant="warning">
                Limits below 2 Mbit/s can stall playback and queue progress.
              </Alert>
            )}
            {bandwidthLive?.enabled && (
              <p className="font-mono text-xs text-base-content/70">
                Current {formatMbitPerSecond(bandwidthLive.currentBytesPerSecond)} /{" "}
                {formatMbitPerSecond(bandwidthLive.limitBytesPerSecond)} Mbit/s
              </p>
            )}
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.idle-connection-timeout-seconds">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="idle-connection-timeout-input"
            >
              Idle connection timeout (seconds)
            </label>
            <Input
              {...className([
                "w-full max-w-48",
                !isValidIdleConnectionTimeout(config["usenet.idle-connection-timeout-seconds"]) &&
                  "input-error",
              ])}
              type="text"
              inputMode="numeric"
              id="idle-connection-timeout-input"
              aria-describedby="idle-connection-timeout-help"
              placeholder="60"
              value={config["usenet.idle-connection-timeout-seconds"] ?? "60"}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.idle-connection-timeout-seconds": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="idle-connection-timeout-help"
            >
              How long unused NNTP connections remain open (15–300s, default 60). Takes effect after
              the next connection-pool rebuild or restart.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.nntp-read-timeout-seconds">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="nntp-read-timeout-input"
            >
              NNTP response timeout (seconds)
            </label>
            <Input
              {...className([
                "w-full max-w-48",
                !isValidNntpReadTimeout(config["usenet.nntp-read-timeout-seconds"]) &&
                  "input-error",
              ])}
              type="text"
              inputMode="numeric"
              id="nntp-read-timeout-input"
              aria-describedby="nntp-read-timeout-help"
              placeholder="30"
              value={config["usenet.nntp-read-timeout-seconds"] ?? "30"}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.nntp-read-timeout-seconds": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="nntp-read-timeout-help"
            >
              Maximum stalled wait for any NNTP response, including BODY, ARTICLE, and STAT (5–120s,
              default 30). This is an inactivity timeout, not a total transfer deadline.
              Connect/auth still uses a 15-second ceiling, and streaming budgets can expire first.
              Takes effect after the next connection-pool rebuild or restart.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.reconnect-delay-milliseconds">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="reconnect-delay-input"
            >
              Replacement reconnect spacing (milliseconds)
            </label>
            <Input
              {...className([
                "w-full max-w-48",
                !isValidReconnectDelay(config["usenet.reconnect-delay-milliseconds"]) &&
                  "input-error",
              ])}
              type="text"
              inputMode="numeric"
              id="reconnect-delay-input"
              aria-describedby="reconnect-delay-help"
              placeholder="500"
              value={config["usenet.reconnect-delay-milliseconds"] ?? "500"}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.reconnect-delay-milliseconds": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="reconnect-delay-help"
            >
              Spaces replacement handshakes after a poisoned socket is closed (0–5000ms, default
              500) so providers can release the old server-side session. Zero disables ordinary
              spacing; failed TCP/TLS/AUTHINFO still back off from 500ms. Takes effect after the
              next connection-pool rebuild or restart.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.pipelined-body-requests">
          <Tooltip
            className="tooltip-start"
            content="Fetch articles in small pipelined BODY batches for smoother WebDAV playback. Queue imports use the separate Queue pipelining toggle under Usenet settings."
          >
            <Toggle
              id="pipelined-body-requests-checkbox"
              className="cursor-pointer gap-2 p-0"
              checked={config["usenet.pipelined-body-requests"] === "true"}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.pipelined-body-requests": String(e.target.checked),
                })
              }
              label={<span className="text-sm text-base-content">Batched article downloads</span>}
            />
          </Tooltip>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.streaming-body-batch-width">
          <div className="space-y-2">
            <Label htmlFor="streaming-body-batch-width-input" className="text-sm text-base-content">
              Streaming batch width
            </Label>
            <Input
              {...className([
                "w-full max-w-48",
                !isValidStreamingBodyBatchWidth(config["usenet.streaming-body-batch-width"]) &&
                  "input-error",
              ])}
              type="text"
              inputMode="numeric"
              id="streaming-body-batch-width-input"
              aria-describedby="streaming-body-batch-width-help"
              placeholder="4"
              value={config["usenet.streaming-body-batch-width"] ?? ""}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.streaming-body-batch-width": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="streaming-body-batch-width-help"
            >
              BODY requests sent per batch on one connection (1–8, default 4). Higher widths reduce
              connection churn but concentrate stall impact and raise per-stream memory: the segment
              task window and prefetch ceiling are fixed at stream start from this width and do not
              shrink when playback narrows batches. Wide settings can starve other concurrent
              streams via the shared in-flight article budget — leave at 4 unless you have measured
              a benefit.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.container-aware-fill">
          <div className="space-y-2">
            <Tooltip content="Experimental. Applies only after all missing or corrupt article fallbacks are exhausted; transient transport failures still abort so the player can retry the range.">
              <Toggle
                id="container-aware-fill"
                className="cursor-pointer gap-2 p-0"
                checked={config["usenet.container-aware-fill"] === "true"}
                onChange={(e) =>
                  setNewConfig({
                    ...config,
                    "usenet.container-aware-fill": String(e.target.checked),
                  })
                }
                label={
                  <span className="inline-flex items-center gap-2 text-sm text-base-content">
                    Container-aware gap fill
                    <Badge className="badge-warning badge-outline badge-xs">Experimental</Badge>
                  </span>
                }
              />
            </Tooltip>
            <p className="text-[11px] leading-relaxed text-base-content/45">
              For permanently missing data in direct MPEG-TS files, emit packet-aligned null packets
              instead of raw zeros so compatible players can resynchronize sooner.
            </p>
          </div>
        </ManagedSetting>
      </SettingsCard>

      <SettingsCard
        icon="hub"
        title="Shared streams"
        description="Let overlapping playback of the same file share one Usenet stream instead of opening a private copy for each request."
      >
        <ManagedSetting configKey="usenet.shared-streams.enabled">
          <Tooltip content="When on, overlapping GETs of the same file join a shared stream when their offsets are close enough. Turning this off restores a private stream per request without a restart.">
            <Toggle
              id="shared-streams-enabled-checkbox"
              className="cursor-pointer gap-2 p-0"
              checked={config["usenet.shared-streams.enabled"] !== "false"}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.shared-streams.enabled": String(e.target.checked),
                })
              }
              label={
                <span className="text-sm text-base-content">
                  Share one stream across concurrent readers
                </span>
              }
            />
          </Tooltip>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.shared-streams.max-entries">
          <div className="space-y-2">
            <Label htmlFor="shared-streams-max-entries-input" className="text-sm text-base-content">
              Max shared streams
            </Label>
            <Input
              {...className([
                "w-full max-w-48",
                !isValidSharedStreamsMaxEntries(config["usenet.shared-streams.max-entries"]) &&
                  "input-error",
              ])}
              type="text"
              inputMode="numeric"
              id="shared-streams-max-entries-input"
              aria-describedby="shared-streams-max-entries-help"
              placeholder="4"
              value={config["usenet.shared-streams.max-entries"] ?? ""}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.shared-streams.max-entries": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="shared-streams-max-entries-help"
            >
              Global cap on live shared streams (1–32, default 4). Extra overlapping reads fall back
              to a private stream.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.shared-streams.max-entries-per-file">
          <div className="space-y-2">
            <Label
              htmlFor="shared-streams-max-per-file-input"
              className="text-sm text-base-content"
            >
              Max regions per file
            </Label>
            <Input
              {...className([
                "w-full max-w-48",
                !isValidSharedStreamsMaxEntriesPerFile(
                  config["usenet.shared-streams.max-entries-per-file"],
                ) && "input-error",
              ])}
              type="text"
              inputMode="numeric"
              id="shared-streams-max-per-file-input"
              aria-describedby="shared-streams-max-per-file-help"
              placeholder="3"
              value={config["usenet.shared-streams.max-entries-per-file"] ?? ""}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.shared-streams.max-entries-per-file": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="shared-streams-max-per-file-help"
            >
              Separate shared streams for far-apart offsets of the same file (1–8, default 3).
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.shared-streams.ring-mb">
          <div className="space-y-2">
            <Label htmlFor="shared-streams-ring-mb-input" className="text-sm text-base-content">
              Ring size (MiB)
            </Label>
            <Input
              {...className([
                "w-full max-w-48",
                !isValidSharedStreamsRingMb(config["usenet.shared-streams.ring-mb"]) &&
                  "input-error",
              ])}
              type="text"
              inputMode="numeric"
              id="shared-streams-ring-mb-input"
              aria-describedby="shared-streams-ring-mb-help"
              placeholder="32"
              value={config["usenet.shared-streams.ring-mb"] ?? ""}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.shared-streams.ring-mb": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="shared-streams-ring-mb-help"
            >
              Per-stream window of recently fetched bytes that late joiners can read without
              refetching (4–256 MiB, default 32). Not counted against Article RAM.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.shared-streams.grace-seconds">
          <div className="space-y-2">
            <Label
              htmlFor="shared-streams-grace-seconds-input"
              className="text-sm text-base-content"
            >
              Grace period (seconds)
            </Label>
            <Input
              {...className([
                "w-full max-w-48",
                !isValidSharedStreamsGraceSeconds(config["usenet.shared-streams.grace-seconds"]) &&
                  "input-error",
              ])}
              type="text"
              inputMode="numeric"
              id="shared-streams-grace-seconds-input"
              aria-describedby="shared-streams-grace-seconds-help"
              placeholder="10"
              value={config["usenet.shared-streams.grace-seconds"] ?? ""}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.shared-streams.grace-seconds": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="shared-streams-grace-seconds-help"
            >
              Keep a shared stream warm after the last reader disconnects so a quick follow-up
              request can reattach (0–60 seconds, default 10).
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="usenet.shared-streams.small-range-max-mb">
          <div className="space-y-2">
            <Label
              htmlFor="shared-streams-small-range-max-mb-input"
              className="text-sm text-base-content"
            >
              Small-range skip (MiB)
            </Label>
            <Input
              {...className([
                "w-full max-w-48",
                !isValidSharedStreamsSmallRangeMaxMb(
                  config["usenet.shared-streams.small-range-max-mb"],
                ) && "input-error",
              ])}
              type="text"
              inputMode="numeric"
              id="shared-streams-small-range-max-mb-input"
              aria-describedby="shared-streams-small-range-max-mb-help"
              placeholder="16"
              value={config["usenet.shared-streams.small-range-max-mb"] ?? ""}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "usenet.shared-streams.small-range-max-mb": e.target.value,
                })
              }
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="shared-streams-small-range-max-mb-help"
            >
              Closed ranges at or below this size use a private stream unless they already overlap a
              live shared stream (1–256 MiB, default 16).
            </p>
          </div>
        </ManagedSetting>
      </SettingsCard>
    </SettingsPage>
  );
}

export function isStreamingSettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
): boolean {
  return (
    config["usenet.max-download-connections"] !== newConfig["usenet.max-download-connections"] ||
    config["usenet.max-download-connections-per-stream"] !==
      newConfig["usenet.max-download-connections-per-stream"] ||
    config["usenet.max-download-connections-per-stream-preset"] !==
      newConfig["usenet.max-download-connections-per-stream-preset"] ||
    config["usenet.streaming-priority"] !== newConfig["usenet.streaming-priority"] ||
    config["usenet.streaming-segment-timeout-seconds"] !==
      newConfig["usenet.streaming-segment-timeout-seconds"] ||
    config["usenet.streaming-read-timeout-seconds"] !==
      newConfig["usenet.streaming-read-timeout-seconds"] ||
    config["usenet.streaming-write-timeout-seconds"] !==
      newConfig["usenet.streaming-write-timeout-seconds"] ||
    config["usenet.streaming-segment-retries"] !== newConfig["usenet.streaming-segment-retries"] ||
    config["usenet.article-buffer-size"] !== newConfig["usenet.article-buffer-size"] ||
    config["usenet.in-flight-article-budget-mb"] !==
      newConfig["usenet.in-flight-article-budget-mb"] ||
    config["usenet.bandwidth-limit-mbps"] !== newConfig["usenet.bandwidth-limit-mbps"] ||
    config["usenet.idle-connection-timeout-seconds"] !==
      newConfig["usenet.idle-connection-timeout-seconds"] ||
    config["usenet.nntp-read-timeout-seconds"] !== newConfig["usenet.nntp-read-timeout-seconds"] ||
    config["usenet.reconnect-delay-milliseconds"] !==
      newConfig["usenet.reconnect-delay-milliseconds"] ||
    config["usenet.pipelined-body-requests"] !== newConfig["usenet.pipelined-body-requests"] ||
    config["usenet.streaming-body-batch-width"] !==
      newConfig["usenet.streaming-body-batch-width"] ||
    config["usenet.container-aware-fill"] !== newConfig["usenet.container-aware-fill"] ||
    config["usenet.segment-cache.enabled"] !== newConfig["usenet.segment-cache.enabled"] ||
    config["usenet.segment-cache.path"] !== newConfig["usenet.segment-cache.path"] ||
    config["usenet.segment-cache.max-gb"] !== newConfig["usenet.segment-cache.max-gb"] ||
    config["usenet.shared-streams.enabled"] !== newConfig["usenet.shared-streams.enabled"] ||
    config["usenet.shared-streams.max-entries"] !==
      newConfig["usenet.shared-streams.max-entries"] ||
    config["usenet.shared-streams.max-entries-per-file"] !==
      newConfig["usenet.shared-streams.max-entries-per-file"] ||
    config["usenet.shared-streams.ring-mb"] !== newConfig["usenet.shared-streams.ring-mb"] ||
    config["usenet.shared-streams.grace-seconds"] !==
      newConfig["usenet.shared-streams.grace-seconds"] ||
    config["usenet.shared-streams.small-range-max-mb"] !==
      newConfig["usenet.shared-streams.small-range-max-mb"]
  );
}

export function isStreamingSettingsValid(config: Record<string, string>): boolean {
  const segmentCacheValid =
    config["usenet.segment-cache.enabled"] !== "true" ||
    (isValidSegmentCachePath(config["usenet.segment-cache.path"] ?? "") &&
      isPositiveInteger(config["usenet.segment-cache.max-gb"] ?? ""));
  return (
    isValidMaxDownloadConnections(config["usenet.max-download-connections"]) &&
    isValidStreamingPriority(config["usenet.streaming-priority"] ?? "") &&
    isValidStreamingSegmentTimeout(config["usenet.streaming-segment-timeout-seconds"] ?? "") &&
    isValidStreamingReadTimeout(config["usenet.streaming-read-timeout-seconds"] ?? "") &&
    isValidStreamingWriteTimeout(config["usenet.streaming-write-timeout-seconds"] ?? "") &&
    isValidStreamingSegmentRetries(config["usenet.streaming-segment-retries"] ?? "") &&
    isValidArticleBufferSize(config["usenet.article-buffer-size"] ?? "") &&
    isValidInFlightArticleBudget(config["usenet.in-flight-article-budget-mb"]) &&
    isValidUsenetBandwidthLimitMbps(config["usenet.bandwidth-limit-mbps"]) &&
    isValidIdleConnectionTimeout(config["usenet.idle-connection-timeout-seconds"]) &&
    isValidNntpReadTimeout(config["usenet.nntp-read-timeout-seconds"]) &&
    isValidReconnectDelay(config["usenet.reconnect-delay-milliseconds"]) &&
    isValidStreamingBodyBatchWidth(config["usenet.streaming-body-batch-width"]) &&
    isValidSharedStreamsMaxEntries(config["usenet.shared-streams.max-entries"]) &&
    isValidSharedStreamsMaxEntriesPerFile(config["usenet.shared-streams.max-entries-per-file"]) &&
    isValidSharedStreamsRingMb(config["usenet.shared-streams.ring-mb"]) &&
    isValidSharedStreamsGraceSeconds(config["usenet.shared-streams.grace-seconds"]) &&
    isValidSharedStreamsSmallRangeMaxMb(config["usenet.shared-streams.small-range-max-mb"]) &&
    segmentCacheValid
  );
}

function isAutoMaxDownloadConnections(value: string | undefined): boolean {
  return !value || value.trim() === "" || value.trim() === "0";
}

function isValidMaxDownloadConnections(value: string | undefined): boolean {
  return isAutoMaxDownloadConnections(value) || isPositiveInteger(value ?? "");
}

function isValidStreamingPriority(value: string): boolean {
  if (value.trim() === "") return false;
  const number = Number(value);
  return Number.isInteger(number) && number >= 0 && number <= 100;
}

function isValidStreamingSegmentTimeout(value: string): boolean {
  if (value.trim() === "") return false;
  const number = Number(value);
  return Number.isInteger(number) && number >= 2 && number <= 40;
}

function isValidStreamingReadTimeout(value: string): boolean {
  if (value.trim() === "") return false;
  const number = Number(value);
  return Number.isInteger(number) && number >= 5 && number <= 120;
}

function isValidStreamingWriteTimeout(value: string): boolean {
  if (value.trim() === "") return false;
  const number = Number(value);
  return Number.isInteger(number) && number >= 0 && number <= 600;
}

function isValidStreamingSegmentRetries(value: string): boolean {
  if (value.trim() === "") return false;
  const number = Number(value);
  return Number.isInteger(number) && number >= 0 && number <= 5;
}

function isValidArticleBufferSize(value: string): boolean {
  return isPositiveInteger(value);
}

function isValidInFlightArticleBudget(value: string | undefined): boolean {
  if (value == null || value.trim() === "") return true;
  const number = Number(value);
  return Number.isInteger(number) && number >= 64 && number <= 8192;
}

function isValidUsenetBandwidthLimitMbps(value: string | undefined): boolean {
  if (value == null || value.trim() === "") return true;
  const number = Number(value);
  return Number.isFinite(number) && number >= 0;
}

function formatMbitPerSecond(bytesPerSecond: number | undefined): string {
  if (bytesPerSecond == null || !Number.isFinite(bytesPerSecond)) return "0.00";
  return (bytesPerSecond / 125_000).toFixed(2);
}

function isValidIdleConnectionTimeout(value: string | undefined): boolean {
  if (value == null || value.trim() === "") return true;
  const number = Number(value);
  return Number.isInteger(number) && number >= 15 && number <= 300;
}

function isValidNntpReadTimeout(value: string | undefined): boolean {
  return isOptionalIntInRange(value, 5, 120);
}

function isValidReconnectDelay(value: string | undefined): boolean {
  return isOptionalIntInRange(value, 0, 5000);
}

function isValidStreamingBodyBatchWidth(value: string | undefined): boolean {
  if (value == null || value.trim() === "") return true;
  const number = Number(value);
  return Number.isInteger(number) && number >= 1 && number <= 8;
}

function isOptionalIntInRange(value: string | undefined, min: number, max: number): boolean {
  if (value == null || value.trim() === "") return true;
  const number = Number(value);
  return Number.isInteger(number) && number >= min && number <= max;
}

function isValidSharedStreamsMaxEntries(value: string | undefined): boolean {
  return isOptionalIntInRange(value, 1, 32);
}

function isValidSharedStreamsMaxEntriesPerFile(value: string | undefined): boolean {
  return isOptionalIntInRange(value, 1, 8);
}

function isValidSharedStreamsRingMb(value: string | undefined): boolean {
  return isOptionalIntInRange(value, 4, 256);
}

function isValidSharedStreamsGraceSeconds(value: string | undefined): boolean {
  return isOptionalIntInRange(value, 0, 60);
}

function isValidSharedStreamsSmallRangeMaxMb(value: string | undefined): boolean {
  return isOptionalIntInRange(value, 1, 256);
}

function isValidSegmentCachePath(value: string): boolean {
  return value.trim().length > 0;
}
