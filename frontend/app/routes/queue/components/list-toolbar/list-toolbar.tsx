import { Input, Select, Button, Icon } from "~/components/ui";

export type SortOption = { value: string; label: string };

export function ListToolbar({
  label,
  query,
  category,
  status,
  sort,
  categories,
  statuses,
  sorts,
  isFiltered,
  onQueryChange,
  onCategoryChange,
  onStatusChange,
  onSortChange,
  onClear,
}: {
  label: string;
  query: string;
  category: string;
  status: string;
  sort: string;
  categories: string[];
  statuses: SortOption[];
  sorts: SortOption[];
  isFiltered: boolean;
  onQueryChange: (value: string) => void;
  onCategoryChange: (value: string) => void;
  onStatusChange: (value: string) => void;
  onSortChange: (value: string) => void;
  onClear: () => void;
}) {
  return (
    <div className="grid grid-cols-2 items-center gap-2 border-t border-base-content/10 pt-3 lg:grid-cols-[minmax(12rem,1fr)_repeat(3,minmax(0,10rem))_auto]">
      <label className="input input-sm col-span-2 flex w-full min-w-0 items-center gap-2 lg:col-span-1">
        <Icon name="search" className="!text-[18px] text-base-content/50" />
        <Input
          type="search"
          className="h-auto min-w-0 flex-1 border-0 bg-transparent p-0 focus:outline-none"
          value={query}
          onChange={(event) => onQueryChange(event.target.value)}
          aria-label={`Search ${label.toLowerCase()}`}
          placeholder={`Search ${label.toLowerCase()}`}
        />
      </label>
      <Select
        className="select-sm w-full min-w-0"
        value={category}
        onChange={(event) => onCategoryChange(event.target.value)}
        aria-label={`Filter ${label.toLowerCase()} by category`}
      >
        <option value="">All categories</option>
        {categories.map((value) => (
          <option key={value} value={value}>
            {value}
          </option>
        ))}
      </Select>
      <Select
        className="select-sm w-full min-w-0"
        value={status}
        onChange={(event) => onStatusChange(event.target.value)}
        aria-label={`Filter ${label.toLowerCase()} by status`}
      >
        <option value="">All statuses</option>
        {statuses.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </Select>
      <Select
        className="select-sm col-span-2 w-full min-w-0 lg:col-span-1"
        value={sort}
        onChange={(event) => onSortChange(event.target.value)}
        aria-label={`Sort ${label.toLowerCase()}`}
      >
        <option value="">Default order</option>
        {sorts.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </Select>
      {isFiltered && (
        <Button variant="ghost" size="xsmall" onClick={onClear}>
          Clear
        </Button>
      )}
    </div>
  );
}
