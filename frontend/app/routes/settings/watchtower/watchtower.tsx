import { type Dispatch, type SetStateAction } from "react";
import {
  NativeForm as Form,
  ManagedSetting,
  SettingsIntro,
  SettingsPage,
  Tooltip,
} from "~/components/ui";

const GB = 1024 * 1024 * 1024;

type ProfileOption = { token: string; name: string };

function parseProfiles(raw?: string): ProfileOption[] {
  try {
    // `profiles.instances` config value shape (backend contract)
    const parsed = JSON.parse(raw || "{}") as {
      Profiles?: Array<{ Token?: string; Name?: string }>;
    };
    const list = parsed.Profiles ?? [];
    return list
      .filter((p) => p?.Token)
      .map((p) => ({ token: String(p.Token), name: String(p.Name ?? "").trim() }));
  } catch {
    return [];
  }
}

type WatchtowerSettingsProps = {
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

export function WatchtowerSettings({ config, setNewConfig }: WatchtowerSettingsProps) {
  const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
  const enabled = (config["watchtower.enabled"] ?? "false") === "true";
  const autoThroughput = (config["watchtower.auto-throughput"] ?? "false") === "true";
  const verboseLogging = (config["watchtower.verbose-logging"] ?? "false") === "true";
  const profiles = parseProfiles(config["profiles.instances"]);
  const profileToken = config["watchtower.profile-token"] ?? "";
  const orphanToken = profileToken.length > 0 && !profiles.some((p) => p.token === profileToken);
  const scope = config["watchtower.series-scope"] ?? "latest-season";
  const seasonBundles = (config["watchtower.season-bundles"] ?? "true") === "true";
  const bundleFallback = (config["watchtower.season-bundle-fallback"] ?? "false") === "true";
  const bundleFallbackScope = config["watchtower.season-bundle-fallback-scope"] ?? "latest-season";
  const bytesToGb = (b?: string) => {
    const n = Number(b ?? "");
    return n > 0 ? String(+(n / GB).toFixed(2)) : "";
  };
  const setGb = (key: string, gb: string) => {
    const n = Number(gb);
    set(key, n > 0 ? String(Math.round(n * GB)) : "0");
  };

  return (
    <SettingsPage>
      <SettingsIntro>
        Keeps the titles on your lists pre-resolved to a healthy release and re-verified over time,
        so each is found and ready before you need it. Pointer-only and safe-by-default: it stores
        segment maps (kilobytes), never video, and respects your indexer caps. Manage your lists on
        the <b>Watchtower</b> page; tune the engine here.
      </SettingsIntro>

      <ManagedSetting
        configKeys={[
          "watchtower.enabled",
          "watchtower.profile-token",
          "watchtower.ranking",
          "watchtower.size-floor-bytes",
          "watchtower.size-ceiling-bytes",
          "watchtower.shortlist-depth",
          "watchtower.grab-cap-per-resolve",
          "watchtower.active-set-cap",
          "watchtower.daily-resolve-budget",
          "watchtower.auto-throughput",
          "watchtower.sync-interval-seconds",
          "watchtower.series-scope",
          "watchtower.season-bundles",
          "watchtower.series-max-episodes",
          "watchtower.series-cap-keep",
          "watchtower.series-recent-count",
          "watchtower.season-bundle-fallback",
          "watchtower.season-bundle-fallback-scope",
          "watchtower.season-bundle-fallback-recent-count",
          "watchtower.season-bundle-fallback-max-episodes",
          "watchtower.min-grabs",
          "watchtower.verify-sample-count",
          "watchtower.verify-timeout-seconds",
          "watchtower.keepfresh-base-seconds",
          "watchtower.keepfresh-max-seconds",
          "watchtower.unavailable-retry-seconds",
          "watchtower.verbose-logging",
        ]}
      >
        <Form.Group className="flex flex-col gap-2">
          <Tooltip content="When on, syncs lists, resolves the biggest healthy release per item, and keeps it verified. When off, nothing runs.">
            <Form.Check
              type="switch"
              id="watchtower-enabled"
              className="cursor-pointer gap-2 p-0"
              label="Enable Watchtower"
              checked={enabled}
              onChange={(e) => set("watchtower.enabled", String(e.target.checked))}
            />
          </Tooltip>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Search profile</Form.Label>
          <Form.Select
            className="w-full max-w-md"
            disabled={!enabled || profiles.length === 0}
            value={profileToken}
            onChange={(e) => set("watchtower.profile-token", e.target.value)}
          >
            <option value="">First configured profile (default)</option>
            {profiles.map((p) => (
              <option key={p.token} value={p.token}>
                {p.name || `Untitled (${p.token.slice(0, 8)}…)`}
              </option>
            ))}
            {orphanToken && (
              <option value={profileToken}>Unknown profile ({profileToken.slice(0, 8)}…)</option>
            )}
          </Form.Select>
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            {profiles.length === 0 ? (
              <>
                No Search Profiles yet — create one under <b>Settings → Profiles</b> first.
              </>
            ) : (
              "Which Search Profile the resolver uses — this decides which indexers get queried. Default uses the first one."
            )}
          </p>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Release selection</Form.Label>
          <Form.Select
            className="w-full max-w-md"
            disabled={!enabled}
            value={config["watchtower.ranking"] ?? "watchdog"}
            onChange={(e) => set("watchtower.ranking", e.target.value)}
          >
            <option value="watchdog">Match the watchdog's pick</option>
            <option value="largest">Largest healthy release</option>
          </Form.Select>
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            <b>Match the watchdog</b> uses the same rank order as the watchdog, so the release
            Watchtower readies is exactly the one the watchdog would select. <b>Largest</b>
            always prefers the biggest healthy release (it may differ from what the watchdog would
            have chosen).
          </p>
        </Form.Group>

        <div className="flex flex-col gap-2">
          <div className="text-[0.95rem] font-semibold text-base-content">Series expansion</div>
          <div className="text-[0.8125rem] leading-relaxed text-base-content/55">
            How much of a TV show or anime is warmed when a whole series is tracked. Each series is
            expanded into its seasons/episodes via TVmaze (TV) or Kitsu (anime), keyless, then
            resolved and kept fresh like any other item.
          </div>
        </div>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Series scope</Form.Label>
          <Form.Select
            className="w-full max-w-md"
            disabled={!enabled}
            value={scope}
            onChange={(e) => set("watchtower.series-scope", e.target.value)}
          >
            <option value="latest-season">Latest season only</option>
            <option value="first-season">First season only</option>
            <option value="all-aired">All aired seasons</option>
            <option value="recent">Most recent episodes</option>
            <option value="off">Off — don't expand series</option>
          </Form.Select>
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            <b>Latest season</b> warms only the newest season (default). <b>First season</b>
            warms only season one — handy for watchlists you may start later. <b>All aired</b>
            backfills every released season. <b>Recent</b> keeps just the last few episodes across
            the whole series. <b>Off</b> stops series from expanding into episodes. Each list can
            override this on the Watchtower page.
          </p>
        </Form.Group>

        {scope === "recent" && (
          <Form.Group className="flex flex-col gap-2">
            <Form.Label>Recent episode count</Form.Label>
            <Form.Control
              className="w-full max-w-md"
              type="number"
              min={1}
              max={100}
              disabled={!enabled}
              value={config["watchtower.series-recent-count"] ?? "3"}
              onChange={(e) => set("watchtower.series-recent-count", e.target.value)}
            />
            <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
              How many of the most recent episodes to keep ready. Default 3.
            </p>
          </Form.Group>
        )}

        {(scope === "latest-season" || scope === "first-season" || scope === "all-aired") && (
          <>
            <Form.Group className="flex flex-col gap-2">
              <Tooltip content="Warm one season bundle per completed season instead of every episode. Still-airing seasons always use single episodes. Default on.">
                <Form.Check
                  type="switch"
                  id="watchtower-season-bundles"
                  className="cursor-pointer gap-2 p-0"
                  label="Prefer season bundles for finished seasons"
                  disabled={!enabled}
                  checked={seasonBundles}
                  onChange={(e) => set("watchtower.season-bundles", String(e.target.checked))}
                />
              </Tooltip>
            </Form.Group>

            {seasonBundles && (
              <Form.Group className="flex flex-col gap-2">
                <Tooltip content="If no healthy bundle exists, warm individual episodes instead. The pack is parked and stops being searched. Off by default.">
                  <Form.Check
                    type="switch"
                    id="watchtower-season-bundle-fallback"
                    className="cursor-pointer gap-2 p-0"
                    label="Fall back to episodes when no season bundle is found"
                    disabled={!enabled}
                    checked={bundleFallback}
                    onChange={(e) =>
                      set("watchtower.season-bundle-fallback", String(e.target.checked))
                    }
                  />
                </Tooltip>
              </Form.Group>
            )}

            {seasonBundles && bundleFallback && (
              <>
                <Form.Group className="flex flex-col gap-2">
                  <Form.Label>Fallback scope</Form.Label>
                  <Form.Select
                    className="w-full max-w-md"
                    disabled={!enabled}
                    value={bundleFallbackScope}
                    onChange={(e) => set("watchtower.season-bundle-fallback-scope", e.target.value)}
                  >
                    <option value="latest-season">Latest season only</option>
                    <option value="recent">Recent seasons</option>
                    <option value="all">All seasons</option>
                  </Form.Select>
                  <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    Which finished seasons fall back to episodes when their bundle is missing.
                    <b> Latest season</b> covers only the newest season, the one most likely being
                    watched. <b>Recent seasons</b> covers the last few. <b>All seasons</b>
                    covers every season. Seasons left out stay a bundle-only search and keep
                    retrying.
                  </p>
                </Form.Group>

                {bundleFallbackScope === "recent" && (
                  <Form.Group className="flex flex-col gap-2">
                    <Form.Label>Recent season count</Form.Label>
                    <Form.Control
                      className="w-full max-w-md"
                      type="number"
                      min={1}
                      max={100}
                      disabled={!enabled}
                      value={config["watchtower.season-bundle-fallback-recent-count"] ?? "2"}
                      onChange={(e) =>
                        set("watchtower.season-bundle-fallback-recent-count", e.target.value)
                      }
                    />
                    <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                      How many of the most recent seasons fall back to episodes. Default 2.
                    </p>
                  </Form.Group>
                )}

                <Form.Group className="flex flex-col gap-2">
                  <Form.Label>Max fallback episodes per season</Form.Label>
                  <Form.Control
                    className="w-full max-w-md"
                    type="number"
                    min={1}
                    max={1000}
                    disabled={!enabled}
                    value={config["watchtower.season-bundle-fallback-max-episodes"] ?? "50"}
                    onChange={(e) =>
                      set("watchtower.season-bundle-fallback-max-episodes", e.target.value)
                    }
                  />
                  <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                    Caps how many episodes are warmed when a season falls back, so a long season
                    does not fan out too far. Default 50.
                  </p>
                </Form.Group>
              </>
            )}

            <Form.Group className="flex flex-col gap-2">
              <Form.Label>Max items per series</Form.Label>
              <Form.Control
                className="w-full max-w-md"
                type="number"
                min={0}
                max={1000}
                disabled={!enabled}
                value={config["watchtower.series-max-episodes"] ?? "50"}
                onChange={(e) => set("watchtower.series-max-episodes", e.target.value)}
              />
              <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                Hard ceiling on how many items a single series may warm: individual episodes and
                season bundles combined, across every season. No series can expand past this on any
                scope, so a very long title stays bounded instead of fanning out. A season bundle
                counts as one item. 0 = unlimited. Default 50.
              </p>
            </Form.Group>

            {(config["watchtower.series-max-episodes"] ?? "50") !== "0" && (
              <Form.Group className="flex flex-col gap-2">
                <Form.Label>When over the cap, keep</Form.Label>
                <Form.Select
                  className="w-full max-w-md"
                  disabled={!enabled}
                  value={config["watchtower.series-cap-keep"] ?? "newest"}
                  onChange={(e) => set("watchtower.series-cap-keep", e.target.value)}
                >
                  <option value="newest">Newest seasons &amp; episodes</option>
                  <option value="oldest">Oldest seasons &amp; episodes</option>
                </Form.Select>
                <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
                  Which end of the series to keep when it hits the cap. <b>Newest</b> stays current
                  with the latest episodes and season packs. <b>Oldest</b> starts from season one,
                  useful when you plan to watch a series from the beginning.
                </p>
              </Form.Group>
            )}
          </>
        )}

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Junk floor (GB)</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={0}
            step={0.1}
            disabled={!enabled}
            value={bytesToGb(config["watchtower.size-floor-bytes"])}
            onChange={(e) => setGb("watchtower.size-floor-bytes", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            Ignore releases smaller than this. Default 0.5 GB.
          </p>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Bandwidth ceiling (GB)</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={0}
            step={0.5}
            disabled={!enabled}
            placeholder="empty = no ceiling"
            value={bytesToGb(config["watchtower.size-ceiling-bytes"])}
            onChange={(e) => setGb("watchtower.size-ceiling-bytes", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            Ignore releases larger than this. Empty / 0 = no ceiling.
          </p>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Minimum grabs</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={0}
            disabled={!enabled}
            value={config["watchtower.min-grabs"] ?? "0"}
            onChange={(e) => set("watchtower.min-grabs", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            Only consider releases with at least this many grabs (recorded downloads) on the
            indexer. Higher = more proven releases but fewer candidates. 0 = no minimum. Default 0.
          </p>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Active warm-set cap</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={1}
            max={100000}
            disabled={!enabled}
            value={config["watchtower.active-set-cap"] ?? "100"}
            onChange={(e) => set("watchtower.active-set-cap", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            How many items the engine keeps actively ready. Beyond this, items are listed but parked
            until they bubble up. This is what bounds load no matter how big your lists get. Default
            100.
          </p>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Tooltip content="Resolve as fast as indexers allow instead of pacing with the daily budget. Still obeys each indexer's RPM and daily caps. Default off.">
            <Form.Check
              type="switch"
              id="watchtower-auto-throughput"
              className="cursor-pointer gap-2 p-0"
              label="Auto throughput (match indexer limits)"
              disabled={!enabled}
              checked={autoThroughput}
              onChange={(e) => set("watchtower.auto-throughput", String(e.target.checked))}
            />
          </Tooltip>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Daily resolve budget</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={0}
            disabled={!enabled || autoThroughput}
            value={config["watchtower.daily-resolve-budget"] ?? "60"}
            onChange={(e) => set("watchtower.daily-resolve-budget", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            {autoThroughput ? (
              <>
                Ignored while <b>Auto throughput</b> is on — your indexer limits set the pace.
              </>
            ) : (
              "Soft cap on new resolves per day (0 = unlimited; your per-indexer caps always apply). Drips the backlog instead of hammering indexers. Default 60."
            )}
          </p>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Shortlist depth</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={1}
            max={5}
            disabled={!enabled}
            value={config["watchtower.shortlist-depth"] ?? "2"}
            onChange={(e) => set("watchtower.shortlist-depth", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            One live winner + backups kept per item, for instant failover. Default 2.
          </p>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Grab cap per resolve</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={1}
            max={10}
            disabled={!enabled}
            value={config["watchtower.grab-cap-per-resolve"] ?? "3"}
            onChange={(e) => set("watchtower.grab-cap-per-resolve", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            Max NZB fetches (the scarce indexer bucket) per item per pass. Keeps resolves
            grab-thrifty. Default 3.
          </p>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Verify sample count</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={1}
            max={20}
            disabled={!enabled}
            value={config["watchtower.verify-sample-count"] ?? "3"}
            onChange={(e) => set("watchtower.verify-sample-count", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            How many segments are sampled to confirm a release is alive on Usenet, on both the first
            resolve and every re-check. Higher = more thorough but slower. Default 3.
          </p>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Verify timeout (seconds)</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={2}
            max={120}
            disabled={!enabled}
            value={config["watchtower.verify-timeout-seconds"] ?? "10"}
            onChange={(e) => set("watchtower.verify-timeout-seconds", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            Max time a single segment check may run before it's treated as a timeout and its Usenet
            connection is released. Guards against unresponsive providers stalling the engine.
            Default 10.
          </p>
        </Form.Group>

        <div className="flex flex-col gap-2">
          <div className="text-[0.95rem] font-semibold text-base-content">
            Re-check &amp; retry timing
          </div>
          <div className="text-[0.8125rem] leading-relaxed text-base-content/55">
            How often the engine re-verifies items over time. Re-checks are Usenet-only — they
            confirm a release is still downloadable and do not query your indexers or spend the
            daily resolve budget.
          </div>
        </div>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Re-check interval (seconds)</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={300}
            max={604800}
            disabled={!enabled}
            value={config["watchtower.keepfresh-base-seconds"] ?? "21600"}
            onChange={(e) => set("watchtower.keepfresh-base-seconds", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            How often a ready item is re-verified on Usenet to confirm it's still downloadable.
            Items that stay healthy gradually stretch toward the max below. Default 21600 (6 hours).
          </p>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Max re-check interval (seconds)</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={600}
            max={2592000}
            disabled={!enabled}
            value={config["watchtower.keepfresh-max-seconds"] ?? "604800"}
            onChange={(e) => set("watchtower.keepfresh-max-seconds", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            The longest a repeatedly-healthy item waits between re-checks. Default 604800 (7 days).
          </p>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>Dead-item retry interval (seconds)</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={600}
            max={604800}
            disabled={!enabled}
            value={config["watchtower.unavailable-retry-seconds"] ?? "21600"}
            onChange={(e) => set("watchtower.unavailable-retry-seconds", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            How long an unavailable ("dead") item waits before it's searched again. Lower retries
            more often but spends more of your daily resolve budget. Default 21600 (6 hours).
          </p>
        </Form.Group>

        <Form.Group className="flex flex-col gap-2">
          <Form.Label>List sync interval (seconds)</Form.Label>
          <Form.Control
            className="w-full max-w-md"
            type="number"
            min={60}
            max={86400}
            disabled={!enabled}
            value={config["watchtower.sync-interval-seconds"] ?? "3600"}
            onChange={(e) => set("watchtower.sync-interval-seconds", e.target.value)}
          />
          <p className="m-0 text-[11px] leading-relaxed text-base-content/45">
            How often remote lists are re-fetched to catch additions/removals. Default 3600.
          </p>
        </Form.Group>

        <div className="flex flex-col gap-2">
          <div className="text-[0.95rem] font-semibold text-base-content">Diagnostics</div>
          <div className="text-[0.8125rem] leading-relaxed text-base-content/55">
            Extra visibility into what the engine is doing, for troubleshooting. Off by default.
          </div>
        </div>

        <Form.Group className="flex flex-col gap-2">
          <Tooltip content="Log per-item Watchtower activity at Information level (resolves, skips, heartbeats). Chatty — leave off for normal use.">
            <Form.Check
              type="switch"
              id="watchtower-verbose-logging"
              className="cursor-pointer gap-2 p-0"
              label="Verbose activity logging"
              checked={verboseLogging}
              onChange={(e) => set("watchtower.verbose-logging", String(e.target.checked))}
            />
          </Tooltip>
        </Form.Group>
      </ManagedSetting>
    </SettingsPage>
  );
}

export function isWatchtowerSettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
) {
  return [
    "watchtower.enabled",
    "watchtower.profile-token",
    "watchtower.ranking",
    "watchtower.size-floor-bytes",
    "watchtower.size-ceiling-bytes",
    "watchtower.shortlist-depth",
    "watchtower.grab-cap-per-resolve",
    "watchtower.active-set-cap",
    "watchtower.daily-resolve-budget",
    "watchtower.auto-throughput",
    "watchtower.sync-interval-seconds",
    "watchtower.series-scope",
    "watchtower.season-bundles",
    "watchtower.series-max-episodes",
    "watchtower.series-cap-keep",
    "watchtower.series-recent-count",
    "watchtower.season-bundle-fallback",
    "watchtower.season-bundle-fallback-scope",
    "watchtower.season-bundle-fallback-recent-count",
    "watchtower.season-bundle-fallback-max-episodes",
    "watchtower.min-grabs",
    "watchtower.verify-sample-count",
    "watchtower.verify-timeout-seconds",
    "watchtower.keepfresh-base-seconds",
    "watchtower.keepfresh-max-seconds",
    "watchtower.unavailable-retry-seconds",
    "watchtower.verbose-logging",
  ].some((k) => config[k] !== newConfig[k]);
}
