import { backendClient } from "~/clients/backend-client.server";

const ALLOWED_CAPACITIES = [20_000, 50_000, 100_000, 200_000] as const;

export async function loader() {
  const status = await backendClient.getStreamTracingStatus();
  return Response.json(status);
}

export async function action({ request }: { request: Request }) {
  const formData = await request.formData();
  if (formData.get("intent") === "discard") {
    return Response.json(await backendClient.discardStreamTraces());
  }
  const enabled = formData.get("enabled") === "true";
  const minutesEntry = formData.get("minutes");
  const minutesRaw = Number(typeof minutesEntry === "string" ? minutesEntry : "30");
  const minutes = [15, 30, 60].includes(minutesRaw) ? minutesRaw : 30;
  const capacityEntry = formData.get("capacity");
  const capacityRaw = Number(typeof capacityEntry === "string" ? capacityEntry : "100000");
  const capacity = (ALLOWED_CAPACITIES as readonly number[]).includes(capacityRaw)
    ? capacityRaw
    : 100_000;
  return Response.json(await backendClient.setStreamTracing(enabled, minutes, capacity));
}
