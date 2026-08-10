// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MediaPreview } from "./media-preview";

const PREVIEW_URL = "/view/content/movie.mkv?downloadKey=deadbeef&extension=.mkv";

type MediaElementProto = {
    play: () => Promise<void>;
    load: () => void;
};

let playMock: ReturnType<typeof vi.fn>;
let loadMock: ReturnType<typeof vi.fn>;
const savedDialog: Partial<Record<"showModal" | "close", PropertyDescriptor | undefined>> = {};

beforeEach(() => {
    playMock = vi.fn<MediaElementProto["play"]>().mockResolvedValue(undefined);
    loadMock = vi.fn<MediaElementProto["load"]>().mockImplementation(() => {});
    Object.defineProperty(HTMLMediaElement.prototype, "play", { configurable: true, value: playMock });
    Object.defineProperty(HTMLMediaElement.prototype, "load", { configurable: true, value: loadMock });

    // jsdom may not implement modal dialog show/close.
    savedDialog.showModal = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, "showModal");
    savedDialog.close = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, "close");
    Object.defineProperty(HTMLDialogElement.prototype, "showModal", {
        configurable: true,
        value(this: HTMLDialogElement) { this.setAttribute("open", ""); },
    });
    Object.defineProperty(HTMLDialogElement.prototype, "close", {
        configurable: true,
        value(this: HTMLDialogElement) { this.removeAttribute("open"); },
    });
});

afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
    for (const key of ["showModal", "close"] as const) {
        const descriptor = savedDialog[key];
        if (descriptor) {
            Object.defineProperty(HTMLDialogElement.prototype, key, descriptor);
        } else {
            Reflect.deleteProperty(HTMLDialogElement.prototype, key);
        }
    }
});

function renderPreview(overrides: Partial<Parameters<typeof MediaPreview>[0]> = {}) {
    return render(
        <MediaPreview
            fileName="movie.mkv"
            filePath="content/movie.mkv"
            mimeType="video/x-matroska"
            sizeBytes={1_500_000_000}
            previewUrl={PREVIEW_URL}
            onClose={vi.fn()}
            {...overrides}
        />,
    );
}

function fireMediaError(el: HTMLMediaElement, code: number, message = "boom") {
    Object.defineProperty(el, "error", {
        configurable: true,
        get: () => ({ code, message }),
    });
    fireEvent(el, new Event("error"));
}

describe("MediaPreview", () => {
    it("renders a video element for video files with a playerSession on the source", () => {
        const { container } = renderPreview();
        const video = container.querySelector("video");
        expect(video).not.toBeNull();
        expect(video!.getAttribute("src")).toMatch(
            /^\/view\/content\/movie\.mkv\?downloadKey=deadbeef&extension=\.mkv&playerSession=[A-Za-z0-9_-]+$/,
        );
        expect(video!.hasAttribute("controls")).toBe(true);
        expect(video!.getAttribute("preload")).toBe("metadata");
        expect(video!.hasAttribute("playsinline")).toBe(true);
    });

    it("renders an audio element for audio files", () => {
        const { container } = renderPreview({
            fileName: "song.flac",
            filePath: "content/song.flac",
            mimeType: "audio/flac",
        });
        expect(container.querySelector("audio")).not.toBeNull();
        expect(container.querySelector("video")).toBeNull();
    });

    it("attempts autoplay when the media can play", () => {
        const { container } = renderPreview();
        fireEvent(container.querySelector("video")!, new Event("canplay"));
        expect(playMock).toHaveBeenCalled();
    });

    it("keeps escape hatches pointing at the direct and download URLs", () => {
        renderPreview();
        const openDirect = screen.getByRole("link", { name: /open direct/i });
        expect(openDirect.getAttribute("href")).toBe(PREVIEW_URL);
        expect(openDirect.getAttribute("target")).toBe("_blank");
        expect(screen.getByRole("link", { name: /download/i }).getAttribute("href"))
            .toBe(`${PREVIEW_URL}&download=true`);
    });

    it("recovers from network errors with the same URL and resumes playback", () => {
        vi.useFakeTimers();
        try {
            const { container } = renderPreview();
            const video = container.querySelector("video")!;
            fireEvent(video, new Event("canplay"));
            fireEvent(video, new Event("play"));

            fireMediaError(video, 2, "network down");
            expect(screen.getByRole("status").textContent).toContain("Stream interrupted");
            expect(screen.getByRole("status").textContent).toContain("attempt 1/3");

            const srcBefore = video.getAttribute("src");
            act(() => { vi.advanceTimersByTime(1000); });
            // Same URL reloaded — playerSession continuity preserved.
            expect(video.getAttribute("src")).toBe(srcBefore);
            expect(loadMock).toHaveBeenCalled();
        } finally {
            vi.useRealTimers();
        }
    });

    it("gives up after the retry budget and offers a manual retry", () => {
        vi.useFakeTimers();
        try {
            const { container } = renderPreview();
            const video = container.querySelector("video")!;
            fireEvent(video, new Event("play"));

            fireMediaError(video, 2);
            act(() => { vi.advanceTimersByTime(1000); });
            fireMediaError(video, 2);
            act(() => { vi.advanceTimersByTime(2000); });
            fireMediaError(video, 2);
            act(() => { vi.advanceTimersByTime(4000); });
            fireMediaError(video, 2);

            expect(screen.getByRole("alert").textContent).toContain("Playback failed after 3 attempts");
            const retry = screen.getByRole("button", { name: /retry/i });
            fireEvent.click(retry);
            expect(loadMock).toHaveBeenCalled();
            expect(screen.queryByRole("alert")?.textContent ?? "").not.toContain("Playback failed");
        } finally {
            vi.useRealTimers();
        }
    });

    it("shows an unsupported-format state instead of looping on decode errors", () => {
        const { container } = renderPreview();
        const video = container.querySelector("video")!;
        fireMediaError(video, 4, "no decoder");

        expect(screen.getByRole("alert").textContent).toContain("cannot decode");
        expect(screen.getByRole("alert").textContent).toContain("video/x-matroska");
        expect(screen.queryByRole("button", { name: /retry/i })).toBeNull();
    });

    it("ignores self-inflicted abort errors from reload/close", () => {
        const { container } = renderPreview();
        const video = container.querySelector("video")!;
        fireMediaError(video, 1, "aborted by us");
        expect(screen.queryByRole("alert")).toBeNull();
        expect(screen.getByRole("status").textContent).toContain("Loading");
    });

    it("releases the source on unmount so the stream is aborted", () => {
        const { container, unmount } = renderPreview();
        const video = container.querySelector("video")!;
        unmount();
        expect(video.getAttribute("src")).toBeNull();
        expect(loadMock).toHaveBeenCalled();
    });

    it("toggles the diagnostics drawer", () => {
        renderPreview();
        const toggle = screen.getByRole("button", { name: /diagnostics/i });
        expect(toggle.getAttribute("aria-expanded")).toBe("false");
        fireEvent.click(toggle);
        expect(toggle.getAttribute("aria-expanded")).toBe("true");
        expect(screen.getByText("Backend read")).toBeTruthy();
        expect(screen.getByText("Stream trace")).toBeTruthy();
    });
});
