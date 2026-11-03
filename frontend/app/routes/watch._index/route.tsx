import { Link, useLoaderData, useNavigation, useRevalidator } from "react-router";
import { useEffect, useRef, useState } from "react";
import type { Route } from "./+types/route";
import { Alert, Button, Icon } from "~/components/ui";
import { ContinueWatchingRow } from "~/components/watch/continue-watching-row";
import { TitleCard } from "~/components/watch/title-card";
import { TitleRow } from "~/components/watch/title-row";
import { fetchCatalogSearch } from "~/clients/catalog-client";
import { backendClient } from "~/clients/backend-client.server";
import type {
  CatalogHistoryItem,
  CatalogRow,
  CatalogSearchPage,
  CatalogSummary,
} from "~/clients/catalog-contract";

type WatchLoaderData = {
  mode: "home" | "search";
  query: string;
  rows: CatalogRow[];
  /** Continue Watching, richest form: position and episode coordinates per title. */
  history: CatalogHistoryItem[];
  page: CatalogSearchPage | null;
  error: string | null;
};

export async function loader({ request }: Route.LoaderArgs): Promise<WatchLoaderData> {
  const query = (new URL(request.url).searchParams.get("q") ?? "").trim();

  if (query.length === 0) {
    try {
      const [home, history] = await Promise.all([
        backendClient.getCatalogHome(),
        // A history failure must not blank the catalog: it costs one row, not the page.
        backendClient.getCatalogHistory(20).catch(() => null),
      ]);
      return {
        mode: "home",
        query: "",
        rows: home.rows,
        history: history?.items ?? [],
        page: null,
        error: null,
      };
    } catch (cause) {
      return {
        mode: "home",
        query: "",
        rows: [],
        history: [],
        page: null,
        error:
          cause instanceof Error ? cause.message : "The catalog could not be loaded right now.",
      };
    }
  }

  if (query.length > 100) {
    return {
      mode: "search",
      query,
      rows: [],
      history: [],
      page: null,
      error: "Search queries are limited to 100 characters.",
    };
  }

  try {
    const page = await backendClient.searchCatalog(query, 1);
    return { mode: "search", query, rows: [], history: [], page, error: null };
  } catch (cause) {
    return {
      mode: "search",
      query,
      rows: [],
      history: [],
      page: null,
      error:
        cause instanceof Error ? cause.message : "The catalog could not be searched right now.",
    };
  }
}

export default function WatchHomeRoute() {
  const data = useLoaderData<typeof loader>();
  const navigation = useNavigation();
  const isNavigating =
    navigation.state !== "idle" && (navigation.location?.pathname ?? "").startsWith("/watch");

  if (isNavigating) return <CatalogSkeletons />;
  if (data.mode === "home")
    return <CatalogHomeView rows={data.rows} history={data.history} error={data.error} />;
  return (
    <CatalogSearchView key={data.query} query={data.query} page={data.page} error={data.error} />
  );
}

function CatalogSkeletons() {
  return (
    <div className="space-y-6 py-6" aria-hidden="true">
      <div className="skeleton h-48 w-full rounded-xl" />
      {[0, 1].map((row) => (
        <div key={row} className="space-y-3">
          <div className="skeleton h-5 w-40 rounded" />
          <div className="flex gap-3 overflow-hidden">
            {[0, 1, 2, 3, 4, 5].map((card) => (
              <div key={card} className="skeleton aspect-[2/3] w-36 shrink-0 rounded-lg sm:w-44 lg:w-48" />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

function CatalogHomeView({
  rows,
  history,
  error,
}: {
  rows: CatalogRow[];
  history: CatalogHistoryItem[];
  error: string | null;
}) {
  const revalidator = useRevalidator();
  const hero = rows.find((row) => row.id === "trending")?.items?.[0] ?? null;

  if (error !== null) {
    return (
      <div className="py-8">
        <Alert variant="warning" className="alert-soft" role="alert">
          <Icon name="error" className="!text-[18px]" />
          <span>{error}</span>
          <Button variant="warning" size="xsmall" onClick={() => void revalidator.revalidate()}>
            <Icon name="refresh" className="!text-[16px]" />
            Retry
          </Button>
        </Alert>
      </div>
    );
  }

  return (
    <>
      {hero && <CatalogHero item={hero} />}
      <ContinueWatchingRow items={history} />
      {rows.map((row) => (
        <TitleRow key={row.id} row={row} />
      ))}
    </>
  );
}

function CatalogHero({ item }: { item: CatalogSummary }) {
  return (
    <section className="relative mt-4 overflow-hidden rounded-xl border border-base-content/10 bg-base-200">
      {item.backdropUrl && (
        <img src={item.backdropUrl} alt="" className="absolute inset-0 h-full w-full object-cover opacity-45" />
      )}
      <div className="relative flex flex-col gap-3 bg-gradient-to-t from-base-100 via-base-100/60 to-transparent p-6 md:p-10">
        <h1 className="max-w-3xl text-3xl font-bold tracking-tight md:text-4xl">{item.title}</h1>
        <p className="text-sm text-base-content/60">
          {[item.year, item.rating ? `${item.rating.toFixed(1)}/10` : null]
            .filter(Boolean)
            .join(" · ")}
        </p>
        {item.overview && (
          <p className="line-clamp-3 max-w-2xl text-sm text-base-content/80">{item.overview}</p>
        )}
        <div>
          <Link to={`/watch/title/${item.type}/${item.tmdbId}`} className="btn btn-primary btn-sm">
            <Icon name="info" className="!text-[18px]" />
            View details
          </Link>
        </div>
      </div>
    </section>
  );
}

function CatalogSearchView({
  query,
  page,
  error,
}: {
  query: string;
  page: CatalogSearchPage | null;
  error: string | null;
}) {
  const revalidator = useRevalidator();
  const [items, setItems] = useState<CatalogSummary[]>(page?.results ?? []);
  const [loadedPage, setLoadedPage] = useState(page?.page ?? 0);
  const [hasMore, setHasMore] = useState(page?.hasMore ?? false);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const controllerRef = useRef<AbortController | null>(null);

  useEffect(() => () => controllerRef.current?.abort(), []);

  const loadMore = async () => {
    if (loading || !hasMore) return;
    const controller = new AbortController();
    controllerRef.current = controller;
    setLoading(true);
    setLoadError(null);

    try {
      const next = await fetchCatalogSearch(query, loadedPage + 1, controller.signal);
      setItems((current) => {
        const seen = new Set(current.map((item) => `${item.type}:${item.tmdbId}`));
        const merged = [...current];
        for (const item of next.results) {
          const key = `${item.type}:${item.tmdbId}`;
          if (seen.has(key)) continue;
          seen.add(key);
          merged.push(item);
        }
        return merged;
      });
      setLoadedPage(next.page);
      setHasMore(next.hasMore);
    } catch (cause) {
      if (controller.signal.aborted) return;
      // The page number stays unchanged so a retry asks for the same page again.
      setLoadError(cause instanceof Error ? cause.message : "Could not load more titles.");
    } finally {
      if (!controller.signal.aborted) setLoading(false);
    }
  };

  const failure = error ?? loadError;

  return (
    <div className="py-6">
      <div className="mb-4 flex flex-wrap items-center gap-2">
        <h1 className="text-xl font-semibold">Results for “{query}”</h1>
        <Button variant="ghost" size="xsmall" onClick={() => void revalidator.revalidate()}>
          <Icon name="refresh" className="!text-[16px]" />
          Refresh
        </Button>
      </div>

      {failure !== null && (
        <Alert variant="warning" className="alert-soft mb-4" role="alert">
          <Icon name="error" className="!text-[18px]" />
          <span>{failure}</span>
          <Button
            variant="warning"
            size="xsmall"
            onClick={() => {
              if (error !== null) void revalidator.revalidate();
              else void loadMore();
            }}
          >
            <Icon name="refresh" className="!text-[16px]" />
            Retry
          </Button>
        </Alert>
      )}

      {items.length === 0 && failure === null ? (
        <p className="py-12 text-center text-sm text-base-content/60">No titles found</p>
      ) : (
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4 lg:grid-cols-6 xl:grid-cols-7">
          {items.map((item) => (
            <TitleCard key={`${item.type}:${item.tmdbId}`} item={item} />
          ))}
        </div>
      )}

      {hasMore && (
        <div className="mt-6 flex justify-center">
          <Button variant="secondary" size="small" onClick={() => void loadMore()} disabled={loading}>
            {loading && <span className="loading loading-spinner loading-xs" />}
            Load more
          </Button>
        </div>
      )}
    </div>
  );
}
