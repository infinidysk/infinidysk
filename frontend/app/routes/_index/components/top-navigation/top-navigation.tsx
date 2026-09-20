import { memo, useEffect, useRef } from "react";
import { Form, useNavigate } from "react-router";
import type { RequiredTopNavProps } from "../page-layout/page-layout";
import { LiveUsenetConnections } from "../live-usenet-connections/live-usenet-connections";
import { HeaderAlerts } from "./header-alerts";
import { LiveReadCount } from "./live-read-count";
import { Icon } from "~/components/ui";
import type { UpdateAvailable } from "~/utils/update-check";
import { withUrlBase } from "~/utils/url-base";

export type TopNavigationProps = RequiredTopNavProps & {
  updateAvailable?: UpdateAvailable | null;
  isFrontendAuthDisabled?: boolean;
  username?: string | null;
  hasUsenetProviders?: boolean;
};

export const TopNavigation = memo(function TopNavigation(props: TopNavigationProps) {
  const {
    isHamburgerMenuOpen,
    drawerToggleId,
    updateAvailable,
    isFrontendAuthDisabled,
    username,
    hasUsenetProviders,
  } = props;
  const navigate = useNavigate();
  const menusRef = useRef<HTMLDivElement>(null);
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
          onClick={() => {
            void navigate("/");
          }}
        >
          <img
            className="h-10 w-10 rounded-xl bg-gradient-to-br from-primary via-info to-success p-0.5 shadow-md shadow-primary/20"
            src={withUrlBase("/logo.png")}
            alt=""
          />
          <span className="flex flex-col items-start leading-none">
            <span className="text-xl font-bold tracking-tight text-primary">InfiniDysk</span>
            <span className="mt-1 hidden text-[10px] font-medium tracking-wide text-base-content/60 sm:block">
              The NzbDAV SuperFork
            </span>
          </span>
        </button>
      </div>

      <div
        ref={menusRef}
        className="navbar-end !w-auto ml-auto min-w-0 items-center gap-2 px-2 md:px-4"
      >
        <LiveReadCount />
        <LiveUsenetConnections hasUsenetProviders={!!hasUsenetProviders} />
        <HeaderAlerts hasUsenetProviders={!!hasUsenetProviders} updateAvailable={updateAvailable} />
        {showUserMenu && (
          <>
            <Form method="post" action="/logout" id="top-nav-logout" className="hidden">
              <input name="confirm" value="true" type="hidden" />
            </Form>
            <details className="dropdown dropdown-end" name="top-nav">
              <summary
                className="btn btn-ghost btn-circle h-10 min-h-10 w-10 p-0 list-none"
                aria-label="User menu"
              >
                <div className="avatar avatar-placeholder">
                  <div className="w-10 rounded-full bg-neutral text-neutral-content">
                    <span className="text-sm">{initial}</span>
                  </div>
                </div>
              </summary>
              <ul className="dropdown-content menu z-50 mt-2 w-56 rounded-box border border-base-content/10 bg-base-200 p-2 shadow-lg">
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
