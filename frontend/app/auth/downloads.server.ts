import { createHmac } from "node:crypto";

export function getDownloadKey(path: string): string {
    const apiKey = process.env["FRONTEND_BACKEND_API_KEY"] || "";
    return createHmac("sha256", apiKey).update(path).digest("hex");
}