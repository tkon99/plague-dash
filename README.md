# Plague Dash

A live time-series dashboard for **Plague Inc: Evolved**. A single self-contained
mod patches the game to read disease/cure/country stats, holds them in memory, and
**serves the dashboard itself** to your browser — charts of infectivity, severity,
lethality, cure effort, cure speed, spread speed, infected/dead, plus per-continent
and per-country breakdowns, all updating live as you play.

```
 Plague Inc + UMM mod  ── (Harmony patch reads live game state) ──▶  in-memory LiveState
                                                                       │
                                            browser ◀── SSE stream ────┤  (embedded TcpListener
                                          (EventSource)                  serves dashboard UI +
                                                                         Chart.js from the DLL)
```

The mod **is** the server. One DLL: install it, start the game, open
`http://localhost:8765`. No files on disk, no separate components to run.

## Status

- **Self-contained mod: built, deployed, and confirmed loading in-game.**
  Compiles cleanly with `mod/build.bat` (MSBuild + offline net48 reference
  assemblies included). The embedded server (UI + SSE stream over a `TcpListener`)
  is verified serving: index.html, chart.js, and the live `/events` SSE stream all
  return correct HTTP/SSE. See [`mod/README.md`](mod/README.md).

## Quick start

1. Build + install the mod (`mod/build.bat` deploys to the game; see
   [`mod/README.md`](mod/README.md) for prerequisites).
2. Launch Plague Inc: Evolved, enable **Plague Dash** in the UMM in-game menu.
3. Open **http://localhost:8765** in your browser.
4. Start a run — charts fill in live. Put the browser on a second monitor.

## What the charts show

| Chart | Meaning |
|---|---|
| **Cure Race** | Infected & dead (left axis) vs. cure % progress (right axis) over days — the headline "who wins" view |
| **Plague Stats** | Infectivity · Severity · Lethality over time |
| **Cure Effort** | Total cure effort needed vs. current research speed |
| **Spread Speed** | New infections per day (the velocity of the outbreak) |
| **Transmission** | Air · Sea · Land transmission effectiveness |
| **DNA Economy** | DNA balance · total earned · total spent over time |
| **Continental Spread** | Infected by continent over time |
| **Country Breakdown** | Latest per-country table: infected, % of population, continent |
| **DNA Spent** | Live feed of every evolve/devolve: day, trait, category, DNA cost |

The KPI strip shows the current day's snapshot: total infected, dead, day number,
cure %, and an ETA estimate for cure completion.

## Project layout

```
plague-dash/
├─ mod/              # the self-contained UMM mod (C# source) — build target
│  ├─ Main.cs              # UMM entry: patches + server start/stop + settings
│  ├─ DashboardServer.cs   # embedded TcpListener: serves UI + SSE stream
│  ├─ LiveState.cs         # in-memory store (history + meta + countries)
│  ├─ Patches/             # Harmony patches reading live game state
│  ├─ build.bat            # one-command build + deploy
│  └─ README.md            # build + install instructions
├─ dashboard/        # dashboard UI source — baked into the DLL at build time
│  ├─ index.html
│  ├─ style.css
│  ├─ app.js         # EventSource consumer; renders charts + KPIs
│  └─ vendor/chart.js      # vendored Chart.js 4.4.1 (embedded)
├─ data/             # dev-only: sample generator for standalone dashboard testing
│  ├─ gen_sample.py
│  └─ *.sample.jsonl
├─ serve.bat         # dev-only: standalone server for editing the dashboard
└─ README.md         # this file
```

`dashboard/` and `data/` are **build-time inputs / dev tooling**, not runtime
components. The shipped artifact is just `PlagueDash.dll` + `Info.json`.

## Data contract (the SSE stream)

The mod streams typed events over `GET /events`:

- `event: meta` — `data:` run metadata: `{"plagueType":..., "difficulty":..., "startCountry":..., "status":"in_progress"}`
- `event: sample` — `data:` one day's stats (on connect the full history is replayed, then one per new in-game day):

```jsonl
{"day":42,"infected":1234567,"dead":4321,"inf":12.5,"sev":8.3,"let":2.1,"cureNeed":2.5e7,"cureSpd":1.2e6,"curePct":0.34,"dna":5,"dnaEarned":120,"dnaSpent":115,"airT":0.5,"seaT":0.6,"landT":0.7,"mutCnt":3.2,"continents":[{"n":"ASIA","inf":...}]}
```

- `event: countries` — `data:` a periodic full per-country snapshot: `{"day":N,"countries":[{"id","name","continent","inf","dead","pop"}]}`
- `event: purchase` — `data:` an evolve/devolve the instant it happens: `{"day":42,"action":"evolve","name":"Air 1 (Water)","category":"...","cost":12}` (devolves report the refund as a positive cost)

All numeric fields are optional — a missing field degrades gracefully (its series
just shows gaps), so the schema can grow without breaking the dashboard.

## Credits

The disease/cure field names the mod reads were identified from the research behind:
- [PIStatsOverlay](https://github.com/LittleYe233/PIStatsOverlay) (MIT) — an in-game
  stats overlay mod that proved which `SPDisease` fields are readable via Harmony.
- [plaguechanges](https://github.com/sschr15/plaguechanges) (MPL-2.0) — independently
  confirms the same internal field names.

This project reuses **no code** from either; it adds the time-series recording +
dashboard layer that neither provides.

## License

To be decided. The dashboard code is original; the mod code is original.
```
