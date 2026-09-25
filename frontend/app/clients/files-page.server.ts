import { lookup as getMimeType } from "mime-types";
import { backendClient } from "./backend-client.server";
import {
  parseFilesParameters,
  type FileRow,
  type FileResourceRow,
  type FilesResourcePage,
} from "./files-contract";
import { getDownloadKey } from "~/auth/downloads.server";
import { getFrontendRuntimeConfig } from "../../server/runtime-config";
import { getExtension } from "~/utils/file-kind";
import { withUrlBase } from "~/utils/url-base";

export function addMediaUrl(row: FileRow, frontendBackendApiKey: string): FileResourceRow {
  if (row.isDirectory) return { ...row, mimeType: "", previewUrl: null };
  const relativePath = row.path.replace(/^\//, "");
  const encodedPath = relativePath.split("/").map(encodeURIComponent).join("/");
  const parameters = new URLSearchParams({
    downloadKey: getDownloadKey(relativePath, frontendBackendApiKey),
  });
  const extension = getExtension(row.name);
  if (extension) parameters.set("extension", extension);
  return {
    ...row,
    mimeType: getMimeType(row.name) || "",
    previewUrl: withUrlBase(`/view/${encodedPath}?${parameters}`),
  };
}

export async function loadFilesPage(
  parameters: URLSearchParams,
  signal: AbortSignal,
): Promise<FilesResourcePage> {
  const page = await backendClient.browseFiles(parseFilesParameters(parameters), signal);
  const { frontendBackendApiKey } = getFrontendRuntimeConfig();
  return { ...page, rows: page.rows.map((row) => addMediaUrl(row, frontendBackendApiKey)) };
}
