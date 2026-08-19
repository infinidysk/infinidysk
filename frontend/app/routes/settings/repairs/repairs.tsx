import { ManagedSetting, SettingsCard, SettingsIntro, SettingsPage, Tooltip } from "~/components/ui";
import { Input, Select, Toggle } from "~/components/ui/form";
import { type Dispatch, type SetStateAction } from "react";
import { isPositiveInteger } from "../validation";

type RepairsSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

function isNonNegativeInteger(value: string) {
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && value.trim() === num.toString();
}

export function RepairsSettings({ config, setNewConfig }: RepairsSettingsProps) {
    const libraryDirConfig = config["media.library-dir"];
    // `arr.instances` config value shape (backend contract)
    const arrConfig = JSON.parse(config["arr.instances"]!) as { RadarrInstances: unknown[]; SonarrInstances: unknown[] };
    const areArrInstancesConfigured =
        arrConfig.RadarrInstances.length > 0 ||
        arrConfig.SonarrInstances.length > 0;
    const canEnableRepairs = !!libraryDirConfig && areArrInstancesConfigured;
    const helpText = canEnableRepairs
        ? "When enabled, usenet items will be continuously monitored for health. Unhealthy items will be removed. If an unhealthy item is part of your Radarr/Sonarr library, a new search will be triggered to find a replacement."
        : "When enabled, usenet items will be continuously monitored for health. Unhealthy items will be removed and replaced. This setting can only be enabled once your Library-Directory and Radarr/Sonarr instances are configured.";
    const isRepairEnabled = canEnableRepairs && config["repair.enable"] === "true";
    const autoRemoveAfter = config["repair.auto-remove-after-failures"] ?? "0";
    const autoRemoveEnabled = isNonNegativeInteger(autoRemoveAfter) && Number(autoRemoveAfter) > 0;

    return (
        <SettingsPage>
            <SettingsIntro>
                Monitor mounted media for missing articles, tune health-check coverage, and control how
                broken files are removed or replaced.
            </SettingsIntro>

            <div className="flex flex-col gap-4">
            <SettingsCard
                icon="build"
                title="Background repairs"
                description="Connect repair monitoring to the organized media library."
                contentClassName="grid grid-cols-1 gap-4 lg:grid-cols-2"
            >
            <ManagedSetting configKey="repair.enable">
            <Tooltip placement="bottom" content={helpText}>
                <Toggle
                    id="enable-repairs-checkbox"
                    className="cursor-pointer gap-2 p-0"
                    checked={canEnableRepairs && config["repair.enable"] === "true"}
                    disabled={!canEnableRepairs}
                    onChange={e => setNewConfig({ ...config, "repair.enable": "" + e.target.checked })}
                    label={<span className="text-sm text-base-content">Enable Background Repairs</span>}
                />
            </Tooltip>
            </ManagedSetting>

            <ManagedSetting configKey="media.library-dir">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="library-dir-input">Library Directory</label>
                <Input
                    className={'w-full'}
                    type="text"
                    id="library-dir-input"
                    aria-describedby="library-dir-help"
                    value={config["media.library-dir"]}
                    onChange={e => setNewConfig({ ...config, "media.library-dir": e.target.value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45" id="library-dir-help">
                    The path to your organized media library that contains all your imported symlinks or *.strm files.
                    Make sure this path is visible to your InfiniDysk container.
                </p>
            </div>
            </ManagedSetting>
            </SettingsCard>

            <SettingsCard
                icon="monitor_heart"
                title="Health checks"
                description="Balance verification coverage against provider connection pressure."
            >
            <ManagedSetting configKey="repair.healthcheck-concurrency">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="healthcheck-concurrency-input">Health Check Concurrency</label>
                <Input
                    className={`w-full ${!isPositiveInteger(config["repair.healthcheck-concurrency"] || "50") ? "input-error" : ""}`}
                    type="text"
                    id="healthcheck-concurrency-input"
                    aria-describedby="healthcheck-concurrency-help"
                    placeholder="50"
                    value={config["repair.healthcheck-concurrency"] ?? ""}
                    onChange={e => setNewConfig({ ...config, "repair.healthcheck-concurrency": e.target.value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45" id="healthcheck-concurrency-help">
                    The maximum number of concurrent NNTP connections used for health check STAT commands.
                    Lower values reduce connection pressure on your usenet providers during health checks.
                    Capped at your total provider pool size.
                </p>
            </div>
            </ManagedSetting>
            <ManagedSetting
                configKeys={["repair.healthcheck-depth", "repair.healthcheck-aging"]}
                className="grid grid-cols-1 gap-4 lg:grid-cols-2"
            >
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="healthcheck-depth-input">Health Check Depth</label>
                <Select
                    className="w-full"
                    id="healthcheck-depth-input"
                    aria-describedby="healthcheck-depth-help"
                    value={config["repair.healthcheck-depth"] ?? "standard"}
                    onChange={e => setNewConfig({ ...config, "repair.healthcheck-depth": e.target.value })}
                >
                    <option value="standard">Standard</option>
                    <option value="enhanced">Enhanced</option>
                    <option value="deep">Deep</option>
                    <option value="complete">Complete</option>
                </Select>
                <p className="text-[11px] leading-relaxed text-base-content/45" id="healthcheck-depth-help">
                    How much of each file a health check verifies. Files up to 8000 segments are
                    checked in full, unless the aging option below is turned on. Above that, larger files
                    are sampled from the start, end, and evenly spaced points in between, so a big release
                    costs a bounded number of STAT commands. Deeper settings verify more of each file and
                    use more usenet traffic. Complete checks every segment.
                </p>
            </div>
            <div className="space-y-2">
                <Tooltip content="Off by default. When enabled, coverage tapers for releases past their first year (stops at ten years), useful for large libraries of long-posted content.">
                    <Toggle
                        id="healthcheck-aging-checkbox"
                        className="cursor-pointer gap-2 p-0"
                        checked={(config["repair.healthcheck-aging"] ?? "false") === "true"}
                        onChange={e => setNewConfig({ ...config, "repair.healthcheck-aging": "" + e.target.checked })}
                        label={<span className="text-sm text-base-content">Check older releases less thoroughly</span>}
                    />
                </Tooltip>
            </div>
            </ManagedSetting>
            </SettingsCard>

            <SettingsCard
                icon="delete_sweep"
                title="Streaming failure handling"
                description="Choose when repeated playback failures should trigger repair or removal."
                contentClassName="grid grid-cols-1 gap-4 lg:grid-cols-2"
            >
            {!isRepairEnabled && (
                <p className="text-[11px] leading-relaxed text-base-content/45 lg:col-span-2">
                    Enable Background Repairs above to activate streaming failure handling.
                </p>
            )}
            <ManagedSetting configKey="repair.auto-remove-after-failures">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="auto-remove-after-failures-input">Repair After Streaming Failures</label>
                <Input
                    className={`w-full ${!isNonNegativeInteger(autoRemoveAfter || "0") ? "input-error" : ""}`}
                    type="text"
                    id="auto-remove-after-failures-input"
                    aria-describedby="auto-remove-after-failures-help"
                    placeholder="0"
                    value={autoRemoveAfter}
                    disabled={!isRepairEnabled}
                    onChange={e => setNewConfig({ ...config, "repair.auto-remove-after-failures": e.target.value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45" id="auto-remove-after-failures-help">
                    Wait for this many consecutive streaming playback failures before urgent repair starts. Linked library
                    items are removed and blocklisted through Radarr/Sonarr, which then applies its failed-download
                    redownload policy. Unlinked items are removed. Set to 0 for immediate repair (default).
                </p>
            </div>
            </ManagedSetting>
            <ManagedSetting configKey="repair.auto-remove-unlinked-only">
            <Tooltip content="When enabled (default), library-linked releases are removed and blocklisted through Radarr/Sonarr. Disable to force-delete linked files after the failure threshold.">
                <Toggle
                    id="auto-remove-unlinked-only-checkbox"
                    className="cursor-pointer gap-2 p-0"
                    checked={(config["repair.auto-remove-unlinked-only"] ?? "true") === "true"}
                    disabled={!isRepairEnabled || !autoRemoveEnabled}
                    onChange={e => setNewConfig({ ...config, "repair.auto-remove-unlinked-only": "" + e.target.checked })}
                    label={<span className="text-sm text-base-content">Auto-remove unlinked files only</span>}
                />
            </Tooltip>
            </ManagedSetting>
            </SettingsCard>

            <SettingsCard
                icon="healing"
                title="PAR2 gap repair"
                description="Reconstruct missing segments from parity volumes in the background instead of triggering an immediate Arr replacement."
                contentClassName="grid grid-cols-1 gap-4 lg:grid-cols-2"
            >
            {!isRepairEnabled && (
                <p className="text-[11px] leading-relaxed text-base-content/45 lg:col-span-2">
                    Enable Background Repairs above to activate PAR2 gap repair.
                </p>
            )}
            <ManagedSetting configKey="repair.par2-enabled">
            <Tooltip content="When enabled, missing segments discovered during streaming or health checks are reconstructed from PAR2 recovery data when feasible. Defaults to off because repairs read the full recovery set once.">
                <Toggle
                    id="par2-repair-enabled-checkbox"
                    className="cursor-pointer gap-2 p-0"
                    checked={isRepairEnabled && config["repair.par2-enabled"] === "true"}
                    disabled={!isRepairEnabled}
                    onChange={e => setNewConfig({ ...config, "repair.par2-enabled": "" + e.target.checked })}
                    label={<span className="text-sm text-base-content">Enable PAR2 background repair</span>}
                />
            </Tooltip>
            </ManagedSetting>
            <ManagedSetting configKey="repair.par2-preferred-over-arr">
            <Tooltip content="When enabled (default), try PAR2 reconstruction before removing the release through Radarr/Sonarr.">
                <Toggle
                    id="par2-preferred-over-arr-checkbox"
                    className="cursor-pointer gap-2 p-0"
                    checked={(config["repair.par2-preferred-over-arr"] ?? "true") === "true"}
                    disabled={!isRepairEnabled || config["repair.par2-enabled"] !== "true"}
                    onChange={e => setNewConfig({ ...config, "repair.par2-preferred-over-arr": "" + e.target.checked })}
                    label={<span className="text-sm text-base-content">Prefer PAR2 over Arr replacement</span>}
                />
            </Tooltip>
            </ManagedSetting>
            <ManagedSetting
                configKeys={[
                    "repair.par2-max-missing-slices",
                    "repair.par2-max-release-gb",
                    "repair.par2-max-memory-mb",
                    "repair.par2-max-patch-gb",
                    "repair.par2-fetch-concurrency",
                    "repair.par2-failure-cooldown-hours",
                ]}
                className="grid grid-cols-1 gap-4 lg:col-span-2 lg:grid-cols-2"
            >
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="par2-max-missing-slices-input">Max missing slices</label>
                <Input
                    className={`w-full ${!isPositiveInteger(config["repair.par2-max-missing-slices"] ?? "8") ? "input-error" : ""}`}
                    type="text"
                    id="par2-max-missing-slices-input"
                    placeholder="8"
                    disabled={!isRepairEnabled || config["repair.par2-enabled"] !== "true"}
                    value={config["repair.par2-max-missing-slices"] ?? ""}
                    onChange={e => setNewConfig({ ...config, "repair.par2-max-missing-slices": e.target.value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45">
                    Maximum number of missing PAR2 slices to reconstruct in one job (1–64).
                </p>
            </div>
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="par2-max-release-gb-input">Max release size (GB)</label>
                <Input
                    className={`w-full ${!isPositiveInteger(config["repair.par2-max-release-gb"] ?? "16") ? "input-error" : ""}`}
                    type="text"
                    id="par2-max-release-gb-input"
                    placeholder="16"
                    disabled={!isRepairEnabled || config["repair.par2-enabled"] !== "true"}
                    value={config["repair.par2-max-release-gb"] ?? ""}
                    onChange={e => setNewConfig({ ...config, "repair.par2-max-release-gb": e.target.value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45">
                    Refuse PAR2 repair when the recovery set exceeds this size. A repair reads the full recovery set once.
                </p>
            </div>
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="par2-max-memory-mb-input">Max memory (MB)</label>
                <Input
                    className={`w-full ${!isPositiveInteger(config["repair.par2-max-memory-mb"] ?? "256") ? "input-error" : ""}`}
                    type="text"
                    id="par2-max-memory-mb-input"
                    placeholder="256"
                    disabled={!isRepairEnabled || config["repair.par2-enabled"] !== "true"}
                    value={config["repair.par2-max-memory-mb"] ?? ""}
                    onChange={e => setNewConfig({ ...config, "repair.par2-max-memory-mb": e.target.value })} />
            </div>
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="par2-max-patch-gb-input">Patch store cap (GB)</label>
                <Input
                    className={`w-full ${!isPositiveInteger(config["repair.par2-max-patch-gb"] ?? "4") ? "input-error" : ""}`}
                    type="text"
                    id="par2-max-patch-gb-input"
                    placeholder="4"
                    disabled={!isRepairEnabled || config["repair.par2-enabled"] !== "true"}
                    value={config["repair.par2-max-patch-gb"] ?? ""}
                    onChange={e => setNewConfig({ ...config, "repair.par2-max-patch-gb": e.target.value })} />
            </div>
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="par2-fetch-concurrency-input">Fetch concurrency</label>
                <Input
                    className={`w-full ${!isPositiveInteger(config["repair.par2-fetch-concurrency"] ?? "2") ? "input-error" : ""}`}
                    type="text"
                    id="par2-fetch-concurrency-input"
                    placeholder="2"
                    disabled={!isRepairEnabled || config["repair.par2-enabled"] !== "true"}
                    value={config["repair.par2-fetch-concurrency"] ?? ""}
                    onChange={e => setNewConfig({ ...config, "repair.par2-fetch-concurrency": e.target.value })} />
            </div>
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="par2-failure-cooldown-hours-input">Failure cooldown (hours)</label>
                <Input
                    className={`w-full ${!isPositiveInteger(config["repair.par2-failure-cooldown-hours"] ?? "6") ? "input-error" : ""}`}
                    type="text"
                    id="par2-failure-cooldown-hours-input"
                    placeholder="6"
                    disabled={!isRepairEnabled || config["repair.par2-enabled"] !== "true"}
                    value={config["repair.par2-failure-cooldown-hours"] ?? ""}
                    onChange={e => setNewConfig({ ...config, "repair.par2-failure-cooldown-hours": e.target.value })} />
            </div>
            </ManagedSetting>
            </SettingsCard>
            </div>
        </SettingsPage>
    );
}

export function isRepairsSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["repair.enable"] !== newConfig["repair.enable"]
        || config["repair.healthcheck-concurrency"] !== newConfig["repair.healthcheck-concurrency"]
        || config["repair.healthcheck-depth"] !== newConfig["repair.healthcheck-depth"]
        || config["repair.healthcheck-aging"] !== newConfig["repair.healthcheck-aging"]
        || config["repair.auto-remove-after-failures"] !== newConfig["repair.auto-remove-after-failures"]
        || config["repair.auto-remove-unlinked-only"] !== newConfig["repair.auto-remove-unlinked-only"]
        || config["repair.par2-enabled"] !== newConfig["repair.par2-enabled"]
        || config["repair.par2-preferred-over-arr"] !== newConfig["repair.par2-preferred-over-arr"]
        || config["repair.par2-max-missing-slices"] !== newConfig["repair.par2-max-missing-slices"]
        || config["repair.par2-max-release-gb"] !== newConfig["repair.par2-max-release-gb"]
        || config["repair.par2-max-memory-mb"] !== newConfig["repair.par2-max-memory-mb"]
        || config["repair.par2-max-patch-gb"] !== newConfig["repair.par2-max-patch-gb"]
        || config["repair.par2-fetch-concurrency"] !== newConfig["repair.par2-fetch-concurrency"]
        || config["repair.par2-failure-cooldown-hours"] !== newConfig["repair.par2-failure-cooldown-hours"]
        || config["media.library-dir"] !== newConfig["media.library-dir"];
}

export function isRepairsSettingsValid(newConfig: Record<string, string>) {
    const concurrency = newConfig["repair.healthcheck-concurrency"];
    const autoRemove = newConfig["repair.auto-remove-after-failures"];
    const par2NumericKeys = [
        "repair.par2-max-missing-slices",
        "repair.par2-max-release-gb",
        "repair.par2-max-memory-mb",
        "repair.par2-max-patch-gb",
        "repair.par2-fetch-concurrency",
        "repair.par2-failure-cooldown-hours",
    ] as const;
    const concurrencyOk = concurrency === undefined || concurrency === "" || isPositiveInteger(concurrency);
    const autoRemoveOk = autoRemove === undefined || autoRemove === "" || isNonNegativeInteger(autoRemove);
    const par2Ok = par2NumericKeys.every(key => {
        const value = newConfig[key];
        return value === undefined || value === "" || isPositiveInteger(value);
    });
    return concurrencyOk && autoRemoveOk && par2Ok;
}
