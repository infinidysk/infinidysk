import { Button, Icon } from "~/components/ui";

export function EmptyQueue({ onClearFilters }: { onClearFilters?: (() => void) | undefined }) {
  return (
    <div className="card my-4 border border-base-content/10 bg-base-200">
      <div className="card-body items-center py-10 text-center">
        <Icon
          name={onClearFilters ? "filter_alt_off" : "inbox"}
          className="!text-[40px] text-base-content/40"
        />
        <h2 className="card-title text-base">
          {onClearFilters ? "No jobs match your filters" : "Nothing in the queue"}
        </h2>
        <p className="max-w-sm text-xs leading-relaxed text-base-content/60">
          {onClearFilters
            ? "Try a different search, category, or status, or clear the filters to see every job."
            : "Upload an NZB, or send jobs from Sonarr or Radarr using InfiniDysk as the download client. Active jobs sit at the top of this list; finished jobs stay here as history."}
        </p>
        {onClearFilters && (
          <div className="card-actions mt-2">
            <Button onClick={onClearFilters}>
              <Icon name="filter_alt_off" className="!text-[18px]" />
              Clear filters
            </Button>
          </div>
        )}
      </div>
    </div>
  );
}
