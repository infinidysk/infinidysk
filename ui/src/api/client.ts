import createClient from "openapi-fetch";
import type { components, paths } from "./schema";

export type Schemas = components["schemas"];

export const api = createClient<paths>({ baseUrl: "" });

export class HttpError extends Error {
  constructor(
    readonly status: number,
    message: string,
    /** The dotted setting path a validation error is about, like `mount.dir`. */
    readonly path?: string,
  ) {
    super(message);
  }
}

/** The response body, or an `HttpError` carrying the server's message. */
export async function body<T>(
  request: Promise<{ data?: T; error?: unknown; response: Response }>,
): Promise<T> {
  const { data, error, response } = await request;
  if (!response.ok) {
    const fail = error as { error?: string; path?: string | null } | undefined;
    throw new HttpError(response.status, fail?.error ?? response.statusText, fail?.path ?? undefined);
  }
  return data as T;
}
