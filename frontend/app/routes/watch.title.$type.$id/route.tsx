import { data, useLoaderData, useNavigate } from "react-router";
import { useEffect, useRef, useState } from "react";
import type { Route } from "./+types/route";
import { Button, Icon, Label } from "~/components/ui";
import {
  PlaybackDialog,
  episodeTarget,
  type PlaybackTarget,
} from "~/components/watch/playback-dialog";
import { backendClient } from "~/clients/backend-client.server";
import type { CatalogEpisode, CatalogTitleResponse } from "~/clients/catalog-contract";

export async function loader({ params, request }: Route.LoaderArgs) {
  const type = params.type;
  if (type !== "movie" && type !== "series") {
    throw data("Title not found.", { status: 404 });
  }

  const tmdbId = Number(params.id);
  if (!Number.isInteger(tmdbId) || tmdbId <= 0) {
    throw data("Title not found.", { status: 404 });
  }

  const searchParams = new URL(request.url).searchParams;
  const seasonRaw = searchParams.get("season");
  const season = seasonRaw === null ? undefined : Number(seasonRaw);
  if (season !== undefined && (!Number.isInteger(season) || season < 0)) {
    throw data("Season must be zero or greater.", { status: 404 });
  }

  // Continue Watching deep-links carry the episode so the resume lands on the right row.
  const episodeRaw = searchParams.get("episode");
  const resumeEpisode = episodeRaw === null ? null : Number(episodeRaw);
  if (resumeEpisode !== null) {
    if (!Number.isInteger(resumeEpisode) || resumeEpisode < 1) {
      throw data("Episode must be one or greater.", { status: 404 });
    }
    if (season === undefined) {
      throw data("Episode requires a season.", { status: 404 });
    }
  }

  try {
    const catalogTitle = await backendClient.getCatalogTitle(type, tmdbId, season);
    return { catalogTitle, resumeEpisode };
  } catch (cause) {
    const notFound =
      cause instanceof Error && /404|not found/i.test(`${cause.message} ${String(cause.name)}`);
    throw data(cause instanceof Error ? cause.message : "The title could not be loaded.", {
      status: notFound ? 404 : 500,
    });
  }
}

export default function WatchTitleRoute() {
  const { catalogTitle, resumeEpisode } = useLoaderData<typeof loader>();
  const navigate = useNavigate();
  const [target, setTarget] = useState<PlaybackTarget | null>(null);
  const title = catalogTitle.title;

  if (!title) {
    throw data("Title not found.", { status: 404 });
  }

  const runtime =
    title.runtimeMinutes !== null && title.runtimeMinutes > 0
      ? `${title.runtimeMinutes} min`
      : null;
  const meta = [
    title.year !== null ? String(title.year) : null,
    title.rating !== null ? `${title.rating.toFixed(1)}/10` : null,
    runtime,
  ].filter(Boolean);

  // The season order travels with the target so the player can step and autoplay
  // without another round trip to the title page.
  const lineup = catalogTitle.episodes.map((entry) => ({
    season: entry.seasonNumber,
    episode: entry.episodeNumber,
    name: entry.name,
  }));
  const openEpisode = (episode: CatalogEpisode) =>
    setTarget(
      episodeTarget(
        {
          tmdbId: title.tmdbId,
          seasons: catalogTitle.seasons.map((season) => season.seasonNumber),
          episodes: lineup,
        },
        {
          season: episode.seasonNumber,
          episode: episode.episodeNumber,
          name: episode.name,
        },
      ),
    );

  return (
    <>
      <section className="relative mt-4 overflow-hidden rounded-xl border border-base-content/10 bg-base-200">
        {title.backdropUrl && (
          <img
            src={title.backdropUrl}
            alt=""
            className="absolute inset-0 h-full w-full object-cover opacity-40"
          />
        )}
        <div className="relative flex flex-col gap-5 bg-gradient-to-t from-base-100 via-base-100/70 to-transparent p-6 md:flex-row md:p-10">
          {title.posterUrl && (
            <img
              src={title.posterUrl}
              alt=""
              loading="lazy"
              className="hidden w-40 shrink-0 rounded-lg shadow-lg md:block"
            />
          )}
          <div className="flex min-w-0 flex-col gap-3">
            <h1 className="text-3xl font-bold tracking-tight md:text-4xl">{title.title}</h1>
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-base-content/60">
              {meta.map((entry) => (
                <span key={entry}>{entry}</span>
              ))}
              {title.genres.map((genre) => (
                <span key={genre} className="badge badge-ghost badge-sm">
                  {genre}
                </span>
              ))}
            </div>
            {title.overview && (
              <p className="max-w-3xl text-sm leading-relaxed text-base-content/80">
                {title.overview}
              </p>
            )}
            {title.type === "movie" && (
              <div>
                <Button
                  variant="primary"
                  size="medium"
                  onClick={() =>
                    setTarget({ type: "movie", tmdbId: title.tmdbId, label: title.title })
                  }
                >
                  <Icon name="play_arrow" className="!text-[20px]" />
                  Play
                </Button>
              </div>
            )}
          </div>
        </div>
      </section>

      {title.type === "series" && (
        <SeriesEpisodes
          catalogTitle={catalogTitle}
          onSeasonChange={(season) =>
            void navigate(
              season === null
                ? `/watch/title/series/${title.tmdbId}`
                : `/watch/title/series/${title.tmdbId}?season=${season}`,
            )
          }
          onPlay={openEpisode}
          resumeEpisode={resumeEpisode}
        />
      )}
      {target && <PlaybackDialog target={target} onClose={() => setTarget(null)} />}
    </>
  );
}

function SeriesEpisodes({
  catalogTitle,
  onSeasonChange,
  onPlay,
  resumeEpisode,
}: {
  catalogTitle: CatalogTitleResponse;
  onSeasonChange: (season: number | null) => void;
  onPlay: (episode: CatalogEpisode) => void;
  resumeEpisode: number | null;
}) {
  const { seasons, episodes, selectedSeason } = catalogTitle;
  const resumeRef = useRef<HTMLLIElement>(null);

  // The episode from Continue Watching is scrolled into view once the season renders.
  useEffect(() => {
    resumeRef.current?.scrollIntoView({ block: "center", behavior: "smooth" });
  }, [resumeEpisode, selectedSeason]);

  return (
    <section className="py-6">
      <div className="mb-4 flex flex-wrap items-end gap-3">
        <div>
          <Label htmlFor="watch-season-select">Season</Label>
          <select
            id="watch-season-select"
            className="select select-sm w-48"
            value={selectedSeason ?? ""}
            onChange={(event) =>
              onSeasonChange(event.target.value === "" ? null : Number(event.target.value))
            }
          >
            {selectedSeason === null && <option value="">Select a season</option>}
            {seasons.map((season) => (
              <option key={season.seasonNumber} value={season.seasonNumber}>
                {season.name} ({season.episodeCount} episodes)
              </option>
            ))}
          </select>
        </div>
        <p className="pb-1 text-xs text-base-content/50">
          {episodes.length} episode{episodes.length === 1 ? "" : "s"} loaded
        </p>
      </div>

      {episodes.length === 0 ? (
        <p className="py-8 text-center text-sm text-base-content/60">
          No episodes are available for this season.
        </p>
      ) : (
        <ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
          {episodes.map((episode) => {
            const isResume =
              resumeEpisode !== null &&
              episode.episodeNumber === resumeEpisode &&
              episode.seasonNumber === selectedSeason;
            return (
              <li
                key={`${episode.seasonNumber}:${episode.episodeNumber}`}
                ref={isResume ? resumeRef : undefined}
                aria-current={isResume ? "true" : undefined}
                data-resume={isResume ? "true" : undefined}
                className={`flex gap-3 rounded-lg border bg-base-200/60 p-3 ${
                  isResume ? "border-primary/60 ring-1 ring-primary/30" : "border-base-content/10"
                }`}
              >
                <div className="aspect-video w-32 shrink-0 overflow-hidden rounded bg-base-300">
                  {episode.stillUrl ? (
                    <img
                      src={episode.stillUrl}
                      alt=""
                      loading="lazy"
                      className="h-full w-full object-cover"
                    />
                  ) : (
                    <div className="flex h-full w-full items-center justify-center">
                      <Icon
                        name="image_not_supported"
                        className="!text-[20px] text-base-content/40"
                      />
                    </div>
                  )}
                </div>
                <div className="flex min-w-0 flex-1 flex-col gap-1">
                  <p className="truncate text-sm font-medium" title={episode.name}>
                    {String(episode.episodeNumber).padStart(2, "0")}. {episode.name}
                  </p>
                  <p className="text-xs text-base-content/50">
                    {[
                      episode.airDate,
                      episode.runtimeMinutes ? `${episode.runtimeMinutes} min` : null,
                    ]
                      .filter(Boolean)
                      .join(" · ")}
                  </p>
                  {episode.overview && (
                    <p className="line-clamp-2 text-xs text-base-content/60">{episode.overview}</p>
                  )}
                  <div className="mt-auto flex items-center gap-2 pt-1">
                    <Button variant="primary" size="xsmall" onClick={() => onPlay(episode)}>
                      <Icon name="play_arrow" className="!text-[16px]" />
                      {isResume ? "Resume" : "Play"}
                    </Button>
                    {isResume && (
                      <span className="text-xs font-medium text-primary">
                        {`Resume · S${String(episode.seasonNumber).padStart(2, "0")}E${String(
                          episode.episodeNumber,
                        ).padStart(2, "0")}`}
                      </span>
                    )}
                  </div>
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
