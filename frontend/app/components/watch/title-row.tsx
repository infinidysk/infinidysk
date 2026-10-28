import { useRef } from "react";
import { Icon } from "~/components/ui";
import type { CatalogRow } from "~/clients/catalog-contract";
import { TitleCard } from "./title-card";

export type TitleRowProps = {
  row: CatalogRow;
};

/**
 * Horizontally scrolling poster row. Scrolling is native overflow plus two buttons —
 * no carousel dependency, and keyboard users keep the row's own scroll behaviour.
 */
export function TitleRow({ row }: TitleRowProps) {
  const scrollerRef = useRef<HTMLDivElement>(null);

  const scrollByPage = (direction: 1 | -1) => {
    const scroller = scrollerRef.current;
    if (!scroller) return;
    scroller.scrollBy({
      left: direction * Math.max(320, scroller.clientWidth * 0.8),
      behavior: "smooth",
    });
  };

  if (row.items.length === 0) return null;

  return (
    <section className="py-4" aria-labelledby={`catalog-row-${row.id}`}>
      <div className="mb-3 flex items-center justify-between gap-2">
        <h2 id={`catalog-row-${row.id}`} className="text-lg font-semibold">
          {row.title}
        </h2>
        <div className="flex items-center gap-1">
          <button
            type="button"
            className="btn btn-ghost btn-xs btn-circle"
            onClick={() => scrollByPage(-1)}
            aria-label={`Scroll ${row.title} left`}
          >
            <Icon name="chevron_left" className="!text-[18px]" />
          </button>
          <button
            type="button"
            className="btn btn-ghost btn-xs btn-circle"
            onClick={() => scrollByPage(1)}
            aria-label={`Scroll ${row.title} right`}
          >
            <Icon name="chevron_right" className="!text-[18px]" />
          </button>
        </div>
      </div>
      <div
        ref={scrollerRef}
        className="flex snap-x snap-mandatory gap-3 overflow-x-auto pb-2 [scrollbar-width:thin]"
      >
        {row.items.map((item) => (
          <div key={`${item.type}:${item.tmdbId}`} className="snap-start">
            <TitleCard item={item} />
          </div>
        ))}
      </div>
    </section>
  );
}
