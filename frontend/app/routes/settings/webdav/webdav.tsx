import { ManagedSetting, SettingsIntro, SettingsPage } from "~/components/ui";
import { Checkbox, Input, Select, Toggle } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { type Dispatch, type ReactNode, type SetStateAction } from "react";
import { className } from "~/utils/styling";
import { isPositiveInteger } from "../usenet/usenet";

type SabnzbdSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

function SettingsCard({
    icon,
    title,
    description,
    children,
}: {
    icon: string
    title: string
    description: string
    children: ReactNode
}) {
    return (
        <section className="overflow-hidden rounded-lg border border-base-content/10 bg-base-100">
            <div className="flex items-start gap-3 border-b border-base-content/10 p-4">
                <span className="inline-flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
                    <Icon name={icon} className="!text-[20px]" />
                </span>
                <div>
                    <h2 className="text-sm font-semibold text-base-content">{title}</h2>
                    <p className="mt-0.5 text-xs leading-relaxed text-base-content/50">
                        {description}
                    </p>
                </div>
            </div>
            <div className="space-y-4 p-4">
                {children}
            </div>
        </section>
    );
}

export function WebdavSettings({ config, setNewConfig }: SabnzbdSettingsProps) {
    return (
        <SettingsPage>
            <SettingsIntro>
                Configure WebDAV credentials and how playback shares NNTP connections with queue imports.
                Queue connection budgets live here because they compete with streaming for the same provider pool.
            </SettingsIntro>

            <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
                <SettingsCard
                    icon="lock"
                    title="Access"
                    description="Credentials used by rclone, Plex, and other WebDAV clients."
                >
                    <ManagedSetting configKey="webdav.user">
                        <div className="space-y-2">
                            <label className="block text-sm font-medium text-base-content" htmlFor="webdav-user-input">WebDAV User</label>
                            <Input
                                {...className(['w-full', !isValidUser(config["webdav.user"]) && 'input-error'])}
                                type="text"
                                id="webdav-user-input"
                                aria-describedby="webdav-user-help"
                                placeholder="admin"
                                value={config["webdav.user"]}
                                onChange={e => setNewConfig({ ...config, "webdav.user": e.target.value })} />
                            <p className="text-[11px] leading-relaxed text-base-content/45" id="webdav-user-help">
                                Use this user to connect to the webdav. Only letters, numbers, dashes, and underscores allowed.
                            </p>
                        </div>
                    </ManagedSetting>

                    <ManagedSetting configKey="webdav.pass">
                        <div className="space-y-2">
                            <label className="block text-sm font-medium text-base-content" htmlFor="webdav-pass-input">WebDAV Password</label>
                            <Input
                                className={'w-full'}
                                type="password"
                                id="webdav-pass-input"
                                aria-describedby="webdav-pass-help"
                                value={config["webdav.pass"]}
                                onChange={e => setNewConfig({ ...config, "webdav.pass": e.target.value })} />
                            <p className="text-[11px] leading-relaxed text-base-content/45" id="webdav-pass-help">
                                Use this password to connect to the webdav.
                            </p>
                        </div>
                    </ManagedSetting>
                </SettingsCard>

                <SettingsCard
                    icon="folder_shared"
                    title="Filesystem & Explorer"
                    description="How content appears to WebDAV clients and the Dav Explorer."
                >
                    <ManagedSetting configKey="webdav.enforce-readonly">
                        <div className="space-y-2">
                            <label className="flex items-center gap-2 text-sm text-base-content/80">
                                <Checkbox
                                    id="readonly-checkbox"
                                    aria-describedby="readonly-help"
                                    checked={config["webdav.enforce-readonly"] === "true"}
                                    onChange={e => setNewConfig({ ...config, "webdav.enforce-readonly": "" + e.target.checked })} />
                                <span>{`Enforce Read-Only`}</span>
                            </label>
                            <p className="text-[11px] leading-relaxed text-base-content/45" id="readonly-help">
                                The WebDAV `/content` folder will be readonly when checked. WebDAV clients will not be able to delete files within this directory.
                            </p>
                        </div>
                    </ManagedSetting>

                    <ManagedSetting configKey="webdav.windows-safe-paths">
                        <div className="space-y-2">
                            <label className="flex items-center gap-2 text-sm text-base-content/80">
                                <Checkbox
                                    id="windows-safe-paths-checkbox"
                                    aria-describedby="windows-safe-paths-help"
                                    checked={config["webdav.windows-safe-paths"] !== "false"}
                                    onChange={e => setNewConfig({ ...config, "webdav.windows-safe-paths": String(e.target.checked) })} />
                                <span>Sanitize paths for Windows</span>
                            </label>
                            <p className="text-[11px] leading-relaxed text-base-content/45" id="windows-safe-paths-help">
                                Replace characters that are invalid on Windows (<code>{`<>:"/\\|?*`}</code>), trim trailing
                                dots/spaces, and prefix reserved device names. Recommended when using Windows WebDAV
                                clients or rclone on Windows. Applies to newly mounted content only.
                            </p>
                        </div>
                    </ManagedSetting>

                    <ManagedSetting configKey="webdav.show-hidden-files">
                        <div className="space-y-2">
                            <label className="flex items-center gap-2 text-sm text-base-content/80">
                                <Checkbox
                                    id="show-hidden-files-checkbox"
                                    aria-describedby="show-hidden-files-help"
                                    checked={config["webdav.show-hidden-files"] === "true"}
                                    onChange={e => setNewConfig({ ...config, "webdav.show-hidden-files": "" + e.target.checked })} />
                                <span>{`Show hidden files on Dav Explorer`}</span>
                            </label>
                            <p className="text-[11px] leading-relaxed text-base-content/45" id="show-hidden-files-help">
                                Hidden files or directories are those whose names are prefixed by a period.
                            </p>
                        </div>
                    </ManagedSetting>

                    <ManagedSetting configKey="webdav.preview-par2-files">
                        <div className="space-y-2">
                            <label className="flex items-center gap-2 text-sm text-base-content/80">
                                <Checkbox
                                    id="preview-par2-files-checkbox"
                                    aria-describedby="preview-par2-files-help"
                                    checked={config["webdav.preview-par2-files"] === "true"}
                                    onChange={e => setNewConfig({ ...config, "webdav.preview-par2-files": "" + e.target.checked })} />
                                <span>{`Preview par2 files on Dav Explorer`}</span>
                            </label>
                            <p className="text-[11px] leading-relaxed text-base-content/45" id="preview-par2-files-help">
                                When enabled, par2 files will be rendered as text files on the Dav Explorer page, displaying all File-Descriptor entries.
                            </p>
                        </div>
                    </ManagedSetting>
                </SettingsCard>
            </div>

            <SettingsCard
                icon="tune"
                title="Queue & connection allocation"
                description="Split provider connections between queue imports and WebDAV playback."
            >
                <ManagedSetting configKey="usenet.max-queue-connections">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="max-queue-connections-input">Queue Download Connections</label>
                        <Input
                            {...className(['w-full', !isValidMaxQueueConnections(config["usenet.max-queue-connections"]) && 'input-error'])}
                            type="text"
                            id="max-queue-connections-input"
                            aria-describedby="max-queue-connections-help"
                            placeholder="Auto (all connections)"
                            value={config["usenet.max-queue-connections"]}
                            onChange={e => setNewConfig({ ...config, "usenet.max-queue-connections": e.target.value })} />
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="max-queue-connections-help">
                            Connections available to queue imports. Leave blank to use all provider connections.
                            Shared across concurrent queue workers and background health checks.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="queue.worker-count">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="queue-worker-count-select">Concurrent Queue Downloads</label>
                        <Select
                            className="w-full"
                            id="queue-worker-count-select"
                            aria-describedby="queue-worker-count-help"
                            value={config["queue.worker-count"] || "1"}
                            onChange={e => setNewConfig({ ...config, "queue.worker-count": e.target.value })}>
                            <option value="1">1 — one at a time (default)</option>
                            <option value="2">2</option>
                            <option value="3">3</option>
                            <option value="4">4</option>
                        </Select>
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="queue-worker-count-help">
                            How many NZB queue items may process at once. The first active item
                            gets preferred access to Queue Download Connections; additional items use spare capacity.
                            Raising this does not increase the connection budget.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.max-download-connections">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="max-download-connections-auto-checkbox">Max Download Connections</label>
                        <Toggle
                            id="max-download-connections-auto-checkbox"
                            aria-describedby="max-download-connections-help"
                            label="Auto — use all Pool provider connections"
                            checked={isAutoMaxDownloadConnections(config["usenet.max-download-connections"])}
                            onChange={e => setNewConfig({ ...config, "usenet.max-download-connections": e.target.checked ? "0" : "15" })} />
                        {!isAutoMaxDownloadConnections(config["usenet.max-download-connections"]) && (
                            <Input
                                {...className(['w-full', !isValidMaxDownloadConnections(config["usenet.max-download-connections"]) && 'input-error'])}
                                type="text"
                                id="max-download-connections-input"
                                aria-describedby="max-download-connections-help"
                                placeholder="15"
                                value={config["usenet.max-download-connections"]}
                                onChange={e => setNewConfig({ ...config, "usenet.max-download-connections": e.target.value })} />
                        )}
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="max-download-connections-help">
                            The total connections used for <strong>webdav streaming</strong> (playback). Leave on
                            <strong> Auto</strong> to use the combined connection limit of your Pool providers — it
                            tracks changes as you add or remove providers — or turn Auto off to set a fixed number.
                            Queue imports use their own budget — see Queue Download Connections above.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKeys={["usenet.max-download-connections-per-stream", "usenet.max-download-connections-per-stream-preset"]}>
                    <div className="space-y-2">
                        <Toggle
                            id="max-download-connections-per-stream-checkbox"
                            aria-describedby="max-download-connections-per-stream-help"
                            label="Apply limit per stream"
                            checked={config["usenet.max-download-connections-per-stream"] === "true"}
                            onChange={e => setNewConfig({ ...config, "usenet.max-download-connections-per-stream": String(e.target.checked) })} />
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="max-download-connections-per-stream-help">
                            By default the budget above is a <strong>shared total</strong> across all active playback
                            streams. Enable this to give each concurrent stream <strong>its own budget</strong> instead,
                            sized by the performance preset below. Your provider&apos;s connection limit still applies as a
                            hard ceiling on the total connections actually opened.
                        </p>
                        {config["usenet.max-download-connections-per-stream"] === "true" && (
                            <div className="space-y-2 border-l border-base-content/10 pl-4">
                                <label className="block text-sm font-medium text-base-content" htmlFor="max-download-connections-per-stream-preset-select">Per-stream performance</label>
                                <Select
                                    className="w-full"
                                    id="max-download-connections-per-stream-preset-select"
                                    aria-describedby="max-download-connections-per-stream-preset-help"
                                    value={config["usenet.max-download-connections-per-stream-preset"] || "high"}
                                    onChange={e => setNewConfig({ ...config, "usenet.max-download-connections-per-stream-preset": e.target.value })}>
                                    <option value="low">Low — 25% of the budget per stream</option>
                                    <option value="medium">Medium — 50% of the budget per stream</option>
                                    <option value="high">High — 75% of the budget per stream</option>
                                    <option value="max">Max — 100% (full budget per stream)</option>
                                </Select>
                                <p className="text-[11px] leading-relaxed text-base-content/45" id="max-download-connections-per-stream-preset-help">
                                    How aggressively each stream may use the budget above. Higher fills and seeks faster
                                    per stream; lower keeps more connections free for other simultaneous streams.
                                </p>
                            </div>
                        )}
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.streaming-priority">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="streaming-priority-input">Streaming Priority (vs Queue)</label>
                        <div className="flex w-full">
                            <Input
                                className={!isValidStreamingPriority(config["usenet.streaming-priority"]) ? 'input-error' : undefined}
                                type="text"
                                id="streaming-priority-input"
                                aria-describedby="streaming-priority-help"
                                placeholder="80"
                                value={config["usenet.streaming-priority"]}
                                onChange={e => setNewConfig({ ...config, "usenet.streaming-priority": e.target.value })} />
                            <span className="flex items-center rounded-r border border-l-0 border-base-content/20 bg-base-200 px-2 text-sm text-base-content/80">%</span>
                        </div>
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="streaming-priority-help">
                            When streaming from the webdav while the queue is also active, how much bandwidth should be dedicated to streaming?
                        </p>
                    </div>
                </ManagedSetting>
            </SettingsCard>

            <SettingsCard
                icon="speed"
                title="Streaming performance"
                description="Buffering, caching, and timeout behavior for WebDAV playback."
            >
                <ManagedSetting configKeys={["usenet.segment-cache.enabled", "usenet.segment-cache.path", "usenet.segment-cache.max-gb"]}>
                    <div className="space-y-2">
                        <label className="flex items-center gap-2 text-sm text-base-content/80">
                            <Checkbox
                                id="segment-cache-enabled-checkbox"
                                aria-describedby="segment-cache-enabled-help"
                                checked={config["usenet.segment-cache.enabled"] === "true"}
                                onChange={e => setNewConfig({ ...config, "usenet.segment-cache.enabled": String(e.target.checked) })} />
                            <span>Enable Segment Cache</span>
                        </label>
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="segment-cache-enabled-help">
                            Cache decoded segments on disk so repeat reads and seeks avoid provider traffic. Takes effect after restart.
                        </p>
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
                        <label className="block text-sm font-medium text-base-content" htmlFor="streaming-segment-timeout-input">Streaming Segment Timeout</label>
                        <div className="flex w-full">
                            <Input
                                className={!isValidStreamingSegmentTimeout(config["usenet.streaming-segment-timeout-seconds"]) ? 'input-error' : undefined}
                                type="text"
                                id="streaming-segment-timeout-input"
                                aria-describedby="streaming-segment-timeout-help"
                                placeholder="8"
                                value={config["usenet.streaming-segment-timeout-seconds"]}
                                onChange={e => setNewConfig({ ...config, "usenet.streaming-segment-timeout-seconds": e.target.value })} />
                            <span className="flex items-center rounded-r border border-l-0 border-base-content/20 bg-base-200 px-2 text-sm text-base-content/80">sec</span>
                        </div>
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="streaming-segment-timeout-help">
                            Per-segment deadline for WebDAV playback (2–40s). Stalled connections are replaced and retried before waiting for the provider&apos;s ~40s read timeout.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.streaming-segment-retries">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="streaming-segment-retries-input">Streaming Segment Retries</label>
                        <Input
                            className={!isValidStreamingSegmentRetries(config["usenet.streaming-segment-retries"]) ? 'input-error' : undefined}
                            type="text"
                            id="streaming-segment-retries-input"
                            aria-describedby="streaming-segment-retries-help"
                            placeholder="3"
                            value={config["usenet.streaming-segment-retries"]}
                            onChange={e => setNewConfig({ ...config, "usenet.streaming-segment-retries": e.target.value })} />
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="streaming-segment-retries-help">
                            Extra attempts on a fresh connection after a streaming segment timeout (0–5). Queue and health checks are unaffected.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.article-buffer-size">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="article-buffer-size-input">Article Buffer Size</label>
                        <Input
                            {...className(['w-full', !isValidArticleBufferSize(config["usenet.article-buffer-size"]) && 'input-error'])}
                            type="text"
                            id="article-buffer-size-input"
                            aria-describedby="article-buffer-size-help"
                            placeholder="40"
                            value={config["usenet.article-buffer-size"]}
                            onChange={e => setNewConfig({ ...config, "usenet.article-buffer-size": e.target.value })} />
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="article-buffer-size-help">
                            The number of articles to buffer ahead, per stream, when reading from the webdav.
                            Host-wide decoded-byte retention is capped separately by In-flight article budget.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.in-flight-article-budget-mb">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="in-flight-article-budget-input">In-flight article budget (MiB)</label>
                        <Input
                            {...className(['w-full', !isValidInFlightArticleBudget(config["usenet.in-flight-article-budget-mb"]) && 'input-error'])}
                            type="text"
                            id="in-flight-article-budget-input"
                            aria-describedby="in-flight-article-budget-help"
                            placeholder="512"
                            value={config["usenet.in-flight-article-budget-mb"] ?? "512"}
                            onChange={e => setNewConfig({ ...config, "usenet.in-flight-article-budget-mb": e.target.value })} />
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="in-flight-article-budget-help">
                            Host-wide cap on decoded article bytes retained in RAM across concurrent WebDAV streams (64–8192, default 512).
                            Prevents unbounded prefetch under heavy Arr/rclone load from OOM-killing the container.
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.idle-connection-timeout-seconds">
                    <div className="space-y-2">
                        <label className="block text-sm font-medium text-base-content" htmlFor="idle-connection-timeout-input">Idle connection timeout (seconds)</label>
                        <Input
                            {...className(['w-full', !isValidIdleConnectionTimeout(config["usenet.idle-connection-timeout-seconds"]) && 'input-error'])}
                            type="text"
                            id="idle-connection-timeout-input"
                            aria-describedby="idle-connection-timeout-help"
                            placeholder="60"
                            value={config["usenet.idle-connection-timeout-seconds"] ?? "60"}
                            onChange={e => setNewConfig({ ...config, "usenet.idle-connection-timeout-seconds": e.target.value })} />
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="idle-connection-timeout-help">
                            How long unused NNTP connections stay in the pool before being closed (15–300, default 60).
                            Raising this can reduce reconnect stalls during playback gaps, but values above your
                            provider&apos;s server-side idle timeout are counterproductive. Takes effect on the next
                            connection-pool rebuild (provider config change or restart).
                        </p>
                    </div>
                </ManagedSetting>

                <ManagedSetting configKey="usenet.pipelined-body-requests">
                    <div className="space-y-2">
                        <label className="flex items-center gap-2 text-sm text-base-content/80">
                            <Checkbox
                                id="pipelined-body-requests-checkbox"
                                aria-describedby="pipelined-body-requests-help"
                                checked={config["usenet.pipelined-body-requests"] === "true"}
                                onChange={e => setNewConfig({ ...config, "usenet.pipelined-body-requests": "" + e.target.checked })} />
                            <span>Pipelined article downloads</span>
                        </label>
                        <p className="text-[11px] leading-relaxed text-base-content/45" id="pipelined-body-requests-help">
                            Fetch articles in small NNTP batches for smoother WebDAV streaming. Queue imports use the
                            separate <strong>Enable NNTP pipelining</strong> toggle under Usenet settings. Disable this
                            to use the legacy one-at-a-time API while retaining the configured article buffer.
                        </p>
                    </div>
                </ManagedSetting>
            </SettingsCard>
        </SettingsPage>
    );
}

export function isWebdavSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["webdav.user"] !== newConfig["webdav.user"]
        || config["webdav.pass"] !== newConfig["webdav.pass"]
        || config["usenet.max-download-connections"] !== newConfig["usenet.max-download-connections"]
        || config["usenet.max-download-connections-per-stream"] !== newConfig["usenet.max-download-connections-per-stream"]
        || config["usenet.max-download-connections-per-stream-preset"] !== newConfig["usenet.max-download-connections-per-stream-preset"]
        || config["usenet.max-queue-connections"] !== newConfig["usenet.max-queue-connections"]
        || config["queue.worker-count"] !== newConfig["queue.worker-count"]
        || config["usenet.streaming-priority"] !== newConfig["usenet.streaming-priority"]
        || config["usenet.streaming-segment-timeout-seconds"] !== newConfig["usenet.streaming-segment-timeout-seconds"]
        || config["usenet.streaming-segment-retries"] !== newConfig["usenet.streaming-segment-retries"]
        || config["usenet.article-buffer-size"] !== newConfig["usenet.article-buffer-size"]
        || config["usenet.in-flight-article-budget-mb"] !== newConfig["usenet.in-flight-article-budget-mb"]
        || config["usenet.idle-connection-timeout-seconds"] !== newConfig["usenet.idle-connection-timeout-seconds"]
        || config["usenet.pipelined-body-requests"] !== newConfig["usenet.pipelined-body-requests"]
        || config["webdav.show-hidden-files"] !== newConfig["webdav.show-hidden-files"]
        || config["webdav.enforce-readonly"] !== newConfig["webdav.enforce-readonly"]
        || config["webdav.preview-par2-files"] !== newConfig["webdav.preview-par2-files"]
        || config["webdav.windows-safe-paths"] !== newConfig["webdav.windows-safe-paths"]
        || config["usenet.segment-cache.enabled"] !== newConfig["usenet.segment-cache.enabled"]
        || config["usenet.segment-cache.path"] !== newConfig["usenet.segment-cache.path"]
        || config["usenet.segment-cache.max-gb"] !== newConfig["usenet.segment-cache.max-gb"]
}

export function isWebdavSettingsValid(newConfig: Record<string, string>) {
    const segmentCacheValid = newConfig["usenet.segment-cache.enabled"] !== "true"
        || (isValidSegmentCachePath(newConfig["usenet.segment-cache.path"])
            && isPositiveInteger(newConfig["usenet.segment-cache.max-gb"]));
    return isValidUser(newConfig["webdav.user"])
        && isValidMaxDownloadConnections(newConfig["usenet.max-download-connections"])
        && isValidMaxQueueConnections(newConfig["usenet.max-queue-connections"])
        && isValidQueueWorkerCount(newConfig["queue.worker-count"])
        && isValidStreamingPriority(newConfig["usenet.streaming-priority"])
        && isValidStreamingSegmentTimeout(newConfig["usenet.streaming-segment-timeout-seconds"])
        && isValidStreamingSegmentRetries(newConfig["usenet.streaming-segment-retries"])
        && isValidArticleBufferSize(newConfig["usenet.article-buffer-size"])
        && isValidInFlightArticleBudget(newConfig["usenet.in-flight-article-budget-mb"])
        && isValidIdleConnectionTimeout(newConfig["usenet.idle-connection-timeout-seconds"])
        && segmentCacheValid;
}

function isValidSegmentCachePath(value: string): boolean {
    return value.trim().length > 0;
}

function isValidUser(user: string): boolean {
    const regex = /^[A-Za-z0-9_-]+$/;
    return regex.test(user);
}

function isAutoMaxDownloadConnections(value: string | undefined): boolean {
    return !value || value.trim() === "" || value.trim() === "0";
}

function isValidMaxDownloadConnections(value: string | undefined): boolean {
    return isAutoMaxDownloadConnections(value) || isPositiveInteger(value ?? "");
}

function isValidMaxQueueConnections(value: string): boolean {
    return value.trim() === "" || isPositiveInteger(value);
}

function isValidQueueWorkerCount(value: string | undefined): boolean {
    if (value == null || value.trim() === "") return true;
    const num = Number(value);
    return Number.isInteger(num) && num >= 1 && num <= 4;
}

function isValidStreamingPriority(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= 100;
}

function isValidStreamingSegmentTimeout(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 2 && num <= 40;
}

function isValidStreamingSegmentRetries(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= 5;
}

function isValidArticleBufferSize(value: string): boolean {
    return isPositiveInteger(value);
}

function isValidInFlightArticleBudget(value: string | undefined): boolean {
    if (value == null || value.trim() === "") return true;
    const num = Number(value);
    return Number.isInteger(num) && num >= 64 && num <= 8192;
}

function isValidIdleConnectionTimeout(value: string | undefined): boolean {
    if (value == null || value.trim() === "") return true;
    const num = Number(value);
    return Number.isInteger(num) && num >= 15 && num <= 300;
}
