import { Input } from "~/components/ui/form";
import { type Dispatch, type SetStateAction } from "react";

type LibrarySettingsProps = {
  savedConfig: Record<string, string>;
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

export function LibrarySettings({ config, setNewConfig }: LibrarySettingsProps) {
  return (
    <div className={"space-y-6"}>
      <div className="space-y-2">
        <label className="block text-sm font-medium text-base-content" htmlFor="library-dir-input">
          Library Directory
        </label>
        <Input
          className={"w-full"}
          type="text"
          id="library-dir-input"
          aria-describedby="library-dir-help"
          value={config["media.library-dir"]}
          onChange={(e) => setNewConfig({ ...config, "media.library-dir": e.target.value })}
        />
        <p className="text-[11px] leading-relaxed text-base-content/45" id="library-dir-help">
          The path to your organized media library that contains all your imported symlinks. Make
          sure this path is visible to your InfiniDysk container.
        </p>
      </div>
    </div>
  );
}

export function isLibrarySettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
) {
  return config["media.library-dir"] !== newConfig["media.library-dir"];
}
