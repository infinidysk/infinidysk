import { data, Link, useLoaderData } from "react-router";
import { useState } from "react";
import type { Route } from "./+types/route";
import { Icon } from "~/components/ui";
import { TitleRow } from "~/components/watch/title-row";
import { backendClient } from "~/clients/backend-client.server";
import type { CatalogPersonDetails, CatalogSummary } from "~/clients/catalog-contract";

export async function loader({ params }: Route.LoaderArgs) {
  const personId = params.id;
  if (!personId || !/^\d+$/.test(personId)) {
    throw data("Person not found.", { status: 404 });
  }

  let person: CatalogPersonDetails | null;
  let credits: CatalogSummary[];
  try {
    const response = await backendClient.getCatalogPerson(personId);
    person = response.person;
    credits = response.credits;
  } catch (cause) {
    throw data(
      cause instanceof Error ? cause.message : "The person could not be loaded right now.",
      { status: 502 },
    );
  }

  if (!person) {
    throw data("Person not found.", { status: 404 });
  }

  return { person, credits };
}

/** Biographies shorter than this fit the collapsed view without a toggle. */
const BIOGRAPHY_PREVIEW_CHARS = 320;

/**
 * Radarr-style person page: circular mugshot and identity header, the biography, then a
 * single horizontal credit row that links back into the catalog.
 */
export default function WatchPersonRoute() {
  const { person, credits } = useLoaderData<typeof loader>();
  const [expanded, setExpanded] = useState(false);

  const biography = (person.biography ?? "").trim();
  const expandable = biography.length > BIOGRAPHY_PREVIEW_CHARS;
  const facts = [person.knownForDepartment, person.birthday, person.placeOfBirth].filter(
    (fact): fact is string => typeof fact === "string" && fact.length > 0,
  );

  return (
    <article className="py-6">
      <div className="flex flex-col gap-5 sm:flex-row sm:items-start">
        <div className="h-[180px] w-[180px] shrink-0 overflow-hidden rounded-full bg-base-300 sm:h-[220px] sm:w-[220px]">
          {person.profileUrl ? (
            <img
              src={person.profileUrl}
              alt=""
              className="h-full w-full object-cover"
              decoding="async"
            />
          ) : (
            <div className="flex h-full w-full items-center justify-center">
              <Icon name="person" className="!text-[72px] text-base-content/40" />
            </div>
          )}
        </div>

        <div className="flex min-w-0 flex-col gap-2">
          <h1 className="text-2xl font-bold tracking-tight md:text-3xl">{person.name}</h1>
          {facts.length > 0 && <p className="text-sm text-base-content/60">{facts.join(" · ")}</p>}
          {biography.length > 0 && (
            <div className="max-w-3xl">
              <p
                className={`whitespace-pre-line text-sm text-base-content/80 ${expandable && !expanded ? "line-clamp-4" : ""}`}
              >
                {biography}
              </p>
              {expandable && (
                <button
                  type="button"
                  className="mt-1 text-sm font-medium text-primary hover:underline"
                  onClick={() => setExpanded((current) => !current)}
                >
                  {expanded ? "Read less" : "Read more"}
                </button>
              )}
            </div>
          )}
        </div>
      </div>

      <div className="mt-4">
        <Link to="/watch" className="btn btn-ghost btn-xs">
          <Icon name="arrow_back" className="!text-[16px]" />
          Back to catalog
        </Link>
      </div>

      <TitleRow row={{ id: "person-credits", title: "Credits", items: credits, people: null }} />
    </article>
  );
}
