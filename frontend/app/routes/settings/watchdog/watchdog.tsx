import { Link } from "react-router";
import type { Dispatch, ReactNode, SetStateAction } from "react";
import { Icon, NativeForm as Form, ManagedSetting, SettingsIntro, SettingsPage, Tooltip } from "~/components/ui";

type WatchdogSettingsProps = {
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
    description: ReactNode
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
            <div className="grid grid-cols-1 gap-4 p-4 lg:grid-cols-2">
                {children}
            </div>
        </section>
    );
}

export function WatchdogSettings({ config, setNewConfig }: WatchdogSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const verifyMode = config["play.verify-mode"] ?? "none";
    const enabled = (config["play.watchdog-enabled"] ?? "true") === "true";
    const stallFailoverEnabled = (config["grab.stall-failover-enabled"] ?? "true") === "true";
    const subtitlePreference = (config["play.prefer-subtitles"] ?? "true") === "true";

    const variantsMode = config["variants.mode"] ?? "off";
    const variantsEnabled = variantsMode !== "off";
    const variantsFallback = (config["variants.fallback-on-failure"] ?? "true") === "true";

    return (
        <SettingsPage>
            <SettingsIntro>
                Configure how playback recovers from unavailable or stalled releases, and how alternate
                size variants are retained and selected.
            </SettingsIntro>

            <ManagedSetting configKeys={[
                "play.watchdog-enabled", "play.total-budget-seconds", "play.hedge-delay-seconds",
                "play.max-candidates", "play.max-attempts", "play.verify-mode",
                "play.candidate-negative-cache-minutes", "play.resolution-cache-ttl-hours",
                "play.prefer-subtitles", "grab.stall-failover-enabled",
                "grab.stall-failover-window-seconds", "grab.stall-failover-ceiling-seconds",
                "variants.mode", "variants.tolerance-pct", "variants.max-per-group",
                "variants.replay-strategy", "variants.fallback-on-failure",
                "variants.eviction-strategy", "variants.eviction-active-grace-seconds",
            ]}>
            <div className="flex flex-col gap-4">
            <SettingsCard
                icon="alt_route"
                title="Candidate failover"
                description={
                    <>
                        Try the top-ranked release first, then move to alternatives when it cannot be served
                        fast enough. These controls keep requests from hanging on a dead release.
                    </>
                }
            >

            <Form.Group className="flex flex-col gap-2">
                <Tooltip placement="bottom" content="When off, only the chosen release is tried (legacy). When on, alternatives are tried on failure and in-flight queue items are deduped.">
                    <Form.Check
                        type="switch"
                        id="play-watchdog-enabled"
                        className="cursor-pointer gap-2 p-0"
                        label="Enable failover watchdog"
                        checked={enabled}
                        onChange={e => set("play.watchdog-enabled", String(e.target.checked))} />
                </Tooltip>
                {enabled && (
                    <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                        Live reports appear in the <Link to="/watchdog">Watchdog</Link> tab.
                    </p>
                )}
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Total budget (seconds)</Form.Label>
                <Form.Control
                    className="w-full max-w-md"
                    type="number"
                    min={3}
                    max={180}
                    value={config["play.total-budget-seconds"] ?? "30"}
                    onChange={e => set("play.total-budget-seconds", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    Hard ceiling for a request. Big UHD releases need ~15–30s for the queue to extract
                    file metadata. If exceeded, the client gets a retry-able error; the queue item keeps
                    processing in the background and a re-request resolves it. Default 30.
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Hedge delay (seconds)</Form.Label>
                <Form.Control
                    className="w-full max-w-md"
                    type="number"
                    min={1}
                    max={30}
                    disabled={!enabled}
                    value={config["play.hedge-delay-seconds"] ?? "3"}
                    onChange={e => set("play.hedge-delay-seconds", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    If the primary candidate hasn't passed verification by this many seconds, backup
                    candidates start in parallel. Lower = more eager fallback, slightly higher provider load. Default 3.
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Parallel candidates per batch</Form.Label>
                <Form.Control
                    className="w-full max-w-md"
                    type="number"
                    min={1}
                    max={10}
                    disabled={!enabled}
                    value={config["play.max-candidates"] ?? "3"}
                    onChange={e => set("play.max-candidates", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    How many candidates run at the same time in one round. Higher means faster
                    failover when a candidate fails, but more simultaneous indexer requests — too
                    many in parallel can look like spamming and risk a ban. Default 3.
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Total candidates per request</Form.Label>
                <Form.Control
                    className="w-full max-w-md"
                    type="number"
                    min={1}
                    max={200}
                    disabled={!enabled}
                    value={config["play.max-attempts"] ?? "10"}
                    onChange={e => set("play.max-attempts", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    The most candidates one request will try in total before giving up. With the
                    defaults (3 per batch, 10 total) it tries up to 10 candidates, a few at a time,
                    then stops. Also stops sooner if the total budget runs out. Default 10.
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Verify mode</Form.Label>
                <Form.Select
                    className="w-full max-w-md"
                    disabled={!enabled}
                    value={verifyMode}
                    onChange={e => set("play.verify-mode", e.target.value)}>
                    <option value="stat">stat — STAT first segment (~0.2s; skips candidates flagged dead, recommended)</option>
                    <option value="body">body — strict, downloads first article (~1–2s)</option>
                    <option value="none">none — no pre-check, enqueue right away</option>
                </Form.Select>
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    `none` (the default) skips the pre-check for the fastest start; every candidate is enqueued
                    right away. `stat` is a cheap NNTP check that weeds out dead releases before the queue
                    commits, avoiding a re-fetch of their NZB from the indexer on every request.
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Negative-cache TTL (minutes)</Form.Label>
                <Form.Control
                    className="w-full max-w-md"
                    type="number"
                    min={1}
                    max={1440}
                    disabled={!enabled}
                    value={config["play.candidate-negative-cache-minutes"] ?? "5"}
                    onChange={e => set("play.candidate-negative-cache-minutes", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    How long a recently-failed release is skipped on subsequent requests, so we don't hammer
                    the same dead release (and its indexer) over and over. Default 5.
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Search link lifetime (hours)</Form.Label>
                <Form.Control
                    className="w-full max-w-md"
                    type="number"
                    min={1}
                    max={720}
                    value={config["play.resolution-cache-ttl-hours"] ?? "168"}
                    onChange={e => set("play.resolution-cache-ttl-hours", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    How long play links embedded in search results stay valid. Set at or above your search
                    client's result-cache lifetime (AIOStreams defaults to 7 days). Links now also survive
                    restarts. Default 168 (7 days).
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Tooltip content="On failover, try subtitle-bearing releases first; when languages are known, prefer matching subtitle language. Candidates are reordered, never dropped. On by default.">
                    <Form.Check
                        type="switch"
                        id="play-prefer-subtitles"
                        className="cursor-pointer gap-2 p-0"
                        label="Prefer releases with subtitles on failover"
                        disabled={!enabled}
                        checked={subtitlePreference}
                        onChange={e => set("play.prefer-subtitles", String(e.target.checked))} />
                </Tooltip>
            </Form.Group>
            </SettingsCard>

            <SettingsCard
                icon="timer_off"
                title="Stall failover"
                description={
                    <>
                        Set aside candidates that stop progressing and try the next one without recording the
                        slow candidate as failed. Requires the failover watchdog.
                    </>
                }
            >

            <Form.Group className="flex flex-col gap-2">
                <Tooltip placement="bottom" content="When a candidate stops progressing, set it aside and try the next one without recording it as failed. On by default.">
                    <Form.Check
                        type="switch"
                        id="grab-stall-failover-enabled"
                        className="cursor-pointer gap-2 p-0"
                        label="Enable stall failover"
                        disabled={!enabled}
                        checked={stallFailoverEnabled}
                        onChange={e => set("grab.stall-failover-enabled", String(e.target.checked))} />
                </Tooltip>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Stall window (seconds)</Form.Label>
                <Form.Control
                    className="w-full max-w-md"
                    type="number"
                    min={2}
                    max={60}
                    disabled={!enabled || !stallFailoverEnabled}
                    value={config["grab.stall-failover-window-seconds"] ?? "2"}
                    onChange={e => set("grab.stall-failover-window-seconds", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    A candidate that has started but reported no progress for this many seconds is set aside.
                    A candidate that keeps reporting progress is never set aside. Default 2.
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Per-candidate ceiling (seconds)</Form.Label>
                <Form.Control
                    className="w-full max-w-md"
                    type="number"
                    min={5}
                    max={120}
                    disabled={!enabled || !stallFailoverEnabled}
                    value={config["grab.stall-failover-ceiling-seconds"] ?? "5"}
                    onChange={e => set("grab.stall-failover-ceiling-seconds", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    Upper limit on how long a single candidate is given before moving on, regardless of
                    progress — a backstop for a candidate that is queued but not yet started. Default 5.
                </p>
            </Form.Group>
            </SettingsCard>

            <SettingsCard
                icon="content_copy"
                title="Variants"
                description={
                    <>
                        Keep multiple size copies of the same item and serve the copy closest to the size
                        selected on future requests. Off by default.
                    </>
                }
            >

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Mode</Form.Label>
                <Form.Select
                    className="w-full max-w-md"
                    value={variantsMode}
                    onChange={e => set("variants.mode", e.target.value)}>
                    <option value="off">off — always reuse existing, biggest copy (today's behavior)</option>
                    <option value="smart">smart — reuse if size is close enough, else fetch the new variant</option>
                    <option value="collect-all">collect-all — every meaningfully different size adds a new copy</option>
                </Form.Select>
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    `smart` is the recommended default once enabled. `collect-all` adds a new
                    copy for every distinct size you pick (no near-exact match) — usually fine
                    since files are mounted, not stored locally; only the metadata grows.
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Size tolerance (%)</Form.Label>
                <Form.Control
                    className="w-full max-w-md"
                    type="number"
                    min={0}
                    max={100}
                    disabled={variantsMode !== "smart"}
                    value={config["variants.tolerance-pct"] ?? "25"}
                    onChange={e => set("variants.tolerance-pct", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    `smart` mode only. Existing copy is reused if its size is within ±N% of
                    what you selected. Outside that → fetch the new variant and keep both.
                    Default 25 (generous to absorb indexer-vs-actual size drift).
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Max copies per group</Form.Label>
                <Form.Control
                    className="w-full max-w-md"
                    type="number"
                    min={0}
                    max={50}
                    disabled={!variantsEnabled}
                    value={config["variants.max-per-group"] ?? "3"}
                    onChange={e => set("variants.max-per-group", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    Cap on how many size copies of the same item to keep at once. When the
                    cap is hit, the eviction strategy below decides which to drop. Set to 0
                    for unlimited. Default 3.
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Selection strategy</Form.Label>
                <Form.Select
                    className="w-full max-w-md"
                    disabled={!variantsEnabled}
                    value={config["variants.replay-strategy"] ?? "closest-to-click"}
                    onChange={e => set("variants.replay-strategy", e.target.value)}>
                    <option value="closest-to-click">closest-to-selection — match the size I picked (recommended)</option>
                    <option value="largest">largest — always pick the biggest copy, ignore my selection</option>
                    <option value="smallest">smallest — always pick the smallest copy, ignore my selection</option>
                </Form.Select>
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    When multiple copies exist for the same group, which one to serve.
                    `closest-to-selection` uses what you just picked as the intent signal.
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Tooltip content="If the requested size can't be fetched, serve the closest existing copy instead of an error. On by default.">
                    <Form.Check
                        type="switch"
                        id="variants-fallback-on-failure"
                        className="cursor-pointer gap-2 p-0"
                        label="Fallback to closest existing on fetch failure"
                        disabled={!variantsEnabled}
                        checked={variantsFallback}
                        onChange={e => set("variants.fallback-on-failure", String(e.target.checked))} />
                </Tooltip>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Eviction strategy</Form.Label>
                <Form.Select
                    className="w-full max-w-md"
                    disabled={!variantsEnabled}
                    value={config["variants.eviction-strategy"] ?? "lru"}
                    onChange={e => set("variants.eviction-strategy", e.target.value)}>
                    <option value="lru">lru — least recently used first (recommended)</option>
                    <option value="largest-first">largest-first — drop biggest first, keep small copies</option>
                    <option value="smallest-first">smallest-first — drop smallest first, keep big copies</option>
                    <option value="never">never — never auto-remove; new copies exceed cap and stay</option>
                </Form.Select>
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    Decides which copy is removed when `max copies per group` is hit. LRU is the
                    safe default. `never` means you remove copies manually from the History view.
                </p>
            </Form.Group>

            <Form.Group className="flex flex-col gap-2">
                <Form.Label>Active-use grace (seconds)</Form.Label>
                <Form.Control
                    className="w-full max-w-md"
                    type="number"
                    min={0}
                    max={300}
                    disabled={!variantsEnabled}
                    value={config["variants.eviction-active-grace-seconds"] ?? "60"}
                    onChange={e => set("variants.eviction-active-grace-seconds", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    Eviction skips any copy used within the last N seconds. Safety net so we
                    never remove an item that's still being accessed. Default 60.
                </p>
            </Form.Group>
            </SettingsCard>
            </div>
            </ManagedSetting>
        </SettingsPage>
    );
}

export function isWatchdogSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["play.watchdog-enabled"] !== newConfig["play.watchdog-enabled"]
        || config["play.total-budget-seconds"] !== newConfig["play.total-budget-seconds"]
        || config["play.hedge-delay-seconds"] !== newConfig["play.hedge-delay-seconds"]
        || config["play.max-candidates"] !== newConfig["play.max-candidates"]
        || config["play.max-attempts"] !== newConfig["play.max-attempts"]
        || config["play.verify-mode"] !== newConfig["play.verify-mode"]
        || config["play.candidate-negative-cache-minutes"] !== newConfig["play.candidate-negative-cache-minutes"]
        || config["play.resolution-cache-ttl-hours"] !== newConfig["play.resolution-cache-ttl-hours"]
        || config["play.prefer-subtitles"] !== newConfig["play.prefer-subtitles"]
        || config["grab.stall-failover-enabled"] !== newConfig["grab.stall-failover-enabled"]
        || config["grab.stall-failover-window-seconds"] !== newConfig["grab.stall-failover-window-seconds"]
        || config["grab.stall-failover-ceiling-seconds"] !== newConfig["grab.stall-failover-ceiling-seconds"]
        || config["variants.mode"] !== newConfig["variants.mode"]
        || config["variants.tolerance-pct"] !== newConfig["variants.tolerance-pct"]
        || config["variants.max-per-group"] !== newConfig["variants.max-per-group"]
        || config["variants.replay-strategy"] !== newConfig["variants.replay-strategy"]
        || config["variants.fallback-on-failure"] !== newConfig["variants.fallback-on-failure"]
        || config["variants.eviction-strategy"] !== newConfig["variants.eviction-strategy"]
        || config["variants.eviction-active-grace-seconds"] !== newConfig["variants.eviction-active-grace-seconds"];
}
