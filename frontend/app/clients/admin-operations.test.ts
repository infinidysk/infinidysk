import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { adminApi, adminFrontendOperations } from "./admin-operations";

type OpenApiDocument = {
  paths?: Record<string, Record<string, { operationId?: string }>>;
};

function loadContract(): OpenApiDocument {
  let directory = dirname(fileURLToPath(import.meta.url));
  while (true) {
    const contractPath = resolve(directory, "contracts/openapi/admin-v1.json");
    if (existsSync(contractPath)) {
      return JSON.parse(readFileSync(contractPath, "utf8")) as OpenApiDocument;
    }

    const parent = dirname(directory);
    if (parent === directory) {
      throw new Error("Could not find contracts/openapi/admin-v1.json.");
    }
    directory = parent;
  }
}

describe("adminFrontendOperations", () => {
  it("maps every frontend admin method to a committed OpenAPI operation", () => {
    const paths = loadContract().paths ?? {};
    for (const operation of adminFrontendOperations) {
      const item = paths[operation.path];
      expect(item, operation.path).toBeDefined();
      expect(item?.[operation.method]?.operationId).toBe(operation.operationId);
    }
  });

  it("exposes path constants used by the backend client facade", () => {
    expect(adminApi.getConfig).toBe("/api/get-config");
    expect(adminApi.excludeSync).toBe("/api/exclude-sync");
  });
});
