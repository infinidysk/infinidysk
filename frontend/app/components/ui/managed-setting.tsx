import { createContext, useContext, type ReactNode } from "react";
import { Icon } from "~/components/ui/icon";

/** configName → NZBDAV_CONFIG__... environment variable name */
export type ManagedEnvMap = Record<string, string>;

const ManagedEnvContext = createContext<ManagedEnvMap>({});

export function ManagedEnvProvider({
  value,
  children,
}: {
  value: ManagedEnvMap;
  children: ReactNode;
}) {
  return <ManagedEnvContext.Provider value={value}>{children}</ManagedEnvContext.Provider>;
}

export function useManagedEnvMap(): ManagedEnvMap {
  return useContext(ManagedEnvContext);
}

export function useManagedEnv(configKey: string): string | undefined {
  return useContext(ManagedEnvContext)[configKey];
}

export function useIsAnyManaged(configKeys: string[]): boolean {
  const map = useContext(ManagedEnvContext);
  return configKeys.some((key) => key in map);
}

/**
 * Accessible read-only wrapper for settings managed by an authoritative
 * NZBDAV_CONFIG__... environment variable. Disables nested controls via
 * fieldset[disabled] and names the exact ENV var operators must change.
 */
export function ManagedSetting({
  configKey,
  configKeys,
  children,
  className = "",
}: {
  configKey?: string;
  configKeys?: string[];
  children: ReactNode;
  className?: string;
}) {
  const map = useManagedEnvMap();
  const keys = configKeys ?? (configKey ? [configKey] : []);
  const managed = keys
    .map((key) => ({ key, env: map[key] }))
    .filter((entry): entry is { key: string; env: string } => Boolean(entry.env));

  if (managed.length === 0) {
    return <div className={className}>{children}</div>;
  }

  const envNames = [...new Set(managed.map((entry) => entry.env))];
  const label =
    envNames.length === 1
      ? `Managed by ${envNames[0]}`
      : `Managed by environment (${envNames.length} variables)`;

  return (
    <fieldset
      disabled
      className={`relative m-0 min-w-0 rounded-lg border border-warning/30 bg-warning/5 p-3 ${className}`}
      aria-label={label}
    >
      <legend className="float-right mb-2 flex max-w-full items-center gap-1.5 rounded-md border border-warning/40 bg-base-100 px-2 py-1 text-[11px] font-medium text-warning">
        <Icon name="lock" className="!text-[14px] shrink-0" />
        <span className="min-w-0 truncate" title={envNames.join(", ")}>
          {envNames.length === 1 ? (
            <>
              Managed by <code className="font-mono text-[10px]">{envNames[0]}</code>
            </>
          ) : (
            <>Managed by environment</>
          )}
        </span>
      </legend>
      <div className="clear-both opacity-80">{children}</div>
      {envNames.length > 1 && (
        <p className="mt-2 text-[11px] leading-relaxed text-base-content/55">
          Variables:{" "}
          {envNames.map((name, index) => (
            <span key={name}>
              {index > 0 ? ", " : ""}
              <code className="font-mono text-[10px]">{name}</code>
            </span>
          ))}
        </p>
      )}
    </fieldset>
  );
}

/** Drop ENV-managed keys from a settings save payload. */
export function omitManagedConfigKeys(
  changed: Record<string, string>,
  managed: ManagedEnvMap,
): Record<string, string> {
  const next: Record<string, string> = {};
  for (const [key, value] of Object.entries(changed)) {
    if (!(key in managed)) next[key] = value;
  }
  return next;
}

/** Keep ENV-managed keys pinned to their effective (loaded) values. */
export function pinManagedConfigKeys(
  next: Record<string, string>,
  baseline: Record<string, string>,
  managed: ManagedEnvMap,
): Record<string, string> {
  if (Object.keys(managed).length === 0) return next;
  const pinned = { ...next };
  for (const key of Object.keys(managed)) {
    if (key in baseline) {
      const value = baseline[key];
      if (value !== undefined) pinned[key] = value;
    }
  }
  return pinned;
}
