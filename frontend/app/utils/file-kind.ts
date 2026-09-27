import type { DirectoryItem } from "~/clients/backend-client.server";

export type NamedFile = Pick<DirectoryItem, "name"> & {
  // mime-types lookup() returns false for unknown extensions; tolerate it here
  // so classification never throws on files the MIME database doesn't know.
  mimeType?: string | false | null;
};

export function getExtension(filename: string): string | undefined {
  const lastDotIndex = filename.lastIndexOf(".");
  if (lastDotIndex === -1 || lastDotIndex === 0) return undefined;
  return filename.slice(lastDotIndex);
}

export function getMime(file: NamedFile): string {
  return typeof file.mimeType === "string" ? file.mimeType : "";
}

export function isVideoFile(file: NamedFile): boolean {
  const mime = getMime(file);
  return (
    mime.startsWith("video") ||
    getExtension(file.name)?.toLowerCase() === ".mkv" ||
    mime === "application/mp4"
  );
}

export function isAudioFile(file: NamedFile): boolean {
  return getMime(file).startsWith("audio");
}

/** Media the in-app preview can attempt with a native element. */
export function isPlayableMedia(file: NamedFile): boolean {
  return isVideoFile(file) || isAudioFile(file);
}

export function fileKindRank(item: NamedFile): number {
  if (isVideoFile(item)) return 0;
  const mime = getMime(item);
  if (mime.startsWith("image")) return 1;
  if (mime.startsWith("audio")) return 2;
  return 3;
}

export function getIcon(file: NamedFile): string {
  if (isVideoFile(file)) return "movie";
  const mime = getMime(file);
  if (mime.startsWith("image")) return "image";
  if (mime.startsWith("audio")) return "audio_file";
  return "draft";
}
