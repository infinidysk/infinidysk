import type { Route } from "./+types/route";
import { z } from "zod";
import { backendClient, type ConfigItem } from "~/clients/backend-client.server";

const configFormSchema = z.object({
    config: z.string().min(1),
});

const configMapSchema = z.record(z.string().min(1), z.string());

export async function action({ request }: Route.ActionArgs) {
    const formData = await request.formData();
    const form = configFormSchema.safeParse({ config: formData.get("config") });
    if (!form.success) {
        throw new Error("config required");
    }

    let parsedJson: unknown;
    try {
        parsedJson = JSON.parse(form.data.config) as unknown;
    } catch {
        throw new Error("Config payload is not valid JSON.");
    }

    const config = configMapSchema.safeParse(parsedJson);
    if (!config.success) {
        throw new Error("Config values must be a map of string keys to string values.");
    }

    const configItems: ConfigItem[] = Object.entries(config.data).map(([configName, configValue]) => ({
        configName,
        configValue,
    }));

    await backendClient.updateConfig(configItems);
    return { config: config.data };
}
