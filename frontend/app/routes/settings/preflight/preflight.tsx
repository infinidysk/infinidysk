import { type Dispatch, type SetStateAction } from "react";
import { Field, Input, Label, ManagedSetting, Select, SettingsCard, SettingsIntro, SettingsPage } from "~/components/ui";

type PreflightSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function PreflightSettings({ config, setNewConfig }: PreflightSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const mode = config["preflight.mode"] ?? "off";
    const enabled = mode !== "off";

    return (
        <SettingsPage>
            <SettingsIntro>
                When a client asks for the list of available articles, InfiniDysk can quietly
                do upfront work on the top-ranked ones so the next request reuses that warm
                state instead of redoing everything from scratch. The harder the mode, the
                more it does.
            </SettingsIntro>

            <SettingsCard
                icon="fact_check"
                title="Preflight behavior"
                description="Choose how much speculative work runs and how long its warm state is reused."
                contentClassName="grid grid-cols-1 gap-4 lg:grid-cols-2"
            >
            <ManagedSetting configKey="preflight.mode">
            <Field>
                <Label htmlFor="preflight-mode">Mode</Label>
                <Select
                    id="preflight-mode"
                    className="w-full max-w-md"
                    value={mode}
                    onChange={e => set("preflight.mode", e.target.value)}>
                    <option value="off">off — no background work</option>
                    <option value="light">light — quick existence check on the top results</option>
                    <option value="standard">standard — light + cache the article descriptor</option>
                    <option value="full">full — standard + resolve archive layout for previously completed items</option>
                </Select>
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    <b>light</b> performs a cheap existence check against your provider, so missing
                    articles are skipped without re-asking the indexer.
                    <b> standard</b> additionally caches the article descriptor locally so the next
                    request skips the indexer round-trip entirely.
                    <b> full</b> additionally resolves trailing-archive metadata for any top result
                    that maps to a previously completed item — useful when re-opening something.
                </p>
            </Field>
            </ManagedSetting>

            <ManagedSetting configKey="preflight.max-attempts">
            <Field>
                <Label htmlFor="preflight-max-attempts">Max candidates to try</Label>
                <Input
                    id="preflight-max-attempts"
                    className="w-full max-w-md"
                    type="number"
                    min={1}
                    max={50}
                    disabled={!enabled}
                    value={config["preflight.max-attempts"] ?? "20"}
                    onChange={e => set("preflight.max-attempts", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    Walks the top-ranked results one at a time and stops on the first one that
                    passes the check. So a missing top result automatically falls through to
                    the next one — same idea as the watchdog at click time, but in the
                    background. Default 20.
                </p>
            </Field>
            </ManagedSetting>

            <ManagedSetting configKey="preflight.ttl-seconds">
            <Field>
                <Label htmlFor="preflight-ttl">Keep preflight state for (seconds)</Label>
                <Input
                    id="preflight-ttl"
                    className="w-full max-w-md"
                    type="number"
                    min={10}
                    max={1800}
                    disabled={!enabled}
                    value={config["preflight.ttl-seconds"] ?? "120"}
                    onChange={e => set("preflight.ttl-seconds", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    How long a preflighted result stays warm before it's discarded. Long enough
                    to scroll through and pick something, short enough not to hold stale state.
                    Default 120.
                </p>
            </Field>
            </ManagedSetting>

            <ManagedSetting configKey="preflight.indexer-max-wait-seconds">
            <Field>
                <Label htmlFor="preflight-max-wait">Skip if indexer wait exceeds (seconds)</Label>
                <Input
                    id="preflight-max-wait"
                    className="w-full max-w-md"
                    type="number"
                    min={0}
                    max={120}
                    disabled={!enabled}
                    value={config["preflight.indexer-max-wait-seconds"] ?? "5"}
                    onChange={e => set("preflight.indexer-max-wait-seconds", e.target.value)} />
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    Preflight is best-effort: if an indexer's rate limit would force it to wait
                    longer than this before a request can fire, preflight on that result is
                    skipped. Keeps real requests from being queued behind speculative work.
                    Default 5.
                </p>
            </Field>
            </ManagedSetting>
            </SettingsCard>
        </SettingsPage>
    );
}

export function isPreflightSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["preflight.mode"] !== newConfig["preflight.mode"]
        || config["preflight.max-attempts"] !== newConfig["preflight.max-attempts"]
        || config["preflight.ttl-seconds"] !== newConfig["preflight.ttl-seconds"]
        || config["preflight.indexer-max-wait-seconds"] !== newConfig["preflight.indexer-max-wait-seconds"];
}
