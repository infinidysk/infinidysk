import type { RequestHandler } from "express";
import { createColors } from "picocolors";
import { clientErrorKey, shouldLogClientError } from "./request-log-throttle.js";
import { isWithinBackendStartupGrace } from "./startup-grace.js";

type LogLevel = "debug" | "info" | "warn" | "error";

const levels: Record<LogLevel, number> = {
  debug: 10,
  info: 20,
  warn: 30,
  error: 40,
};

const levelAliases: Record<string, LogLevel> = {
  verbose: "debug",
  debug: "debug",
  information: "info",
  info: "info",
  warning: "warn",
  warn: "warn",
  error: "error",
  fatal: "error",
};
const configuredLevel = levelAliases[process.env["LOG_LEVEL"]?.toLowerCase() ?? ""];
const minimumLevel: LogLevel = configuredLevel
  ? configuredLevel
  : process.env["NODE_ENV"] === "development"
    ? "debug"
    : "info";

const colorEnabled =
  process.env["NO_COLOR"] === undefined &&
  (process.env["FORCE_COLOR"] !== undefined || process.stdout.isTTY);
const color = createColors(colorEnabled);

const levelLabels: Record<LogLevel, string> = {
  debug: color.gray("DBG"),
  info: color.cyan("INF"),
  warn: color.yellow("WRN"),
  error: color.red("ERR"),
};

function timestamp(): string {
  return new Date().toLocaleTimeString("en-GB", {
    hour12: false,
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

function formatDetail(detail: unknown): string {
  if (detail instanceof Error) {
    return detail.stack ?? detail.message;
  }
  if (typeof detail === "string") {
    return detail;
  }
  try {
    return JSON.stringify(detail);
  } catch {
    return String(detail);
  }
}

function write(level: LogLevel, message: string, details: unknown[]): void {
  if (levels[level] < levels[minimumLevel]) {
    return;
  }

  const prefix = `${color.dim(timestamp())} ${levelLabels[level]}`;
  const suffix = details.length > 0 ? ` ${details.map(formatDetail).join(" ")}` : "";
  const line = `[${prefix}] ${message}${suffix}\n`;
  (level === "error" ? process.stderr : process.stdout).write(line);
}

export const logger = {
  debug: (message: string, ...details: unknown[]) => write("debug", message, details),
  info: (message: string, ...details: unknown[]) => write("info", message, details),
  warn: (message: string, ...details: unknown[]) => write("warn", message, details),
  error: (message: string, ...details: unknown[]) => write("error", message, details),
};

function colorMethod(method: string): string {
  switch (method) {
    case "GET":
      return color.cyan(method);
    case "POST":
      return color.green(method);
    case "PUT":
    case "PATCH":
      return color.yellow(method);
    case "DELETE":
      return color.red(method);
    default:
      return color.magenta(method);
  }
}

function colorStatus(status: number): string {
  const value = String(status);
  if (status >= 500) return color.red(value);
  if (status >= 400) return color.yellow(value);
  if (status >= 300) return color.cyan(value);
  return color.green(value);
}

export const requestLogger: RequestHandler = (req, res, next) => {
  const startedAt = process.hrtime.bigint();

  res.on("finish", () => {
    if (req.originalUrl === "/favicon.ico") {
      return;
    }

    const elapsedMs = Number(process.hrtime.bigint() - startedAt) / 1_000_000;
    // On error lines, identify the client: without this, a stream of 4xx
    // (an rclone mount retrying MKCOL/PUT against the read-only tree, a
    // misconfigured player hammering a dead URL) gives method/url/status
    // but no way to tell WHICH downstream client is responsible. `req.ip`
    // honors trust-proxy; the raw socket address is included when it
    // differs (i.e. behind a reverse proxy), plus the User-Agent.
    const socketAddr = req.socket?.remoteAddress ?? "-";
    const ip = req.ip ?? socketAddr;
    const userAgent = req.headers["user-agent"] ?? "-";
    const clientInfo = () => {
      const via = ip === socketAddr ? ip : `${ip} (via ${socketAddr})`;
      return color.dim(`${via} "${userAgent}"`);
    };
    const message =
      `${colorMethod(req.method)} ${req.originalUrl} ` +
      `${colorStatus(res.statusCode)} ${color.dim(`${elapsedMs.toFixed(1)} ms`)}` +
      (res.statusCode >= 400 ? ` ${clientInfo()}` : "");

    // During Docker's frontend-first startup window, proxied 502s are expected
    // while the backend is still binding. Downgrade so they are not double-logged
    // as ERR alongside the proxy error handler.
    if (res.statusCode === 502 && isWithinBackendStartupGrace()) {
      logger.debug(message);
    } else if (res.statusCode >= 500) {
      logger.error(message);
    } else if (res.statusCode >= 400) {
      // A client repeatedly probing the read-only tree (writing metadata sidecars,
      // an rclone mount retrying MKCOL/PUT) would otherwise emit a warn per attempt
      // and bury every other line. Keep the first, collapse the rest to debug.
      const key = clientErrorKey(
        req.method,
        res.statusCode,
        req.path ?? req.originalUrl,
        `${ip} ${userAgent}`,
      );
      const { log, suppressed } = shouldLogClientError(key);
      if (!log) {
        logger.debug(message);
      } else if (suppressed > 0) {
        logger.warn(`${message} ${color.dim(`(+${suppressed} similar suppressed)`)}`);
      } else {
        logger.warn(message);
      }
    } else if (process.env["NODE_ENV"] === "development") {
      logger.debug(message);
    }
  });

  next();
};
