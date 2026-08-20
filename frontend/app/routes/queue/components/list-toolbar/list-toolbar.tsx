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
    <div className="flex flex-wrap items-center gap-2 border-t border-base-content/10 pt-4">
      <label className="input input-sm flex min-w-48 flex-1 items-center gap-2">
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
        className="select-sm"
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
        className="select-sm"
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
        className="select-sm max-[899px]:flex"
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
