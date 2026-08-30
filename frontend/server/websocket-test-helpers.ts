import http from "node:http";
import type { AddressInfo } from "node:net";
import WebSocket from "ws";

export function deferred<T>(): {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason?: unknown) => void;
} {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

export function listenOnLoopback(server: http.Server): Promise<AddressInfo> {
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      if (!address || typeof address === "string") {
        reject(new Error("server has no address"));
        return;
      }
      resolve(address);
    });
  });
}

export function closeServer(server: http.Server): Promise<void> {
  return new Promise((resolve, reject) => {
    server.close((error) => {
      if (error) reject(error);
      else resolve();
    });
  });
}

export function waitForOpen(ws: WebSocket, timeoutMs = 5_000): Promise<void> {
  if (ws.readyState === WebSocket.OPEN) return Promise.resolve();
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("WebSocket open timed out")), timeoutMs);
    ws.once("open", () => {
      clearTimeout(timer);
      resolve();
    });
  });
}

export function waitForClose(ws: WebSocket, timeoutMs = 5_000): Promise<number> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("WebSocket close timed out")), timeoutMs);
    ws.once("close", (code) => {
      clearTimeout(timer);
      resolve(code);
    });
  });
}

export function connectClient(port: number): WebSocket {
  const client = new WebSocket(`ws://127.0.0.1:${port}/ws`);
  client.on("error", () => {});
  return client;
}

export function websocketUpgradeRequest(port: number): Buffer {
  const key = Buffer.from("1234567890abcdef").toString("base64");
  return Buffer.from(
    `GET /ws HTTP/1.1\r\n` +
      `Host: 127.0.0.1:${port}\r\n` +
      `Upgrade: websocket\r\n` +
      `Connection: Upgrade\r\n` +
      `Sec-WebSocket-Key: ${key}\r\n` +
      `Sec-WebSocket-Version: 13\r\n` +
      `\r\n`,
  );
}

export function oversizedMaskedBinaryFrame(payloadLength: number): Buffer {
  const header = Buffer.alloc(2 + 8 + 4);
  header[0] = 0x82;
  header[1] = 0x80 | 127;
  header.writeBigUInt64BE(BigInt(payloadLength), 2);
  return Buffer.concat([header, Buffer.alloc(payloadLength)]);
}

export async function waitUntil(predicate: () => boolean, timeoutMs = 5_000): Promise<void> {
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  throw new Error("waitUntil timed out");
}
