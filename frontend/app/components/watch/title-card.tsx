import { Link } from "react-router";
import { Icon } from "~/components/ui";
import type { CatalogSummary } from "~/clients/catalog-contract";

export type TitleCardProps = {
  item: CatalogSummary;
  /** Secondary line under the title; defaults to the release year. */
  caption?: string;
  /** Watched fraction (0–1). Renders the Continue Watching bar along the card bottom. */
  progress?: number;
  /** Resume link target, used when the item resumes at a specific episode. */
  to?: string;
};

/** 2:3 poster card. Missing artwork renders a titled placeholder, not a broken image. */
export function TitleCard({ item, caption, progress, to }: TitleCardProps) {
  const watched = progress === undefined ? null : Math.min(1, Math.max(0, progress));

  return (
    <Link
      to={to ?? `/watch/title/${item.type}/${item.tmdbId}`}
      className="group w-full min-w-0 rounded-lg outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-base-100"
      aria-label={`${item.title}${item.year ? ` (${item.year})` : ""}`}
    >
      <div className="relative aspect-[2/3] w-full overflow-hidden rounded-lg bg-base-300">
        {item.posterUrl ? (
          <img
            src={item.posterUrl}
            alt=""
            loading="lazy"
            decoding="async"
            className="h-full w-full object-cover transition duration-300 group-hover:scale-[1.03]"
          />
        ) : (
          <div className="flex h-full w-full flex-col items-center justify-center gap-2 p-3 text-center">
            <Icon name="image_not_supported" className="!text-[28px] text-base-content/40" />
            <span className="line-clamp-4 text-xs font-medium text-base-content/60">
              {item.title}
            </span>
          </div>
        )}
        {watched !== null && (
          <div
            className="absolute inset-x-0 bottom-0 h-0.5 bg-base-content/25"
            role="progressbar"
            aria-label={`${item.title} progress`}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={Math.round(watched * 100)}
          >
            <div className="h-full bg-primary" style={{ width: `${watched * 100}%` }} />
          </div>
        )}
      </div>
      <p className="mt-2 truncate text-sm font-medium" title={item.title}>
        {item.title}
      </p>
      <p className="truncate text-xs text-base-content/50">{caption ?? item.year ?? "\u2014"}</p>
    </Link>
  );
}
