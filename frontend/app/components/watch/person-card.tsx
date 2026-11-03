import { Link } from "react-router";
import { Icon } from "~/components/ui";
import type { CatalogPerson } from "~/clients/catalog-contract";

export type PersonCardProps = {
  person: CatalogPerson;
};

/** Trending-people card: same footprint as a poster card, links to the person page. */
export function PersonCard({ person }: PersonCardProps) {
  return (
    <Link
      to={`/watch/person/${person.personId}`}
      className="group w-full min-w-0 rounded-lg outline-none focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-offset-2 focus-visible:ring-offset-base-100"
      aria-label={person.name}
    >
      <div className="aspect-[2/3] w-full overflow-hidden rounded-lg bg-base-300">
        {person.profileUrl ? (
          <img
            src={person.profileUrl}
            alt=""
            loading="lazy"
            decoding="async"
            className="h-full w-full object-cover transition duration-300 group-hover:scale-[1.03]"
          />
        ) : (
          <div className="flex h-full w-full flex-col items-center justify-center gap-2 p-3 text-center">
            <Icon name="person" className="!text-[28px] text-base-content/40" />
            <span className="line-clamp-4 text-xs font-medium text-base-content/60">
              {person.name}
            </span>
          </div>
        )}
      </div>
      <p className="mt-2 truncate text-sm font-medium" title={person.name}>
        {person.name}
      </p>
    </Link>
  );
}
