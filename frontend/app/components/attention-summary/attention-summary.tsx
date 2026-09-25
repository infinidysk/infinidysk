import { useEffect, useRef, useState } from "react";
import { Link } from "react-router";
import type { ArrHealthResponse, ProviderCircuitBreakerRow } from "~/clients/backend-client.server";
import { adminApi } from "~/clients/admin-operations";
import { Icon } from "~/components/ui";
import { settingsPath } from "~/navigation/settings-tabs";
import { withUrlBase } from "~/utils/url-base";
import type { UpdateAvailable } from "~/utils/update-check";

const HEALTH_CHECK_TIMEOUT_MS = 5_000;

export function AttentionSummary({
  providers,
  arrHealth,
  hasConfiguredArrs,
  providersLoading = false,
  arrLoading = false,
  updateAvailable,
}: {
  providers:
    | Pick<
        ProviderCircuitBreakerRow,
        "provider" | "circuitState" | "nickname" | "lastFailureReason" | "consecutiveTrips"
      >[]
    | null;
  arrHealth: ArrHealthResponse | null;
  hasConfiguredArrs: boolean;
  providersLoading?: boolean;
  arrLoading?: boolean;
  updateAvailable?: UpdateAvailable | null | undefined;
}) {
  const [healthCount, setHealthCount] = useState<number | null>(null);
  const [healthError, setHealthError] = useState(false);
  const menuRef = useRef<HTMLDetailsElement>(null);
  const [isOpen, setIsOpen] = useState(false);
  const [seenAlerts, setSeenAlerts] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    let pending = false;
    const refresh = async () => {
      if (pending || document.hidden) return;
      pending = true;
      const requestController = new AbortController();
      const abortRequest = () => requestController.abort();
      const timeout = setTimeout(abortRequest, HEALTH_CHECK_TIMEOUT_MS);
      controller.signal.addEventListener("abort", abortRequest, { once: true });
      try {
        const response = await fetch(
          withUrlBase(
            `${adminApi.getHealthCheckHistory}?page=1&pageSize=1&currentActionNeeded=true`,
          ),
          { signal: requestController.signal },
        );
        if (!response.ok) throw new Error("Health status unavailable");
        const data = (await response.json()) as { totalCount?: number };
        if (
          typeof data.totalCount !== "number" ||
          !Number.isFinite(data.totalCount) ||
          data.totalCount < 0
        )
          throw new Error("Invalid health count");
        if (!controller.signal.aborted) {
          setHealthCount(data.totalCount);
          setHealthError(false);
        }
      } catch {
        if (!controller.signal.aborted) {
          setHealthCount(null);
          setHealthError(true);
        }
      } finally {
        clearTimeout(timeout);
        controller.signal.removeEventListener("abort", abortRequest);
        pending = false;
      }
    };
    void refresh();
    const interval = setInterval(() => void refresh(), 60_000);
    const onVisible = () => {
      if (!document.hidden) void refresh();
    };
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      controller.abort();
      clearInterval(interval);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, []);

  const affectedProviders = providers?.filter(
    (provider) => provider.circuitState === "open" || provider.circuitState === "halfOpen",
  );
  const stalledProviders = affectedProviders?.filter(
    (provider) => (provider.consecutiveTrips ?? 0) >= 3,
  );
  const recentlyAffectedProviders = affectedProviders?.filter(
    (provider) => (provider.consecutiveTrips ?? 0) < 3,
  );
  const affectedArrs = arrHealth?.instances.filter(
    (instance) => instance.status === "degraded" || instance.status === "offline",
  );
  const pendingArrs = arrHealth?.instances.some((instance) => instance.status === "pending");
  const providersUnavailable = affectedProviders == null && !providersLoading;
  const arrUnavailable = hasConfiguredArrs && affectedArrs == null && !arrLoading;

  const alertSignature = JSON.stringify([
    updateAvailable,
    healthCount || healthError,
    affectedProviders?.map((provider) => [provider.provider, provider.circuitState]),
    stalledProviders?.map((provider) => provider.provider),
    hasConfiguredArrs
      ? (affectedArrs?.map((instance) => [
          instance.key,
          instance.status,
          instance.hasWarnings,
          instance.hasErrors,
        ]) ?? "unavailable")
      : [],
  ]);
  const hasAlerts = Boolean(
    updateAvailable ||
    healthError ||
    healthCount ||
    providersUnavailable ||
    affectedProviders?.length ||
    arrUnavailable ||
    (hasConfiguredArrs && affectedArrs?.length),
  );
  const isChecking =
    (healthCount == null && !healthError) ||
    providersLoading ||
    arrLoading ||
    (hasConfiguredArrs && pendingArrs);
  const label = hasAlerts
    ? "Alerts: needs attention"
    : isChecking
      ? "Alerts: checking status"
      : "Alerts: no issues need attention";
  const statusMessage = stalledProviders?.length
    ? `${stalledProviders.length} provider ${stalledProviders.length === 1 ? "circuit" : "circuits"} repeatedly open`
    : label;

  return (
    <details
      ref={menuRef}
      className="dropdown dropdown-end"
      name="top-nav"
      onToggle={(event) => {
        setIsOpen(event.currentTarget.open);
        setSeenAlerts(alertSignature);
      }}
      onKeyDown={(event) => {
        if (event.key === "Escape" && menuRef.current?.open) {
          menuRef.current.open = false;
          menuRef.current.querySelector("summary")?.focus();
          event.stopPropagation();
        }
      }}
    >
      <summary
        aria-label={label}
        title={label}
        className={`btn btn-ghost btn-square relative h-10 min-h-10 w-10 shrink-0 list-none ${hasAlerts ? "text-warning" : "text-base-content/70"}`}
      >
        <span
          key={alertSignature}
          className={
            hasAlerts && !isOpen && seenAlerts !== alertSignature
              ? "motion-safe:animate-[pulse_1.2s_ease-in-out_3]"
              : undefined
          }
        >
          <Icon name="notifications" className="!text-[22px]" />
        </span>
        {hasAlerts && (
          <span
            aria-hidden="true"
            className="absolute right-1.5 top-1.5 h-1.5 w-1.5 rounded-full bg-warning"
          />
        )}
      </summary>
      <span className="sr-only" role="status" aria-live="polite">
        {statusMessage}
      </span>
      <section
        aria-label="Alerts"
        className="dropdown-content z-50 mt-2 max-h-[min(32rem,calc(100dvh-5rem))] w-88 max-w-[calc(100vw-2rem)] overflow-y-auto rounded-box border border-base-content/15 bg-base-200 text-base-content shadow-lg max-sm:fixed max-sm:left-4 max-sm:right-4 max-sm:w-auto"
        onClick={(event) => {
          if ((event.target as HTMLElement).closest("a") && menuRef.current)
            menuRef.current.open = false;
        }}
      >
        <h2 className="border-b border-base-content/10 px-4 py-3 text-sm font-semibold">Alerts</h2>
        <div className="space-y-1 p-2">
          {updateAvailable && (
            <a
              href={
                updateAvailable.kind === "release"
                  ? updateAvailable.releaseUrl
                  : updateAvailable.compareUrl
              }
              target="_blank"
              rel="noreferrer"
              className="flex min-h-11 items-center gap-2 rounded-sm px-2 py-2 text-sm text-warning hover:bg-base-content/5 focus-visible:outline-2 focus-visible:outline-primary"
            >
              <Icon name="arrow_circle_up" className="shrink-0 !text-[18px]" />
              <span className="min-w-0 flex-1 [overflow-wrap:anywhere]">
                {updateAvailable.kind === "release"
                  ? `Update to v${updateAvailable.latestVersion}`
                  : `${updateAvailable.commitsBehind} new ${updateAvailable.commitsBehind === 1 ? "commit" : "commits"} on ${updateAvailable.trackRef}`}
              </span>
              <Icon name="open_in_new" className="shrink-0 !text-[18px]" />
            </a>
          )}
          {(healthError || (healthCount != null && healthCount > 0)) && (
            <AttentionLink to="/health" icon="health_and_safety" warning>
              {healthError
                ? "Health status unavailable"
                : `${healthCount!.toLocaleString()} ${healthCount === 1 ? "file needs" : "files need"} attention`}
            </AttentionLink>
          )}
          {(providersUnavailable || !!recentlyAffectedProviders?.length) && (
            <AttentionLink to={settingsPath("usenet")} icon="cloud" warning>
              {recentlyAffectedProviders == null
                ? "Provider status unavailable"
                : `${recentlyAffectedProviders.length} provider ${recentlyAffectedProviders.length === 1 ? "circuit" : "circuits"} open or recovering`}
            </AttentionLink>
          )}
          {!!stalledProviders?.length && (
            <div className="min-w-0 px-2 py-2">
              <div className="mb-3 flex items-center gap-2 text-sm text-warning">
                <Icon name="cloud" className="shrink-0 !text-[18px]" />
                <span className="min-w-0 flex-1">
                  {stalledProviders.length} provider{" "}
                  {stalledProviders.length === 1 ? "circuit" : "circuits"} repeatedly open
                </span>
              </div>
              <ul className="space-y-3 pb-2 text-sm">
                {stalledProviders.map((provider) => (
                  <li key={provider.provider} className="min-w-0 [overflow-wrap:anywhere]">
                    <p className="font-semibold text-base-content">
                      {provider.nickname ?? provider.provider} unreachable
                    </p>
                    {provider.lastFailureReason && (
                      <p className="mt-1 whitespace-pre-wrap text-base-content/80">
                        {provider.lastFailureReason}
                      </p>
                    )}
                    <p className="text-base-content/80">
                      Missing-article checks are paused while this provider stays enabled: affected
                      reads answer try again and repairs wait. Files parked as Action needed
                      re-check on their own once it recovers.
                    </p>
                  </li>
                ))}
              </ul>
              <Link to={settingsPath("usenet")} className="link text-sm text-base-content/80">
                Disable or fix it in Usenet settings
              </Link>
            </div>
          )}
          {hasConfiguredArrs && !!affectedArrs?.length ? (
            <div className="min-w-0 px-2 py-2">
              <div className="mb-3 flex items-center gap-2 text-sm text-warning">
                <Icon name="sync_alt" className="shrink-0 !text-[18px]" />
                <span className="min-w-0 flex-1">
                  {affectedArrs.length} Arr{" "}
                  {affectedArrs.length === 1 ? "integration" : "integrations"} degraded or offline
                </span>
              </div>
              <ul className="space-y-3 pb-2 text-sm">
                {affectedArrs.map((instance) => (
                  <li key={instance.key} className="min-w-0 [overflow-wrap:anywhere]">
                    <p className="font-semibold text-base-content">
                      <span className="capitalize">{instance.appType}</span>: {instance.name}
                    </p>
                    <p className="text-base-content/80">
                      {instance.status === "offline"
                        ? "InfiniDysk could not poll this instance. Check its availability and connection settings."
                        : instance.hasWarnings && instance.hasErrors
                          ? "This app reports queue warnings and errors. Check Activity > Queue in the Arr app."
                          : instance.hasErrors
                            ? "This app reports queue errors. Check Activity > Queue in the Arr app."
                            : instance.hasWarnings
                              ? "This app reports queue warnings. Check Activity > Queue in the Arr app."
                              : "Imports are taking longer than expected. Check Activity > Queue in the Arr app."}
                    </p>
                    {instance.status === "offline" && instance.lastError && (
                      <p className="mt-1 whitespace-pre-wrap text-base-content/80">
                        {instance.lastError}
                      </p>
                    )}
                  </li>
                ))}
              </ul>
              <Link to={settingsPath("arrs")} className="link text-sm text-base-content/80">
                Arr connection settings
              </Link>
            </div>
          ) : (
            arrUnavailable && (
              <AttentionLink to={settingsPath("arrs")} icon="sync_alt" warning>
                Arr status unavailable
              </AttentionLink>
            )
          )}
          {isChecking && (
            <p className="px-2 py-3 text-sm text-base-content/70">Checking status...</p>
          )}
          {!hasAlerts && !isChecking && (
            <p className="flex items-center gap-2 px-2 py-4 text-sm text-base-content/80">
              <Icon name="check_circle" className="!text-[20px] text-success" />
              No issues need attention
            </p>
          )}
        </div>
      </section>
    </details>
  );
}

function AttentionLink({
  to,
  icon,
  warning,
  children,
}: {
  to: string;
  icon: string;
  warning: boolean;
  children: React.ReactNode;
}) {
  return (
    <Link
      to={to}
      className={`flex min-h-11 min-w-0 items-center gap-2 rounded-sm px-2 py-2 text-sm hover:bg-base-content/5 focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary ${warning ? "text-warning" : "text-base-content/80"}`}
    >
      <Icon name={icon} className="shrink-0 !text-[18px]" />
      <span className="min-w-0 flex-1">{children}</span>
      <Icon name="chevron_right" className="shrink-0 !text-[18px]" />
    </Link>
  );
}
