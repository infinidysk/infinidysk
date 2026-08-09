import { type Dispatch, type SetStateAction } from "react";
import {
  Input,
  ManagedSetting,
  SettingsCard,
  SettingsIntro,
  SettingsPage,
  Toggle,
  Tooltip,
} from "~/components/ui";
import { className } from "~/utils/styling";

type WebdavSettingsProps = {
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

export function WebdavSettings({ config, setNewConfig }: WebdavSettingsProps) {
  return (
    <SettingsPage>
      <SettingsIntro>
        Configure WebDAV credentials and choose how mounted content appears to filesystem clients
        and the Dav Explorer.
      </SettingsIntro>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <SettingsCard
          icon="lock"
          title="Access"
          description="Credentials used by rclone, Plex, and other WebDAV clients."
        >
          <ManagedSetting configKey="webdav.user">
            <div className="space-y-2">
              <label
                className="block text-sm font-medium text-base-content"
                htmlFor="webdav-user-input"
              >
                WebDAV User
              </label>
              <Input
                {...className(["w-full", !isValidUser(config["webdav.user"]) && "input-error"])}
                type="text"
                id="webdav-user-input"
                aria-describedby="webdav-user-help"
                placeholder="admin"
                value={config["webdav.user"]}
                onChange={(e) => setNewConfig({ ...config, "webdav.user": e.target.value })}
              />
              <p className="text-[11px] leading-relaxed text-base-content/45" id="webdav-user-help">
                Use this username to connect to WebDAV. Only letters, numbers, dashes, and
                underscores are allowed.
              </p>
            </div>
          </ManagedSetting>

          <ManagedSetting configKey="webdav.pass">
            <div className="space-y-2">
              <label
                className="block text-sm font-medium text-base-content"
                htmlFor="webdav-pass-input"
              >
                WebDAV Password
              </label>
              <Input
                className="w-full"
                type="password"
                id="webdav-pass-input"
                aria-describedby="webdav-pass-help"
                value={config["webdav.pass"]}
                onChange={(e) => setNewConfig({ ...config, "webdav.pass": e.target.value })}
              />
              <p className="text-[11px] leading-relaxed text-base-content/45" id="webdav-pass-help">
                Use this password to connect to WebDAV.
              </p>
            </div>
          </ManagedSetting>
        </SettingsCard>

        <SettingsCard
          icon="folder_shared"
          title="Filesystem & Explorer"
          description="Choose how content appears to WebDAV clients and the Dav Explorer."
        >
          <ManagedSetting configKey="webdav.enforce-readonly">
            <Tooltip
              placement="bottom"
              content="Make the WebDAV /content folder read-only so clients cannot delete files there."
            >
              <Toggle
                id="readonly-checkbox"
                className="cursor-pointer gap-2 p-0"
                checked={config["webdav.enforce-readonly"] === "true"}
                onChange={(e) =>
                  setNewConfig({
                    ...config,
                    "webdav.enforce-readonly": String(e.target.checked),
                  })
                }
                label={<span className="text-sm text-base-content">Enforce Read-Only</span>}
              />
            </Tooltip>
          </ManagedSetting>

          <ManagedSetting configKey="webdav.windows-safe-paths">
            <Tooltip content='Replace characters invalid on Windows (<>:"/\|?*), trim trailing dots and spaces, and prefix reserved device names. Applies to newly mounted content only.'>
              <Toggle
                id="windows-safe-paths-checkbox"
                className="cursor-pointer gap-2 p-0"
                checked={config["webdav.windows-safe-paths"] !== "false"}
                onChange={(e) =>
                  setNewConfig({
                    ...config,
                    "webdav.windows-safe-paths": String(e.target.checked),
                  })
                }
                label={
                  <span className="text-sm text-base-content">Sanitize paths for Windows</span>
                }
              />
            </Tooltip>
          </ManagedSetting>

          <ManagedSetting configKey="webdav.show-hidden-files">
            <Tooltip content="Show files and directories whose names are prefixed by a period in Dav Explorer.">
              <Toggle
                id="show-hidden-files-checkbox"
                className="cursor-pointer gap-2 p-0"
                checked={config["webdav.show-hidden-files"] === "true"}
                onChange={(e) =>
                  setNewConfig({
                    ...config,
                    "webdav.show-hidden-files": String(e.target.checked),
                  })
                }
                label={
                  <span className="text-sm text-base-content">
                    Show hidden files on Dav Explorer
                  </span>
                }
              />
            </Tooltip>
          </ManagedSetting>

          <ManagedSetting configKey="webdav.preview-par2-files">
            <Tooltip content="Render par2 files as text in Dav Explorer, showing all File-Descriptor entries.">
              <Toggle
                id="preview-par2-files-checkbox"
                className="cursor-pointer gap-2 p-0"
                checked={config["webdav.preview-par2-files"] === "true"}
                onChange={(e) =>
                  setNewConfig({
                    ...config,
                    "webdav.preview-par2-files": String(e.target.checked),
                  })
                }
                label={
                  <span className="text-sm text-base-content">
                    Preview par2 files on Dav Explorer
                  </span>
                }
              />
            </Tooltip>
          </ManagedSetting>
        </SettingsCard>
      </div>
    </SettingsPage>
  );
}

export function isWebdavSettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
): boolean {
  return (
    config["webdav.user"] !== newConfig["webdav.user"] ||
    config["webdav.pass"] !== newConfig["webdav.pass"] ||
    config["webdav.show-hidden-files"] !== newConfig["webdav.show-hidden-files"] ||
    config["webdav.enforce-readonly"] !== newConfig["webdav.enforce-readonly"] ||
    config["webdav.preview-par2-files"] !== newConfig["webdav.preview-par2-files"] ||
    config["webdav.windows-safe-paths"] !== newConfig["webdav.windows-safe-paths"]
  );
}

export function isWebdavSettingsValid(config: Record<string, string>): boolean {
  return isValidUser(config["webdav.user"]);
}

function isValidUser(user: string | undefined): boolean {
  return user !== undefined && /^[A-Za-z0-9_-]+$/.test(user);
}
