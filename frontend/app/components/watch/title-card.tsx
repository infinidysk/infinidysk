import { Link } from "react-router";
import { Icon } from "~/components/ui";
import type { CatalogSummary } from "~/clients/catalog-contract";

export type TitleCardProps = {
  item: CatalogSummary;
};

/** 2:3 poster card. Missing artwork renders a titled placeholder, not a broken image. */
export function TitleCard({ item }: TitleCardProps) {
  return (
    <Link
      to={`/watch/title/${item.type}/${item.tmdbId}`}
      className="group w-36 shrink-0 rounded-lg outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-base-100 sm:w-44 lg:w-48"
      aria-label={`${item.title}${item.year ? ` (${item.year})` : ""}`}
    >
      <div className="aspect-[2/3] w-full overflow-hidden rounded-lg bg-base-300">
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
      </div>
      <p className="mt-2 truncate text-sm font-medium" title={item.title}>
        {item.title}
      </p>
      <p className="text-xs text-base-content/50">{item.year ?? "\u2014"}</p>
    </Link>
  );
}
