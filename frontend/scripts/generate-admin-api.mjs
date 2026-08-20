#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const frontendRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const contract = resolve(
  process.env["ADMIN_OPENAPI_CONTRACT"] ??
    resolve(frontendRoot, "../contracts/openapi/admin-v1.json"),
);
const out = resolve(
  process.env["ADMIN_OPENAPI_TYPES"] ?? resolve(frontendRoot, "app/generated/admin-api.ts"),
);
const bin = resolve(frontendRoot, "node_modules/.bin/openapi-typescript");

if (!existsSync(contract)) {
  console.error(`Admin OpenAPI contract missing: ${contract}`);
  process.exit(1);
}
if (!existsSync(bin)) {
  console.error("openapi-typescript is not installed. Run npm ci in frontend/.");
  process.exit(1);
}

mkdirSync(dirname(out), { recursive: true });
const result = spawnSync(bin, [contract, "-o", out], { stdio: "inherit" });
process.exit(result.status ?? 1);
