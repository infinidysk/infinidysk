import { data } from "react-router";
import { z } from "zod";
import { getSessionUser, IS_FRONTEND_AUTH_DISABLED } from "~/auth/authentication.server";
import {
  backendClient,
  BackendApiError,
  BackendContractError,
  BackendUnavailableError,
} from "~/clients/backend-client.server";
import { loadFilesPage } from "~/clients/files-page.server";
import { normalizeContentPath, parseFilesParameters } from "~/clients/files-contract";

const headers = { "Cache-Control": "private, no-store" };
async function requireFilesAccess(request: Request, mutation: boolean): Promise<void> {
  const user = await getSessionUser(request);
  if (!IS_FRONTEND_AUTH_DISABLED && !user)
    throw data({ ok: false, error: "Authentication required." }, { status: 401, headers });
  if (mutation && user?.role === "readonly")
    throw data({ ok: false, error: "Administrator access required." }, { status: 403, headers });
}
function readValue(values: FormData | URLSearchParams, name: string): string {
  const entries = values.getAll(name);
  if (entries.length !== 1 || typeof entries[0] !== "string")
    throw data({ ok: false, error: `Invalid ${name}.` }, { status: 400, headers });
  return entries[0];
}
function validate<T>(read: () => T): T {
  try {
    return read();
  } catch {
    throw data({ ok: false, error: "Invalid Files request." }, { status: 400, headers });
  }
}
function failure(request: Request, error: unknown) {
  request.signal.throwIfAborted();
  if (error instanceof BackendApiError)
    return data(
      { ok: false, error: error.detail, traceId: error.traceId },
      { status: error.status, headers },
    );
  if (error instanceof BackendUnavailableError)
    return data({ ok: false, error: "Backend temporarily unavailable." }, { status: 503, headers });
  if (error instanceof BackendContractError)
    return data(
      { ok: false, error: "The backend returned an invalid Files response." },
      { status: 502, headers },
    );
  throw error;
}
export async function loader({ request }: { request: Request }) {
  await requireFilesAccess(request, false);
  const parameters = new URL(request.url).searchParams;
  const operation = parameters.get("operation") ?? "browse";
  if (parameters.getAll("operation").length > 1)
    throw data({ ok: false, error: "Invalid operation." }, { status: 400, headers });
  if (operation === "delete-preview") {
    await requireFilesAccess(request, true);
    const path = validate(() => normalizeContentPath(readValue(parameters, "path")));
    const id = validate(() => z.guid().parse(readValue(parameters, "expectedDavItemId")));
    try {
      return data(await backendClient.previewFileRemoval(path, id, request.signal), { headers });
    } catch (error) {
      return failure(request, error);
    }
  }
  if (operation !== "browse")
    throw data({ ok: false, error: "Invalid operation." }, { status: 400, headers });
  const validated = validate(() => parseFilesParameters(parameters));
  try {
    return data(await loadFilesPage(validated, request.signal), { headers });
  } catch (error) {
    return failure(request, error);
  }
}
export async function action({ request }: { request: Request }) {
  if (request.method !== "POST")
    throw data({ ok: false, error: "POST required." }, { status: 405, headers });
  await requireFilesAccess(request, true);
  const form = await request.formData();
  const intent = validate(() =>
    z.enum(["recheck", "arr-search", "remove"]).parse(readValue(form, "intent")),
  );
  const id = validate(() =>
    z
      .guid()
      .refine((value) => value !== "00000000-0000-0000-0000-000000000000")
      .parse(readValue(form, intent === "remove" ? "expectedDavItemId" : "davItemId")),
  );
  if (intent !== "recheck" && readValue(form, "confirmed") !== "true")
    throw data({ ok: false, error: "Confirmation required." }, { status: 400, headers });
  const path =
    intent === "remove" ? validate(() => normalizeContentPath(readValue(form, "path"))) : null;
  try {
    const result =
      intent === "remove"
        ? await backendClient.removeFile(path!, id, request.signal)
        : intent === "recheck"
          ? await backendClient.recheckFile(id, request.signal)
          : await backendClient.searchFileInArr(id, request.signal);
    return data({ ok: true, intent, davItemId: id, result }, { headers });
  } catch (error) {
    return failure(request, error);
  }
}
