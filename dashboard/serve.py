#!/usr/bin/env python3
"""Plague Dash local server.

Serves the dashboard and the ../data directory so a browser can fetch
plague-dash.jsonl while the game mod appends to it concurrently. Browsers
forbid fetch() on file:// URLs, so a tiny static server is required.

Usage:
    python dashboard/serve.py
    # then open http://localhost:8765
"""
import sys
import functools
import http.server
import socketserver
from pathlib import Path

PORT = 8765
# Serve the dashboard dir at /, and also expose the data dir at /data.
DASH_DIR = Path(__file__).resolve().parent
PROJECT_DIR = DASH_DIR.parent
DATA_DIR = PROJECT_DIR / "data"


class Handler(http.server.SimpleHTTPRequestHandler):
    """Routes /data/* to the project data dir, everything else to dashboard."""

    def translate_path(self, path):
        # Strip query string
        path = path.split("?", 1)[0].split("#", 1)[0]
        if path.startswith("/data/"):
            return str(DATA_DIR / path[len("/data/"):])
        return str(DASH_DIR / path.lstrip("/"))

    def end_headers(self):
        # Disable caching so the polling fetch always gets fresh data.
        self.send_header("Cache-Control", "no-store, must-revalidate")
        super().end_headers()

    def log_message(self, fmt, *args):
        # Quieter logging: one line per request, skip the noisy 200/304 spam.
        sys.stderr.write("[%s] %s\n" % (self.log_date_time_string(), fmt % args))


class ReusableTCPServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main():
    if not DATA_DIR.exists():
        print(f"WARNING: data dir not found at {DATA_DIR}", file=sys.stderr)
    url = f"http://localhost:{PORT}"
    print(f"Plague Dash serving on {url}")
    print(f"  dashboard : {DASH_DIR}")
    print(f"  data      : {DATA_DIR}")
    print(f"  (Ctrl+C to stop)")
    try:
        with ReusableTCPServer(("", PORT), Handler) as httpd:
            httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nStopping.")
    except OSError as e:
        print(f"\nCould not bind port {PORT}: {e}", file=sys.stderr)
        print("If it's in use, change PORT at the top of this file.", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
