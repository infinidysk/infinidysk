import { backendClient } from "~/clients/backend-client.server";

// Resource route bridging the browser to the backend's /api/exclude-sync endpoint.
// GET  -> current per-URL sync status. POST -> force a refresh, then return status.
export async function loader() {
  const urls = await backendClient.getExcludeSyncStatus();
  return Response.json({ urls });
}

export async function action() {
  const urls = await backendClient.refreshExcludeSync();
  return Response.json({ urls });
}
