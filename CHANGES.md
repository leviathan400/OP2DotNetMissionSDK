# Changes — Sessions 2026-05-26 / 2026-05-27

Brought OP2DotNetMissionSDK from "won't load" to "human vs AI on `on6_01.map`, with full diagnostic logs". This document records every change, why it was needed, and what it unblocks.

## Validation

End-to-end test: cTest mission (Colony Game, Eden, 2 players) launches via OPULauncher, both AI bots are created (BotType=LaunchStarship for each), each builds a CommandCenter, top-level goal selection runs, and AI events fire ("Wreckage Discovered", etc.). Confirmed visually and via per-bot logs (`BotPlayer_0.txt`, `BotPlayer_1.txt`) showing top goals per cycle and DoBuild commands.

## Build environment

### MSVC toolset override
- **Problem**: `NativeSDK/HFL/Source/HFL.vcxproj` and `NativeSDK/Outpost2DLL/Outpost2DLL.vcxproj` target the `v141` (VS 2017) toolset. Modern Visual Studio installs ship newer toolsets; `v141` may not be present.
- **Fix**: build with `-p:PlatformToolset=v143` to override at MSBuild time. No source change required.
- **Command** (from sln dir):
  ```
  MSBuild.exe DotNetMissionSDK.sln -t:Restore -p:Configuration=Release        # first time
  MSBuild.exe DotNetMissionSDK.sln -p:Configuration=Release -p:PlatformToolset=v143 -m
  ```

### HFL submodule sync
- **Problem**: The bundled `NativeMissionSDK/NativeSDK/HFL` submodule was older than what `DotNetInterop/HFL/UnitEx.cpp` expects. Build failed with ~50 C2039 errors (missing methods like `SetLaunchPadCargo`, `HasPower`, `HasWorkers`, `GetBarYield`, `GetOreType`, `GetSurveyedBy`, `GetLabCurrentTopic`, `GetLabScientistCount`, `IsInfected`, `GetNumTruckLoadsSoFar`).
- **Fix**: Copy `OP2MissionSDK/HFL/Source/*` (the newer upstream) over `NativeMissionSDK/NativeSDK/HFL/Source/`. Adds `Research.cpp`/`Research.h`, refreshes `UnitEx.h`.
- **Caveat**: `OP2Helper` and `Outpost2DLL` submodules were already aligned; only HFL needed updating.

## Native plugin

### `NativeMissionSDK/NativePlugin/LevelMain.cpp`
- **Problem**: shipped with hex-edit placeholder X-strings for level metadata. A fresh build produces a DLL with literal `"LevelDescXXXXX..."` and `"DotNetMissionSDKXXXXXXX..."` strings, so OP2 can't load it and the C# DLL name resolution fails.
- **Fix**: Replaced `ExportLevelDetails(...)` with `ExportLevelDetailsEx("Colony Game, Eden", "on6_01.map", "MULTITEK.TXT", Colony, 2, 12, false)` and set `SdkPath = "DotNetMissionSDK_v0.dll"` (matching the versioned C# DLL name).
- **Caveat**: Emits deprecation warning ("`ExportLevelDetailsEx` has been deprecated. Please use `ExportLevelDetailsFull` instead"). Non-blocking; switch to `ExportLevelDetailsFull` when convenient.

## C# JSON deserialization

`DataContractJsonSerializer` does **not** call constructors. Reference-type fields default to `null` after deserialization unless `[OnDeserializing]` initializes them. The repo only had partial OnDeserializing coverage. Without these, the code crashes with `NullReferenceException` during mission initialization on otherwise-valid JSON.

### `DotNetMissionSDK/MissionReader/Json/MissionRoot.cs`
- Extended existing `OnDeserializing` to default `LevelDetails`, `MasterVariant`, `MissionVariants`, `Disasters`, `Triggers` (in addition to `SdkVersion`, `Regions` it already defaulted).

### `DotNetMissionSDK/MissionReader/Json/MissionVariant.cs`
- **Added** `[OnDeserializing]` method defaulting `Name`, `TethysGame`, `TethysDifficulties`, `Players`, `Layouts`.

### `DotNetMissionSDK/MissionReader/Json/PlayerData.cs`
- **Added** `[OnDeserializing]` method defaulting `BotType`, `Color`, `Allies`, `Resources`, `Difficulties`.

## Mission data schema migration — `D:\Outpost 2\cTest.opm`

The shipped `cTest.opm` is in an older schema that the current `MissionRoot` doesn't recognize. Migrated to the new schema:

1. **Wrapped top-level `TethysGame`/`Players`/`AutoLayouts` inside `MasterVariant`**. The new schema expects `MasterVariant.{TethysGame,Players,AutoLayouts}`, not these as top-level keys. `Disasters`/`Triggers` stay at top level.
2. **Nested each player's resource fields into a `Resources` sub-object**. Previously: `Player.{TechLevel,MoraleLevel,Kids,Workers,CommonOre,Units,...}` flat. New: `Player.Resources.{TechLevel,...}`. Top-level player keeps `ID,IsEden,IsHuman,BotType,Color,Allies,Resources`.
3. **Trimmed Players from 6 to 2** to match `LevelDetails.NumPlayers=2`. Extra player blocks caused `ArgumentOutOfRangeException` in BotPlayer construction (see defensive fix below).
4. **Saved without UTF-8 BOM**. `DataContractJsonSerializer` rejects the BOM with "Encountered unexpected character 'ï'". PowerShell's `Set-Content -Encoding UTF8` writes the BOM; use `[System.IO.File]::WriteAllText(path, content, [System.Text.UTF8Encoding]::new($false))` instead.
5. Note: `Triggers2` field remains (old name). Current SDK reads `Triggers`. Migration of the trigger payload to the new event-driven schema is a follow-up.

## Defensive runtime fixes

### `DotNetMissionSDK/MissionSDK/MissionLogic.cs` — StartMission player loop
- **Problem**: `StartMission` iterates JSON `missionVariant.Players` with loop index `i`, creating a `BotPlayer(botType, i)` for each. If the JSON has more players than `TethysGame.PlayerCount()`, the BotPlayer's downstream code (`MaintainDefenseTask.GeneratePrerequisites` → `GameState.players[ownerID]`) crashes with `ArgumentOutOfRangeException`.
- **Fix**: Loop condition now also bounds by active player count:
  ```csharp
  int activePlayerCount = TethysGame.PlayerCount();
  for (int i=0; i < missionVariant.Players.Count && i < activePlayerCount; ++i)
  ```

### `DotNetMissionSDK/Main.cs` — Initialize exception visibility
- **Problem**: C# exceptions thrown inside `Initialize()` propagated to native OP2.exe and crashed the process without any diagnostic.
- **Fix**: Wrapped `InitializeSystems()` + `m_MissionLogic.InitializeNewMission()` in try/catch that writes `ex.ToString()` to `DotNetLog.txt` and returns `false` (which surfaces as OP2's "Could not initialize game" rather than a hard crash).

## Diagnostics — per-bot event log

New diagnostic infrastructure for AI behavior analysis. One log file per AI player in the OP2 working directory.

### `DotNetMissionSDK/MissionSDK/AI/BotLog.cs` (new file)
- Thread-safe static accessor: `BotLog.Get(ownerID)` returns the per-bot logger
- Writes to `BotPlayer_{ownerID}.txt` (FileMode.Create — overwrites each mission start)
- `Write(int tick, string message)` — usable from main thread AND AsyncPump worker threads
- Pass `stateSnapshot.time` for the tick when called from inside a task (worker thread)

### Wired into:
- **`BotPlayer.cs`** — logs construction with botType
- **`BaseManager.cs`** — captures `TethysGame.Time()` on main thread before scheduling async work; the async worker logs the top 3 goals by importance each cycle
- **`BuildStructureTask.cs`** — logs every PerformTask invocation: "no convec", "convec busy action=X", "NO VALID TILE near (x,y)", or "issuing DoBuild at (x,y) convec=N"

## What the diagnostics revealed (not yet addressed)

The first run with logging exposed several AI behaviors worth examining later:

- **`IsTaskComplete()` always returns `false`** in `BuildStructureTask` ([line 46](DotNetMissionSDK/MissionSDK/AI/Tasks/Base/Structure/BuildStructureTask.cs)). After a structure is built, the same `DoBuild` is re-issued repeatedly (e.g. StructureFactory at (53,35) was issued at t=342, t=348, t=474, t=480 — same convec, same location). This is what produces the in-game "Building command not successful" messages.
- **Multiple goals share a single convec via redundant prereqs.** At each cycle ~5 different goals (MaintainPower, MaintainArmy, MaintainResearch, etc.) each independently create a `BuildStructureTask(StructureFactory)` prereq. All five find the same convec, see it's busy, log a failure, repeat. Out of 3,702 `BuildStructureTask(StructureFactory)` invocations across both bots, only 272 issued a real `DoBuild`.
- **`BaseManager.cs:88` BotType weight tables are still stubbed** — all 10 BotType personalities currently behave identically because the switch cases are empty.

## Deployment

Build → deploy paths:
- Build output: `Release/` (native) and `DotNetMissionSDK/bin/Release/netstandard2.0/` (C#)
- Post-build copy: `Outpost2/` (staging dir under repo root)
- **Recommended runtime layout** (matches OPU 1.4.1+ convention):
  - `DotNetInterop.dll` — at OP2 install root (shared across all missions)
  - `OPU/maps/<MissionName>/cTest.dll`, `cTest.opm`, `DotNetMissionSDK_v0.dll` — per-mission folder

## Additional fixes — 2026-05-27

### `.opm` path resolution (DotNetMissionSDK/Main.cs)
- **Problem**: `Attach()` looked for `<MissionName>.opm` via a relative path, which resolved to OP2's current working directory. OPULauncher sets CWD to its own `OPU/` subdir, so the JSON had to be duplicated there.
- **Fix**: Resolve the `.opm` path relative to the mission DLL's directory first; fall back to CWD for legacy installs. Eliminates the dual-deploy requirement.

### Mission metadata documentation (NativeMissionSDK/NativePlugin/LevelMain.cpp)
- Added a block comment documenting every parameter of `ExportLevelDetailsEx` so future mission authors can see what to change for their own mission.

### Wall-clock-timestamped logs (3 new files + wiring)
- **`DotNetMissionSDK/MissionSdkLog.cs`** (new) — append-mode `MissionSDK.log` for SDK lifecycle events (DLL load, attach, init, detach). Wall-clock-stamped. Persists history across mission runs.
- **`DotNetMissionSDK/TimestampedTextWriter.cs`** (new) — wraps Console.Out/Error so every `Console.WriteLine` in the C# SDK automatically gets `[yyyy-MM-dd HH:mm:ss.fff]` prefix in `DotNetLog.txt`. Handles multi-line strings (e.g. exception traces) correctly — one timestamp per line.
- **`BotLog`** got a wall-clock header (`# BotLog opened at ...`) and a wall-clock footer on close. New `BotLog.WriteAll(tick, msg)` static broadcasts to every open bot log — used to record "Mission Ended" in every bot log simultaneously.
- **`MissionLogic.Dispose()`** now broadcasts "Mission Ended" to all logs so events align across files.
- **`DotNetMissionEntry.Detach()`** calls `BotLog.CloseAll()` to release file handles cleanly (was previously leaked).

### Bundled `Outpost2/cTest.opm` migrated to new schema
- The shipped `.opm` was in the old (pre-MasterVariant) schema, causing fresh clones to crash on init. Replaced with the migrated version (MasterVariant wrapper, Resources sub-object per player, 2 players: 1 human + 1 AI, UTF-8 without BOM).

### `LevelMain.cpp` `SdkPath` matches versioned DLL name
- `Export const char SdkPath[] = "DotNetMissionSDK_v0.dll"` — matches the C# build output. Without this, the native side wouldn't find the C# DLL.

### README rewrite
- Complete rewrite covering: what the SDK is, layer architecture, build prerequisites with explicit toolset notes, OPU `maps/` folder convention for installation, JSON-only mission creation, C# mission creation, the four diagnostic log files, differences from native SDK, maintenance status.

## De-submoduling — 2026-05-27 (post-initial-release)

**Problem**: `NativeMissionSDK/NativeSDK` was a git submodule pointing at `OutpostUniverse/OP2MissionSDK @ 441ef49` (V4.1.0 tag). That repo itself has further nested submodules for HFL, OP2Helper, Outpost2DLL. The nested-submodule structure caused multiple practical issues:
- Stale pinning: V4.1.0 (2019-era) pinned an older HFL than what `DotNetInterop/HFL/UnitEx.cpp` needs. Builds failed with ~50 C2039 errors until the HFL submodule was manually advanced.
- Cloning required `git clone --recurse-submodules` plus an awareness of the manual HFL update — friction for anyone trying the SDK.
- GitHub Desktop awkwardness: navigating into a submodule treats it as a separate repo, and accidental commits there land in detached HEAD with no way to push (unless you fork the submodule too).
- Confirmation from BlackBox (HFL author) that **HFL is no longer maintained upstream**, so tracking submodule updates buys nothing.

**Fix**: Convert the NativeSDK submodule to vendored regular files. Two-commit operation:

1. **`7408ab1 Remove NativeSDK submodule`** — `git submodule deinit -f NativeMissionSDK/NativeSDK`, `git rm -rf NativeMissionSDK/NativeSDK`, remove `.gitmodules`, clean `.git/modules/NativeMissionSDK/NativeSDK`.
2. **`c06328b Vendor NativeSDK source files`** — restore 277 source files (HFL, OP2Helper, Outpost2DLL, odasl, Readme.md) as regular tracked files. HFL Source matches `OutpostUniverse/HFL` master (which has the methods we need despite being 6 years old). OP2Helper/Outpost2DLL/odasl are as-shipped in OP2MissionSDK V4.1.0.

**Result**: repo is now fully self-contained.
- `git clone https://github.com/leviathan400/OP2DotNetMissionSDK` works without `--recurse-submodules`
- No more manual HFL sync step needed
- Build succeeds straight off a fresh clone (with the `-p:PlatformToolset=v143` override still required for the v141-targeted vcxprojs inside the vendored OP2MissionSDK)
- Trade-off: lost automatic upstream tracking (which we weren't really getting via the pinned V4.1.0 anyway). If we want to sync from upstream HFL/OP2MissionSDK in the future, that becomes a deliberate manual sync, not an "init the submodule" step.

**Files changed**: 209 new tracked files in commit 2 (+11,569 lines), 2 deletions in commit 1.
