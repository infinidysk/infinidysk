import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";

export async function loader({ request }: Route.LoaderArgs) {
  const url = new URL(request.url);
  const limit = Number(url.searchParams.get("limit") ?? "200");
  const entries = await backendClient.getWatchdogEntries(limit);
  return { entries };
}

export async function action({ request }: Route.ActionArgs) {
  if (request.method !== "POST" && request.method !== "DELETE") {
    return new Response("Method not allowed", { status: 405 });
  }
  const deleted = await backendClient.clearWatchdogEntries();
  return { deleted };
}
