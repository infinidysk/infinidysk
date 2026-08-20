import type { Route } from "./+types/route";
import { backendClient, type ConfigItem } from "~/clients/backend-client.server";

export async function action({ request }: Route.ActionArgs) {
  // get the ConfigItems to update
  const formData = await request.formData();
  const configJson = formData.get("config");
  if (typeof configJson !== "string") throw new Error("config required");
  // settings.update posts a JSON object mapping config names to string values (backend contract)
  const config = JSON.parse(configJson) as Record<string, string>;
  const configItems: ConfigItem[] = [];
  for (const [key, value] of Object.entries<string>(config)) {
    configItems.push({
      configName: key,
      configValue: value,
    });
  }

  // update the config items
  await backendClient.updateConfig(configItems);
  return { config: config };
}
