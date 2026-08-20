import { useCallback, useEffect, useRef, useState } from "react";
import type { ChangeEvent, DragEvent } from "react";
import type { UploadingFile } from "../route";

function isIosUserAgent(): boolean {
  if (typeof navigator === "undefined") return false;
  return (
    /iPad|iPhone|iPod/i.test(navigator.userAgent) ||
    (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1)
  );
}

function isNzbFile(file: File): boolean {
  const name = file.name.toLowerCase();
  return name.endsWith(".nzb") || name.endsWith(".nzb.gz");
}

export function useQueueDropzone(
  setUploadingFiles: (value: React.SetStateAction<UploadingFile[]>) => void,
  uploadQueueRef: React.RefObject<UploadingFile[]>,
  manualCategoryRef: React.RefObject<string>,
) {
  const inputRef = useRef<HTMLInputElement>(null);
  const dragDepthRef = useRef(0);
  const [isDragActive, setIsDragActive] = useState(false);
  const [rejectMessage, setRejectMessage] = useState<string | null>(null);

  // iOS Safari greys out files whose extension/MIME it doesn't recognize
  // (like .nzb) when `accept` is set. Strip the attribute after mount on
  // iOS instead of branching during render, which would mismatch the SSR
  // markup and trigger a hydration error.
  useEffect(() => {
    if (isIosUserAgent()) inputRef.current?.removeAttribute("accept");
  }, []);

  const enqueueFiles = useCallback(
    (acceptedFiles: File[]) => {
      const newFiles: UploadingFile[] = acceptedFiles.map((file) => ({
        file,
        queueSlot: {
          isUploading: true,
          nzo_id: `upload-${Date.now()}-${Math.random()}`,
          priority: "Normal",
          filename: file.name,
          cat: manualCategoryRef.current,
          percentage: "0",
          true_percentage: "0",
          status: "pending",
          mb: (file.size / (1024 * 1024)).toFixed(2),
          mbleft: (file.size / (1024 * 1024)).toFixed(2),
        },
      }));

      setUploadingFiles((files) => [...files, ...newFiles]);
      uploadQueueRef.current = [...uploadQueueRef.current, ...newFiles];
    },
    [manualCategoryRef, setUploadingFiles, uploadQueueRef],
  );

  const onDragEnter = useCallback((event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    dragDepthRef.current += 1;
    setIsDragActive(true);
  }, []);

  const onDragOver = useCallback((event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    event.dataTransfer.dropEffect = "copy";
  }, []);

  const onDragLeave = useCallback((event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    dragDepthRef.current = Math.max(0, dragDepthRef.current - 1);
    if (dragDepthRef.current === 0) setIsDragActive(false);
  }, []);

  const onDrop = useCallback(
    (event: DragEvent<HTMLDivElement>) => {
      event.preventDefault();
      dragDepthRef.current = 0;
      setIsDragActive(false);
      setRejectMessage(null);
      enqueueFiles(Array.from(event.dataTransfer.files).filter(isNzbFile));
    },
    [enqueueFiles],
  );

  const onInputChange = useCallback(
    (event: ChangeEvent<HTMLInputElement>) => {
      const selected = Array.from(event.target.files ?? []);
      const nzbFiles = selected.filter(isNzbFile);
      const rejected = selected.length - nzbFiles.length;
      setRejectMessage(
        rejected > 0
          ? `Skipped ${rejected} ${rejected === 1 ? "file that was" : "files that were"} not .nzb or .nzb.gz.`
          : null,
      );
      enqueueFiles(nzbFiles);
      event.target.value = "";
    },
    [enqueueFiles],
  );

  return {
    isDragActive,
    rejectMessage,
    clearRejectMessage: () => setRejectMessage(null),
    open: () => inputRef.current?.click(),
    getRootProps: () => ({
      onDragEnter,
      onDragOver,
      onDragLeave,
      onDrop,
    }),
    getInputProps: () => ({
      ref: inputRef,
      type: "file",
      accept: ".nzb,.nzb.gz,application/x-nzb,application/gzip",
      multiple: true,
      hidden: true,
      onChange: onInputChange,
    }),
  };
}
