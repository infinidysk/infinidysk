import { useState, type Dispatch, type SetStateAction } from "react";
import { Alert, Button, ManagedSetting, SettingsCard } from "~/components/ui";
import { PlexSources } from "./plex-sources";
import { PrefetchQueue } from "./prefetch-queue";
import {
  PREFETCH_KEY,
  hasSmartPrefetchSettingsChanged,
  parsePrefetchSettings,
  validatePrefetchSettings,
  type PrefetchSettings,
} from "./smart-prefetch-model";
import { SmartPrefetchPolicyControls } from "./smart-prefetch-policy-controls";
import { PlexServerStatus } from "./plex-server-status";
export function SmartPrefetchSettings({
  config,
  savedConfig,
  setNewConfig,
  persistConfigPatch,
}: {
  config: Record<string, string>;
  savedConfig: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
  persistConfigPatch: (patch: Record<string, string>) => Promise<void>;
}) {
  const [applyState, setApplyState] = useState<"idle" | "pending" | "success" | "error">("idle");
  let settings: PrefetchSettings | null = null;
  let error: string | null;
  try {
    settings = parsePrefetchSettings(config[PREFETCH_KEY]);
    error = validatePrefetchSettings(settings);
  } catch {
    error =
      "Saved Smart Prefetch settings are malformed. Correct the saved JSON or environment value before editing.";
  }
  const update = (next: PrefetchSettings) => {
    setApplyState("idle");
    setNewConfig((current) => ({ ...current, [PREFETCH_KEY]: JSON.stringify(next) }));
  };
  const isDirty = hasSmartPrefetchSettingsChanged(savedConfig, config);
  const applySourceChanges = async () => {
    const draft = config[PREFETCH_KEY];
    if (!draft || error) return;
    setApplyState("pending");
    try {
      await persistConfigPatch({ [PREFETCH_KEY]: draft });
      setApplyState("success");
    } catch {
      setApplyState("error");
    }
  };
  return (
    <div className="space-y-5">
      {error && <Alert variant="danger">{error}</Alert>}
      {settings && (
        <ManagedSetting configKey={PREFETCH_KEY}>
          <SettingsCard
            icon="auto_awesome"
            title="Smart Prefetch"
            description="Keep the next thing you watch ready. Save Native cache settings and restart before enabling."
          >
            <SmartPrefetchPolicyControls settings={settings} error={error} onChange={update} />
            <PlexServerStatus />
            <p className="text-xs text-base-content/60">
              Raw read activity is not verified Plex playback. Local-library eligibility still
              requires a symlink or STRM path that resolves to an imported DAV file; regular local
              files are skipped. Connection and concurrency caps do not create extra provider
              capacity, and all warming remains below foreground playback admission.
            </p>
          </SettingsCard>
          <details className="collapse collapse-arrow mt-5 border border-base-content/10 bg-base-200/40">
            <summary className="collapse-title text-sm font-semibold">
              Plex libraries and sources
            </summary>
            <div className="collapse-content">
              <SettingsCard
                icon="video_library"
                title="Plex libraries, users and sources"
                description="Choose movie or TV hubs and collections. Enable flags, per-source limits and excluded TV show IDs save with General Apply."
              >
                <PlexSources settings={settings} onChange={update} />
                <div className="mt-4 flex flex-wrap items-center gap-3 border-t border-base-content/10 pt-4">
                  <Button
                    type="button"
                    disabled={!isDirty || Boolean(error) || applyState === "pending"}
                    onClick={() => void applySourceChanges()}
                  >
                    {applyState === "pending" ? "Applying source changes…" : "Apply source changes"}
                  </Button>
                  {isDirty && applyState !== "success" && (
                    <span className="text-xs text-warning">Source changes not applied</span>
                  )}
                </div>
                {applyState === "success" && (
                  <Alert variant="success" className="mt-3">
                    Source changes applied.
                  </Alert>
                )}
                {applyState === "error" && (
                  <Alert variant="danger" className="mt-3">
                    Could not apply source changes. Try again.
                  </Alert>
                )}
              </SettingsCard>
            </div>
          </details>
        </ManagedSetting>
      )}
      <details className="collapse collapse-arrow border border-base-content/10 bg-base-200/40">
        <summary className="collapse-title text-sm font-semibold">Prefetch activity</summary>
        <div className="collapse-content">
          <PrefetchQueue />
        </div>
      </details>
    </div>
  );
}
