import { type Dispatch, type SetStateAction } from "react";
import {
    Badge,
    Input,
    ManagedSetting,
    Select,
    SettingsCard,
    SettingsIntro,
    SettingsPage,
    Toggle,
    Tooltip,
} from "~/components/ui";
import { className } from "~/utils/styling";
import { isPositiveInteger } from "../validation";

type StreamingSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function StreamingSettings({ config, setNewConfig }: StreamingSettingsProps) {
    return (
        <SettingsPage>
            <SettingsIntro>
                Tune how WebDAV playback uses provider connections, memory, caching, and retries.
                Queue import capacity is configured separately under Queue.
            </SettingsIntro>

            <SettingsCard
                icon="tune"
                title="Connection allocation"
                description="Control how playback shares provider capacity with other streams and queue imports."
            >
                <ManagedSetting configKey="usenet.max-download-connections">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="max-download-connections-auto-checkbox">
                            Max Download Connections
                        </label>
                        <Tooltip content="Connections used for WebDAV streaming. Auto uses the combined Pool provider limit; turn off to set a fixed number. Queue imports use their own budget under Queue settings.">
                            <Toggle
                                id="max-download-connections-auto-checkbox"
                                className="cursor-pointer gap-2 p-0"
                                checked={isAutoMaxDownloadConnections(config["usenet.max-download-connections"])}
                                onChange={e => setNewConfig({
                                    ...config,
                                    "usenet.max-download-connections": e.target.checked ? "0" : "15",
                                })}
                                label={<span className="text-sm text-base-content">Auto — use all Pool provider connections</span>}
                            />
                        </Tooltip>
                        {!isAutoMaxDownloadConnections(config["usenet.max-download-connections"]) && (
                            <Input
                                {...className(["w-full", !isValidMaxDownloadConnections(config["usenet.max-download-connections"]) && "input-error"])}
                                type="text"
                                inputMode="numeric"
                                id="max-download-connections-input"
                                placeholder="15"
                                value={config["usenet.max-download-connections"]}
                                onChange={e => setNewConfig({ ...config, "usenet.max-download-connections": e.target.value })} />
                        )}
                    </div>
                </ManagedSetting>

                <ManagedSetting configKeys={[
                    "usenet.max-download-connections-per-stream",
                    "usenet.max-download-connections-per-stream-preset",
                ]}>
                    <div className="space-y-2">
                        <Tooltip content="By default, the budget above is shared across streams. Enable this to give each concurrent stream its own budget, sized by the preset below. Provider limits still cap total connections.">
                            <Toggle
                                id="max-download-connections-per-stream-checkbox"
                                className="cursor-pointer gap-2 p-0"
                                checked={config["usenet.max-download-connections-per-stream"] === "true"}
                                onChange={e => setNewConfig({
                                    ...config,
                                    "usenet.max-download-connections-per-stream": String(e.target.checked),
                                })}
                                label={<span className="text-sm text-base-content">Apply limit per stream</span>}
                            />
                        </Tooltip>
                        {config["usenet.max-download-connections-per-stream"] === "true" && (
                            <div className="space-y-2 border-l border-base-content/10 pl-4">
                                <label className="block text-sm font-medium text-base-content" htmlFor="max-download-connections-per-stream-preset-select">
                                    Per-stream performance
                                </label>
                                <Select
                                    className="w-full"
                                    id="max-download-connections-per-stream-preset-select"
                                    aria-describedby="max-download-connections-per-stream-preset-help"
                                    value={config["usenet.max-download-connections-per-stream-preset"] || "high"}
                                    onChange={e => setNewConfig({
                                        ...config,
                                        "usenet.max-download-connections-per-stream-preset": e.target.value,
                                    })}>
                                    <option value="low">Low — 25% of the budget per stream</option>
                                    <option value="medium">Medium — 50% of the budget per stream</option>
                                    <option value="high">High — 75% of the budget per stream</option>
                                    <option value="max">Max — 100% (full budget per stream)</option>
                                </Select>
                                <p className="text-[11px] leading-relaxed text-base-content/45" id="max-download-connections-per-stream-preset-help">
                                    Higher settings fill and seek faster per stream; lower settings keep more
                                    connections free for other simultaneous streams.
                                </p>
                            </div>
                        )}
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.streaming-priority">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="streaming-priority-input">
                            Streaming Priority (vs Queue)
                        </label>
                        <div className="flex w-full">
                            <Input
                                className={!isValidStreamingPriority(config["usenet.streaming-priority"]) ? "input-error" : undefined}
                                type="text"
                                inputMode="numeric"
                                id="streaming-priority-input"
                                aria-describedby="streaming-priority-help"
                                placeholder="80"
                                value={config["usenet.streaming-priority"]}
                                onChange={e => setNewConfig({ ...config, "usenet.streaming-priority": e.target.value })} />
                            <span className="flex items-center rounded-r border border-l-0 border-base-content/20 bg-base-200 px-2 text-sm text-base-content/80">
                                %
                            </span>
                        </div>
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="streaming-priority-help">
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
                <ManagedSetting configKeys={[
                    "usenet.segment-cache.enabled",
                    "usenet.segment-cache.path",
                    "usenet.segment-cache.max-gb",
                ]}>
                    <div className="space-y-2">
                        <Tooltip placement="bottom" content="Cache decoded segments on disk so repeat reads and seeks avoid provider traffic. Takes effect after restart.">
                            <Toggle
                                id="segment-cache-enabled-checkbox"
                                className="cursor-pointer gap-2 p-0"
                                checked={config["usenet.segment-cache.enabled"] === "true"}
                                onChange={e => setNewConfig({
                                    ...config,
                                    "usenet.segment-cache.enabled": String(e.target.checked),
                                })}
                                label={<span className="text-sm text-base-content">Enable Segment Cache</span>}
                            />
                        </Tooltip>
                        {config["usenet.segment-cache.enabled"] === "true" && (
                            <div className="grid gap-4 border-l border-base-content/10 pl-4 sm:grid-cols-2">
                                <label className="space-y-2 text-sm text-base-content/80">
                                    <span>Cache path</span>
                                    <Input
                                        className={`w-full ${!isValidSegmentCachePath(config["usenet.segment-cache.path"]) ? "input-error" : ""}`}
                                        value={config["usenet.segment-cache.path"]}
                                        placeholder="/config/segment-cache"
                                        onChange={e => setNewConfig({ ...config, "usenet.segment-cache.path": e.target.value })} />
                                </label>
                                <label className="space-y-2 text-sm text-base-content/80">
                                    <span>Maximum size (GB)</span>
                                    <Input
                                        className={`w-full ${!isPositiveInteger(config["usenet.segment-cache.max-gb"]) ? "input-error" : ""}`}
                                        inputMode="numeric"
                                        value={config["usenet.segment-cache.max-gb"]}
                                        onChange={e => setNewConfig({ ...config, "usenet.segment-cache.max-gb": e.target.value })} />
                                </label>
                            </div>
                        )}
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.streaming-segment-timeout-seconds">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="streaming-segment-timeout-input">
                            Streaming Segment Timeout
                        </label>
                        <div className="flex w-full">
                            <Input
                                className={!isValidStreamingSegmentTimeout(config["usenet.streaming-segment-timeout-seconds"]) ? "input-error" : undefined}
                                type="text"
                                inputMode="numeric"
                                id="streaming-segment-timeout-input"
                                aria-describedby="streaming-segment-timeout-help"
                                placeholder="8"
                                value={config["usenet.streaming-segment-timeout-seconds"]}
                                onChange={e => setNewConfig({
                                    ...config,
                                    "usenet.streaming-segment-timeout-seconds": e.target.value,
                                })} />
                            <span className="flex items-center rounded-r border border-l-0 border-base-content/20 bg-base-200 px-2 text-sm text-base-content/80">
                                sec
                            </span>
                        </div>
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="streaming-segment-timeout-help">
                            Per-segment deadline for WebDAV playback (2–40s). Stalled connections are replaced
                            and retried before waiting for the provider&apos;s roughly 40-second read timeout.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.streaming-read-timeout-seconds">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="streaming-read-timeout-input">
                            Streaming Read Timeout
                        </label>
                        <div className="flex w-full">
                            <Input
                                className={!isValidStreamingReadTimeout(config["usenet.streaming-read-timeout-seconds"]) ? "input-error" : undefined}
                                type="text"
                                inputMode="numeric"
                                id="streaming-read-timeout-input"
                                aria-describedby="streaming-read-timeout-help"
                                placeholder="30"
                                value={config["usenet.streaming-read-timeout-seconds"]}
                                onChange={e => setNewConfig({
                                    ...config,
                                    "usenet.streaming-read-timeout-seconds": e.target.value,
                                })} />
                            <span className="flex items-center rounded-r border border-l-0 border-base-content/20 bg-base-200 px-2 text-sm text-base-content/80">
                                sec
                            </span>
                        </div>
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="streaming-read-timeout-help">
                            Initial backend wait budget to open a WebDAV or /view read (5–120s, default 30).
                            Once bytes start flowing, the per-segment timeout above applies instead.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.streaming-write-timeout-seconds">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="streaming-write-timeout-input">
                            Streaming Write Timeout
                        </label>
                        <div className="flex w-full">
                            <Input
                                className={!isValidStreamingWriteTimeout(config["usenet.streaming-write-timeout-seconds"]) ? "input-error" : undefined}
                                type="text"
                                inputMode="numeric"
                                id="streaming-write-timeout-input"
                                aria-describedby="streaming-write-timeout-help"
                                placeholder="60"
                                value={config["usenet.streaming-write-timeout-seconds"]}
                                onChange={e => setNewConfig({
                                    ...config,
                                    "usenet.streaming-write-timeout-seconds": e.target.value,
                                })} />
                            <span className="flex items-center rounded-r border border-l-0 border-base-content/20 bg-base-200 px-2 text-sm text-base-content/80">
                                sec
                            </span>
                        </div>
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="streaming-write-timeout-help">
                            Per-write deadline for streaming bytes to the client (0–600s, default 60; 0 disables).
                            Cancels a stream whose client stopped reading but kept the connection open, releasing
                            its Article RAM instead of wedging until restart.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.streaming-segment-retries">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="streaming-segment-retries-input">
                            Streaming Segment Retries
                        </label>
                        <Input
                            className={!isValidStreamingSegmentRetries(config["usenet.streaming-segment-retries"]) ? "input-error" : undefined}
                            type="text"
                            inputMode="numeric"
                            id="streaming-segment-retries-input"
                            aria-describedby="streaming-segment-retries-help"
                            placeholder="3"
                            value={config["usenet.streaming-segment-retries"]}
                            onChange={e => setNewConfig({
                                ...config,
                                "usenet.streaming-segment-retries": e.target.value,
                            })} />
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="streaming-segment-retries-help">
                            Extra attempts on a fresh connection after a streaming segment timeout (0–5).
                            Queue and health checks are unaffected.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.article-buffer-size">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="article-buffer-size-input">
                            Article Buffer Size
                        </label>
                        <Input
                            {...className(["w-full", !isValidArticleBufferSize(config["usenet.article-buffer-size"]) && "input-error"])}
                            type="text"
                            inputMode="numeric"
                            id="article-buffer-size-input"
                            aria-describedby="article-buffer-size-help"
                            placeholder="40"
                            value={config["usenet.article-buffer-size"]}
                            onChange={e => setNewConfig({ ...config, "usenet.article-buffer-size": e.target.value })} />
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="article-buffer-size-help">
                            Articles buffered ahead per stream. Host-wide decoded-byte retention is capped
                            separately by the in-flight article budget.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.in-flight-article-budget-mb">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="in-flight-article-budget-input">
                            In-flight article budget (MiB)
                        </label>
                        <Input
                            {...className(["w-full", !isValidInFlightArticleBudget(config["usenet.in-flight-article-budget-mb"]) && "input-error"])}
                            type="text"
                            inputMode="numeric"
                            id="in-flight-article-budget-input"
                            aria-describedby="in-flight-article-budget-help"
                            placeholder="auto"
                            value={config["usenet.in-flight-article-budget-mb"] ?? ""}
                            onChange={e => setNewConfig({
                                ...config,
                                "usenet.in-flight-article-budget-mb": e.target.value,
                            })} />
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="in-flight-article-budget-help">
                            Host-wide cap on decoded article bytes retained across concurrent WebDAV streams
                            (64–8192 MiB). Leave empty for an automatic budget based on container memory.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.idle-connection-timeout-seconds">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="idle-connection-timeout-input">
                            Idle connection timeout (seconds)
                        </label>
                        <Input
                            {...className(["w-full", !isValidIdleConnectionTimeout(config["usenet.idle-connection-timeout-seconds"]) && "input-error"])}
                            type="text"
                            inputMode="numeric"
                            id="idle-connection-timeout-input"
                            aria-describedby="idle-connection-timeout-help"
                            placeholder="60"
                            value={config["usenet.idle-connection-timeout-seconds"] ?? "60"}
                            onChange={e => setNewConfig({
                                ...config,
                                "usenet.idle-connection-timeout-seconds": e.target.value,
                            })} />
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="idle-connection-timeout-help">
                            How long unused NNTP connections remain open (15–300s, default 60). Takes effect
                            after the next connection-pool rebuild or restart.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.pipelined-body-requests">
                    <Tooltip content="Fetch articles in small NNTP batches for smoother WebDAV streaming. Queue imports use the separate NNTP pipelining toggle under Usenet settings.">
                        <Toggle
                            id="pipelined-body-requests-checkbox"
                            className="cursor-pointer gap-2 p-0"
                            checked={config["usenet.pipelined-body-requests"] === "true"}
                            onChange={e => setNewConfig({
                                ...config,
                                "usenet.pipelined-body-requests": String(e.target.checked),
                            })}
                            label={<span className="text-sm text-base-content">Pipelined article downloads</span>}
                        />
                    </Tooltip>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.container-aware-fill">
                    <div className="space-y-2">
                        <Tooltip content="Experimental. Applies only after all missing or corrupt article fallbacks are exhausted; transient transport failures still abort so the player can retry the range.">
                            <Toggle
                                id="container-aware-fill"
                                className="cursor-pointer gap-2 p-0"
                                checked={config["usenet.container-aware-fill"] === "true"}
                                onChange={e => setNewConfig({
                                    ...config,
                                    "usenet.container-aware-fill": String(e.target.checked),
                                })}
                                label={
                                    <span className="inline-flex items-center gap-2 text-sm text-base-content">
                                        Container-aware gap fill
                                        <Badge className="badge-warning badge-outline badge-xs">Experimental</Badge>
                                    </span>
                                }
                            />
                        </Tooltip>
                        <p className="text-[11px] leading-relaxed text-base-content/45">
                            For permanently missing data in direct MPEG-TS files, emit packet-aligned null
                            packets instead of raw zeros so compatible players can resynchronize sooner.
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
    return config["usenet.max-download-connections"] !== newConfig["usenet.max-download-connections"]
        || config["usenet.max-download-connections-per-stream"] !== newConfig["usenet.max-download-connections-per-stream"]
        || config["usenet.max-download-connections-per-stream-preset"] !== newConfig["usenet.max-download-connections-per-stream-preset"]
        || config["usenet.streaming-priority"] !== newConfig["usenet.streaming-priority"]
        || config["usenet.streaming-segment-timeout-seconds"] !== newConfig["usenet.streaming-segment-timeout-seconds"]
        || config["usenet.streaming-read-timeout-seconds"] !== newConfig["usenet.streaming-read-timeout-seconds"]
        || config["usenet.streaming-write-timeout-seconds"] !== newConfig["usenet.streaming-write-timeout-seconds"]
        || config["usenet.streaming-segment-retries"] !== newConfig["usenet.streaming-segment-retries"]
        || config["usenet.article-buffer-size"] !== newConfig["usenet.article-buffer-size"]
        || config["usenet.in-flight-article-budget-mb"] !== newConfig["usenet.in-flight-article-budget-mb"]
        || config["usenet.idle-connection-timeout-seconds"] !== newConfig["usenet.idle-connection-timeout-seconds"]
        || config["usenet.pipelined-body-requests"] !== newConfig["usenet.pipelined-body-requests"]
        || config["usenet.container-aware-fill"] !== newConfig["usenet.container-aware-fill"]
        || config["usenet.segment-cache.enabled"] !== newConfig["usenet.segment-cache.enabled"]
        || config["usenet.segment-cache.path"] !== newConfig["usenet.segment-cache.path"]
        || config["usenet.segment-cache.max-gb"] !== newConfig["usenet.segment-cache.max-gb"];
}

export function isStreamingSettingsValid(config: Record<string, string>): boolean {
    const segmentCacheValid = config["usenet.segment-cache.enabled"] !== "true"
        || (isValidSegmentCachePath(config["usenet.segment-cache.path"])
            && isPositiveInteger(config["usenet.segment-cache.max-gb"]));
    return isValidMaxDownloadConnections(config["usenet.max-download-connections"])
        && isValidStreamingPriority(config["usenet.streaming-priority"])
        && isValidStreamingSegmentTimeout(config["usenet.streaming-segment-timeout-seconds"])
        && isValidStreamingReadTimeout(config["usenet.streaming-read-timeout-seconds"])
        && isValidStreamingWriteTimeout(config["usenet.streaming-write-timeout-seconds"])
        && isValidStreamingSegmentRetries(config["usenet.streaming-segment-retries"])
        && isValidArticleBufferSize(config["usenet.article-buffer-size"])
        && isValidInFlightArticleBudget(config["usenet.in-flight-article-budget-mb"])
        && isValidIdleConnectionTimeout(config["usenet.idle-connection-timeout-seconds"])
        && segmentCacheValid;
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

function isValidIdleConnectionTimeout(value: string | undefined): boolean {
    if (value == null || value.trim() === "") return true;
    const number = Number(value);
    return Number.isInteger(number) && number >= 15 && number <= 300;
}

function isValidSegmentCachePath(value: string): boolean {
    return value.trim().length > 0;
}
