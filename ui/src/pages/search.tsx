import { type FormEvent, useEffect, useMemo, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { CircleAlert, Loader2, Plus, Search as SearchIcon, TriangleAlert } from "lucide-react";
import { Link } from "react-router";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { api, body, HttpError, type Schemas } from "../api/client";
import { authKey } from "../auth";
import { bytes, count } from "../format";
import { useSettings } from "../settings";
import { Empty, Failure, Loading, PAGE, PageHeader, Pager, Panel, ReleaseName, Status } from "../ui";

type SearchResponse = Schemas["IndexerSearchResponse"];
type SearchResult = Schemas["SearchResult"];
type QueueState = {
  kind: "created" | "existing" | "rejected" | "expired" | "error";
  message: string;
};
type SearchState =
  | { phase: "idle" }
  | { phase: "loading"; term: string }
  | { phase: "ready"; term: string; data: SearchResponse }
  | { phase: "error"; term: string; error: Error };

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : "The request failed. Try again.";
}

function QueueControls({
  item,
  categories,
  category,
  onCategory,
  onQueue,
  pending,
  outcome,
  readOnly,
}: {
  item: SearchResult;
  categories: string[];
  category: string;
  onCategory: (value: string) => void;
  onQueue: () => void;
  pending: boolean;
  outcome?: QueueState;
  readOnly: boolean;
}) {
  const unavailable = !item.can_queue || outcome?.kind === "expired";
  const locked = readOnly || unavailable || pending || (outcome !== undefined && outcome.kind !== "error");
  return (
    <div className="flex min-w-0 flex-col gap-1.5">
      <div className="flex flex-wrap items-center gap-2">
        <Select value={category} onValueChange={onCategory} disabled={locked || categories.length === 0}>
          <SelectTrigger size="sm" className="w-32 max-w-full" aria-label={"Category for " + item.title}>
            <SelectValue placeholder="Category" />
          </SelectTrigger>
          <SelectContent>
            {categories.map((entry) => (
              <SelectItem key={entry} value={entry}>{entry}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Button type="button" size="sm" disabled={locked || !categories.includes(category)} onClick={onQueue}>
          {pending ? <Loader2 className="animate-spin" /> : <Plus />}
          {pending ? "Queuing" : "Queue"}
        </Button>
      </div>
      {outcome ? (
        <p role="status" className={outcome.kind === "error" || outcome.kind === "expired" || outcome.kind === "rejected" ? "text-destructive text-xs" : "text-success-text text-xs"}>
          {outcome.message}
          {(outcome.kind === "created" || outcome.kind === "existing") && (
            <> · <Link to="/queue" className="underline underline-offset-2">View queue</Link></>
          )}
        </p>
      ) : unavailable ? (
        <p className="text-muted-foreground text-xs">Not available to queue</p>
      ) : readOnly ? (
        <p className="text-muted-foreground text-xs">Queue access is read-only</p>
      ) : categories.length === 0 ? (
        <p className="text-muted-foreground text-xs">No queue categories configured</p>
      ) : null}
    </div>
  );
}

export function SearchPage() {
  const client = useQueryClient();
  const settings = useSettings();
  const [draft, setDraft] = useState("");
  const [validation, setValidation] = useState("");
  const [search, setSearch] = useState<SearchState>({ phase: "idle" });
  const [page, setPage] = useState(0);
  const [chosen, setChosen] = useState<Record<string, string>>({});
  const [outcomes, setOutcomes] = useState<Record<string, QueueState>>({});
  const [busy, setBusy] = useState<ReadonlySet<string>>(new Set());
  const [readOnly, setReadOnly] = useState(false);
  const activeSearch = useRef<AbortController | null>(null);
  const searchVersion = useRef(0);
  const pendingQueue = useRef(new Set<string>());
  const categories = useMemo(() => {
    const sab = settings.data?.settings.sab;
    return sab
      ? [...new Set([sab.manual_category, ...sab.categories].map((category) => category.trim()).filter(Boolean))]
      : [];
  }, [settings.data?.settings.sab]);
  const defaultCategory = categories[0] ?? "";

  useEffect(() => () => {
    searchVersion.current += 1;
    activeSearch.current?.abort();
  }, []);

  const runSearch = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const term = draft.trim();
    const length = Array.from(term).length;
    if (length === 0 || length > 250) {
      setValidation("Enter a search term of 1–250 characters.");
      return;
    }
    setValidation("");
    const version = ++searchVersion.current;
    activeSearch.current?.abort();
    const controller = new AbortController();
    activeSearch.current = controller;
    setPage(0);
    setChosen({});
    setOutcomes({});
    setSearch({ phase: "loading", term });

    void (async () => {
      try {
        const data = await body(api.GET("/api/v1/indexers/search", {
          params: { query: { q: term, limit: 100 } },
          signal: controller.signal,
        }));
        if (searchVersion.current === version && !controller.signal.aborted) {
          setSearch({ phase: "ready", term, data });
        }
      } catch (error) {
        if (searchVersion.current === version && !controller.signal.aborted) {
          if (error instanceof HttpError && error.status === 401) {
            void client.invalidateQueries({ queryKey: authKey });
          }
          setSearch({ phase: "error", term, error: error instanceof Error ? error : new Error(errorMessage(error)) });
        }
      } finally {
        if (activeSearch.current === controller) activeSearch.current = null;
      }
    })();
  };

  const queue = (item: SearchResult) => {
    const category = chosen[item.nzb_ref] ?? defaultCategory;
    if (!item.can_queue || readOnly || !categories.includes(category) || pendingQueue.current.has(item.nzb_ref)) return;
    const previous = outcomes[item.nzb_ref];
    if (previous && previous.kind !== "error") return;

    const version = searchVersion.current;
    pendingQueue.current.add(item.nzb_ref);
    setBusy((current) => new Set([...current, item.nzb_ref]));
    setOutcomes((current) => {
      const next = { ...current };
      delete next[item.nzb_ref];
      return next;
    });

    void (async () => {
      let next: QueueState;
      try {
        const { data, error, response } = await api.POST("/api/v1/indexers/search/queue", {
          body: { nzb_ref: item.nzb_ref, category },
        });
        if (response.status === 201 || response.status === 200 || response.status === 422) {
          const outcome = (data ?? error) as Schemas["SearchQueued"] | undefined;
          const expected = response.status === 201 ? "created" : response.status === 200 ? "existing" : "rejected";
          if (!outcome || outcome.status !== expected) throw new Error("Unexpected queue response. Check the queue before retrying.");
          next = {
            kind: expected,
            message: expected === "created" ? "Added to queue" : expected === "existing" ? "Already in queue" : "Rejected by queue. Check the history.",
          };
          void client.invalidateQueries({ queryKey: ["queue"] });
          void client.invalidateQueries({ queryKey: ["history"] });
        } else if (response.status === 404) {
          next = { kind: "expired", message: "Reference expired or indexer changed. Search again." };
        } else {
          const failure = error as { error?: string } | undefined;
          if (response.status === 401) void client.invalidateQueries({ queryKey: authKey });
          if (response.status === 403) setReadOnly(true);
          if (response.status === 400) void client.invalidateQueries({ queryKey: ["settings"] });
          next = {
            kind: "error",
            message: response.status === 403 ? "Queue access denied." :
              response.status === 400 ? "Category or request rejected. Check queue categories." :
              response.status === 503 ? "Indexer unavailable. Retry this item." :
              failure?.error ?? "Could not queue this result.",
          };
        }
      } catch (error) {
        next = { kind: "error", message: errorMessage(error) };
      } finally {
        pendingQueue.current.delete(item.nzb_ref);
        setBusy((current) => {
          const remaining = new Set(current);
          remaining.delete(item.nzb_ref);
          return remaining;
        });
      }
      if (version === searchVersion.current) {
        setOutcomes((current) => ({ ...current, [item.nzb_ref]: next }));
      }
    })();
  };

  const results = search.phase === "ready" ? search.data.results : [];
  const visible = results.slice(page, page + PAGE);

  const controls = (item: SearchResult) => (
    <QueueControls
      item={item}
      categories={categories}
      category={chosen[item.nzb_ref] ?? defaultCategory}
      onCategory={(category) => setChosen((current) => ({ ...current, [item.nzb_ref]: category }))}
      onQueue={() => queue(item)}
      pending={busy.has(item.nzb_ref)}
      outcome={outcomes[item.nzb_ref]}
      readOnly={readOnly}
    />
  );

  return (
    <>
      <PageHeader title="Search" description="Search enabled Newznab indexers and queue matching NZBs" />
      <form onSubmit={runSearch} className="mb-6 space-y-2">
        <Label htmlFor="newznab-query">Search term</Label>
        <div className="flex flex-col gap-2 sm:flex-row">
          <Input
            id="newznab-query"
            maxLength={500}
            value={draft}
            onChange={(event) => { setDraft(event.target.value); if (validation) setValidation(""); }}
            placeholder="Search all enabled indexers"
            className="min-w-0 flex-1"
            aria-invalid={!!validation}
            aria-describedby={validation ? "newznab-query-error" : "newznab-query-hint"}
          />
          <Button type="submit" className="w-full sm:w-auto">
            {search.phase === "loading" ? <Loader2 className="animate-spin" /> : <SearchIcon />}
            Search
          </Button>
        </div>
        {validation ? <p id="newznab-query-error" className="text-destructive text-xs">{validation}</p> :
          <p id="newznab-query-hint" className="text-muted-foreground text-xs">1–250 characters. Search starts when you select Search.</p>}
      </form>

      {settings.isError && <Failure error={settings.error} className="mb-4" />}
      {readOnly && (
        <Alert className="mb-4">
          <CircleAlert />
          <AlertDescription>Your account cannot queue search results. Search remains available.</AlertDescription>
        </Alert>
      )}
      {search.phase === "idle" && (
        <Empty title="No search yet">Enter a term and select Search to find NZBs across enabled indexers.</Empty>
      )}
      {search.phase === "loading" && (
        <section aria-live="polite" aria-label="Searching indexers">
          <p className="text-muted-foreground mb-3 flex items-center gap-2"><Loader2 className="size-4 animate-spin" /> Searching enabled indexers…</p>
          <Loading />
        </section>
      )}
      {search.phase === "error" && (
        <div className="space-y-3">
          <Failure error={search.error} />
          <Button size="sm" variant="outline" onClick={() => document.getElementById("newznab-query")?.focus()}>Change search</Button>
        </div>
      )}
      {search.phase === "ready" && (
        <div className="space-y-6">
          <section aria-label="Indexer status">
            <h2 className="mb-3 text-sm font-medium">Indexer status</h2>
            {search.data.indexers.length === 0 ? (
              <Alert><TriangleAlert className="text-warning-text" /><AlertDescription>No enabled indexers. <Link to="/settings/indexers" className="text-primary underline underline-offset-2">Add an indexer in settings</Link> to start searching.</AlertDescription></Alert>
            ) : (
              <Panel>
                <ul className="divide-y">
                  {search.data.indexers.map((indexer) => (
                    <li key={indexer.indexer_id} className="flex flex-wrap items-start justify-between gap-x-4 gap-y-1 px-3 py-2.5">
                      <span className="min-w-0 font-medium break-words">{indexer.indexer_name}</span>
                      <div className="flex min-w-0 flex-col items-start gap-1 sm:items-end">
                        <Status tone={indexer.ok ? (indexer.error ? "warning" : "success") : "destructive"}>
                          {indexer.ok ? count(indexer.result_count, "result") : "Failed"}
                        </Status>
                        {indexer.error && <span className={indexer.ok ? "text-warning-text text-xs" : "text-destructive text-xs"}>{indexer.error}</span>}
                      </div>
                    </li>
                  ))}
                </ul>
              </Panel>
            )}
          </section>

          <section aria-label="Search results">
            <div className="mb-3 flex flex-wrap items-baseline justify-between gap-2">
              <h2 className="text-sm font-medium">Results</h2>
              <span role="status" className="text-muted-foreground text-xs tabular-nums wrap-anywhere">{count(results.length, "result")} for “{search.term}”</span>
            </div>
            {results.length === 0 ? (
              <Empty title="No results found">Try another search term, or check the indexer status above.</Empty>
            ) : (
              <>
                <Panel>
                  <div className="hidden md:block">
                    <Table>
                      <TableHeader><TableRow>
                        <TableHead className="w-full">Title</TableHead>
                        <TableHead className="w-28 text-right">Size</TableHead>
                        <TableHead className="w-32">Indexer</TableHead>
                        <TableHead className="w-56"><span className="sr-only">Category and queue action</span>Queue</TableHead>
                      </TableRow></TableHeader>
                      <TableBody>
                        {visible.map((item) => (
                          <TableRow key={item.nzb_ref}>
                            <TableCell className="w-full max-w-0">
                              <div title={item.title} className="truncate font-medium"><ReleaseName name={item.title} /></div>
                            </TableCell>
                            <TableCell className="text-right tabular-nums">{bytes(item.size_bytes)}</TableCell>
                            <TableCell className="max-w-32 truncate" title={item.indexer_name}>{item.indexer_name}</TableCell>
                            <TableCell className="whitespace-normal">{controls(item)}</TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </div>
                  <ul className="divide-y md:hidden">
                    {visible.map((item) => (
                      <li key={item.nzb_ref} className="space-y-2 p-3">
                        <p className="font-medium break-words"><ReleaseName name={item.title} /></p>
                        <div className="text-muted-foreground flex flex-wrap gap-x-3 gap-y-1 text-xs">
                          <span>{bytes(item.size_bytes)}</span><span className="break-words">{item.indexer_name}</span>
                        </div>
                        {controls(item)}
                      </li>
                    ))}
                  </ul>
                </Panel>
                <Pager offset={page} more={page + PAGE < results.length} onChange={setPage} />
              </>
            )}
          </section>
        </div>
      )}
    </>
  );
}
