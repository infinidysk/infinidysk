import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

const coverageThresholds = JSON.parse(
  readFileSync(new URL("./coverage-thresholds.json", import.meta.url), "utf8"),
) as {
  global: {
    branches: number;
    functions: number;
    lines: number;
    statements: number;
  };
  globs?: Record<string, { lines?: number }>;
};

export default defineConfig({
  resolve: {
    alias: {
      "~": fileURLToPath(new URL("./app", import.meta.url)),
    },
  },
  test: {
    environment: "node",
    coverage: {
      provider: "v8",
      reporter: ["text", "json-summary", "lcov", "cobertura"],
      reportsDirectory: "./coverage",
      thresholds: {
        ...coverageThresholds.global,
        ...coverageThresholds.globs,
      },
    },
  },
});
