import { useRevalidator } from "react-router";
import { useState } from "react";
import { Icon } from "~/components/ui";
import type { CatalogHistoryItem } from "~/clients/catalog-contract";
import { removeCatalogHistoryItem } from "~/clients/catalog-client";
import { TitleCard } from "./title-card";
import { CARD_WIDTH, RowShell } from "./title-row";

export type ContinueWatchingRowProps = {
  items: CatalogHistoryItem[];
};

/**
 * Continue Watching: the last position per title, most recent first. Each card carries
 * the progress bar, resumes at the exact episode, and can drop itself from the row.
 */
export function ContinueWatchingRow({ items }: ContinueWatchingRowProps) {
  const revalidator = useRevalidator();
  const [removing, setRemoving] = useState<number | null>(null);
  // The remove control is revealed by state rather than `group-hover:`: in this app's
  // Tailwind/daisyUI layer order the plain `opacity-0` utility wins over the variant.
  const [activeKey, setActiveKey] = useState<string | null>(null);

  // One card per title+episode. The API keeps a single resumable row per key, but a
  // database written before the supersede rule can still hold a finished row next to
  // the live one — the resume row wins because it sorts first.
  const seen = new Set<string>();
  const cards = items.flatMap((entry) => {
    const key = [entry.type, entry.tmdbId, entry.season ?? "-", entry.episode ?? "-"].join(":");
    if (seen.has(key)) return [];
    seen.add(key);
    return [{ key, entry }];
  });

  if (cards.length === 0) return null;

  const remove = (entry: CatalogHistoryItem) => {
    setRemoving(entry.id);
    void removeCatalogHistoryItem(entry.id)
      // The loader owns the row: revalidate rather than patching local state, so the
      // list stays consistent with the backend after a failed or partial write.
      .then(() => revalidator.revalidate())
      .catch(() => undefined)
      .finally(() => setRemoving(null));
  };

  return (
    <RowShell id="continue-watching" title="Continue Watching">
      {cards.map(({ key, entry }) => {
        const base = `/watch/title/${entry.type}/${entry.tmdbId}`;
        const resume =
          entry.type === "series" && entry.season !== null && entry.episode !== null
            ? `${base}?season=${entry.season}&episode=${entry.episode}`
            : base;
        const watched =
          entry.durationSeconds > 0
            ? Math.min(1, entry.positionSeconds / entry.durationSeconds)
            : entry.finished
              ? 1
              : 0;

        return (
          <div
            key={key}
            className={`${CARD_WIDTH} relative snap-start`}
            onPointerEnter={() => setActiveKey(key)}
            onPointerLeave={() => setActiveKey((current) => (current === key ? null : current))}
            onFocusCapture={() => setActiveKey(key)}
            onBlurCapture={() => setActiveKey((current) => (current === key ? null : current))}
          >
            <TitleCard
              item={{
                type: entry.type,
                tmdbId: entry.tmdbId,
                title: entry.title,
                year: null,
                overview: null,
                rating: null,
                posterUrl: entry.posterUrl,
                backdropUrl: null,
              }}
              caption={[entry.finished ? "Watched" : "Resume", entry.subtitle]
                .filter(Boolean)
                .join(" · ")}
              progress={watched}
              to={resume}
            />
            <button
              type="button"
              onClick={() => remove(entry)}
              disabled={removing === entry.id}
              aria-label={`Remove ${entry.title}${entry.subtitle ? ` · ${entry.subtitle}` : ""} from Continue Watching`}
              title="Remove from Continue Watching"
              className={`absolute right-1.5 top-1.5 z-10 grid h-7 w-7 place-items-center rounded-full bg-base-100/90 text-base-content/80 shadow transition hover:text-error ${
                activeKey === key || removing === entry.id
                  ? "opacity-100"
                  : "pointer-events-none opacity-0"
              }`}
            >
              <Icon
                name={removing === entry.id ? "progress_activity" : "close"}
                className={`!text-[16px] ${removing === entry.id ? "animate-spin" : ""}`}
              />
            </button>
          </div>
        );
      })}
    </RowShell>
  );
}
