import { withUrlBase } from "~/utils/url-base";

export async function plexRequest<T>(
  operation: string,
  body: object = {},
  signal?: AbortSignal,
): Promise<T> {
  const response = await fetch(withUrlBase(`/api/plex/${operation}`), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    ...(signal ? { signal } : {}),
  });
  if (!response.ok)
    throw new Error(
      response.status === 401
        ? "Plex session expired. Reload and sign in again."
        : response.status === 409
          ? "Plex settings or login changed. Reload and retry."
          : "Plex request failed. Check authorization, connection, and settings.",
    );
  return (await response.json()) as T;
}
