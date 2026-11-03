import { Form, Link, useOutletContext } from "react-router";
import { Icon } from "~/components/ui";
import type { AppOutletContext } from "~/auth/authorization";
import { ProfileAvatar } from "~/components/watch/profile-avatar";
import { withUrlBase } from "~/utils/url-base";

export type WatchHeaderProps = {
  /** Current search text, so the box keeps its value across navigations. */
  query: string;
};

/**
 * Catalog chrome: the product mark, home/search controls, and the way back into the
 * admin UI. Deliberately separate from the admin shell — /watch renders no sidebar.
 */
export function WatchHeader({ query }: WatchHeaderProps) {
  const { role, viewerProfile } = useOutletContext<AppOutletContext>();

  return (
    <header className="sticky top-0 z-30 border-b border-base-content/10 bg-base-100/85 backdrop-blur">
      <div className="mx-auto flex w-full max-w-[1600px] flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3 md:px-6">
        <Link to="/watch" className="flex items-center gap-2" aria-label="InfiniDysk Watch home">
          <img src={withUrlBase("/logo.png")} alt="" className="h-8 w-8" />
          <span className="text-lg font-bold tracking-tight">InfiniDysk</span>
        </Link>

        <nav aria-label="Watch" className="flex items-center gap-1 text-sm">
          <Link to="/watch" className="btn btn-ghost btn-sm gap-1">
            <Icon name="home" className="!text-[18px]" />
            Home
          </Link>
        </nav>

        <form
          method="get"
          action={withUrlBase("/watch")}
          role="search"
          className="ml-auto flex items-center gap-2"
        >
          <label className="input input-sm flex w-52 items-center gap-2 sm:w-64">
            <Icon name="search" className="!text-[16px] text-base-content/50" />
            <input
              type="search"
              name="q"
              defaultValue={query}
              placeholder="Search movies and series"
              aria-label="Search movies and series"
              className="grow"
            />
          </label>
        </form>

        {role === "profile" ? (
          <details className="dropdown dropdown-end">
            <summary className="btn btn-ghost btn-sm gap-2">
              <ProfileAvatar name={viewerProfile?.name ?? "Profile"} className="h-6 w-6 text-xs" />
              {viewerProfile?.name ?? "Profile"}
            </summary>
            <ul className="menu dropdown-content z-40 mt-2 w-48 rounded-box border border-base-content/10 bg-base-100 p-2 shadow-lg">
              <li>
                <Form method="post" action={withUrlBase("/logout")}>
                  <input name="confirm" value="true" type="hidden" />
                  <button type="submit">Switch profile</button>
                </Form>
              </li>
              <li>
                <Link to="/logout">Sign out</Link>
              </li>
            </ul>
          </details>
        ) : (
          <Link to="/overview" className="btn btn-ghost btn-sm gap-1">
            <Icon name="admin_panel_settings" className="!text-[18px]" />
            Admin
          </Link>
        )}
      </div>
    </header>
  );
}
