import type { IncomingMessage, ServerResponse } from "node:http";
import { logger } from "./logger";

/**
 * Closes a proxied backend response downstream.
 *
 * The backend aborts a streaming response mid-body when it discovers the data it
 * promised is not there. Ending the downstream response on that abort keeps the
 * client from hanging, but a client that gets a clean end treats the bytes it
 * already has as the whole file — the same silent truncation the backend went out
 * of its way to refuse. So an incomplete upstream body destroys the downstream
 * response instead, and only a complete one ends normally.
 */
export function handleBackendProxyResponse(
  proxyRes: IncomingMessage,
  req: IncomingMessage,
  res: ServerResponse,
): void {
  if (shouldRenderFileUnavailablePage(proxyRes, req)) {
    proxyRes.resume();
    const explorePaths = getExplorePaths(req);
    res.writeHead(200, {
      "Content-Type": "text/html; charset=utf-8",
      "Cache-Control": "no-store",
    });
    res.end(renderFileUnavailablePage(explorePaths));
    return;
  }

  res.writeHead(proxyRes.statusCode ?? 502, proxyRes.headers);
  proxyRes.pipe(res);

  proxyRes.on("close", () => {
    if (res.writableEnded) return;

    // Node sets `complete` only once the whole upstream message has been read,
    // so it covers both a declared Content-Length that was not met and a
    // chunked body that never got its terminating chunk.
    if (proxyRes.complete) {
      res.end();
      return;
    }

    // If the downstream client already disconnected (rclone VFS chunk churn,
    // a scrub, a tab close), the incomplete upstream body is the client's
    // doing — not a backend failure. `req.destroyed` is set when the client
    // closes the connection; a backend abort leaves the client's request
    // alive, so this check distinguishes the two without racing on `res`
    // teardown.
    if (req.destroyed) return;

    logger.warn(
      `Backend response for ${req.method ?? "?"} ${req.url ?? "?"} ended before its body was `
      + "complete; aborting the client transfer instead of ending it successfully.",
    );
    res.destroy();
  });
}

function shouldRenderFileUnavailablePage(proxyRes: IncomingMessage, req: IncomingMessage): boolean {
  const statusCode = proxyRes.statusCode;
  if (statusCode !== 400 && statusCode !== 404) return false;

  const accept = req.headers.accept?.toLowerCase() ?? "";
  if (!accept.includes("text/html")) return false;

  return getRequestPath(req).pathname.startsWith("/view/");
}

function getExplorePaths(req: IncomingMessage): { directory: string; root: string } {
  const { pathname, viewIndex } = getRequestPath(req);
  const basePath = viewIndex > 0 ? pathname.slice(0, viewIndex) : "";
  const itemPath = pathname.slice(viewIndex + "/view/".length);
  const parentPath = itemPath.slice(0, itemPath.lastIndexOf("/"));
  const root = `${basePath}/explore`;
  return { directory: `${root}${parentPath ? `/${parentPath}` : ""}`, root };
}

function getRequestPath(req: IncomingMessage): { pathname: string; viewIndex: number } {
  const originalUrl = (req as IncomingMessage & { originalUrl?: string }).originalUrl;
  const pathname = new URL(originalUrl ?? req.url ?? "/", "http://localhost").pathname;
  return { pathname, viewIndex: pathname.indexOf("/view/") };
}

function renderFileUnavailablePage(explorePaths: { directory: string; root: string }): string {
  const escapedDirectoryPath = escapeHtml(explorePaths.directory);
  const escapedRootPath = escapeHtml(explorePaths.root);
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>File unavailable · InfiniDysk</title>
  <style>
    :root { color-scheme: dark; }
    body { align-items: center; background: #111827; color: #f9fafb; display: flex; font-family: ui-sans-serif, system-ui, sans-serif; justify-content: center; margin: 0; min-height: 100vh; padding: 1.5rem; }
    main { background: #1f2937; border: 1px solid #374151; border-radius: 1rem; box-shadow: 0 20px 25px rgb(0 0 0 / 0.25); max-width: 34rem; padding: 2rem; text-align: center; }
    h1 { font-size: 1.5rem; margin: 0 0 0.75rem; }
    p { color: #d1d5db; line-height: 1.5; margin: 0 0 1.5rem; }
    .actions { display: flex; flex-wrap: wrap; gap: 0.75rem; justify-content: center; }
    a { background: #6366f1; border-radius: 0.5rem; color: white; display: inline-block; font-weight: 600; padding: 0.65rem 1rem; text-decoration: none; }
    a:hover { background: #4f46e5; }
    .secondary { background: #374151; }
    .secondary:hover { background: #4b5563; }
  </style>
</head>
<body>
  <main>
    <h1>File unavailable</h1>
    <p>This file may have been removed or is no longer available. Refresh the directory to see its current contents.</p>
    <div class="actions">
      <a href="${escapedDirectoryPath}">Back to directory</a>
      <a class="secondary" href="${escapedRootPath}">Explore root</a>
    </div>
  </main>
</body>
</html>`;
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, (character) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    '"': "&quot;",
    "'": "&#39;",
  })[character]!);
}
