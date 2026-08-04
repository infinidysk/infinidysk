import { Button } from "~/components/ui/button";
import { ManagedSetting, SettingsPage, Tooltip } from "~/components/ui";
import { Checkbox, Input, Select, Toggle } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { useCallback, useEffect, useMemo, useRef, type Dispatch, type SetStateAction } from "react";
import { TagInput } from "~/components/tag-input/tag-input";
import { MultiCheckboxInput } from "~/components/multi-checkbox-input/multi-checkbox-input";
import { ExpandingTextInput } from "~/components/expanding-text-input/expanding-text-input";

type SabnzbdSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
    appVersion: string,
};

export function SabnzbdSettings({ config, setNewConfig, appVersion }: SabnzbdSettingsProps) {

    const onRefreshApiKey = useCallback(() => {
        setNewConfig({ ...config, "api.key": generateNewApiKey() })
    }, [setNewConfig, config]);

    const ensureArticleExistanceSetting =
        useEnsureArticleExistanceSetting(config, setNewConfig);
    const queueMaxItems = parseNonNegativeInteger(config["queue.max-items"]);
    const queueResumeThreshold = parseNonNegativeInteger(config["queue.resume-threshold"]);
    const queueAdmissionValid = isValidQueueAdmission(config);

    return (
        <SettingsPage>
            <ManagedSetting configKey="api.key">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="api-key-input">API Key</label>
                <div className="flex w-full">
                    <Input
                        type="text"
                        id="api-key-input"
                        aria-describedby="api-key-help"
                        value={config["api.key"]}
                        readOnly />
                    <Button variant="primary" onClick={onRefreshApiKey}>
                        <Icon name="refresh" className="!text-[18px]" />
                        Refresh
                    </Button>
                </div>
                <p className="text-[11px] leading-relaxed text-base-content/45" id="api-key-help">
                    Use this API key when configuring your download client in Radarr or Sonarr.
                </p>
            </div>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKey="api.categories">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="categories-input">Categories</label>
                <TagInput
                    className={!isValidCategories(config["api.categories"]) ? 'input-error w-full' : 'w-full'}
                    id="categories-input"
                    aria-describedby="categories-help"
                    value={config["api.categories"]}
                    placeholder="tv, movies, audio, software"
                    onChange={value => setNewConfig({ ...config, "api.categories": value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45" id="categories-help">
                    The complete list of categories for organizing imported nzbs. Only letters, numbers, and dashes are allowed.
                </p>
            </div>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKey="api.manual-category">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="manual-category-input">Manual Upload Category</label>
                <Input
                    className={'w-full'}
                    type="text"
                    id="manual-category-input"
                    aria-describedby="manual-category-help"
                    value={config["api.manual-category"]}
                    placeholder="uncategorized"
                    onChange={e => setNewConfig({ ...config, "api.manual-category": e.target.value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45" id="manual-category-help">
                    The category to use for manual uploads through the Queue page on the UI.
                </p>
            </div>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKeys={["queue.max-items", "queue.resume-threshold"]}>
            <div className="space-y-4">
                <div className="space-y-2">
                    <label className="block text-sm font-medium text-base-content" htmlFor="queue-max-items-input">
                        Maximum queued jobs
                    </label>
                    <Input
                        className={`w-full ${queueAdmissionValid ? "" : "input-error"}`}
                        type="number"
                        min={0}
                        step={1}
                        id="queue-max-items-input"
                        aria-describedby="queue-max-items-help"
                        aria-invalid={!queueAdmissionValid}
                        value={config["queue.max-items"] ?? "0"}
                        onChange={e => setNewConfig({ ...config, "queue.max-items": e.target.value })} />
                    <p className="text-[11px] leading-relaxed text-base-content/45" id="queue-max-items-help">
                        Reject new SAB submissions when this many jobs are queued. Radarr and Sonarr keep
                        rejected grabs pending and retry later. Use <code>0</code> for no limit.
                    </p>
                </div>
                <div className="space-y-2">
                    <label className="block text-sm font-medium text-base-content" htmlFor="queue-resume-threshold-input">
                        Resume threshold
                    </label>
                    <Input
                        className={`w-full ${queueAdmissionValid ? "" : "input-error"}`}
                        type="number"
                        min={0}
                        max={queueMaxItems ?? undefined}
                        step={1}
                        id="queue-resume-threshold-input"
                        aria-describedby="queue-resume-threshold-help"
                        aria-invalid={!queueAdmissionValid}
                        value={config["queue.resume-threshold"] ?? "0"}
                        disabled={queueMaxItems === 0}
                        onChange={e => setNewConfig({ ...config, "queue.resume-threshold": e.target.value })} />
                    <p className="text-[11px] leading-relaxed text-base-content/45" id="queue-resume-threshold-help">
                        After the limit is reached, accept submissions again at or below this queue depth.
                        Use <code>0</code> to resume immediately below the maximum.
                        {queueResumeThreshold !== null && queueMaxItems !== null && queueResumeThreshold > queueMaxItems
                            ? " The threshold cannot exceed the maximum."
                            : ""}
                    </p>
                </div>
            </div>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKey="api.import-strategy">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="import-strategy-input">Import Strategy</label>
                <Select
                    className={'w-full'}
                    value={config["api.import-strategy"]}
                    onChange={e => setNewConfig({ ...config, "api.import-strategy": e.target.value })}
                >
                    <option value="symlinks">Symlinks — Plex</option>
                    <option value="strm">STRM Files — Emby/Jellyfin</option>
                </Select>
                <p className="text-[11px] leading-relaxed text-base-content/45" id="import-strategy-help">
                    If you need to be able to stream from Plex, you will need to configure rclone and should select the `Symlinks` option here. If you only need to stream through Emby or Jellyfin, then you can skip rclone altogether and select the `STRM Files` option.
                </p>
            </div>
            </ManagedSetting>
            {/* <hr /> */}
            {config["api.import-strategy"] === 'symlinks' &&
                <ManagedSetting configKey="rclone.mount-dir">
                <div className={'ml-4 space-y-2 border-l border-base-content/10 pl-4'}>
                    <label className="block text-sm font-medium text-base-content" htmlFor="mount-dir-input">Rclone Mount Directory</label>
                    <Input
                        className={'w-full'}
                        type="text"
                        id="mount-dir-input"
                        aria-describedby="mount-dir-help"
                        placeholder="/mnt/nzbdav"
                        value={config["rclone.mount-dir"]}
                        onChange={e => setNewConfig({ ...config, "rclone.mount-dir": e.target.value })} />
                    <p className="text-[11px] leading-relaxed text-base-content/45" id="mount-dir-help">
                        The location at which you've mounted (or will mount) the webdav root, through Rclone. This is used to tell Radarr / Sonarr where to look for completed "downloads."
                    </p>
                </div>
                </ManagedSetting>
            }
            {config["api.import-strategy"] === 'strm' && <>
                <ManagedSetting configKey="api.completed-downloads-dir">
                <div className={'ml-4 space-y-2 border-l border-base-content/10 pl-4'}>
                    <label className="block text-sm font-medium text-base-content" htmlFor="completed-downloads-dir-input">Completed Downloads Dir</label>
                    <Input
                        className={'w-full'}
                        type="text"
                        id="completed-downloads-dir-input"
                        aria-describedby="completed-downloads-dir-help"
                        placeholder="/data/completed-downloads"
                        value={config["api.completed-downloads-dir"]}
                        onChange={e => setNewConfig({ ...config, "api.completed-downloads-dir": e.target.value })} />
                    <p className="text-[11px] leading-relaxed text-base-content/45" id="completed-downloads-dir-help">
                        This is used to tell Radarr / Sonarr where to look for completed "downloads." Make sure this path is also visible to your Radarr / Sonarr containers. The "downloads" placed in this folder will all be *.strm files that point to InfiniDysk for streaming.
                    </p>
                </div>
                </ManagedSetting>
                <ManagedSetting configKey="general.base-url">
                <div className={'ml-4 space-y-2 border-l border-base-content/10 pl-4'}>
                    <label className="block text-sm font-medium text-base-content" htmlFor="base-url-input">Base URL</label>
                    <Input
                        className={'w-full'}
                        type="text"
                        id="base-url-input"
                        aria-describedby="base-url-help"
                        placeholder="http://localhost:3000"
                        value={config["general.base-url"]}
                        onChange={e => setNewConfig({ ...config, "general.base-url": e.target.value })} />
                    <p className="text-[11px] leading-relaxed text-base-content/45" id="base-url-help">
                        What is the base URL at which you access InfiniDysk? Make sure that Emby/Jellyfin can access this URL. This is where they will connect for streaming. All *.strm files will point to this URL.
                    </p>
                </div>
                </ManagedSetting>
            </>}
            <hr />
            <ManagedSetting configKey="api.download-file-blocklist">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="ignored-files-input">Ignored Files</label>
                <TagInput
                    className={'w-full'}
                    id="ignored-files-input"
                    aria-describedby="ignored-files-help"
                    placeholder="*.nfo, *.par2, *.sfv, *unpack.mkv"
                    value={config["api.download-file-blocklist"]}
                    onChange={value => setNewConfig({ ...config, "api.download-file-blocklist": value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45" id="ignored-files-help">
                    Files that match these patterns will be ignored and not mounted onto the webdav when processing an nzb. Wildcards (* and ?) are supported. Sample videos are filtered separately (see below).
                </p>
            </div>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKey="api.sample-filter-enabled">
            <Tooltip content="Discard video files whose name contains 'sample' as a whole word and that are under 20% of the largest video in the same NZB. Prevents Sonarr/Radarr from importing samples in STRM mode.">
                <Toggle
                    id="sample-filter-enabled-checkbox"
                    className="cursor-pointer gap-2 p-0"
                    checked={config["api.sample-filter-enabled"] !== "false"}
                    onChange={e => setNewConfig({ ...config, "api.sample-filter-enabled": "" + e.target.checked })}
                    label={<span className="text-sm text-base-content">Filter sample videos from downloads</span>}
                />
            </Tooltip>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKey="api.duplicate-nzb-behavior">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="duplicate-nzb-behavior-input">Behavior for Duplicate NZBs</label>
                <Select
                    className={'w-full'}
                    aria-describedby="duplicate-nzb-behavior-help"
                    value={config["api.duplicate-nzb-behavior"]}
                    onChange={e => setNewConfig({ ...config, "api.duplicate-nzb-behavior": e.target.value })}
                >
                    <option value="increment">Download again with suffix (2)</option>
                    <option value="mark-failed">Mark the download as failed</option>
                </Select>
                <p className="text-[11px] leading-relaxed text-base-content/45" id="duplicate-nzb-behavior-help">
                    When an NZB is added, a new folder is created on the webdav. What should be done when the download folder for an NZB already exists?
                </p>
            </div>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKey="api.user-agent">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="user-agent-input">User Agent</label>
                <ExpandingTextInput
                    className={'w-full'}
                    id="user-agent-input"
                    aria-describedby="user-agent-help"
                    value={config["api.user-agent"]}
                    placeholder={`nzbdav/${appVersion}`}
                    onChange={value => setNewConfig({ ...config, "api.user-agent": value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45" id="user-agent-help">
                    The user-agent used by the&nbsp;
                    <a href="https://sabnzbd.org/wiki/configuration/4.5/api#addurl">addurl</a> api
                    for fetching nzb files.
                </p>
            </div>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKey="api.addurl-trusted-hosts">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="addurl-trusted-hosts-input">Trusted local hosts</label>
                <ExpandingTextInput
                    className={'w-full'}
                    id="addurl-trusted-hosts-input"
                    aria-describedby="addurl-trusted-hosts-help"
                    value={config["api.addurl-trusted-hosts"]}
                    placeholder="prowlarr, hydra.lan, 192.168.1.0/24"
                    onChange={value => setNewConfig({ ...config, "api.addurl-trusted-hosts": value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45" id="addurl-trusted-hosts-help">
                    By default, <code>addurl</code> refuses NZB URLs that resolve to private or loopback
                    addresses (SSRF protection). List comma-separated hostnames, IP literals, or CIDR
                    ranges that should be allowed anyway — for example Docker service names like{" "}
                    <code>prowlarr</code> or a LAN subnet like <code>192.168.1.0/24</code>. Use{" "}
                    <code>*</code> to trust every non-public address. Only list hosts you control.
                </p>
            </div>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKey="api.ensure-importable-video">
            <Tooltip content="Mark downloads as failed when no video file is found, so Radarr/Sonarr can grab another NZB.">
                <Toggle
                    id="ensure-importable-video-checkbox"
                    className="cursor-pointer gap-2 p-0"
                    checked={config["api.ensure-importable-video"] === "true"}
                    onChange={e => setNewConfig({ ...config, "api.ensure-importable-video": "" + e.target.checked })}
                    label={<span className="text-sm text-base-content">Fail downloads for nzbs without video content</span>}
                />
            </Tooltip>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKey="api.skip-non-video-on-missing-articles">
            <Tooltip content="By default, missing articles in PAR2/NFO/subtitles are skipped. Enable to fail the download so *Arr can grab an alternate.">
                <Toggle
                    id="fail-missing-non-video-checkbox"
                    className="cursor-pointer gap-2 p-0"
                    checked={config["api.skip-non-video-on-missing-articles"] === "false"}
                    onChange={e => setNewConfig({
                        ...config,
                        "api.skip-non-video-on-missing-articles": String(!e.target.checked)
                    })}
                    label={<span className="text-sm text-base-content">Fail downloads when non-video files have missing articles</span>}
                />
            </Tooltip>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKey="api.ensure-article-existence-categories">
            <div className="space-y-2">
                <label className="flex items-center gap-2 text-sm text-base-content/80">
                    <Checkbox
                    id="ensure-article-existence-checkbox"
                    aria-describedby="ensure-article-existence-help"
                    ref={ensureArticleExistanceSetting.masterCheckboxRef}
                    checked={!ensureArticleExistanceSetting.areNoneSelected}
                    onChange={e => ensureArticleExistanceSetting.onMasterCheckboxChange(e.target.checked)}  />
                    <span>{`Perform article health check during downloads`}</span>
                </label>
                <p className="text-[11px] leading-relaxed text-base-content/45" id="ensure-article-existence-help">
                    Check article availability in the selected categories before mounting the NZB.
                </p>
                <MultiCheckboxInput
                    options={ensureArticleExistanceSetting.categories}
                    value={config["api.ensure-article-existence-categories"] ?? ""}
                    onChange={value => setNewConfig({ ...config, "api.ensure-article-existence-categories": value })}
                />
            </div>
            </ManagedSetting>
            <ManagedSetting configKey="api.article-existence-check-mode">
            <div className="ml-4 mt-4 space-y-2 border-l border-base-content/10 pl-4">
                <label className="block text-sm font-medium text-base-content" htmlFor="article-existence-check-mode-input">
                    Article health check mode
                </label>
                <Select
                    className="w-full"
                    id="article-existence-check-mode-input"
                    aria-describedby="article-existence-check-mode-help"
                    value={config["api.article-existence-check-mode"] ?? "full"}
                    disabled={ensureArticleExistanceSetting.areNoneSelected}
                    onChange={e => setNewConfig({
                        ...config,
                        "api.article-existence-check-mode": e.target.value
                    })}
                >
                    <option value="full">Full — check every segment</option>
                    <option value="sampled">Sampled — first, last, and evenly spaced segments per file</option>
                </Select>
                <p className="text-[11px] leading-relaxed text-base-content/45" id="article-existence-check-mode-help">
                    Sampled mode reduces import time for large files while still detecting common truncated
                    or partially removed releases. Files below the sampling threshold are checked in full.
                </p>
            </div>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKey="api.ignore-history-limit">
            <Tooltip content="Ignore the History limit from Radarr/Sonarr and always reply with all History items (workaround for Sonarr issue #5452).">
                <Toggle
                    id="ignore-history-limit-checkbox"
                    className="cursor-pointer gap-2 p-0"
                    checked={config["api.ignore-history-limit"] === "true"}
                    onChange={e => setNewConfig({ ...config, "api.ignore-history-limit": "" + e.target.checked })}
                    label={<span className="text-sm text-base-content">Always send full History to Radarr/Sonarr</span>}
                />
            </Tooltip>
            </ManagedSetting>
            <hr />
            <ManagedSetting configKeys={[
                "api.nzb-backup-enabled",
                "api.nzb-backup-location",
                "api.nzb-backup-retention-days",
            ]}>
            <div className="space-y-2">
                <Tooltip content="Save a copy of each incoming NZB to the directory below, organized by category. The directory is created if missing.">
                    <Toggle
                        id="nzb-backup-enabled-checkbox"
                        className="cursor-pointer gap-2 p-0"
                        checked={config["api.nzb-backup-enabled"] === "true"}
                        onChange={e => setNewConfig({ ...config, "api.nzb-backup-enabled": "" + e.target.checked })}
                        label={<span className="text-sm text-base-content">Save backup copies of incoming NZBs</span>}
                    />
                </Tooltip>
                <Input
                    className="mt-4 w-full"
                    type="text"
                    id="nzb-backup-location-input"
                    aria-describedby="nzb-backup-location-help"
                    placeholder="/data/nzb-backups"
                    value={config["api.nzb-backup-location"]}
                    disabled={config["api.nzb-backup-enabled"] !== "true"}
                    aria-invalid={!isValidNzbBackupLocation(config)}
                    onChange={e => setNewConfig({ ...config, "api.nzb-backup-location": e.target.value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45" id="nzb-backup-location-help">
                    Directory for NZB backups, organized by category.
                </p>
                <label className="mt-4 flex items-center gap-2 text-sm text-base-content/80" htmlFor="nzb-backup-retention-days-input">
                    <span>Keep NZB backups for (days)</span>
                </label>
                <Input
                    className="mt-2 w-full"
                    type="number"
                    min={0}
                    id="nzb-backup-retention-days-input"
                    aria-describedby="nzb-backup-retention-days-help"
                    value={config["api.nzb-backup-retention-days"] ?? "30"}
                    disabled={config["api.nzb-backup-enabled"] !== "true"}
                    onChange={e => setNewConfig({ ...config, "api.nzb-backup-retention-days": e.target.value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45" id="nzb-backup-retention-days-help">
                    Aged <code>*.nzb</code> files under the backup directory are pruned hourly. Use <code>0</code> to keep backups forever. Default is 30 days.
                </p>
            </div>
            </ManagedSetting>
        </SettingsPage>
    );
}

function useEnsureArticleExistanceSetting(
    config: Record<string, string>,
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
) {
    const manualCategoryValue = config["api.manual-category"];
    const categoriesValue = config["api.categories"];
    const healthCheckCategoriesValue = config["api.ensure-article-existence-categories"];

    const manualCategory = useMemo(() => {
        return !!(manualCategoryValue?.trim())
            ? manualCategoryValue.trim()
            : "uncategorized";
    }, [manualCategoryValue]);

    const categories = useMemo(() => {
        const list = !!(categoriesValue?.trim())
            ? categoriesValue.split(",").map(c => c.trim()).filter(c => c.length > 0)
            : ["audio", "software", "tv", "movies"];
        return [manualCategory, ...list];
    }, [categoriesValue]);

    const healthCheckCategories = useMemo(() => {
        const cats = healthCheckCategoriesValue;
        if (!cats || cats.trim() === "") return [];
        return cats.split(",").map(c => c.trim()).filter(c => c.length > 0);
    }, [healthCheckCategoriesValue]);

    const masterCheckboxRef = useRef<HTMLInputElement>(null);
    const areAllSelected = categories.length > 0 && categories.every(c => healthCheckCategories.includes(c));
    const areNoneSelected = healthCheckCategories.length === 0 || categories.every(c => !healthCheckCategories.includes(c));
    const areSomeSelected = !areAllSelected && !areNoneSelected;

    useEffect(() => {
        if (masterCheckboxRef.current) {
            masterCheckboxRef.current.indeterminate = areSomeSelected;
        }
    }, [areSomeSelected]);

    const onMasterCheckboxChange = useCallback((checked: boolean) => {
        if (checked) {
            setNewConfig(prev => ({ ...prev, "api.ensure-article-existence-categories": categories.join(", ") }));
        } else {
            setNewConfig(prev => ({ ...prev, "api.ensure-article-existence-categories": "" }));
        }
    }, [setNewConfig, categories]);

    return {
        categories,
        masterCheckboxRef,
        areAllSelected,
        areNoneSelected,
        areSomeSelected,
        onMasterCheckboxChange
    }
}

export function isSabnzbdSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["api.key"] !== newConfig["api.key"]
        || config["api.categories"] !== newConfig["api.categories"]
        || config["api.manual-category"] !== newConfig["api.manual-category"]
        || config["queue.max-items"] !== newConfig["queue.max-items"]
        || config["queue.resume-threshold"] !== newConfig["queue.resume-threshold"]
        || config["rclone.mount-dir"] !== newConfig["rclone.mount-dir"]
        || config["api.ensure-importable-video"] !== newConfig["api.ensure-importable-video"]
        || config["api.sample-filter-enabled"] !== newConfig["api.sample-filter-enabled"]
        || config["api.skip-non-video-on-missing-articles"] !== newConfig["api.skip-non-video-on-missing-articles"]
        || config["api.ensure-article-existence-categories"] !== newConfig["api.ensure-article-existence-categories"]
        || config["api.article-existence-check-mode"] !== newConfig["api.article-existence-check-mode"]
        || config["api.ignore-history-limit"] !== newConfig["api.ignore-history-limit"]
        || config["api.duplicate-nzb-behavior"] !== newConfig["api.duplicate-nzb-behavior"]
        || config["api.download-file-blocklist"] !== newConfig["api.download-file-blocklist"]
        || config["api.import-strategy"] !== newConfig["api.import-strategy"]
        || config["api.completed-downloads-dir"] !== newConfig["api.completed-downloads-dir"]
        || config["general.base-url"] !== newConfig["general.base-url"]
        || config["api.user-agent"] !== newConfig["api.user-agent"]
        || config["api.addurl-trusted-hosts"] !== newConfig["api.addurl-trusted-hosts"]
        || config["api.nzb-backup-enabled"] !== newConfig["api.nzb-backup-enabled"]
        || config["api.nzb-backup-location"] !== newConfig["api.nzb-backup-location"]
        || config["api.nzb-backup-retention-days"] !== newConfig["api.nzb-backup-retention-days"]
}

export function isSabnzbdSettingsValid(newConfig: Record<string, string>) {
    return isValidCategories(newConfig["api.categories"])
        && isValidNzbBackupLocation(newConfig)
        && isValidQueueAdmission(newConfig);
}

export function generateNewApiKey(): string {
    return crypto.randomUUID().toString().replaceAll("-", "");
}

function isValidCategories(categories: string): boolean {
    if (categories === "") return true;
    const parts = categories.split(",");
    return parts.map(x => x.trim()).every(x => isAlphaNumericWithDashes(x));
}

function isValidNzbBackupLocation(config: Record<string, string>) {
    return config["api.nzb-backup-enabled"] !== "true"
        || !!config["api.nzb-backup-location"]?.trim();
}

function isValidQueueAdmission(config: Record<string, string>) {
    const maxItems = parseNonNegativeInteger(config["queue.max-items"]);
    const resumeThreshold = parseNonNegativeInteger(config["queue.resume-threshold"]);
    if (maxItems === null || resumeThreshold === null) return false;
    return maxItems === 0 || resumeThreshold === 0 || resumeThreshold <= maxItems;
}

function parseNonNegativeInteger(value: string | undefined): number | null {
    if (value === undefined || value.trim() === "") return 0;
    if (!/^\d+$/.test(value)) return null;
    const parsed = Number(value);
    return Number.isSafeInteger(parsed) ? parsed : null;
}

function isAlphaNumericWithDashes(input: string): boolean {
    const regex = /^[A-Za-z0-9-]+$/;
    return regex.test(input);
}
