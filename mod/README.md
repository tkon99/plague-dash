# Plague Dash — the mod (self-contained)

A UnityModManager (UMM) mod that **is** the dashboard: it patches Plague Inc:
Evolved to read live disease/cure/country stats, holds them in memory, and serves
the dashboard UI + a live data stream to your browser from an embedded web server.
One DLL, no other components. See the [project README](../README.md).

> ⚠ **Single-player only.** This mod gives a competitive information advantage.
> Using it in multiplayer could get you banned. The mod blocks multiplayer at
> two levels: the **Multiplayer button is hidden from the main menu**
> (`CMainStartSubScreen.buttonMultiPlayer` GameObject set inactive), and if a
> multiplayer game is somehow started, all data collection is disabled
> (`MultiplayerGuard` checks `CGameManager.IsMultiplayerGame`). The dashboard
> shows a warning overlay. Do not bypass this guard.

> **You build this** — it's C#/.NET 4.8 source that compiles against your local
> Plague Inc install. The build bakes the dashboard UI + Chart.js into the DLL.

## How it works

Plague Inc: Evolved is a Unity/Mono game. Its gameplay code is a managed .NET
assembly (`Assembly-CSharp.dll`). UMM loads this mod into that same runtime, and
[Harmony](https://github.com/pardeike/Harmony) weaves a **postfix** onto
`SPDisease.GameUpdate()` (the game's per-tick update). After each tick we read the
live fields off the disease object and, when the in-game day advances, publish a
sample to the in-memory `LiveState`.

The embedded `DashboardServer` (a `TcpListener` on a background thread) serves the
dashboard UI + Chart.js (embedded resources baked into the DLL) and streams the
live data to the browser over **Server-Sent Events** (`GET /events`). No files are
written to disk; the browser connects with `new EventSource('/events')`.

This patches *methods by name*, not byte offsets — far more robust across game
updates than memory scanning or save-file parsing.

Files:
- `Main.cs` — UMM entry point; applies/removes patches, starts/stops the server, settings.
- `MultiplayerGuard.cs` — detects MP sessions; guards all data-collection patches.
- `MenuButtonOverlay.cs` — IMGUI "Open Plague Dash" button + hides the MP button on the menu.
- `Patches/DiseaseStatsPatch.cs` — the data collector (the important one).
- `Patches/DnaTrackingPatch.cs` — captures each evolve/devolve as a DNA-spend event.
- `Patches/RunLifecyclePatch.cs` — detects new-run to reset the live state + meta.
- `LiveState.cs` — thread-safe in-memory store of history + meta + countries.
- `DashboardServer.cs` — embedded `TcpListener` server: static UI + SSE stream.
- `MenuButtonOverlay.cs` — IMGUI "Open Plague Dash" button overlaid on the main menu.
- `StatsRecorder.cs` — thin facade over `LiveState` (keeps patch call sites stable).
- `DiseaseSnapshot.cs` / `CountrySnapshot.cs` — one sample's data + JSON serialization.
- `Info.json` — UMM mod manifest.

## Prerequisites

1. **Visual Studio 2022 Build Tools** (or full VS 2022) — provides MSBuild. Only
   the build tools are needed; the full IDE is optional.
2. **UnityModManager** installed and configured for Plague Inc: Evolved.
   - Download UMM: <https://www.nexusmods.com/site/mods/21>
   - Run UMM, pick **Plague Inc: Evolved**, point it at your install
     (`C:\Program Files (x86)\Steam\steamapps\common\PlagueInc`), and Install.
   - This drops the modding DLLs (`UnityModManager.dll`, `0Harmony.dll`) into
     `…\PlagueIncEvolved_Data\Managed\UnityModManager\`, which the `.csproj`
     references. **You must do this step before the build will resolve.**

> **No .NET 4.8 Developer Pack needed.** If the Framework 4.8 reference assemblies
> aren't found (common on minimal Build Tools setups), the project automatically
> falls back to the offline copies bundled under `mod/tools/net48ref/`
> (extracted from the `Microsoft.NETFramework.ReferenceAssemblies.net48` package).
> This is configured via `<FrameworkPathOverride>` in the `.csproj`.

## Build

**Easiest — one command (builds + deploys to the game):**
```
cd mod
build.bat
```
`build.bat` auto-detects MSBuild (VS 2022 BuildTools / Enterprise / Pro / Community),
compiles `bin\Release\PlagueDash.dll`, and copies it + `Info.json` into the game's
`Mods\PlagueDash\` folder. Add `/nodeploy` to build without copying.

**In Visual Studio:** open `mod/PlagueDash.sln`, set **Release**, build (Ctrl+Shift+B).
If your Plague Inc install isn't at the default Steam path, edit `<GameManaged>` at
the top of `PlagueDash.csproj`.

**Raw MSBuild** (note: use `-p:` not `/p:` in Git Bash, which mangles `/p:`):
```
"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" ^
  PlagueDash.csproj -p:Configuration=Release -p:CopyToMods=false
```
Output: `bin\Release\PlagueDash.dll`.

## Install

> Already done in this environment — the mod is built, deployed, and confirmed
> loading cleanly (see the UMM log below). These steps are for a fresh machine.

1. Create `…\Steam\steamapps\common\PlagueInc\Mods\PlagueDash\` (make the `Mods`
   folder if it doesn't exist).
2. Copy in: `bin\Release\PlagueDash.dll` and `Info.json`. (`build.bat` does both.)
3. Launch Plague Inc: Evolved, open the UMM in-game menu, and enable **Plague Dash**.

**Confirmed-working on this machine** (from
`…\Managed\UnityModManager\Log.txt`):
```
[PlagueDash] Version '0.1.0'. Loading.
[PlagueDash] Loaded. Will write to ...\plague-dash\data\plague-dash.jsonl
[PlagueDash] Patches applied.
[PlagueDash] Active.
[Manager] FINISH. SUCCESSFUL LOADED 1/1 MODS.
```
UMM version 0.32.5.0, Unity 2019.4.412, game build July 2026.

## Use

1. Launch Plague Inc: Evolved with **Plague Dash** enabled in UMM. The embedded
   server starts automatically.
2. On the **main menu**, an "☣ Open Plague Dash" button appears in the
   bottom-right — click it to launch the dashboard in your default browser. (The
   UMM in-game menu also has an "Open Dashboard in Browser" button as a fallback,
   and you can open `http://localhost:8765` manually; port is configurable in the
   settings panel.)
3. Start a Plague Inc run. As in-game days pass, the dashboard's charts fill in
   live over the SSE stream. Put the browser on a second monitor.

No disk files, no separate server to run — the mod *is* the server. The menu
button only shows on the title screen, never during gameplay.

## Troubleshooting

- **Build error: cannot find `UnityModManager.dll` / `0Harmony.dll`** → UMM isn't
  installed for Plague Inc yet (see Prerequisites). These DLLs only appear after
  you install UMM.
- **Build error: reference assemblies for .NETFramework v4.8 not found** → the
  .NET 4.8 Developer Pack isn't installed. The project should auto-fall back to
  `tools/net48ref/`; if that's missing, either reinstall it (run the steps in
  `tools/README.md`) or install the [Developer Pack](https://aka.ms/msbuild/developerpacks).
- **`-p:` vs `/p:` weirdness in Git Bash** → Git Bash mangles `/p:` as a path.
  Use `-p:` (e.g. `-p:Configuration=Release`), or run `build.bat` via `cmd`.
- **Build error CS0311 (Settings / IDrawable)** → the settings class must
  implement `UnityModManagerNet.IDrawable` (a top-level interface, *not* nested
  under `UnityModManager`). This is already correct in `Main.cs`.
- **Mod loads but no data appears** → check the UMM log at
  `…\Managed\UnityModManager\Log.txt` for `RecordSample failed:` messages. Most
  likely the data path isn't writable, or a field name changed in a newer game
  version (the patch reads fields defensively and skips missing ones, but logs
  failures).
- **Charts show but a series is flat/empty** → that field wasn't readable from
  this game version. The patch tries known names first; see the field list in
  `Patches/DiseaseStatsPatch.cs`. Open the UMM log to see what's failing.

## Tuning field names for a newer game version

The patch reads fields by *name* via Harmony's reflection helper (`Traverse`).
If a field is renamed in a future patch, that one value will go missing rather
than crash. To find the new name, decompile `Assembly-CSharp.dll` with
[dnSpy](https://github.com/dnSpy/dnSpy) or [ILSpy](https://github.com/icsharpcode/ILSpy),
locate `SPDisease`, and update the name strings in `DiseaseStatsPatch.cs`.

The current field names were verified against the **July 2026** game build:
`globalInfectiousness/Severity/Lethality`, `cureRequirements`,
`globalCureResearchThisTurn`, `researchInefficiencyMultiplier`,
`mutationCounter`, `air/sea/land/corpseTransmission`, `totalInfected`,
`totalDead`, and `currentGameDate`/`startDate` on `CGameManager`.
