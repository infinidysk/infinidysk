import type { Request, Response } from "express";
import compression from "compression";
import { shouldSkipCompression } from "./proxy-path.js";

// React Router streams document and `.data` responses and calls res.flush()
// after every chunk. Express `compression` maps that to zlib flush, which
// stacks 'drain' listeners under backpressure and trips
// MaxListenersExceededWarning (11+). Decide from response Content-Type at
// onHeaders time (after RR sets headers, before the body), not from Accept —
// curl/fetch often send `*/*` for the same SSR paths.
const REACT_ROUTER_STREAM_TYPE = /^(?:text\/html|text\/x-script|text\/event-stream)(?:\s*;|$)/i;

function contentType(res: Response): string | undefined {
  const header = res.getHeader("Content-Type");
  if (typeof header === "string") return header;
  if (Array.isArray(header)) return header[0];
  return undefined;
}

/** True when Express compression should wrap this response. */
export function shouldCompressResponse(req: Request, res: Response): boolean {
  if (shouldSkipCompression(req.path || "")) {
    return false;
  }

  const type = contentType(res);
  if (type && REACT_ROUTER_STREAM_TYPE.test(type)) {
    return false;
  }

  return compression.filter(req, res);
}
