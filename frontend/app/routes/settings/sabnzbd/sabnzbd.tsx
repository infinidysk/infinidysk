import { useCallback, useEffect, useMemo, useRef, type Dispatch, type SetStateAction } from "react";
import { Button } from "~/components/ui/button";
import {
  Checkbox,
  Input,
  ManagedSetting,
  Select,
  SettingsCard,
  SettingsIntro,
  SettingsPage,
  Toggle,
  Tooltip,
} from "~/components/ui";
import { ExpandingTextInput } from "~/components/expanding-text-input/expanding-text-input";
import { MultiCheckboxInput } from "~/components/multi-checkbox-input/multi-checkbox-input";
import { TagInput } from "~/components/tag-input/tag-input";
import { Icon } from "~/components/ui/icon";
import { generateUuid } from "~/utils/uuid";

type SabnzbdSettingsProps = {
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

export function SabnzbdSettings({ config, setNewConfig }: SabnzbdSettingsProps) {
  const ensureArticleExistence = useEnsureArticleExistenceSetting(config, setNewConfig);
  const primaryOutput = normalizeImportStrategy(config["api.import-strategy"]);
  const isSymlinkOutputEnabled =
    primaryOutput === "symlinks" || isOutputEnabled(config["api.symlink-output-enabled"]);
  const isStrmOutputEnabled =
    primaryOutput === "strm" || isOutputEnabled(config["api.strm-output-enabled"]);

  const refreshApiKey = useCallback(() => {
    setNewConfig({ ...config, "api.key": generateNewApiKey() });
  }, [config, setNewConfig]);

  return (
    <SettingsPage>
      <SettingsIntro>
        Configure the SABnzbd-compatible API used by Radarr and Sonarr, then choose how submitted
        NZBs are imported, checked, and backed up.
      </SettingsIntro>

      <SettingsCard
        icon="link"
        title="Connection"
        description="Credentials, categories, and network access for SAB-compatible clients."
      >
        <ManagedSetting configKey="api.key">
          <div className="space-y-2">
            <label className="block text-sm font-medium text-base-content" htmlFor="api-key-input">
              API Key
            </label>
            <div className="flex w-full">
              <Input
                type="text"
                id="api-key-input"
                aria-describedby="api-key-help"
                value={config["api.key"]}
                readOnly
              />
              <Button variant="primary" onClick={refreshApiKey}>
                <Icon name="refresh" className="!text-[18px]" />
                Refresh
              </Button>
            </div>
            <p className="text-[11px] leading-relaxed text-base-content/45" id="api-key-help">
              Use this API key when configuring InfiniDysk as a download client in Radarr or Sonarr.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="api.categories">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="categories-input"
            >
              Categories
            </label>
            <TagInput
              className={
                !isValidCategories(config["api.categories"] ?? "") ? "input-error w-full" : "w-full"
              }
              id="categories-input"
              aria-describedby="categories-help"
              value={config["api.categories"] ?? ""}
              placeholder="tv, movies, audio, software"
              onChange={(value) => setNewConfig({ ...config, "api.categories": value })}
            />
            <p className="text-[11px] leading-relaxed text-base-content/45" id="categories-help">
              Categories available to SAB clients for organizing imported NZBs. Only letters,
              numbers, and dashes are allowed.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="api.manual-category">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="manual-category-input"
            >
              Manual Upload Category
            </label>
            <Input
              className="w-full"
              type="text"
              id="manual-category-input"
              aria-describedby="manual-category-help"
              value={config["api.manual-category"]}
              placeholder="uncategorized"
              onChange={(e) => setNewConfig({ ...config, "api.manual-category": e.target.value })}
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="manual-category-help"
            >
              Category assigned to NZBs uploaded manually from the Queue page.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="api.addurl-trusted-hosts">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="addurl-trusted-hosts-input"
            >
              Trusted local hosts
            </label>
            <ExpandingTextInput
              className="w-full"
              id="addurl-trusted-hosts-input"
              aria-describedby="addurl-trusted-hosts-help"
              value={config["api.addurl-trusted-hosts"] ?? ""}
              placeholder="prowlarr, hydra.lan, 192.168.1.0/24"
              onChange={(value) => setNewConfig({ ...config, "api.addurl-trusted-hosts": value })}
            />
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="addurl-trusted-hosts-help"
            >
              By default, <code>addurl</code> refuses NZB URLs resolving to private or loopback
              addresses. List trusted hostnames, IP addresses, or CIDR ranges that should be
              allowed. Use <code>*</code> only when every non-public address is under your control.
            </p>
          </div>
        </ManagedSetting>
      </SettingsCard>

      <SettingsCard
        icon="download"
        title="Import behavior"
        description="Choose the primary *Arr import path, then optionally emit an additional media-server output."
      >
        <ManagedSetting configKey="api.import-strategy">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="import-strategy-input"
            >
              Primary *Arr import output
            </label>
            <Select
              className="w-full"
              id="import-strategy-input"
              aria-describedby="import-strategy-help"
              value={config["api.import-strategy"]}
              onChange={(e) => setNewConfig({ ...config, "api.import-strategy": e.target.value })}
            >
              <option value="symlinks">Symlinks — Plex</option>
              <option value="strm">STRM Files — Emby/Jellyfin</option>
            </Select>
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="import-strategy-help"
            >
              SAB can report one completed-downloads path. Radarr or Sonarr imports from this
              output; any other enabled output is for a separate media-server library.
            </p>
          </div>
        </ManagedSetting>

        <div className="ml-4 space-y-4 border-l border-base-content/10 pl-4">
          <ManagedSetting configKey="api.symlink-output-enabled">
            <div className="space-y-3">
              <div>
                <Toggle
                  id="symlink-output-enabled"
                  className="cursor-pointer"
                  checked={isSymlinkOutputEnabled}
                  disabled={primaryOutput === "symlinks"}
                  label={
                    <span>
                      <span className="block text-sm font-medium text-base-content">
                        Symlink output — Plex
                      </span>
                      <span className="block text-[11px] leading-relaxed text-base-content/45">
                        {primaryOutput === "symlinks"
                          ? "Required because this is the primary *Arr import output."
                          : "Create an additional Plex-compatible output."}
                      </span>
                    </span>
                  }
                  onChange={(e) =>
                    setNewConfig({
                      ...config,
                      "api.symlink-output-enabled": String(e.target.checked),
                    })
                  }
                />
              </div>
              {isSymlinkOutputEnabled && (
                <>
                  <ManagedSetting configKey="rclone.mount-dir">
                    <div className="space-y-2">
                      <label
                        className="block text-sm font-medium text-base-content"
                        htmlFor="mount-dir-input"
                      >
                        Rclone Mount Directory
                      </label>
                      <Input
                        className="w-full"
                        type="text"
                        id="mount-dir-input"
                        aria-describedby="mount-dir-help"
                        placeholder="/mnt/nzbdav"
                        value={config["rclone.mount-dir"]}
                        onChange={(e) =>
                          setNewConfig({ ...config, "rclone.mount-dir": e.target.value })
                        }
                      />
                      <p
                        className="text-[11px] leading-relaxed text-base-content/45"
                        id="mount-dir-help"
                      >
                        The WebDAV mount containing <code>.ids</code>. Generated symlinks target
                        this path.
                      </p>
                    </div>
                  </ManagedSetting>
                  <ManagedSetting configKey="api.symlink-output-dir">
                    <div className="space-y-2">
                      <label
                        className="block text-sm font-medium text-base-content"
                        htmlFor="symlink-output-dir-input"
                      >
                        Symlink Output Directory
                      </label>
                      <Input
                        className="w-full"
                        type="text"
                        id="symlink-output-dir-input"
                        aria-describedby="symlink-output-dir-help"
                        placeholder="/mnt/Plex"
                        value={config["api.symlink-output-dir"]}
                        onChange={(e) =>
                          setNewConfig({ ...config, "api.symlink-output-dir": e.target.value })
                        }
                      />
                      <p
                        className="text-[11px] leading-relaxed text-base-content/45"
                        id="symlink-output-dir-help"
                      >
                        Optional. Leave blank to use the virtual <code>completed-symlinks</code>{" "}
                        rclone tree. Set a directory to create real symlinks at queue completion.
                      </p>
                    </div>
                  </ManagedSetting>
                </>
              )}
            </div>
          </ManagedSetting>

          <ManagedSetting configKey="api.strm-output-enabled">
            <div className="space-y-3">
              <div>
                <Toggle
                  id="strm-output-enabled"
                  className="cursor-pointer"
                  checked={isStrmOutputEnabled}
                  disabled={primaryOutput === "strm"}
                  label={
                    <span>
                      <span className="block text-sm font-medium text-base-content">
                        STRM output — Emby/Jellyfin
                      </span>
                      <span className="block text-[11px] leading-relaxed text-base-content/45">
                        {primaryOutput === "strm"
                          ? "Required because this is the primary *Arr import output."
                          : "Create authenticated streaming sidecars for another media-server library."}
                      </span>
                    </span>
                  }
                  onChange={(e) =>
                    setNewConfig({
                      ...config,
                      "api.strm-output-enabled": String(e.target.checked),
                    })
                  }
                />
              </div>
              {isStrmOutputEnabled && (
                <div className="space-y-4">
                  <ManagedSetting configKey="api.completed-downloads-dir">
                    <div className="space-y-2">
                      <label
                        className="block text-sm font-medium text-base-content"
                        htmlFor="completed-downloads-dir-input"
                      >
                        Completed Downloads Dir
                      </label>
                      <Input
                        className="w-full"
                        type="text"
                        id="completed-downloads-dir-input"
                        aria-describedby="completed-downloads-dir-help"
                        placeholder="/data/completed-downloads"
                        value={config["api.completed-downloads-dir"]}
                        onChange={(e) =>
                          setNewConfig({
                            ...config,
                            "api.completed-downloads-dir": e.target.value,
                          })
                        }
                      />
                      <p
                        className="text-[11px] leading-relaxed text-base-content/45"
                        id="completed-downloads-dir-help"
                      >
                        Directory visible to Radarr or Sonarr where completed STRM files are
                        written.
                      </p>
                    </div>
                  </ManagedSetting>
                  <ManagedSetting configKey="general.base-url">
                    <div className="space-y-2">
                      <label
                        className="block text-sm font-medium text-base-content"
                        htmlFor="base-url-input"
                      >
                        Base URL
                      </label>
                      <Input
                        className="w-full"
                        type="text"
                        id="base-url-input"
                        aria-describedby="base-url-help"
                        placeholder="http://localhost:3000"
                        value={config["general.base-url"]}
                        onChange={(e) =>
                          setNewConfig({ ...config, "general.base-url": e.target.value })
                        }
                      />
                      <p
                        className="text-[11px] leading-relaxed text-base-content/45"
                        id="base-url-help"
                      >
                        URL Emby or Jellyfin can reach. Generated STRM files point to this address.
                      </p>
                    </div>
                  </ManagedSetting>
                </div>
              )}
            </div>
          </ManagedSetting>
        </div>

        <ManagedSetting configKey="api.download-file-blocklist">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="ignored-files-input"
            >
              Ignored Files
            </label>
            <TagInput
              className="w-full"
              id="ignored-files-input"
              aria-describedby="ignored-files-help"
              placeholder="*.nfo, *.par2, *.sfv, *unpack.mkv"
              value={config["api.download-file-blocklist"] ?? ""}
              onChange={(value) =>
                setNewConfig({ ...config, "api.download-file-blocklist": value })
              }
            />
            <p className="text-[11px] leading-relaxed text-base-content/45" id="ignored-files-help">
              Files matching these wildcard patterns are not mounted when an NZB is processed.
              Sample videos are filtered separately below.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="api.sample-filter-enabled">
          <Tooltip content="Discard video files whose name contains 'sample' as a whole word and that are under 20% of the largest video in the same NZB.">
            <Toggle
              id="sample-filter-enabled-checkbox"
              className="cursor-pointer gap-2 p-0"
              checked={config["api.sample-filter-enabled"] !== "false"}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "api.sample-filter-enabled": String(e.target.checked),
                })
              }
              label={
                <span className="text-sm text-base-content">
                  Filter sample videos from downloads
                </span>
              }
            />
          </Tooltip>
        </ManagedSetting>

        <ManagedSetting configKey="api.duplicate-nzb-behavior">
          <div className="space-y-2">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="duplicate-nzb-behavior-input"
            >
              Behavior for Duplicate NZBs
            </label>
            <Select
              className="w-full"
              id="duplicate-nzb-behavior-input"
              aria-describedby="duplicate-nzb-behavior-help"
              value={config["api.duplicate-nzb-behavior"]}
              onChange={(e) =>
                setNewConfig({ ...config, "api.duplicate-nzb-behavior": e.target.value })
              }
            >
              <option value="increment">Download again with suffix (2)</option>
              <option value="mark-failed">Mark the download as failed</option>
            </Select>
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="duplicate-nzb-behavior-help"
            >
              Applied when an NZB would create a download folder that already exists on WebDAV.
            </p>
          </div>
        </ManagedSetting>
      </SettingsCard>

      <SettingsCard
        icon="fact_check"
        title="Validation & health"
        description="Decide which import problems fail a download and trigger an alternate grab."
      >
        <ManagedSetting configKey="api.ensure-importable-video">
          <Tooltip content="Mark downloads as failed when no video or audio file is found, so your *Arr app can grab another NZB.">
            <Toggle
              id="ensure-importable-video-checkbox"
              className="cursor-pointer gap-2 p-0"
              checked={config["api.ensure-importable-video"] === "true"}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "api.ensure-importable-video": String(e.target.checked),
                })
              }
              label={
                <span className="text-sm text-base-content">
                  Fail downloads for NZBs without video or audio content
                </span>
              }
            />
          </Tooltip>
        </ManagedSetting>

        <ManagedSetting configKey="api.skip-non-video-on-missing-articles">
          <Tooltip content="By default, missing articles in PAR2, NFO, or subtitle files are skipped. Audio and video files always fail. Enable this to also fail the download for other missing files.">
            <Toggle
              id="fail-missing-non-video-checkbox"
              className="cursor-pointer gap-2 p-0"
              checked={config["api.skip-non-video-on-missing-articles"] === "false"}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "api.skip-non-video-on-missing-articles": String(!e.target.checked),
                })
              }
              label={
                <span className="text-sm text-base-content">
                  Fail downloads when non-media files have missing articles
                </span>
              }
            />
          </Tooltip>
        </ManagedSetting>

        <ManagedSetting configKey="api.ensure-article-existence-categories">
          <div className="space-y-2">
            <label className="flex items-center gap-2 text-sm text-base-content/80">
              <Checkbox
                id="ensure-article-existence-checkbox"
                aria-describedby="ensure-article-existence-help"
                ref={ensureArticleExistence.masterCheckboxRef}
                checked={!ensureArticleExistence.areNoneSelected}
                onChange={(e) => ensureArticleExistence.onMasterCheckboxChange(e.target.checked)}
              />
              <span>Perform article health check during downloads</span>
            </label>
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="ensure-article-existence-help"
            >
              Check article availability in the selected categories before mounting the NZB.
            </p>
            <MultiCheckboxInput
              options={ensureArticleExistence.categories}
              value={config["api.ensure-article-existence-categories"] ?? ""}
              onChange={(value) =>
                setNewConfig({
                  ...config,
                  "api.ensure-article-existence-categories": value,
                })
              }
            />
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="api.article-existence-check-mode">
          <div className="ml-4 space-y-2 border-l border-base-content/10 pl-4">
            <label
              className="block text-sm font-medium text-base-content"
              htmlFor="article-existence-check-mode-input"
            >
              Article health check mode
            </label>
            <Select
              className="w-full"
              id="article-existence-check-mode-input"
              aria-describedby="article-existence-check-mode-help"
              value={config["api.article-existence-check-mode"] ?? "full"}
              disabled={ensureArticleExistence.areNoneSelected}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "api.article-existence-check-mode": e.target.value,
                })
              }
            >
              <option value="full">Full — check every segment</option>
              <option value="sampled">
                Sampled — first, last, and evenly spaced segments per file
              </option>
            </Select>
            <p
              className="text-[11px] leading-relaxed text-base-content/45"
              id="article-existence-check-mode-help"
            >
              Sampled mode reduces import time for large files while still detecting common
              truncated or partially removed releases.
            </p>
          </div>
        </ManagedSetting>

        <ManagedSetting configKey="api.ignore-history-limit">
          <Tooltip content="Ignore the History limit from Radarr or Sonarr and always return all History items (workaround for Sonarr issue #5452).">
            <Toggle
              id="ignore-history-limit-checkbox"
              className="cursor-pointer gap-2 p-0"
              checked={config["api.ignore-history-limit"] === "true"}
              onChange={(e) =>
                setNewConfig({
                  ...config,
                  "api.ignore-history-limit": String(e.target.checked),
                })
              }
              label={
                <span className="text-sm text-base-content">
                  Always send full History to Radarr/Sonarr
                </span>
              }
            />
          </Tooltip>
        </ManagedSetting>
      </SettingsCard>

      <SettingsCard
        icon="archive"
        title="NZB backups"
        description="Optionally retain incoming NZB files outside the application database."
      >
        <ManagedSetting
          configKeys={[
            "api.nzb-backup-enabled",
            "api.nzb-backup-location",
            "api.nzb-backup-retention-days",
          ]}
        >
          <div className="space-y-4">
            <Tooltip content="Save a copy of each incoming NZB to the directory below, organized by category.">
              <Toggle
                id="nzb-backup-enabled-checkbox"
                className="cursor-pointer gap-2 p-0"
                checked={config["api.nzb-backup-enabled"] === "true"}
                onChange={(e) =>
                  setNewConfig({
                    ...config,
                    "api.nzb-backup-enabled": String(e.target.checked),
                  })
                }
                label={
                  <span className="text-sm text-base-content">
                    Save backup copies of incoming NZBs
                  </span>
                }
              />
            </Tooltip>
            <div className="space-y-2">
              <label
                className="block text-sm font-medium text-base-content"
                htmlFor="nzb-backup-location-input"
              >
                Backup directory
              </label>
              <Input
                className={`w-full ${isValidNzbBackupLocation(config) ? "" : "input-error"}`}
                type="text"
                id="nzb-backup-location-input"
                aria-describedby="nzb-backup-location-help"
                placeholder="/data/nzb-backups"
                value={config["api.nzb-backup-location"]}
                disabled={config["api.nzb-backup-enabled"] !== "true"}
                aria-invalid={!isValidNzbBackupLocation(config)}
                onChange={(e) =>
                  setNewConfig({
                    ...config,
                    "api.nzb-backup-location": e.target.value,
                  })
                }
              />
              <p
                className="text-[11px] leading-relaxed text-base-content/45"
                id="nzb-backup-location-help"
              >
                Backups are organized into category subdirectories.
              </p>
            </div>
            <div className="space-y-2">
              <label
                className="block text-sm font-medium text-base-content"
                htmlFor="nzb-backup-retention-days-input"
              >
                Keep NZB backups for (days)
              </label>
              <Input
                className="w-full"
                type="number"
                min={0}
                id="nzb-backup-retention-days-input"
                aria-describedby="nzb-backup-retention-days-help"
                value={config["api.nzb-backup-retention-days"] ?? "30"}
                disabled={config["api.nzb-backup-enabled"] !== "true"}
                onChange={(e) =>
                  setNewConfig({
                    ...config,
                    "api.nzb-backup-retention-days": e.target.value,
                  })
                }
              />
              <p
                className="text-[11px] leading-relaxed text-base-content/45"
                id="nzb-backup-retention-days-help"
              >
                Old NZB files are pruned hourly. Use <code>0</code> to keep backups forever.
              </p>
            </div>
          </div>
        </ManagedSetting>
      </SettingsCard>
    </SettingsPage>
  );
}

function useEnsureArticleExistenceSetting(
  config: Record<string, string>,
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>,
) {
  const manualCategoryValue = config["api.manual-category"];
  const categoriesValue = config["api.categories"];
  const healthCheckCategoriesValue = config["api.ensure-article-existence-categories"];

  const manualCategory = useMemo(
    () => manualCategoryValue?.trim() || "uncategorized",
    [manualCategoryValue],
  );
  const categories = useMemo(() => {
    const configured = categoriesValue?.trim()
      ? categoriesValue
          .split(",")
          .map((category) => category.trim())
          .filter(Boolean)
      : ["audio", "software", "tv", "movies"];
    return [manualCategory, ...configured];
  }, [categoriesValue, manualCategory]);
  const healthCheckCategories = useMemo(
    () =>
      healthCheckCategoriesValue?.trim()
        ? healthCheckCategoriesValue
            .split(",")
            .map((category) => category.trim())
            .filter(Boolean)
        : [],
    [healthCheckCategoriesValue],
  );

  const masterCheckboxRef = useRef<HTMLInputElement>(null);
  const areAllSelected =
    categories.length > 0 &&
    categories.every((category) => healthCheckCategories.includes(category));
  const areNoneSelected =
    healthCheckCategories.length === 0 ||
    categories.every((category) => !healthCheckCategories.includes(category));
  const areSomeSelected = !areAllSelected && !areNoneSelected;

  useEffect(() => {
    if (masterCheckboxRef.current) {
      masterCheckboxRef.current.indeterminate = areSomeSelected;
    }
  }, [areSomeSelected]);

  const onMasterCheckboxChange = useCallback(
    (checked: boolean) => {
      setNewConfig((previous) => ({
        ...previous,
        "api.ensure-article-existence-categories": checked ? categories.join(", ") : "",
      }));
    },
    [categories, setNewConfig],
  );

  return {
    categories,
    masterCheckboxRef,
    areNoneSelected,
    onMasterCheckboxChange,
  };
}

export function isSabnzbdSettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
): boolean {
  return (
    config["api.key"] !== newConfig["api.key"] ||
    config["api.categories"] !== newConfig["api.categories"] ||
    config["api.manual-category"] !== newConfig["api.manual-category"] ||
    config["rclone.mount-dir"] !== newConfig["rclone.mount-dir"] ||
    config["api.ensure-importable-video"] !== newConfig["api.ensure-importable-video"] ||
    config["api.sample-filter-enabled"] !== newConfig["api.sample-filter-enabled"] ||
    config["api.skip-non-video-on-missing-articles"] !==
      newConfig["api.skip-non-video-on-missing-articles"] ||
    config["api.ensure-article-existence-categories"] !==
      newConfig["api.ensure-article-existence-categories"] ||
    config["api.article-existence-check-mode"] !== newConfig["api.article-existence-check-mode"] ||
    config["api.ignore-history-limit"] !== newConfig["api.ignore-history-limit"] ||
    config["api.duplicate-nzb-behavior"] !== newConfig["api.duplicate-nzb-behavior"] ||
    config["api.download-file-blocklist"] !== newConfig["api.download-file-blocklist"] ||
    config["api.import-strategy"] !== newConfig["api.import-strategy"] ||
    config["api.completed-downloads-dir"] !== newConfig["api.completed-downloads-dir"] ||
    config["api.symlink-output-enabled"] !== newConfig["api.symlink-output-enabled"] ||
    config["api.symlink-output-dir"] !== newConfig["api.symlink-output-dir"] ||
    config["api.strm-output-enabled"] !== newConfig["api.strm-output-enabled"] ||
    config["general.base-url"] !== newConfig["general.base-url"] ||
    config["api.addurl-trusted-hosts"] !== newConfig["api.addurl-trusted-hosts"] ||
    config["api.nzb-backup-enabled"] !== newConfig["api.nzb-backup-enabled"] ||
    config["api.nzb-backup-location"] !== newConfig["api.nzb-backup-location"] ||
    config["api.nzb-backup-retention-days"] !== newConfig["api.nzb-backup-retention-days"]
  );
}

export function isSabnzbdSettingsValid(config: Record<string, string>): boolean {
  return (
    isValidCategories(config["api.categories"] ?? "") &&
    isValidNzbBackupLocation(config) &&
    isValidStrmOutput(config)
  );
}

export function generateNewApiKey(): string {
  return generateUuid().replaceAll("-", "");
}

function isValidCategories(categories: string): boolean {
  if (categories === "") return true;
  return categories
    .split(",")
    .map((category) => category.trim())
    .every((category) => /^[A-Za-z0-9-]+$/.test(category));
}

function isValidNzbBackupLocation(config: Record<string, string>): boolean {
  return (
    config["api.nzb-backup-enabled"] !== "true" ||
    Boolean(config["api.nzb-backup-location"]?.trim())
  );
}

function isValidStrmOutput(config: Record<string, string>): boolean {
  const strmEnabled =
    normalizeImportStrategy(config["api.import-strategy"]) === "strm" ||
    isOutputEnabled(config["api.strm-output-enabled"]);
  return (
    !strmEnabled ||
    (Boolean(config["api.completed-downloads-dir"]?.trim()) &&
      Boolean(config["general.base-url"]?.trim()))
  );
}

function normalizeImportStrategy(value: string | undefined): "symlinks" | "strm" {
  return value?.trim().toLowerCase() === "strm" ? "strm" : "symlinks";
}

function isOutputEnabled(value: string | undefined): boolean {
  return value?.trim().toLowerCase() === "true";
}
