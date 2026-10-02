import { useEffect, useState } from "react";
import { Button, Input } from "~/components/ui";
import type { PlexMedia } from "../plex/plex-api";
import type { PrefetchSource } from "./smart-prefetch-model";

type PlexSourceCustomizationProps = {
  source: PrefetchSource;
  preview: PlexMedia[] | null;
  busy: boolean;
  onChange: (source: PrefetchSource) => void;
  onPreview: () => void;
  onClose: () => void;
};

export function PlexSourceCustomization({
  source,
  preview,
  busy,
  onChange,
  onPreview,
  onClose,
}: PlexSourceCustomizationProps) {
  const [excluded, setExcluded] = useState(source.ExcludedShows.join(", "));
  useEffect(() => setExcluded(source.ExcludedShows.join(", ")), [source]);
  return (
    <div className="space-y-3 rounded-xl border border-base-content/15 bg-base-100 p-4">
      <div className="flex items-center justify-between gap-3">
        <h4 className="font-semibold">Customize {source.Title}</h4>
        <Button type="button" variant="ghost" onClick={onClose}>
          Close
        </Button>
      </div>
      <label className="grid gap-1 text-sm">
        Item limit
        <Input
          autoFocus
          aria-label={`Item limit for ${source.Title}`}
          type="number"
          min={1}
          max={1000}
          value={source.Limit}
          onChange={(event) => onChange({ ...source, Limit: Number(event.target.value) })}
        />
      </label>
      {source.Type === "show" || source.Type === "episode" ? (
        <label className="grid gap-1 text-sm">
          Excluded shows
          <Input
            aria-label={`Excluded shows for ${source.Title}`}
            value={excluded}
            onChange={(event) => setExcluded(event.target.value)}
            onBlur={() =>
              onChange({
                ...source,
                ExcludedShows: [...new Set(excluded.split(/[\s,;]+/).filter(Boolean))],
              })
            }
          />
        </label>
      ) : null}
      <Button className="min-h-11" type="button" disabled={busy} onClick={onPreview}>
        Preview {source.Title}
      </Button>
      {preview && (
        <div className="space-y-2" aria-live="polite">
          {preview.length === 0 && <p className="text-sm">No members returned.</p>}
          {preview.map((item, index) => (
            <div key={`${item.ratingKey}:${index}`} className="rounded-lg bg-base-200/60 p-3">
              <p className="font-medium">{item.title}</p>
              {item.mappingStatus && (
                <p className="text-xs text-base-content/65">
                  {item.mappingStatus} — {item.mappingReason}
                </p>
              )}
              <p className="text-xs text-base-content/55">
                {item.file
                  ? `Plex path: ${item.file}`
                  : "No file path returned; not yet mapped for warming."}
              </p>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
