import { SimpleDropdown } from "~/components/simple-dropdown/simple-dropdown";
import { memo } from "react";
import { Icon } from "~/components/ui";

export type PaginationProps = {
  pageNumber: number;
  totalPages: number;
  totalCount: number;
  pageSize: number;
  pageSizeOptions: readonly number[];
  onPageSelected?: (page: number) => void;
  onPageSizeSelected?: (pageSize: number) => void;
};

export const Pagination = memo(
  ({
    pageNumber,
    totalPages,
    totalCount,
    pageSize,
    pageSizeOptions,
    onPageSelected,
    onPageSizeSelected,
  }: PaginationProps) => {
    const handlePageClick = (page: number) => {
      if (onPageSelected && page !== pageNumber && page >= 1 && page <= totalPages) {
        onPageSelected(page);
      }
    };

    const handleDropdownChange = (value: string) => {
      const page = parseInt(value, 10);
      if (onPageSelected && !isNaN(page)) {
        onPageSelected(page);
      }
    };

    const handlePageSizeChange = (value: string) => {
      const size = parseInt(value, 10);
      if (onPageSizeSelected && !isNaN(size) && size !== pageSize) {
        onPageSizeSelected(size);
      }
    };

    const start = totalCount === 0 ? 0 : (pageNumber - 1) * pageSize + 1;
    const end = Math.min(pageNumber * pageSize, totalCount);
    const pageOptions = Array.from({ length: totalPages }, (_, i) => String(i + 1));
    const sizeOptions = pageSizeOptions.map(String);

    return (
      <div className="flex w-full flex-col gap-2 sm:flex-row sm:flex-wrap sm:items-center sm:justify-between">
        <div className="flex flex-wrap items-center justify-center gap-3 text-xs text-base-content/60 sm:justify-start">
          <span>
            Showing <span className="font-mono tabular-nums">{start}</span>–
            <span className="font-mono tabular-nums">{end}</span> of{" "}
            <span className="font-mono tabular-nums">{totalCount}</span>
          </span>
          <label className="flex items-center gap-2">
            <span>Per page</span>
            <SimpleDropdown
              type="bordered"
              options={sizeOptions}
              value={String(pageSize)}
              onChange={handlePageSizeChange}
              ariaLabel="Items per page"
            />
          </label>
        </div>

        {totalPages > 1 && (
          <div className="join join-horizontal flex flex-wrap items-center justify-center">
            <button
              type="button"
              className="btn btn-ghost btn-xs join-item"
              disabled={pageNumber <= 1}
              onClick={() => handlePageClick(pageNumber - 1)}
            >
              <Icon name="chevron_left" className="!text-[16px]" /> Prev
            </button>

            <div className="join-item flex items-center gap-2 border-x border-base-content/10 px-3 text-xs font-medium text-base-content/60">
              <span>Page</span>
              <SimpleDropdown
                type="bordered"
                options={pageOptions}
                value={String(pageNumber)}
                onChange={handleDropdownChange}
                ariaLabel="Page number"
              />
              <span>of {totalPages}</span>
            </div>

            <button
              type="button"
              className="btn btn-ghost btn-xs join-item"
              disabled={pageNumber >= totalPages}
              onClick={() => handlePageClick(pageNumber + 1)}
            >
              Next <Icon name="chevron_right" className="!text-[16px]" />
            </button>
          </div>
        )}
      </div>
    );
  },
);
