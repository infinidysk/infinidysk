import { Input } from "~/components/ui/form";
import { type Dispatch, type SetStateAction } from "react";

type LibrarySettingsProps = {
    savedConfig: Record<string, string>
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function LibrarySettings({ config, setNewConfig }: LibrarySettingsProps) {
    return (
        <div className={'space-y-6'}>
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
                    The organized library root that contains your imported Arr symlinks or STRMs
                    (the parent of your Radarr/Sonarr root folders). Must be visible inside the
                    InfiniDysk container. Do not point this at the rclone mount or at
                    <code className="text-base-content/70">/completed-symlinks</code> — those are
                    InfiniDysk&apos;s virtual filesystem, not your library.
                </p>
            </div>
        </div>
    );
}

export function isLibrarySettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["media.library-dir"] !== newConfig["media.library-dir"]
}