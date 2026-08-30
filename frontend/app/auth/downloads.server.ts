import { createHmac } from "node:crypto";

export function getDownloadKey(path: string, frontendBackendApiKey: string): string {
  return createHmac("sha256", frontendBackendApiKey).update(path).digest("hex");
}
