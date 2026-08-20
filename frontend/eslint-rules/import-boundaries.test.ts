import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  describeImportViolation,
  resolveImportedPath,
} from "./import-boundaries.mjs";

const frontendRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const appRoot = path.join(frontendRoot, "app");

function check(fromRel, source) {
  return describeImportViolation(path.join(frontendRoot, fromRel), source, {
    appRoot,
    frontendRoot,
  });
}

describe("import-boundaries", () => {
  it("resolves ~/ aliases against app/", () => {
    const from = path.join(appRoot, "routes/health/route.tsx");
    expect(
      resolveImportedPath(from, "~/routes/queue/components/truncate/truncate", appRoot),
    ).toBe(path.join(appRoot, "routes/queue/components/truncate/truncate"));
  });

  it("resolves relative imports against the importer", () => {
    const from = path.join(appRoot, "routes/health/components/health-table.tsx");
    expect(
      resolveImportedPath(from, "../../queue/components/truncate/truncate", appRoot),
    ).toBe(path.join(appRoot, "routes/queue/components/truncate/truncate"));
  });

  it("rejects health importing queue via alias", () => {
    const violation = check(
      "app/routes/health/components/health-table.tsx",
      "~/routes/queue/components/truncate/truncate",
    );
    expect(violation?.message).toContain("route feature 'health' cannot import 'queue'");
    expect(violation?.message).toContain("routes/queue/components/truncate/truncate");
  });

  it("rejects health importing queue via a relative path", () => {
    const violation = check(
      "app/routes/health/components/health-table.tsx",
      "../../queue/components/truncate/truncate",
    );
    expect(violation?.message).toContain("route feature 'health' cannot import 'queue'");
  });

  it("rejects shared utils importing routes via alias", () => {
    const violation = check(
      "app/utils/service-provider.ts",
      "~/routes/settings/settings-tabs",
    );
    expect(violation?.message).toContain("must not import route module");
  });

  it("rejects shared navigation importing routes via alias", () => {
    const violation = check(
      "app/navigation/settings-tabs.ts",
      "~/routes/settings/settings-tabs",
    );
    expect(violation?.message).toContain("must not import route module");
  });

  it("rejects shared components importing routes via a relative path", () => {
    const violation = check(
      "app/components/service-provider-gate.tsx",
      "../routes/settings/settings-tabs",
    );
    expect(violation?.message).toContain("must not import route module");
  });

  it("allows a route to import files under its own directory", () => {
    expect(
      check("app/routes/queue/route.tsx", "./components/queue-table/queue-table"),
    ).toBeNull();
    expect(
      check(
        "app/routes/queue/components/queue-table/queue-table.tsx",
        "~/routes/queue/components/pagination/pagination",
      ),
    ).toBeNull();
  });

  it("allows app/routes.ts to import route modules", () => {
    expect(check("app/routes.ts", "./routes/explore/route.tsx")).toBeNull();
  });

  it("allows server composition to import app auth/client code", () => {
    expect(check("server.ts", "./app/auth/authentication.server")).toBeNull();
    expect(check("server/app.ts", "../app/clients/backend-client.server")).toBeNull();
  });
});
