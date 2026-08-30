import { spawn } from "node:child_process";
import http from "node:http";
import net from "node:net";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it } from "vitest";
import { FRONTEND_BACKEND_API_KEY_ERROR } from "./runtime-config";

const frontendRoot = path.resolve(fileURLToPath(new URL("..", import.meta.url)));
const serverEntry = path.join(frontendRoot, "server.ts");
const CHILD_TIMEOUT_MS = 15_000;

type OccupiedPort = {
  port: number;
  close: () => Promise<void>;
};

async function occupyLoopbackPort(): Promise<OccupiedPort> {
  const server = net.createServer();
  const connections: net.Socket[] = [];
  server.on("connection", (socket) => connections.push(socket));
  server.on("error", () => undefined);
  await new Promise<void>((resolve, reject) => {
    server.listen(0, "127.0.0.1", () => resolve());
    server.once("error", reject);
  });
  const address = server.address();
  if (!address || typeof address === "string") {
    server.close();
    throw new Error("occupied port has no address");
  }
  return {
    port: address.port,
    close: () =>
      new Promise((resolve, reject) => {
        for (const socket of connections) socket.destroy();
        server.close((error) => {
          if (error) reject(error);
          else resolve();
        });
      }),
  };
}

async function listenFakeBackend(): Promise<{
  port: number;
  accepted: number;
  close: () => Promise<void>;
}> {
  const server = http.createServer((_req, res) => {
    res.writeHead(200, { "Content-Type": "text/plain" });
    res.end("ok");
  });
  server.on("upgrade", (request, socket) => {
    socket.destroy();
  });
  let accepted = 0;
  server.on("connection", () => {
    accepted += 1;
  });
  server.on("error", () => undefined);
  await new Promise<void>((resolve, reject) => {
    server.listen(0, "127.0.0.1", () => resolve());
    server.once("error", reject);
  });
  const address = server.address();
  if (!address || typeof address === "string") {
    server.close();
    throw new Error("fake backend has no address");
  }
  return {
    get accepted() {
      return accepted;
    },
    port: address.port,
    close: () =>
      new Promise((resolve, reject) => {
        server.close((error) => {
          if (error) reject(error);
          else resolve();
        });
      }),
  };
}

function spawnFrontend(env: NodeJS.ProcessEnv) {
  return spawn(process.execPath, ["--import", "tsx", serverEntry], {
    cwd: frontendRoot,
    env,
    stdio: ["ignore", "pipe", "pipe"],
  });
}

function collectOutput(child: ReturnType<typeof spawn>): Promise<{
  stdout: string;
  stderr: string;
  exitCode: number | null;
  signal: NodeJS.Signals | null;
}> {
  let stdout = "";
  let stderr = "";
  child.stdout?.setEncoding("utf8");
  child.stderr?.setEncoding("utf8");
  child.stdout?.on("data", (chunk: string) => {
    stdout += chunk;
  });
  child.stderr?.on("data", (chunk: string) => {
    stderr += chunk;
  });
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      child.kill("SIGKILL");
      reject(new Error("frontend child did not exit before timeout"));
    }, CHILD_TIMEOUT_MS);
    child.once("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.once("close", (exitCode, signal) => {
      clearTimeout(timer);
      resolve({ stdout, stderr, exitCode, signal });
    });
  });
}

describe("frontend runtime config startup", () => {
  const resources: Array<() => Promise<void>> = [];

  afterEach(async () => {
    const closers = resources.splice(0);
    await Promise.all(closers.map((close) => close()));
  });

  it.each([
    ["missing", undefined],
    ["empty", ""],
    ["spaces", "   "],
    ["mixed whitespace", " \t\r\n "],
  ] as const)(
    "exits before listen when FRONTEND_BACKEND_API_KEY is %s",
    async (_name, fixtureValue) => {
      const occupied = await occupyLoopbackPort();
      resources.push(occupied.close);
      const backend = await listenFakeBackend();
      resources.push(backend.close);

      const childEnvironment: NodeJS.ProcessEnv = {
        ...process.env,
        NODE_ENV: "development",
        NO_COLOR: "1",
        PORT: String(occupied.port),
        BACKEND_URL: `http://127.0.0.1:${backend.port}`,
      };
      delete childEnvironment["FORCE_COLOR"];

      if (fixtureValue === undefined) {
        delete childEnvironment["FRONTEND_BACKEND_API_KEY"];
      } else {
        childEnvironment["FRONTEND_BACKEND_API_KEY"] = fixtureValue;
      }

      const child = spawnFrontend(childEnvironment);
      resources.push(async () => {
        if (child.exitCode === null && child.signalCode === null) {
          child.kill("SIGKILL");
          await new Promise((resolve) => child.once("exit", resolve));
        }
      });

      const result = await collectOutput(child);
      const output = `${result.stdout}\n${result.stderr}`;
      const errorMatches = output.split(FRONTEND_BACKEND_API_KEY_ERROR);

      expect(result.exitCode).toBe(1);
      expect(result.signal).toBeNull();
      expect(errorMatches).toHaveLength(2);
      expect(output).not.toContain("ERR_INVALID_ARG_TYPE");
      expect(output).not.toContain("EADDRINUSE");
      expect(output).not.toContain("Starting frontend development server");
      expect(output).not.toContain("Starting frontend production server");
      expect(output).not.toContain("Frontend server listening");
      expect(output).not.toContain("Backend websocket connected");
      expect(backend.accepted).toBe(0);
    },
    20_000,
  );
});
