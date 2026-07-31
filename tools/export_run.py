#!/usr/bin/env python3
"""Capture a full run from the mod's SSE stream and write it as an export JSON.

The server replays everything on connect (meta + full history + purchases +
countries + techs), then streams live updates. We read for a fixed window to
catch the initial replay burst, then stop.

Usage: python export_run.py [output.json]
"""
import json
import sys
import time
import urllib.request

SSE_URL = "http://localhost:8765/events"
CAPTURE_SECONDS = 4  # the replay burst arrives immediately; 4s is plenty + a bit of live

def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "plague-dash-export.json"

    print(f"Connecting to {SSE_URL} ...")
    req = urllib.request.Request(SSE_URL)
    events = []  # (event_type, data_str)
    try:
        with urllib.request.urlopen(req, timeout=CAPTURE_SECONDS + 2) as resp:
            buf = b""
            deadline = time.time() + CAPTURE_SECONDS
            while time.time() < deadline:
                chunk = resp.read(4096)
                if not chunk:
                    break
                buf += chunk
                # SSE frames end with a blank line; parse complete frames out
                while b"\n\n" in buf:
                    frame, buf = buf.split(b"\n\n", 1)
                    ev_type, data = parse_frame(frame)
                    if data is not None:
                        events.append((ev_type, data))
    except Exception as e:
        print(f"Stream error: {e}")

    if not events:
        print("No events captured — is the game running with the mod enabled?")
        sys.exit(1)

    # Reassemble in the same shape the dashboard export button produces.
    meta = {}
    samples = []
    purchases = []
    countries = None
    techs = None

    for ev_type, data in events:
        try:
            if ev_type == "meta":
                meta = json.loads(data)
            elif ev_type == "sample":
                samples.append(json.loads(data))
            elif ev_type == "purchase":
                purchases.append(json.loads(data))
            elif ev_type == "countries":
                countries = json.loads(data)
            elif ev_type == "techs":
                techs = json.loads(data)
        except json.JSONDecodeError:
            pass

    # Dedupe samples by day (replay + live may overlap on reconnect).
    seen = set()
    deduped = []
    for s in samples:
        d = s.get("day")
        if d in seen:
            continue
        seen.add(d)
        deduped.append(s)
    samples = deduped

    country_count = len(countries.get("countries", [])) if countries else 0
    tech_count = len(techs) if techs else 0
    payload = {
        "exportedAt": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "meta": meta,
        "samples": samples,
        "purchases": purchases,
        "countries": countries,
        "techs": techs,
        "summary": {
            "day": samples[-1]["day"] if samples else None,
            "samples": len(samples),
            "purchases": len(purchases),
            "countries": country_count,
            "techs": tech_count,
        },
    }
    with open(out, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2)

    print(f"Wrote {out}")
    print(f"  meta      : {meta.get('plagueType','?')} / {meta.get('difficulty','?')} / {meta.get('startCountry','?')} / {meta.get('status','?')}")
    print(f"  samples   : {len(samples)} days (day {payload['summary']['day']})")
    print(f"  purchases : {len(purchases)}")
    print(f"  countries : {country_count}")
    print(f"  techs     : {tech_count}")

def parse_frame(frame_bytes):
    """Parse one SSE frame: 'event: X\\ndata: Y' → (X, Y)."""
    ev_type = "message"
    data_lines = []
    for line in frame_bytes.decode("utf-8", errors="replace").split("\n"):
        line = line.strip()
        if line.startswith("event:"):
            ev_type = line[len("event:"):].strip()
        elif line.startswith("data:"):
            data_lines.append(line[len("data:"):].strip())
    if not data_lines:
        return ev_type, None
    return ev_type, "\n".join(data_lines)

if __name__ == "__main__":
    main()
