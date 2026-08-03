import { memo, useEffect, useRef } from "react";
import { Form, useNavigate } from "react-router";
import type { RequiredTopNavProps } from "../page-layout/page-layout";
import { LiveUsenetConnections } from "../live-usenet-connections/live-usenet-connections";
import { Icon } from "~/components/ui";
import { isComparableVersion, type UpdateAvailable } from "~/utils/update-check";

export type TopNavigationProps = RequiredTopNavProps & {
  version?: string,
  updateAvailable?: UpdateAvailable | null,
  isFrontendAuthDisabled?: boolean,
  username?: string | null,
  hasUsenetProviders?: boolean,
};

export const TopNavigation = memo(function TopNavigation(props: TopNavigationProps) {
  const {
    isHamburgerMenuOpen,
    drawerToggleId,
    version,
    updateAvailable,
    isFrontendAuthDisabled,
    username,
    hasUsenetProviders,
  } = props;
  const navigate = useNavigate();
  const menusRef = useRef<HTMLDivElement>(null);
  const displayVersion = version || "unknown";
  const hasUpdate = Boolean(updateAvailable);
  const channelLabel = isComparableVersion(version) ? "Stable" : "Dev";
  const showUserMenu = !isFrontendAuthDisabled && Boolean(username);
  const initial = username?.trim().charAt(0).toUpperCase() || "?";

  useEffect(() => {
    function closeOpenMenusOnOutsidePointer(event: PointerEvent) {
      const root = menusRef.current;
      if (!root) return;

      const target = event.target;
      if (!(target instanceof Node)) return;

      for (const menu of root.querySelectorAll<HTMLDetailsElement>("details.dropdown")) {
        if (!menu.open) continue;
        if (!menu.contains(target)) {
          menu.open = false;
        }
      }
    }

    document.addEventListener("pointerdown", closeOpenMenusOnOutsidePointer);
    return () => document.removeEventListener("pointerdown", closeOpenMenusOnOutsidePointer);
  }, []);

  return (
    <>
      <div className="navbar-start !w-auto shrink-0 gap-1 px-2 md:px-4">
        <label
          htmlFor={drawerToggleId}
          aria-label={isHamburgerMenuOpen ? "Close navigation" : "Open navigation"}
          aria-expanded={isHamburgerMenuOpen}
          className="btn btn-ghost btn-square btn-sm lg:hidden"
        >
          <Icon name={isHamburgerMenuOpen ? "close" : "menu"} className="!text-[24px]" />
        </label>
        <button
          type="button"
          className="btn btn-ghost gap-3 px-2"
          onClick={() => navigate("/")}
        >
          <img className="h-8 w-7" src="/logo.svg" alt="" />
          <span className="text-xl font-bold tracking-tight">NzbDAV</span>
        </button>
      </div>

      <div
        ref={menusRef}
        className="navbar-end !w-auto ml-auto min-w-0 items-center gap-2 px-2 md:px-4"
      >
        <LiveUsenetConnections hasUsenetProviders={!!hasUsenetProviders} />
        <details className="dropdown dropdown-end" name="top-nav">
          <summary
            className={
              hasUpdate
                ? "btn btn-primary h-10 min-h-10 shrink-0 list-none gap-2 rounded-box px-4 whitespace-nowrap"
                : "list-none rounded-box bg-gradient-to-br from-primary via-secondary to-accent p-px"
            }
            aria-label={hasUpdate ? "Update available" : "App menu"}
          >
            {hasUpdate ? (
              <>
                <Icon name="arrow_circle_up" className="!text-[20px]" />
                <span className="text-sm font-semibold">Update available</span>
              </>
            ) : (
              <span className="btn btn-ghost h-10 min-h-10 shrink-0 gap-2 rounded-[calc(var(--radius-box)-1px)] border-0 bg-base-200 px-4 whitespace-nowrap hover:bg-base-200 pointer-events-none">
                <span className="inline-flex items-center gap-2 whitespace-nowrap">
                  <span className="text-[10px] font-semibold uppercase tracking-[0.14em] text-base-content/40">
                    {channelLabel}
                  </span>
                  <span className="h-3 w-px bg-base-content/15" aria-hidden="true" />
                  <span className="font-mono text-sm tracking-tight text-base-content/80">
                    {displayVersion}
                  </span>
                </span>
                <Icon name="expand_more" className="!text-[18px] text-base-content/50" />
              </span>
            )}
          </summary>
          <ul
            className="dropdown-content menu z-50 mt-2 w-64 rounded-box border border-base-content/10 bg-base-200 p-2 shadow-lg"
          >
            <li className="menu-title">
              <span className="flex items-center justify-between gap-2">
                <span>NzbDAV {channelLabel}</span>
                <span className="font-mono font-normal normal-case tracking-normal">
                  {displayVersion}
                </span>
              </span>
            </li>
            {updateAvailable?.kind === "release" && (
              <li>
                <a
                  href={updateAvailable.releaseUrl}
                  target="_blank"
                  rel="noreferrer"
                  className="bg-primary/15 font-medium text-primary"
                >
                  <Icon name="arrow_circle_up" className="!text-[18px]" />
                  Update to v{updateAvailable.latestVersion}
                </a>
              </li>
            )}
            {updateAvailable?.kind === "dev" && (
              <li>
                <a
                  href={updateAvailable.compareUrl}
                  target="_blank"
                  rel="noreferrer"
                  className="bg-primary/15 font-medium text-primary"
                >
                  <Icon name="arrow_circle_up" className="!text-[18px]" />
                  {updateAvailable.commitsBehind === 1
                    ? `1 new commit on ${updateAvailable.trackRef}`
                    : `${updateAvailable.commitsBehind} new commits on ${updateAvailable.trackRef}`}
                </a>
              </li>
            )}
            <li>
              <a
                href="https://github.com/nzbdav/nzbdav"
                target="_blank"
                rel="noreferrer"
              >
                <Icon name="code" className="!text-[18px]" />
                GitHub
              </a>
            </li>
            <li>
              <a
                href="https://github.com/nzbdav/nzbdav/releases"
                target="_blank"
                rel="noreferrer"
              >
                <Icon name="history" className="!text-[18px]" />
                Changelog
              </a>
            </li>
          </ul>
        </details>
        {showUserMenu && (
          <>
            <Form method="post" action="/logout" id="top-nav-logout" className="hidden">
              <input name="confirm" value="true" type="hidden" />
            </Form>
            <details className="dropdown dropdown-end" name="top-nav">
              <summary
                className="btn btn-ghost btn-circle btn-sm list-none"
                aria-label="User menu"
              >
                <div className="avatar avatar-placeholder">
                  <div className="w-8 rounded-full bg-neutral text-neutral-content">
                    <span className="text-xs">{initial}</span>
                  </div>
                </div>
              </summary>
              <ul
                className="dropdown-content menu z-50 mt-2 w-56 rounded-box border border-base-content/10 bg-base-200 p-2 shadow-lg"
              >
                <li className="menu-title">
                  <span>{username}</span>
                </li>
                <li>
                  <button type="submit" form="top-nav-logout">
                    <Icon name="logout" className="!text-[18px]" />
                    Logout
                  </button>
                </li>
              </ul>
            </details>
          </>
        )}
      </div>
    </>
  );
});
