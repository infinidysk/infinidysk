import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";

type MetricThresholds = {
  branches?: number;
  functions?: number;
  lines?: number;
  statements?: number;
};

type CoverageThresholdFile = {
  global: Required<MetricThresholds>;
  globs?: Record<string, MetricThresholds>;
};

const coverageThresholds = JSON.parse(
  readFileSync(new URL("./coverage-thresholds.json", import.meta.url), "utf8"),
) as CoverageThresholdFile;

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
      include: ["app/**/*.{ts,tsx}", "server/**/*.ts", "server.ts"],
      exclude: ["**/*.d.ts", "**/*.test.{ts,tsx}", "app/routes.ts"],
      reporter: ["text", "json-summary", "lcov", "cobertura"],
      reportsDirectory: "./coverage",
      thresholds: {
        ...coverageThresholds.global,
        ...(coverageThresholds.globs ?? {}),
      },
    },
  },
});
