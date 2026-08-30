import http from "node:http";
import net from "node:net";
import { WebSocket, WebSocketServer } from "ws";
import { attachWebsocketServerErrorListener } from "../http-server-lifecycle";
import {
  attachBrowserWebsocketErrorListener,
  errorCode,
  MAX_WEBSOCKET_PAYLOAD_BYTES,
  reportBrowserSocketError,
} from "../websocket-policy";
import { initializeWebsocketServer } from "../websocket.server";
import {
  deferred,
  listenOnLoopback,
  oversizedMaskedBinaryFrame,
  waitForClose,
  waitForOpen,
  websocketUpgradeRequest,
} from "../websocket-test-helpers";

const mode = process.env["WEBSOCKET_PROBE_MODE"];
const auth = deferred<boolean>();
let acceptedErrorCode: string | null = null;
let resolveAccepted: (code: string | null) => void = () => {};
const accepted = new Promise<string | null>((resolve) => {
  resolveAccepted = resolve;
});

function report(error: unknown, context: Parameters<typeof reportBrowserSocketError>[1]): void {
  reportBrowserSocketError(error, context);
  const code = errorCode(error);
  process.stdout.write(`ACCEPTED_ERROR_CODE=${code}\n`);
  if (acceptedErrorCode == null) {
    acceptedErrorCode = code;
    resolveAccepted(code);
  }
}

const httpServer = http.createServer();
const wss = new WebSocketServer({
  server: httpServer,
  path: "/ws",
  maxPayload: MAX_WEBSOCKET_PAYLOAD_BYTES,
});
attachWebsocketServerErrorListener(wss, {
  isOwned: () => false,
  onUnexpectedError: (error) => {
    process.stderr.write(`WSS_FATAL ${error.message}\n`);
    process.exitCode = 1;
    process.exit(1);
  },
});
initializeWebsocketServer(
  wss,
  { backendApiKey: "probe-unused-backend-api-key" },
  {
    authenticate: () => auth.promise,
    startBackendClient: () => ({ stop() {} }),
    reportBrowserSocketError: report,
    registerBrowserSocketErrorListener:
      mode === "oversized-pre-auth-listener-removed"
        ? () => {}
        : attachBrowserWebsocketErrorListener,
  },
);

const address = await listenOnLoopback(httpServer);
const url = `ws://127.0.0.1:${address.port}/ws`;
let clientCloseCode: number | null = null;
let rawSocket: net.Socket | undefined;

try {
  if (mode === "pipelined-oversized") {
    const socket = net.connect(address.port, "127.0.0.1");
    rawSocket = socket;
    socket.on("error", () => {});
    await new Promise<void>((resolve, reject) => {
      socket.once("connect", () => resolve());
      socket.once("error", reject);
    });
    socket.write(
      Buffer.concat([
        websocketUpgradeRequest(address.port),
        oversizedMaskedBinaryFrame(MAX_WEBSOCKET_PAYLOAD_BYTES + 1),
      ]),
    );
    await Promise.race([
      accepted,
      new Promise<never>((_, reject) =>
        setTimeout(() => reject(new Error("accepted error timeout")), 8000),
      ),
    ]);
    socket.destroy();
    rawSocket = undefined;
  } else {
    const client = new WebSocket(url);
    client.on("error", () => {});
    await waitForOpen(client);
    const closed = waitForClose(client);
    client.send(Buffer.alloc(MAX_WEBSOCKET_PAYLOAD_BYTES + 1));
    clientCloseCode = await closed;
    await Promise.race([
      accepted,
      new Promise<never>((_, reject) =>
        setTimeout(() => reject(new Error("accepted error timeout")), 8000),
      ),
    ]);
  }

  const healthy = new WebSocket(url);
  healthy.on("error", () => {});
  await waitForOpen(healthy);
  healthy.close();
  await waitForClose(healthy);

  const observation = {
    acceptedErrorCode,
    clientCloseCode,
    nextConnectionOpened: true,
  };
  process.stdout.write(`PROBE_RESULT=${JSON.stringify(observation)}\n`);
  process.stdout.write("probe-complete\n");
  process.exitCode = 0;
} finally {
  rawSocket?.destroy();
  auth.resolve(false);
  for (const client of wss.clients) client.terminate();
  await new Promise<void>((resolve) => {
    wss.close(() => resolve());
  });
  await new Promise<void>((resolve) => {
    httpServer.close(() => resolve());
  });
}
