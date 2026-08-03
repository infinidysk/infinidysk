import type { Route } from "./+types/route";
import { Form, useFetcher, useNavigation } from "react-router";
import { backendClient, type SearchIndexersResponse } from "~/clients/backend-client.server";
import { Badge, Button, Input, Spinner } from "~/components/ui";
import { formatFileSize } from "~/utils/file-size";
import { useIsReadOnly } from "~/auth/authorization";

export async function loader({ request }: Route.LoaderArgs) {
    const url = new URL(request.url);
    const q = url.searchParams.get("q")?.trim() ?? "";
    if (!q) return { q: "", data: null as SearchIndexersResponse | null };
    const data = await backendClient.searchIndexers(q, 100);
    return { q, data };
}

export async function action({ request }: Route.ActionArgs) {
    const formData = await request.formData();
    const nzbUrl = formData.get("nzbUrl")?.toString() ?? "";
    const nzbName = formData.get("nzbName")?.toString() ?? "";
    if (!nzbUrl || !nzbName) return { ok: false, error: "Missing nzbUrl or nzbName" };
    try {
        const nzoId = await backendClient.addNzbFromUrl(nzbUrl, nzbName);
        return { ok: true, nzoId };
    } catch (e: any) {
        return { ok: false, error: e?.message ?? "Failed to add" };
    }
}

export default function Search({ loaderData }: Route.ComponentProps) {
    const navigation = useNavigation();
    const isSearching = navigation.state === "loading" && navigation.location?.pathname === "/search";
    const { q, data } = loaderData;

    return (
        <div className="mx-auto flex w-full max-w-5xl flex-col gap-6 px-4 py-8 md:px-6">
            <h1 className="sr-only">Search</h1>
            <Form method="get" className="join w-full">
                <Input
                    name="q"
                    defaultValue={q}
                    placeholder="Search your indexers..."
                    aria-label="Search your indexers"
                    className="join-item min-w-0 flex-1"
                    autoFocus
                />
                <Button type="submit" disabled={isSearching} className="join-item">
                    {isSearching ? <Spinner size="sm" /> : "Search"}
                </Button>
            </Form>

            {data && (
                <div className="flex flex-wrap gap-2">
                    {data.indexers.map(i => (
                        <Badge
                            key={i.name}
                            className={`badge-sm ${i.ok ? "badge-success" : "badge-error"}`}
                        >
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
                            Type a query above to search your configured Newznab indexers.
                            Configure indexers under Settings → Indexers.
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
                    {data.results.map((r, idx) => (
                        <ResultRow key={`${r.nzbUrl}-${idx}`} result={r} />
                    ))}
                </ul>
            )}
        </div>
    );
}

function ResultRow({ result }: { result: { indexer: string; title: string; nzbUrl: string; size: number; posted: string | null } }) {
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
            {!isReadOnly && <fetcher.Form method="post">
                <input type="hidden" name="nzbUrl" value={result.nzbUrl} />
                <input type="hidden" name="nzbName" value={result.title} />
                <Button
                    type="submit"
                    size="xsmall"
                    variant={done ? "success" : failed ? "danger" : "primary"}
                    disabled={submitting || done}
                    className="whitespace-nowrap"
                    title={failed ? fetcher.data?.error : undefined}
                >
                    {submitting ? <Spinner size="sm" />
                        : done ? "Mounted"
                        : failed ? "Failed"
                        : "Mount"}
                </Button>
            </fetcher.Form>}
        </li>
    );
}
