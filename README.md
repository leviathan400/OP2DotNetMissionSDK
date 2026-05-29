# OP2DotNetMissionSDK

![Screenshot](https://images.outpostuniverse.org/OP2DotNetMissionSDKAI.png)

.NET Mission SDK for Outpost 2 - write Outpost 2 missions in C# or JSON.

Original author: **TechCor**.
Community-maintained fork (this repo) keeps the SDK buildable and adds diagnostics.

Forum thread: [OP2 Scenario Project](https://forum.outpost2.net/index.php/topic,6245.0.html).

**Latest release**: [v3.0.0 — AI Demo Missions](https://github.com/leviathan400/OP2DotNetMissionSDK/releases/tag/v3.0.0) (2026-05-28). Drop-in bundle of three AI-vs-AI Colony missions for OPU 1.4.1. See [CHANGES.md](CHANGES.md) for the v3.0.0 entry.

**Writing your own mission?** Start with [`MISSION_AUTHORING.md`](MISSION_AUTHORING.md) — a step-by-step walk-through from cloning the SDK to running a custom mission in OPU, using Visual Studio and OP2MissionEditor.

---

## About

DotNetMissionSDK lets mission authors write Outpost 2 scenarios in **C#** (or pure JSON `.opm` files, no coding required). Notable features that have no equivalent in the original C++ SDK:

- **Real AI** - `BotPlayer` plays under the same rules as a human, no cheating
- **JSON Reader** - entire missions describable in a single `.opm` file
- **BaseGenerator** - auto-creates a base from a unit list with distance hints
- **Pathfinder** - wrap-aware A* with rule-based "closest valid tile" search
- **PlayerCommandMap** - tracks command-center tubing connectivity per player
- **StateSnapshot / GameState** - immutable per-tick snapshot + live game state, the foundation for deterministic multi-threaded AI work
- **AsyncPump** - schedule work off-thread, run completion at a specific `TethysGame.Time()` - keeps lockstep multiplayer deterministic

---

## How the layers fit together

The plugin chain Outpost 2 sees:

```
Outpost2.exe
  → <Mission>.dll               (NativeMissionSDK/NativePlugin/LevelMain.cpp)
  → DotNetInterop.dll           (NativeMissionSDK/DotNetInterop - C++/CLI shim)
  → DotNetMissionSDK_v0.dll     (the C# SDK - your mission logic + JSON reader)
  → DotNetInterop.dll           (back out for OP2 API calls)
  → Outpost2.dll
```

Per-tick order inside the .NET side (`DotNetMissionEntry.Update`): TriggerManager → AsyncPump completions → GameState refresh → StateSnapshot build → MissionLogic.Update.

---

## Building from source

### Prerequisites

- **Visual Studio 2022 or newer** with the *Desktop development with C++* workload (must include "C++/CLI support" component - DotNetInterop is a managed-C++ project)
- **.NET SDK** with **.NET Framework 4.7.2 targeting pack** (for DotNetInterop) and **.NET Standard 2.0** (for the C# SDK - automatic with any recent dotnet SDK)
- A working **Outpost 2 install** (any modern version; Outpost Universe community release recommended)

### Cloning

This repo is fully self-contained - no submodules. Just clone normally:

```cmd
git clone https://github.com/leviathan400/OP2DotNetMissionSDK.git
```

`NativeMissionSDK/NativeSDK/` contains vendored copies of HFL, OP2Helper, Outpost2DLL, and odasl (formerly tracked as submodules of `OutpostUniverse/OP2MissionSDK`). HFL is no longer maintained upstream per its author, and the OP2MissionSDK V4.1.0 release the submodule pointed at pinned an HFL commit older than what `DotNetInterop/HFL/UnitEx.cpp` requires. Vendoring eliminates both problems.

If you want to sync newer upstream versions of any of those libraries in the future, that's now a deliberate manual copy operation - not an automatic submodule update.

### Build command

From the repo root (the directory containing `DotNetMissionSDK.sln`):

```cmd
:: First time only - restore NuGet packages
MSBuild.exe DotNetMissionSDK.sln -t:Restore -p:Configuration=Release

:: Build all projects
MSBuild.exe DotNetMissionSDK.sln -p:Configuration=Release -p:PlatformToolset=v143 -m
```

The `-p:PlatformToolset=v143` override is **required**: the bundled HFL and Outpost2DLL submodule projects target the v141 (VS 2017) toolset, which isn't installed by default on modern Visual Studio.

### Build outputs

| File | Location | Role |
|---|---|---|
| `cTest.dll` | `Release\` | Mission native plugin (bundled demo) |
| `DotNetInterop.dll` | `Release\` | Managed-C++ shim - shared across all missions |
| `DotNetMissionSDK_v0.dll` | `DotNetMissionSDK\bin\Release\netstandard2.0\` | C# SDK - versioned, shared across missions |
| `cTest.opm` | `Outpost2\` | Mission data (JSON) |

A post-build step also stages the artifacts into `Outpost2\` for convenience.

---

## Installing the demo mission

The Outpost Universe (OPU) launcher scans `OPU/maps/` for mission folders, so the canonical layout is **one folder per mission**:

```
<OP2 install>\OPU\
├── DotNetInterop.dll                  ← shared, one copy in OPU folder
├── DotNetMissionSDK_v0.dll            ← shared SDK runtime (v0), one copy in OPU folder
└── maps\cTest\
    ├── cTest.dll                       ← mission native plugin
    └── cTest.opm                       ← mission JSON
```

**Two shared DLLs**, both in the OPU folder:
- `DotNetInterop.dll` - C++/CLI shim, loads when Outpost2.exe loads a .NET mission DLL
- `DotNetMissionSDK_v0.dll` - versioned C# SDK runtime; every mission targeting SDK v0 uses this one copy

**Mission folder is just 2 files**: the native plugin (`MissionName.dll`) and the JSON (`MissionName.opm`).

The SDK resolves `DotNetMissionSDK_v0.dll` in priority order:
1. **Mission folder** - bundled SDK takes precedence (lets a mission ship its own SDK version, e.g. a forked or pre-release one)
2. **OPU folder** - shared SDK install (the standard install path, used by 1.4.2+)
3. **OP2 install root** - legacy fallback

So a mission can ship its own bundled SDK by including `DotNetMissionSDK_v0.dll` in its folder, OR rely on the shared one in OPU. Either works without code changes.

To install:

1. Copy `DotNetInterop.dll` and `DotNetMissionSDK_v0.dll` once into your `OPU\` folder
2. Create `OPU\maps\cTest\` and drop `cTest.dll`, `cTest.opm` into it
3. Launch via OPULauncher → pick a multiplayer or custom-game slot → script picker → select `cTest.dll`

(For a clean OPU 1.4.2+ install, both shared DLLs will ship pre-installed in the OPU folder - you only need step 2.)

The bundled `cTest` demo is a Colony Game pitting a human Eden player against a LaunchStarship-personality Plymouth AI on map `on6_01.map`.

---

## Creating a new scenario - JSON only (no C#)

For non-programmers. Make a copy of the cTest folder and rename:

```
OPU\maps\MyMission\
├── MyMission.dll
├── MyMission.opm
└── DotNetMissionSDK_v0.dll
```

The native plugin DLL is currently mission-specific because it has metadata baked in. Until a mission-editor tool exists, you'll need to **rebuild the native plugin** with your own values - see *Creating a new scenario - C#* below for the build step, but the only edits required are in `LevelMain.cpp` and the `.opm`.

The `.opm` JSON top-level structure:

```jsonc
{
  "LevelDetails": { /* description, map, tech tree, mission type, player count */ },
  "MasterVariant": {
    "TethysGame": { /* beacons, markers, wreckage, music, daylight */ },
    "Players":    [ /* per-player config: faction, color, BotType, starting units */ ],
    "AutoLayouts": [ /* optional procedural base layouts */ ]
  },
  "Disasters": [ /* meteor / quake / storm / vortex / volcano */ ],
  "Triggers":  [ /* native OP2 trigger definitions */ ]
}
```

Each `Players[]` entry has:
- `ID`, `IsHuman`, `IsEden`, `Color`, `Allies`
- `BotType` - `None` for a human, or one of: `PopulationGrowth`, `LaunchStarship`, `EconomicGrowth`, `Passive`, `Defender`, `Balanced`, `Aggressive`, `Harassment`, `Wreckless`
- `AIImpl` - which bot implementation drives the slot: empty/`TechCor` (reference), `AIv2` (improvement playground), `AI_Test` (from-scratch single-file reference bot, see [`DotNetMissionSDK/MissionSDK/AI_Test/README.md`](DotNetMissionSDK/MissionSDK/AI_Test/README.md)), or `AI_Blank` (heartbeat-only stub)
- `Resources` - sub-object containing `TechLevel`, `MoraleLevel`, `Kids`, `Workers`, `Scientists`, `CommonOre`, `RareOre`, `Food`, `SolarSatellites`, `CompletedResearch`, `Units`, `WallTubes`

**About `IsHuman` for AI bots**: OP2's engine applies god-mode population (256/4096/4096 colonists, no food simulation) to any seat with `IsHuman: false`. Setting `IsHuman: true` on an AI seat keeps the bot driving the player (the SDK only checks `BotType != None` for bot construction) but switches OP2 to normal population dynamics so the `.opm Resources.Kids/Workers/Scientists` are honored. Use `IsHuman: true` for AI-vs-AI tournaments where you want bots to actually manage population; use `IsHuman: false` for quick tests or fairness compensation in Human-vs-AI. See `AI_OVERVIEW.md` and `ISSUES.md` for the full investigation.

Use the bundled `Outpost2\cTest.opm` as a reference.

**Save the `.opm` as UTF-8 *without* BOM.** `DataContractJsonSerializer` rejects the BOM with `Encountered unexpected character 'ï'`. PowerShell's `Set-Content -Encoding UTF8` writes a BOM - use `[System.IO.File]::WriteAllText(path, content, [System.Text.UTF8Encoding]::new($false))` instead.

---

## Creating a new scenario - C#

For programmers who want logic beyond what the JSON trigger system can express.

1. Edit `NativeMissionSDK\NativePlugin\NativePlugin.vcxproj` and change `<TargetName>cTest</TargetName>` to your mission name (two occurrences - once per build configuration).
2. Edit `NativeMissionSDK\NativePlugin\LevelMain.cpp`:
   - Change the `ExportLevelDetailsEx(...)` parameters (description, map, tech tree, mission type, player count, max tech level, unit-only flag)
   - Optionally change `SdkPath` if you ship your own C# DLL
3. Edit `DotNetMissionSDK\CustomLogic.cs` to add per-mission logic. Override `InitializeNewMission`, `Update`, `OnTriggerExecuted`, etc.
4. Build (`MSBuild.exe DotNetMissionSDK.sln -p:Configuration=Release -p:PlatformToolset=v143 -m`).
5. Deploy the resulting files into `OPU\maps\<MyMission>\`.

For multiple custom missions in parallel: duplicate `NativePlugin.vcxproj` per mission so they can build side-by-side.

---

## Diagnostic logs

When a mission runs, the SDK writes its logs into a `logs/` subfolder of Outpost 2's working directory - for an OPU install that's `OPU\logs\`. The `logs/` folder is created automatically on mission start.

| File | Location | Mode | Purpose |
|---|---|---|---|
| `MissionSDK.log` | `OPU\logs\` | **append** | SDK lifecycle history (DLL load, attach, init, detach) - persists across runs so you can see every mission run in one file |
| `DotNetLog.txt` | `OPU\logs\` | overwrite | Every `Console.WriteLine` from C# code, wall-clock-timestamped (one timestamp per line, multi-line exception traces stay readable) |
| `BotPlayer_<N>.txt` | `OPU\logs\` | overwrite | Per-AI behavior trace: top goals each cycle, build attempts, exceptions caught during Update. One file per bot owner ID. |
| `BotPlayer_<N>_Status.txt` | `OPU\logs\` | overwrite | Per-bot state snapshot rewritten every 100 ticks (1 Mark): resources, workforce, power, building/vehicle counts, combat strength, starship modules in storage. Poll this while a mission runs to see what each bot is doing. |
| `BotPlayer_<N>_Research.txt` | `OPU\logs\` | overwrite | Per-bot research snapshot rewritten every 100 ticks: lab counts (basic/standard/advanced), scientists researching, and every completed tech grouped by category with level + lab type. |
| `Outpost2Log.txt` | `OPU\` | overwrite | OP2's own native log (separate from this SDK; stays at OPU root) |

**Bot log line format**: `[HH:mm:ss.fff t=N] <message>` - wall-clock time-of-day plus game tick, so events can be correlated both with real-world events and with `TethysGame.Time()` references in code.

**Dedup**: bot logs suppress identical consecutive messages with a `(last message repeated Nx)` summary line. Catches rapid-fire bursts (e.g. multiple goals' BuildStructureTask instances all logging the same "convec busy" line within milliseconds).

**Useful greps:**
- `grep "issuing DoBuild" BotPlayer_*.txt` - every build the AI actually issued
- `grep "EXCEPTION" *.txt` - any C# exceptions caught by the Update try/catch (if this returns anything, we want to know)
- `grep "Top goals" BotPlayer_*.txt` - every distinct goal state the bot was in

**On crashes**: if OP2 crashes hard (not a clean exit), `MissionSDK.log` will lack a `Detach: begin` / `Detach: complete` pair - that's how you tell a crash from a normal mission end. For native OP2 crashes, see also `%LOCALAPPDATA%\CrashDumps\` if you have Windows LocalDumps configured for `Outpost2.exe`.

---

## Differences from the native OP2MissionSDK

- **Triggers** are registered to a C# `TriggerManager`. When a trigger fires, the manager raises an event with the corresponding `TriggerStub`; the `id` field on the stub identifies which trigger fired. See `MissionSDK/TriggerManager.cs` and `MissionReader/Json/Triggers/`.
- **Threading** - AI work runs on AsyncPump worker threads using immutable `StateSnapshot` reads. The completion callback fires back on the main thread at a deterministic `TethysGame.Time()`, where `GameState` can be safely mutated. Don't touch `GameState` from worker threads.

---

## Status & maintenance

This is the active community fork. Original development was 2019–2020 by TechCor, with revisited work in 2025–2026 (refactored MissionReader to use the `MasterVariant` schema). The community fork on this branch:

- **Pluggable AI architecture** — `IBotPlayer` contract lets multiple AI implementations coexist. `MissionSDK/AI/` is TechCor's reference (frozen baseline), `MissionSDK/AIv2/` is the improvement playground (forked from AI/), `MissionSDK/AI_Test/` is a from-scratch single-file reference bot, `MissionSDK/AI_Blank/` is a minimal heartbeat-only template for new AI authors. Per-player AI selection via the new `"AIImpl"` field in the `.opm` JSON. See `AI_OVERVIEW.md`.
- Restores buildability on modern Visual Studio
- Adds robust JSON deserialization (`OnDeserializing` defaults across data classes)
- Fixes the `.opm` path resolution (resolves relative to mission DLL - no more dual-deploy)
- Adds per-bot diagnostic logging without changing AI behavior, plus per-bot Status and Research snapshots written every Mark
- Adds wall-clock-timestamped SDK lifecycle log (`MissionSDK.log`, append mode) and timestamps the existing Console.WriteLine log (`DotNetLog.txt`)
- Per-bot logs dedup consecutive identical lines with a repeat counter (cuts log size ~10x in busy missions)
- Fixes the `UnitEx_GetUnknownValue` P/Invoke crash that killed OP2 at ~25 min runtime
- Adds HFL `Kids` / `Workers` / `Scientists` accessors (with the caveat documented in `ISSUES.md` that the underlying fields are slot capacities, not real population)
- Broadcasts "Mission Ended" across all logs so events align across files
- Updates the bundled `cTest.opm` to the new schema (and ships in the OPU `maps/<MissionName>/` convention)
- Vendors HFL / OP2Helper / Outpost2DLL / odasl directly - no more submodules to manage

See `CHANGES.md` for a complete fix-by-fix history, `ISSUES.md` for the running list of known issues, and `AI_OVERVIEW.md` for an architectural tour of `BotPlayer`.

---

## Misc

Original work by TechCor. See the [forum thread](https://forum.outpost2.net/index.php/topic,6245.0.html) for the project's origin story. This fork preserves attribution and seeks to keep the SDK accessible to the community.

[![Video AI Demo](https://i3.ytimg.com/vi/-LvTZeNePBQ/hqdefault.jpg)](https://www.youtube.com/watch?v=-LvTZeNePBQ)

