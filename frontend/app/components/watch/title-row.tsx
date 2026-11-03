import { useRef, type ReactNode } from "react";
import { Icon } from "~/components/ui";
import type { CatalogRow } from "~/clients/catalog-contract";
import { PersonCard } from "./person-card";
import { TitleCard } from "./title-card";

export type TitleRowProps = {
  row: CatalogRow;
};

/**
 * Card width lives on the scroll item, not the card: a percentage-free width on the flex
 * item keeps posters from expanding to their intrinsic pixel size (min-width: auto).
 */
export const CARD_WIDTH = "w-36 shrink-0 sm:w-44 lg:w-48";

/**
 * Horizontally scrolling poster row. Scrolling is native overflow plus two buttons —
 * no carousel dependency, and keyboard users keep the row's own scroll behaviour.
 * The trending-people row renders person cards through the same shell.
 */
export function TitleRow({ row }: TitleRowProps) {
  const people = row.people;
  const items = row.items;
  const empty = people !== null ? people.length === 0 : (items?.length ?? 0) === 0;

  if (empty) return null;

  return (
    <RowShell id={row.id} title={row.title}>
      {people !== null
        ? people.map((person) => (
            <div key={person.personId} className={`${CARD_WIDTH} snap-start`}>
              <PersonCard person={person} />
            </div>
          ))
        : (items ?? []).map((item) => (
            <div key={`${item.type}:${item.tmdbId}`} className={`${CARD_WIDTH} snap-start`}>
              <TitleCard item={item} />
            </div>
          ))}
    </RowShell>
  );
}

export type RowShellProps = {
  id: string;
  title: string;
  children: ReactNode;
};

/** Heading plus the horizontal scroller both card rows share. */
export function RowShell({ id, title, children }: RowShellProps) {
  const scrollerRef = useRef<HTMLDivElement>(null);

  const scrollByPage = (direction: 1 | -1) => {
    const scroller = scrollerRef.current;
    if (!scroller) return;
    scroller.scrollBy({
      left: direction * Math.max(320, scroller.clientWidth * 0.8),
      behavior: "smooth",
    });
  };

  return (
    <section className="py-4" aria-labelledby={`catalog-row-${id}`}>
      <div className="mb-3 flex items-center justify-between gap-2">
        <h2 id={`catalog-row-${id}`} className="text-lg font-semibold">
          {title}
        </h2>
        <div className="flex items-center gap-1">
          <button
            type="button"
            className="btn btn-ghost btn-xs btn-circle"
            onClick={() => scrollByPage(-1)}
            aria-label={`Scroll ${title} left`}
          >
            <Icon name="chevron_left" className="!text-[18px]" />
          </button>
          <button
            type="button"
            className="btn btn-ghost btn-xs btn-circle"
            onClick={() => scrollByPage(1)}
            aria-label={`Scroll ${title} right`}
          >
            <Icon name="chevron_right" className="!text-[18px]" />
          </button>
        </div>
      </div>
      <div
        ref={scrollerRef}
        className="flex snap-x snap-mandatory gap-3 overflow-x-auto pb-2 [scrollbar-width:thin]"
      >
        {children}
      </div>
    </section>
  );
}
