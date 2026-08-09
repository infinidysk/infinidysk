import { useEffect } from "react";
import type { UploadingFile } from "../route";

const UPLOAD_TIMEOUT_MS = 120_000;
const MAX_RESPONSE_TEXT_LENGTH = 500;

type UploadResponse = {
    status?: unknown;
    error?: unknown;
};

type XhrErrorResponse = Pick<XMLHttpRequest, "response" | "responseText" | "status" | "statusText">;

export function useUploadController(
    isUploadingRef: React.RefObject<boolean>,
    uploadQueueRef: React.RefObject<UploadingFile[]>,
    uploadingFiles: UploadingFile[],
    setUploadingFiles: (value: React.SetStateAction<UploadingFile[]>) => void,
) {
    useEffect(() => {
        // fire-and-forget: the queue processor runs in the background and updates state itself
        void processUploadQueue(isUploadingRef, uploadQueueRef, setUploadingFiles);
    }, [uploadingFiles]);
}

export function buildAddFileUploadUrl(category: string): string {
    const params = new URLSearchParams({
        mode: "addfile",
        cat: category,
        priority: "0",
        pp: "0",
    });
    return `/api?${params.toString()}`;
}

export function getXhrErrorMessage(xhr: XhrErrorResponse): string {
    const response: unknown = xhr.response;
    if (
        typeof response === "object" &&
        response !== null &&
        "error" in response &&
        typeof response.error === "string" &&
        response.error.trim() !== ""
    ) {
        return response.error;
    }

    if (xhr.statusText.trim() !== "") return xhr.statusText;

    const responseText = xhr.responseText.trim();
    if (responseText !== "") return responseText.slice(0, MAX_RESPONSE_TEXT_LENGTH);

    return `Upload failed with status ${xhr.status}`;
}

function getUploadResponseError(response: unknown): string | undefined {
    if (
        typeof response !== "object" ||
        response === null ||
        !("status" in response) ||
        response.status !== false
    ) {
        return undefined;
    }

    const error = "error" in response ? response.error : undefined;
    return typeof error === "string" && error.trim() !== ""
        ? error
        : "Upload failed";
}

function uploadNzbFile(fileToUpload: UploadingFile, setUploadingFiles: (value: React.SetStateAction<UploadingFile[]>) => void): Promise<UploadResponse> {
    return new Promise((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        const formData = new FormData();
        let settled = false;

        const settle = (callback: () => void) => {
            if (settled) return;
            settled = true;
            callback();
        };

        formData.append("nzbFile", fileToUpload.file, fileToUpload.file.name);
        xhr.responseType = "json";
        xhr.timeout = UPLOAD_TIMEOUT_MS;
        xhr.upload.addEventListener("progress", (e) => {
            if (!e.lengthComputable) return;

            const progress = Math.round((e.loaded / e.total) * 100);
            setUploadingFiles(files => files.map(f =>
                f.queueSlot.nzo_id === fileToUpload.queueSlot.nzo_id
                    ? {
                        ...f,
                        queueSlot: {
                            ...f.queueSlot,
                            percentage: progress.toString(),
                            true_percentage: progress.toString(),
                        },
                    }
                    : f
            ));
        });

        xhr.addEventListener("load", () => {
            if (xhr.status >= 200 && xhr.status < 300) {
                settle(() => resolve(xhr.response as UploadResponse));
            } else {
                settle(() => reject(new Error(getXhrErrorMessage(xhr))));
            }
        });
        xhr.addEventListener("error", () => settle(() => reject(new Error("Upload failed"))));
        xhr.addEventListener("abort", () => settle(() => reject(new Error("Upload aborted"))));
        xhr.addEventListener("timeout", () => settle(() => reject(new Error("Upload timed out"))));

        xhr.open("POST", buildAddFileUploadUrl(fileToUpload.queueSlot.cat));
        xhr.send(formData);
    });
}

export async function processUploadQueue(
    isUploadingRef: React.RefObject<boolean>,
    uploadQueueRef: React.RefObject<UploadingFile[]>,
    setUploadingFiles: (value: React.SetStateAction<UploadingFile[]>) => void,
) {
    if (isUploadingRef.current || uploadQueueRef.current.length === 0) return;

    isUploadingRef.current = true;
    try {
        while (uploadQueueRef.current.length > 0) {
            const fileToUpload = uploadQueueRef.current[0];
            if (fileToUpload === undefined) break; // unreachable: the while condition guarantees an element
            setUploadingFiles(files => files.map(f =>
                f.queueSlot.nzo_id === fileToUpload.queueSlot.nzo_id
                    ? { ...f, queueSlot: { ...f.queueSlot, status: "uploading" } }
                    : f
            ));

            try {
                const response = await uploadNzbFile(fileToUpload, setUploadingFiles);
                const responseError = getUploadResponseError(response);
                if (responseError) throw new Error(responseError);
            } catch (error) {
                setUploadingFiles(files => files.map(f =>
                    f.queueSlot.nzo_id === fileToUpload.queueSlot.nzo_id ? {
                        ...f,
                        queueSlot: {
                            ...f.queueSlot,
                            status: "upload failed",
                            error: error instanceof Error ? error.message : "Upload failed",
                        },
                    } : f
                ));
            } finally {
                uploadQueueRef.current = uploadQueueRef.current.filter(x => x !== fileToUpload);
            }
        }
    } finally {
        isUploadingRef.current = false;
    }
}