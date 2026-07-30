import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { ConfirmModal, confirmDisabled } from "./confirm-modal";

describe("confirmDisabled", () => {
    it("disables confirmation only when a required checkbox is unchecked", () => {
        expect(confirmDisabled(true, false)).toBe(true);
        expect(confirmDisabled(true, true)).toBe(false);
        expect(confirmDisabled(false, false)).toBe(false);
        expect(confirmDisabled(undefined, false)).toBe(false);
    });
});

describe("ConfirmModal", () => {
    it("disables confirmation until a required checkbox is selected", () => {
        const html = renderToStaticMarkup(
            <ConfirmModal
                show
                title="Confirm"
                message="Review the warning"
                checkboxMessage="I understand"
                requireCheckbox
                onCancel={vi.fn()}
                onConfirm={vi.fn()}
            />,
        );

        const confirmButton = html.match(/<button[^>]*class="[^"]*btn-error[^"]*"[^>]*>/)?.[0]
            ?? html.match(/<button[^>]*>Confirm Removal<\/button>/)?.[0]
            ?? "";
        expect(confirmButton).toContain("disabled");
    });

    it("keeps confirmation enabled when acknowledgement is optional", () => {
        const html = renderToStaticMarkup(
            <ConfirmModal
                show
                title="Confirm"
                message="Review the warning"
                onCancel={vi.fn()}
                onConfirm={vi.fn()}
            />,
        );

        const confirmButton = html.match(/<button[^>]*class="[^"]*btn-error[^"]*"[^>]*>/)?.[0]
            ?? html.match(/<button[^>]*>Confirm Removal<\/button>/)?.[0]
            ?? "";
        expect(confirmButton).not.toContain("disabled");
    });
});
