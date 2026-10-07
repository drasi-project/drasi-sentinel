#!/usr/bin/env python3
"""Capture local webhook POST bodies without external dependencies."""

import argparse
import datetime
import http.server
import json
from pathlib import Path


class CaptureHandler(http.server.BaseHTTPRequestHandler):
    output_path: Path

    def do_POST(self) -> None:
        try:
            length = int(self.headers["Content-Length"])
        except (KeyError, ValueError):
            self.send_error(411, "Content-Length is required")
            return

        if length < 0 or length > 1_048_576:
            self.send_error(413, "Request body exceeds 1 MiB")
            return

        body = self.rfile.read(length).decode("utf-8", errors="replace")
        event = {
            "received_at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "path": self.path,
            "headers": dict(self.headers),
            "body": body,
        }
        with self.output_path.open("a", encoding="utf-8") as output:
            output.write(json.dumps(event, separators=(",", ":")) + "\n")

        print(f"Captured POST {self.path} in {self.output_path}", flush=True)
        self.send_response(204)
        self.end_headers()

    def log_message(self, _format: str, *_args: object) -> None:
        return


def main() -> None:
    parser = argparse.ArgumentParser(description="Capture local Drasi webhook deliveries.")
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--port", default=8081, type=int)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    args.output.parent.mkdir(parents=True, exist_ok=True)
    CaptureHandler.output_path = args.output
    server = http.server.ThreadingHTTPServer((args.bind, args.port), CaptureHandler)
    print(f"Listening on http://{args.bind}:{args.port}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
