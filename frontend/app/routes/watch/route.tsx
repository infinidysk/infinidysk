import { Outlet, useLoaderData } from "react-router";
import type { Route } from "./+types/route";
import { WatchHeader } from "~/components/watch/watch-header";

export function loader({ request }: Route.LoaderArgs) {
  return { query: (new URL(request.url).searchParams.get("q") ?? "").trim() };
}

/**
 * Catalog shell. `/watch` renders the catalog index and `/watch/title/:type/:id`
 * renders a title inside the same chrome, without the admin sidebar or banners.
 */
export default function WatchLayout() {
  const { query } = useLoaderData<typeof loader>();

  return (
    <div className="min-h-dvh bg-base-100 text-base-content">
      <WatchHeader query={query} />
      <main className="mx-auto w-full max-w-[1600px] px-4 pb-16 md:px-6">
        <Outlet />
      </main>
    </div>
  );
}
