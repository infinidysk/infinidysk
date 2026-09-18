import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { HealthTable } from "./health-table";

describe("HealthTable", () => {
  it.each([0, 35])("shows active progress at %i percent on desktop and mobile", (progress) => {
    const markup = renderToStaticMarkup(
      <HealthTable
        isEnabled
        healthCheckItems={[
          {
            id: "active",
            name: "active.mkv",
            path: "/content/active.mkv",
            releaseDate: null,
            lastHealthCheck: null,
            nextHealthCheck: null,
            progress,
          },
        ]}
      />,
    );

    expect(markup.match(/<progress /g)).toHaveLength(2);
    expect(markup).toContain(`${progress}%`);
    expect(markup).not.toContain("ASAP");
  });
});
