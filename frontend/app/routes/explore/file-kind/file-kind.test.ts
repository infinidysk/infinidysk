import { describe, expect, it } from "vitest";
import {
    fileKindRank,
    getExtension,
    getIcon,
    getMime,
    isAudioFile,
    isPlayableMedia,
    isVideoFile,
} from "./file-kind";

describe("getExtension", () => {
    it("returns the lowercase-preserving extension for normal names", () => {
        expect(getExtension("Movie.MKV")).toBe(".MKV");
        expect(getExtension("a.b.c")).toBe(".c");
    });

    it("returns undefined for dotfiles and extensionless names", () => {
        expect(getExtension(".par2")).toBeUndefined();
        expect(getExtension("README")).toBeUndefined();
    });
});

describe("getMime", () => {
    it("coerces false/null/undefined to empty string", () => {
        expect(getMime({ name: "x.bin", mimeType: false })).toBe("");
        expect(getMime({ name: "x.bin", mimeType: null })).toBe("");
        expect(getMime({ name: "x.bin" })).toBe("");
        expect(getMime({ name: "x.bin", mimeType: "video/mp4" })).toBe("video/mp4");
    });
});

describe("isVideoFile", () => {
    it("detects video by mime, .mkv extension, and application/mp4", () => {
        expect(isVideoFile({ name: "a.mp4", mimeType: "video/mp4" })).toBe(true);
        expect(isVideoFile({ name: "a.mkv", mimeType: false })).toBe(true);
        expect(isVideoFile({ name: "a.mp4", mimeType: "application/mp4" })).toBe(true);
        expect(isVideoFile({ name: "a.txt", mimeType: "text/plain" })).toBe(false);
    });
});

describe("isAudioFile / isPlayableMedia", () => {
    it("detects audio by mime only", () => {
        expect(isAudioFile({ name: "a.flac", mimeType: "audio/flac" })).toBe(true);
        expect(isAudioFile({ name: "a.flac", mimeType: false })).toBe(false);
    });

    it("treats video and audio as playable, everything else as not", () => {
        expect(isPlayableMedia({ name: "a.mkv", mimeType: "video/x-matroska" })).toBe(true);
        expect(isPlayableMedia({ name: "a.mp3", mimeType: "audio/mpeg" })).toBe(true);
        expect(isPlayableMedia({ name: "a.nzb", mimeType: false })).toBe(false);
        expect(isPlayableMedia({ name: "a.png", mimeType: "image/png" })).toBe(false);
    });
});

describe("fileKindRank", () => {
    it("does not throw when mimeType is false (mime-types miss)", () => {
        expect(() => fileKindRank({ name: "a.unknownext", mimeType: false })).not.toThrow();
        expect(fileKindRank({ name: "a.unknownext", mimeType: false })).toBe(3);
    });

    it("orders video before image before audio before other", () => {
        expect(fileKindRank({ name: "v.mkv", mimeType: false })).toBe(0);
        expect(fileKindRank({ name: "i.png", mimeType: "image/png" })).toBe(1);
        expect(fileKindRank({ name: "s.flac", mimeType: "audio/flac" })).toBe(2);
        expect(fileKindRank({ name: "r.nfo", mimeType: "text/plain" })).toBe(3);
    });
});

describe("getIcon", () => {
    it("maps kinds to material symbols and handles false mimeType", () => {
        expect(getIcon({ name: "a.mkv", mimeType: false })).toBe("movie");
        expect(getIcon({ name: "a.mp4", mimeType: "video/mp4" })).toBe("movie");
        expect(getIcon({ name: "a.png", mimeType: "image/png" })).toBe("image");
        expect(getIcon({ name: "a.flac", mimeType: "audio/flac" })).toBe("audio_file");
        expect(getIcon({ name: "a.nfo", mimeType: "text/plain" })).toBe("draft");
        expect(getIcon({ name: "a.xyz", mimeType: false })).toBe("draft");
    });
});
