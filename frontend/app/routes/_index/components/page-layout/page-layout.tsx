import { useCallback, useEffect, useId, useState } from "react";
import { useNavigation } from "react-router";
import type { ServiceProviderConfig } from "~/utils/service-provider";

export type PageLayoutProps = {
  topNavComponent: (props: RequiredTopNavProps) => React.ReactNode;
  leftNavChild: React.ReactNode;
  bodyChild: React.ReactNode;
  serviceProvider?: ServiceProviderConfig | null;
};

export type RequiredTopNavProps = {
  isHamburgerMenuOpen: boolean;
  onHamburgerMenuClick: () => void;
  drawerToggleId: string;
};

export function PageLayout(props: PageLayoutProps) {
  const drawerToggleId = useId();
  const [isHamburgerMenuOpen, setIsHamburgerMenuOpen] = useState(false);
  const isNavigating = Boolean(useNavigation().location);

  useEffect(() => {
    if (!isNavigating) setIsHamburgerMenuOpen(false);
  }, [isNavigating]);

  const onHamburgerMenuClick = useCallback(() => {
    setIsHamburgerMenuOpen((open) => !open);
  }, []);

  return (
    <div className="flex h-dvh flex-col overflow-hidden bg-base-300 text-base-content">
      <div className="navbar z-40 h-16 min-h-16 shrink-0 border-b border-base-content/10 bg-base-200/70 px-0 backdrop-blur">
        {props.topNavComponent({
          isHamburgerMenuOpen,
          onHamburgerMenuClick,
          drawerToggleId,
        })}
      </div>

      {/* Override daisyUI drawer-side 100dvh when sticky under the navbar. */}
      <div className="drawer min-h-0 flex-1 overflow-hidden lg:drawer-open">
        <input
          id={drawerToggleId}
          type="checkbox"
          className="drawer-toggle"
          checked={isHamburgerMenuOpen}
          onChange={(event) => setIsHamburgerMenuOpen(event.target.checked)}
        />
        <div className="drawer-content flex h-full min-h-0 min-w-0 flex-col overflow-hidden">
          <main className="yes-scrollbar relative h-full min-h-0 min-w-0 overflow-y-auto bg-base-300">
            <div className="flex min-h-full flex-col">
              <div className="min-h-0 flex-1">{props.bodyChild}</div>
              {props.serviceProvider?.name && (
                <footer className="footer sm:footer-horizontal footer-center border-t border-base-content/10 bg-base-300 px-4 py-4 text-xs text-base-content/50">
                  <aside>
                    <p>
                      This installation of{" "}
                      <a
                        href="https://www.infinidysk.com"
                        target="_blank"
                        rel="noreferrer"
                        className="link link-hover font-semibold"
                      >
                        InfiniDysk
                      </a>{" "}
                      is powered by{" "}
                      <a
                        href={props.serviceProvider.url}
                        target="_blank"
                        rel="noreferrer"
                        className="link link-hover font-semibold"
                      >
                        {props.serviceProvider.name}
                      </a>
                      .
                    </p>
                  </aside>
                </footer>
              )}
            </div>
          </main>
        </div>
        <div className="drawer-side z-50 lg:!h-full lg:!max-h-full">
          <label
            htmlFor={drawerToggleId}
            aria-label="Close navigation"
            className="drawer-overlay"
          />
          <aside className="flex h-full min-h-0 w-64 max-w-[85vw] flex-col border-r border-base-content/10 bg-base-300">
            {props.leftNavChild}
          </aside>
        </div>
      </div>
    </div>
  );
}
