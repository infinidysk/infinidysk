import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { adminApi, adminFrontendOperations } from "./admin-operations";

type OpenApiDocument = {
  paths?: Record<string, Record<string, { operationId?: string }>>;
};

function loadContract(): OpenApiDocument {
  const contractPath = resolve(
    dirname(fileURLToPath(import.meta.url)),
    "../../../contracts/openapi/admin-v1.json",
  );
  return JSON.parse(readFileSync(contractPath, "utf8")) as OpenApiDocument;
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
