import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { HelpText, InputGroup, Label, Toggle } from "./form";

describe("form primitives", () => {
  it("keeps field labels above controls and allows long labels to wrap", () => {
    const markup = renderToStaticMarkup(<Label htmlFor="example">Example label</Label>);

    expect(markup).toContain('for="example"');
    expect(markup).toContain("label flex w-fit max-w-full flex-wrap");
  });

  it("allows help text to wrap instead of inheriting label whitespace", () => {
    const markup = renderToStaticMarkup(<HelpText>Long supporting copy</HelpText>);

    expect(markup).toContain('class="block ');
    expect(markup).not.toContain("label");
  });

  it("uses the semantic success color for enabled toggles", () => {
    const markup = renderToStaticMarkup(
      <Toggle id="enabled" checked readOnly label="Enabled setting" />,
    );

    expect(markup).toContain('class="toggle toggle-success"');
    expect(markup).toContain("whitespace-normal");
    expect(markup).toContain("checked");
  });

  it("renders suffixes inside a single daisyUI input surface", () => {
    const markup = renderToStaticMarkup(
      <InputGroup id="timeout" value="30" readOnly suffix="sec" />,
    );

    expect(markup).toContain('class="input "');
    expect(markup).toContain('<input class="min-w-0 grow "');
    expect(markup).toContain('<span class="label shrink-0">sec</span>');
  });
});
