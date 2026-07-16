#!/usr/bin/env python3
"""
Tiny local HTTP listener for capturing raw Jellyfin (or any) webhook payloads.

Usage:
    python3 scripts/jellyfin-webhook-logger.py [port]

Defaults to port 5005. Every POST body is pretty-printed to stdout and
appended (one JSON object per line) to jellyfin-webhook-events.jsonl in the
current directory, so you can collect several events across a viewing
session and hand the file back for analysis.

In Jellyfin: Dashboard -> Plugins -> Webhooks -> Add Generic Destination.
    Webhook Url: http://<this-machine-ip>:5005/jellyfin
    Notification Type: Playback Progress
    Item Type: Episodes
    Enable: Send All Properties (Ignores Template)
"""
import http.server
import json
import sys
from datetime import datetime, timezone

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 5005
LOG_FILE = "jellyfin-webhook-events.jsonl"


class Handler(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length)
        timestamp = datetime.now(timezone.utc).isoformat()

        try:
            payload = json.loads(body) if body else {}
            pretty = json.dumps(payload, indent=2, ensure_ascii=False, sort_keys=True)
        except json.JSONDecodeError:
            payload = body.decode("utf-8", errors="replace")
            pretty = payload

        print(f"\n=== {timestamp}  POST {self.path} ===")
        print(pretty)

        with open(LOG_FILE, "a", encoding="utf-8") as f:
            f.write(json.dumps(
                {"timestamp": timestamp, "path": self.path, "body": payload},
                ensure_ascii=False,
            ) + "\n")

        self.send_response(200)
        self.send_header("Content-Type", "text/plain")
        self.end_headers()
        self.wfile.write(b"ok")

    def do_GET(self):
        self.send_response(200)
        self.send_header("Content-Type", "text/plain")
        self.end_headers()
        self.wfile.write(b"jellyfin-webhook-logger: listening, POST JSON to any path\n")

    def log_message(self, format, *args):
        pass  # suppress default access log; we print our own per-event output


if __name__ == "__main__":
    server = http.server.HTTPServer(("0.0.0.0", PORT), Handler)
    print(f"Listening on http://0.0.0.0:{PORT} -- POST any JSON here.")
    print(f"Events are appended to ./{LOG_FILE}")
    print("Press Ctrl+C to stop.\n")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nStopping.")
