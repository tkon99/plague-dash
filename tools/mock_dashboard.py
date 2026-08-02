#!/usr/bin/env python3
"""Plague Dash mock server — develop/test the dashboard without the game.

Replays a run export (data/plague-dash-export.json) as a fake live SSE
stream, exactly like the embedded mod server does, plus one synthesized
archived run so the run-history selector can be exercised.

Usage:
    python tools/mock_dashboard.py
    # then open http://localhost:8765
"""
import json
import sys
import time
import threading
from pathlib import Path
import http.server
import socketserver

PORT = 8766  # the live game mod server already owns 8765
DASH_DIR = Path(__file__).resolve().parent.parent / "dashboard"
EXPORT = Path(__file__).resolve().parent.parent / "data" / "plague-dash-export.json"
SAMPLE_DELAY_S = 0.012  # throttle so the stream visibly updates like the real one

# Synthetic trait list (the export doesn't carry techs; the real mod sends them)
FAKE_TECHS = [
    {"name": "Air Transmission 1", "id": "air1", "category": "Transmission", "cost": 250, "air": 0.35},
    {"name": "Air Transmission 2", "id": "air2", "category": "Transmission", "cost": 600, "air": 0.45},
    {"name": "Water Transmission 1", "id": "wat1", "category": "Transmission", "cost": 300, "sea": 0.3},
    {"name": "Symptom 1", "id": "sym1", "category": "Symptom", "cost": 200, "inf": 0.25, "sev": 0.05},
    {"name": "Symptom 2", "id": "sym2", "category": "Symptom", "cost": 500, "inf": 0.3, "let": 0.1},
    {"name": "Genetic Hardening 1", "id": "gh1", "category": "Ability", "cost": 900, "cureMulti": 1.15},
    {"name": "Cold Resistance 1", "id": "cold1", "category": "Ability", "cost": 700, "cold": 0.4},
    {"name": "Drug Resistance 1", "id": "drug1", "category": "Ability", "cost": 1200, "researchIneff": 0.3},
    {"name": "Lethal 1", "id": "let1", "category": "Symptom", "cost": 1500, "let": 0.5},
]

# Exports predate the mod's category fix and used negative costs. Normalize so
# the mock matches current mod output: cost >= 0, category guessed from the name.
TRANSMISSION_WORDS = ("air", "water", "blood", "bird", "rodent", "insect", "livestock", "spore")
ABILITY_WORDS = ("genetic", "drug", "cold", "heat", "salt", "hardening", "resistance", "dormant", "activation")


def normalize_purchase(p):
    name = p.get("name", "")
    cat = p.get("category") or ""
    if not cat:
        low = name.lower()
        if any(w in low for w in TRANSMISSION_WORDS):
            cat = "Transmission"
        elif any(w in low for w in ABILITY_WORDS):
            cat = "Ability"
        else:
            cat = "Symptom"
    return {**p, "cost": abs(p.get("cost", 0)), "category": cat}


def enrich_samples(samples):
    """Old exports predate several mod fixes. Match the current mod's output:
    - curePct: use the run's real value if present, else synthesize from
      cumulative research / effort needed. Apply the monotonic high-water-mark
      clamp the mod now applies (a failed-cure reset like 99%->1% shouldn't
      make the race chart snap backwards).
    - dnaSpent: monotonic high-water mark of the net field (matches the mod's
      own cumulative tracking).
    - dnaEarned: cumulativeSpent + balance."""
    out = []
    research = 0.0
    peak_spent = 0.0
    peak_earned = 0.0
    peak_cure = 0.0
    has_real_cure = any("curePct" in s for s in samples)
    for i, s in enumerate(samples):
        if i > 0:
            prev = out[i - 1]
            gap = max(0, s["day"] - prev["day"])
            research += (prev.get("cureSpd") or 0) * gap
        need = s.get("cureNeed") or 1
        if has_real_cure:
            cp = s.get("curePct") or 0
        else:
            cp = min(1.0, research / need)
        # monotonic clamp — the mod's high-water mark for the race chart/gauge
        peak_cure = max(peak_cure, cp)
        # The old dnaSpent is net (drops on devolve). The new mod tracks the
        # monotonic high-water mark; emulate that so the mock matches.
        raw_spent = s.get("dnaSpent") or 0
        peak_spent = max(peak_spent, raw_spent)
        earned = peak_spent + (s.get("dna") or 0)
        peak_earned = max(peak_earned, earned)
        out.append(dict(
            s,
            curePct=peak_cure,
            dnaSpent=peak_spent,
            dnaEarned=peak_earned,
        ))
    return out


def sse(event, data):
    return f"event: {event}\ndata: {json.dumps(data)}\n\n"


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(DASH_DIR), **kwargs)

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path == "/events":
            self.serve_events()
            return
        if path == "/chart.js":
            # mirror the mod server: vendored Chart.js is served at /chart.js
            data = (DASH_DIR / "vendor" / "chart.js").read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "application/javascript")
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)
            return
        super().do_GET()

    def serve_events(self):
        export = json.loads(EXPORT.read_text(encoding="utf-8"))
        meta = export.get("meta", {})
        samples = enrich_samples(export.get("samples", []))
        purchases = [normalize_purchase(p) for p in export.get("purchases", [])]
        countries = export.get("countries")

        # Synthesize one archived run: a shorter, "won" version of the same data.
        arch_day = max(1, int(len(samples) * 0.6))
        arch_samples = [s for s in samples if s.get("day", 0) <= arch_day]
        arch_purchases = [p for p in purchases if p.get("day", 0) <= arch_day]
        arch_meta = dict(meta, plagueType="Bacterium", difficulty="Normal", status="won")
        archive = {
            "meta": arch_meta, "finalDay": arch_day,
            "plagueType": "Bacterium", "difficulty": "Normal",
            "samples": arch_samples, "purchases": arch_purchases,
            "countries": countries, "techs": FAKE_TECHS,
        }

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "keep-alive")
        self.end_headers()

        def write(chunk):
            try:
                self.wfile.write(chunk.encode())
                self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError):
                raise SystemExit

        try:
            write(sse("meta", meta))
            time.sleep(0.2)
            write(sse("archive", archive))
            write(sse("techs", FAKE_TECHS))
            time.sleep(0.2)
            # Interleave purchases with samples by day, like the real game: each
            # evolve fires the instant it happens, not all at the end. Group
            # purchases by day and emit them right after the sample for that day.
            purchases_by_day = {}
            for p in purchases:
                purchases_by_day.setdefault(p.get("day", 0), []).append(p)
            for s in samples:
                write(sse("sample", s))
                for p in purchases_by_day.get(s.get("day"), []):
                    write(sse("purchase", p))
                time.sleep(SAMPLE_DELAY_S)
            if countries:
                write(sse("countries", countries))
            time.sleep(3600)  # hold the connection open
        except SystemExit:
            pass

    def log_message(self, fmt, *args):
        sys.stderr.write("[%s] %s\n" % (self.log_date_time_string(), fmt % args))


class ReusableTCPServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main():
    if not EXPORT.exists():
        print(f"ERROR: {EXPORT} not found — export a run first.", file=sys.stderr)
        sys.exit(1)
    url = f"http://localhost:{PORT}"
    print(f"Mock Plague Dash serving on {url} (Ctrl+C to stop)")
    try:
        with ReusableTCPServer(("", PORT), Handler) as httpd:
            httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nStopping.")


if __name__ == "__main__":
    main()
