import type { Route } from "./+types/route";
import { Form, useFetcher, useNavigation } from "react-router";
import { backendClient, type SearchIndexersResponse } from "~/clients/backend-client.server";
import { Badge, Button, Input, PageHeader, Spinner } from "~/components/ui";
import { formatFileSize } from "~/utils/file-size";
import { useIsReadOnly } from "~/auth/authorization";

export async function loader({ request }: Route.LoaderArgs) {
  const url = new URL(request.url);
  const q = url.searchParams.get("q")?.trim() ?? "";
  if (!q) return { q: "", data: null as SearchIndexersResponse | null };
  const data = await backendClient.searchIndexers(q, 100);
  return { q, data };
}

type SearchActionResult = { ok: true; nzoId: string } | { ok: false; error: string };

export async function action({ request }: Route.ActionArgs): Promise<SearchActionResult> {
  const formData = await request.formData();
  const nzbUrlEntry = formData.get("nzbUrl");
  const nzbNameEntry = formData.get("nzbName");
  const nzbUrl = typeof nzbUrlEntry === "string" ? nzbUrlEntry : "";
  const nzbName = typeof nzbNameEntry === "string" ? nzbNameEntry : "";
  if (!nzbUrl || !nzbName) return { ok: false, error: "Missing nzbUrl or nzbName" };
  try {
    const nzoId = await backendClient.addNzbFromUrl(nzbUrl, nzbName);
    return { ok: true, nzoId };
  } catch (e: unknown) {
    return { ok: false, error: e instanceof Error ? e.message : "Failed to add" };
  }
}

export function shouldRevalidate({
  formData,
  formMethod,
  defaultShouldRevalidate,
}: {
  formData?: FormData;
  formMethod?: string;
  defaultShouldRevalidate: boolean;
}) {
  if (formMethod?.toUpperCase() === "POST" && formData?.get("nzbUrl")) return false;
  return defaultShouldRevalidate;
}

export default function Search({ loaderData }: Route.ComponentProps) {
  const navigation = useNavigation();
  const isSearching = navigation.state === "loading" && navigation.location?.pathname === "/search";
  const { q, data } = loaderData;

  return (
    <div className="mx-auto flex w-full max-w-5xl flex-col gap-6 px-4 py-8 md:px-6">
      <PageHeader
        title="Search"
        subtitle="Query your configured indexers and add an NZB to the queue."
      />
      <Form method="get" className="join w-full">
        <Input
          name="q"
          defaultValue={q}
          placeholder="Search your indexers..."
          aria-label="Search your indexers"
          className="join-item min-w-0 flex-1"
          autoFocus
        />
        <Button type="submit" variant="primary" disabled={isSearching} className="join-item">
          {isSearching ? <Spinner size="sm" /> : "Search"}
        </Button>
      </Form>

      {data && (
        <div className="flex flex-wrap gap-2">
          {data.indexers.map((i) => (
            <Badge key={i.name} className={`badge-sm ${i.ok ? "badge-success" : "badge-error"}`}>
              {i.name}: {i.ok ? `${i.resultCount} results` : "failed"} ({i.elapsedMs}ms)
              {i.error ? ` — ${i.error}` : ""}
            </Badge>
          ))}
        </div>
      )}

      {data === null && (
        <div className="card border border-base-content/10 bg-base-200">
          <div className="card-body items-center text-center text-base-content/60">
            <p>
              Type a query above to search your configured Newznab indexers. Configure indexers
              under Settings → Indexers.
            </p>
          </div>
        </div>
      )}

      {data && data.results.length === 0 && (
        <div className="card border border-base-content/10 bg-base-200">
          <div className="card-body items-center text-center text-base-content/60">
            <p>No results for &quot;{q}&quot;.</p>
          </div>
        </div>
      )}

      {data && data.results.length > 0 && (
        <ul className="list rounded-box border border-base-content/10 bg-base-200">
          {data.results.map((r) => (
            <ResultRow key={r.nzbUrl} result={r} />
          ))}
        </ul>
      )}
    </div>
  );
}

function ResultRow({
  result,
}: {
  result: { indexer: string; title: string; nzbUrl: string; size: number; posted: string | null };
}) {
  const isReadOnly = useIsReadOnly();
  const fetcher = useFetcher<typeof action>();
  const submitting = fetcher.state !== "idle";
  const done = fetcher.data?.ok === true;
  const failed = fetcher.data && fetcher.data.ok === false;

  return (
    <li className="list-row items-center">
      <div className="list-col-grow min-w-0">
        <div className="font-medium">{result.title}</div>
        <div className="text-xs text-base-content/60">
          {result.indexer} · {formatFileSize(result.size)}
          {result.posted && ` · ${new Date(result.posted).toLocaleDateString()}`}
        </div>
      </div>
      {!isReadOnly && (
        <Button
          size="xsmall"
          variant={done ? "success" : failed ? "danger" : "primary"}
          disabled={submitting || done}
          className="whitespace-nowrap"
          title={fetcher.data && !fetcher.data.ok ? fetcher.data.error : undefined}
          onClick={() => {
            void fetcher.submit(
              { nzbUrl: result.nzbUrl, nzbName: result.title },
              { method: "post" },
            );
          }}
        >
          {submitting ? <Spinner size="sm" /> : done ? "Mounted" : failed ? "Failed" : "Mount"}
        </Button>
      )}
    </li>
  );
}
