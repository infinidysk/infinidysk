export type SortDirection = "asc" | "desc";
export type QueueSortField = "name" | "category" | "status" | "size";
export type HistorySortField = QueueSortField | "completed";

export type ListParams<TSort extends string> = {
  query: string;
  category: string;
  status: string;
  sort: TSort | null;
  direction: SortDirection | null;
};

export type QueueListParams = ListParams<QueueSortField>;
export type HistoryListParams = ListParams<HistorySortField>;

const queueSorts = new Set<QueueSortField>(["name", "category", "status", "size"]);
const historySorts = new Set<HistorySortField>(["name", "category", "status", "size", "completed"]);

function parseSort<TSort extends string>(
  value: string | null,
  supported: Set<TSort>,
): Pick<ListParams<TSort>, "sort" | "direction"> {
  if (!value) return { sort: null, direction: null };
  const [field, direction] = value.split(":");
  if (!field || !supported.has(field as TSort) || (direction !== "asc" && direction !== "desc")) {
    return { sort: null, direction: null };
  }
  return { sort: field as TSort, direction };
}

function parseList<TSort extends string>(
  searchParams: URLSearchParams,
  keys: { query: string; category: string; status: string; sort: string },
  supported: Set<TSort>,
): ListParams<TSort> {
  return {
    query: searchParams.get(keys.query)?.trim() ?? "",
    category: searchParams.get(keys.category)?.trim() ?? "",
    status: searchParams.get(keys.status)?.trim() ?? "",
    ...parseSort(searchParams.get(keys.sort), supported),
  };
}

export function parseQueueListParams(searchParams: URLSearchParams): QueueListParams {
  return parseList(
    searchParams,
    { query: "qq", category: "qcat", status: "qstatus", sort: "qsort" },
    queueSorts,
  );
}

export function parseHistoryListParams(searchParams: URLSearchParams): HistoryListParams {
  return parseList(
    searchParams,
    { query: "hq", category: "hcat", status: "hstatus", sort: "hsort" },
    historySorts,
  );
}

export function isDefaultList(params: ListParams<string>): boolean {
  return !params.query && !params.category && !params.status && !params.sort;
}

export function sortValue(params: ListParams<string>): string {
  return params.sort && params.direction ? `${params.sort}:${params.direction}` : "";
}
